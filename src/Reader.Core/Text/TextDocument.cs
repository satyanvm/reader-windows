using System.Collections.Concurrent;
using UglyToad.PdfPig;

namespace Reader.Core.Text;

/// <summary>
/// Flowing-text view of a PDF, built page by page and cached on disk, so each page is processed
/// once per device:
/// <code>
///     PDF text lines (or OCR of the page image, if the page has no text)
///       → running headers/footers removed (compared with nearby pages)
///       → reflowed into paragraphs and headings with italics/bold
///       → paragraphs split by a page break rejoined
/// </code>
/// Text is kept per source page, so a position ("page 186, 40% down") means the same place here
/// as in the original-pages view.
///
/// All extraction runs on one background worker thread with its own PdfPig document. Results
/// are published on the thread that created this object (the UI thread) through
/// <see cref="Loaded"/> and <see cref="OnPageLoaded"/>.
/// </summary>
public sealed class TextDocument
{
    public int PageCount { get; }

    private readonly Dictionary<int, PageText> _loaded = new();
    private readonly object _loadedLock = new();

    /// <summary>Pages finished so far (a snapshot). Read on the UI thread.</summary>
    public IReadOnlyDictionary<int, PageText> Loaded
    {
        get { lock (_loadedLock) return new Dictionary<int, PageText>(_loaded); }
    }

    public PageText? LoadedPage(int index)
    {
        lock (_loadedLock) return _loaded.GetValueOrDefault(index);
    }

    /// <summary>Called on the UI thread whenever a page is added to <see cref="Loaded"/>.</summary>
    public Action<int>? OnPageLoaded { get; set; }

    private readonly string _pdfPath;
    private readonly string _cacheDir;
    private readonly IOcr? _ocr;
    private readonly IPageRasterizer? _rasterizer;
    private readonly SynchronizationContext? _main;
    private readonly BlockingCollection<Action> _queue = new();

    // Worker-confined state.
    private PdfDocument? _document;
    private readonly Dictionary<int, RawPage> _raw = new();
    private readonly List<int> _rawOrder = new();
    private readonly Dictionary<int, List<Block>> _reflowed = new();
    private readonly List<int> _reflowOrder = new();
    private readonly HashSet<int> _built = new();
    private bool _closed;
    private int _prefetchGeneration;

    private sealed record RawPage(List<Line> Lines, bool Recognized);

    /// <summary>
    /// Bump when the pipeline's output changes, so old cached text is rebuilt. (The Mac app is at
    /// 3; Windows starts at 4 because its extractor is PdfPig, not PDFKit.)
    /// </summary>
    public const int CacheVersion = 4;
    /// <summary>Wide enough that body text is ~30 px tall, which OCR reads reliably.</summary>
    public const int OcrWidth = 1600;
    /// <summary>How many pages of raw lines and reflowed blocks are kept in memory.</summary>
    public const int LruSize = 24;

    public static string CacheDirFor(string baseDir, string documentId) =>
        Path.Combine(baseDir, $"text-v{CacheVersion}", documentId);

    public TextDocument(string pdfPath, string cacheDir, int pageCount, IOcr? ocr, IPageRasterizer? rasterizer = null)
    {
        _pdfPath = pdfPath;
        _cacheDir = cacheDir;
        PageCount = pageCount;
        _ocr = ocr;
        _rasterizer = rasterizer;
        _main = SynchronizationContext.Current;
        try { Directory.CreateDirectory(cacheDir); } catch { }
        var worker = new Thread(Work) { IsBackground = true, Name = "reader.text" };
        worker.Start();
    }

    private void Work()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine($"reader.text: {e}"); }
        }
    }

    private void Enqueue(Action a)
    {
        try
        {
            if (!_queue.IsAddingCompleted) _queue.Add(a);
        }
        catch (InvalidOperationException)
        {
            // Closed meanwhile.
        }
    }

    private void PostMain(Action a)
    {
        if (_main is null) a();
        else _main.Post(_ => a(), null);
    }

    /// <summary>Flowing text for page <paramref name="index"/>, building it if needed. Call from the UI thread.</summary>
    public async Task<PageText> PageAsync(int index)
    {
        if (LoadedPage(index) is { } p) return p;
        var tcs = new TaskCompletionSource<PageText>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try { tcs.SetResult(PageOnQueue(index)); }
            catch (Exception e) { tcs.SetException(e); }
        });
        if (_queue.IsAddingCompleted) tcs.TrySetResult(new PageText(Array.Empty<Block>()));
        var text = await tcs.Task;
        Publish(index, text);
        return text;
    }

    /// <summary>
    /// Builds pages in the background, starting at <paramref name="from"/> and working forward
    /// then backward, so the pages near the reader are ready first. Requests from
    /// <see cref="PageAsync"/> are served between prefetch steps. Calling it again restarts from
    /// the new page.
    /// </summary>
    public void Prefetch(int from)
    {
        var start = Math.Min(Math.Max(0, from), Math.Max(0, PageCount - 1));
        var order = Enumerable.Range(start, Math.Max(0, PageCount - start))
            .Concat(Enumerable.Range(0, start).Reverse()).ToArray();
        Enqueue(() =>
        {
            _prefetchGeneration += 1;
            PrefetchStep(order, 0, _prefetchGeneration);
        });
    }

    public void Close()
    {
        Enqueue(() =>
        {
            _closed = true;
            _document?.Dispose();
            _document = null;
            _raw.Clear();
            _reflowed.Clear();
            _queue.CompleteAdding();
        });
    }

    private void PrefetchStep(int[] order, int i, int generation)
    {
        if (_closed || generation != _prefetchGeneration || i >= order.Length) return;
        var index = order[i];
        if (!_built.Contains(index))
        {
            var text = PageOnQueue(index);
            PostMain(() => Publish(index, text));
        }
        Enqueue(() => PrefetchStep(order, i + 1, generation));
    }

    private void Publish(int index, PageText text)
    {
        lock (_loadedLock)
        {
            if (_loaded.ContainsKey(index)) return;
            _loaded[index] = text;
        }
        OnPageLoaded?.Invoke(index);
    }

    private PageText PageOnQueue(int index)
    {
        try
        {
            if (Cached(index) is { } cached) return cached;
            if (_closed) return new PageText(Array.Empty<Block>());
            var prev = index > 0 ? ReflowPage(index - 1) : null;
            var current = ReflowPage(index);
            var next = index < PageCount - 1 ? ReflowPage(index + 1) : null;
            var text = new PageText(PageAssembly.Assemble(prev, current, next), RawPageAt(index).Recognized);
            // Best effort: if the cache can't be written the page is just rebuilt next time.
            try
            {
                var file = CacheFile(index);
                var tmp = file + ".tmp";
                File.WriteAllBytes(tmp, text.Json());
                File.Move(tmp, file, overwrite: true);
            }
            catch { }
            return text;
        }
        finally
        {
            _built.Add(index);
        }
    }

    private List<Block> ReflowPage(int index)
    {
        if (_reflowed.TryGetValue(index, out var r)) return r;
        var neighbours = Enumerable.Range(index - PageAssembly.Window, PageAssembly.Window * 2 + 1)
            .Where(i => i != index && i >= 0 && i < PageCount)
            .Select(i => (IReadOnlyList<Line>)RawPageAt(i).Lines)
            .ToList();
        var blocks = Reflow.Blocks(PageAssembly.StripRunningLines(RawPageAt(index).Lines, neighbours));
        Remember(_reflowed, _reflowOrder, index, blocks);
        return blocks;
    }

    private RawPage RawPageAt(int index)
    {
        if (_raw.TryGetValue(index, out var r)) return r;
        if (_document is null && !_closed)
        {
            try { _document = PdfDocument.Open(_pdfPath); } catch { _document = null; }
        }
        var lines = new List<Line>();
        if (_document is not null && index >= 0 && index < _document.NumberOfPages)
        {
            try { lines = PdfLineExtractor.Lines(_document.GetPage(index + 1)); } catch { lines = new(); }
        }
        var result = new RawPage(lines, false);
        if (lines.Count == 0 && _ocr is not null && _rasterizer?.Render(_pdfPath, index, OcrWidth) is { } image)
        {
            List<Line> recognized;
            try { recognized = _ocr.Lines(image); } catch { recognized = new(); }
            result = new RawPage(recognized, recognized.Count > 0);
        }
        Remember(_raw, _rawOrder, index, result);
        return result;
    }

    /// <summary>Small LRU: keeps the 24 most recent pages, like the Android and Mac implementations.</summary>
    private static void Remember<T>(Dictionary<int, T> map, List<int> order, int key, T value)
    {
        map[key] = value;
        order.Remove(key);
        order.Add(key);
        while (order.Count > LruSize)
        {
            map.Remove(order[0]);
            order.RemoveAt(0);
        }
    }

    private string CacheFile(int index) => Path.Combine(_cacheDir, $"{index}.json");

    private PageText? Cached(int index)
    {
        try
        {
            var file = CacheFile(index);
            return File.Exists(file) ? PageText.FromJson(File.ReadAllBytes(file)) : null;
        }
        catch
        {
            return null;
        }
    }
}
