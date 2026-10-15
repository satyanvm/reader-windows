using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reader.App.Reading;
using Reader.Core.App;
using Reader.Core.Reader;
using Reader.Core.Sync;

namespace Reader.App;

/// <summary>
/// Acceptance checks against the real app, views and database, enabled only by environment
/// (a port of the Mac app's SelfTest):
/// <code>
///   READER_SELFTEST=all|read|turns|spreads|themes|pages|observe|library   what to do
///   READER_SELFTEST_LOG=&lt;file&gt;        JSON lines: measured and saved positions, PNG checks
///   READER_SELFTEST_OUT=&lt;dir&gt;         where PNG snapshots go (default: next to the log)
///   READER_SELFTEST_IMPORT=&lt;file;…&gt;   books to import first
///   READER_SELFTEST_DOC=&lt;n&gt;           which library book to open (sorted by id; default 0)
///   READER_SELFTEST_PAGE=&lt;n&gt;          the page "read" scrolls to (default 186)
///   READER_SELFTEST_SIGNUP / _SIGNIN=email:password   the account flows Settings runs
///   READER_SELFTEST_LINGER=&lt;seconds&gt;  stay open so background uploads run (default 1)
///   READER_SELFTEST_EXIT=1             close the app when done
/// </code>
/// Killing and restarting between runs is done from outside, so the lifecycle is real too.
/// </summary>
internal static class SelfTest
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("READER_SELFTEST_LOG");
    private static readonly object LogLock = new();

    public static bool Requested => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("READER_SELFTEST")) && LogPath is not null;

    /// <summary>A diagnostic line in the self-test log (no-op unless a self-test is running).</summary>
    public static void Trace(string message)
    {
        if (LogPath is null) return;
        Log(new Dictionary<string, object?> { ["trace"] = message, ["t"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
    }

    private static void Log(Dictionary<string, object?> fields)
    {
        if (LogPath is null) return;
        var line = JsonSerializer.Serialize(new SortedDictionary<string, object?>(fields)) + "\n";
        lock (LogLock)
        {
            try { File.AppendAllText(LogPath, line); } catch { }
        }
    }

    public static void RunIfRequested(MainWindow main)
    {
        if (!Requested) return;
        main.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Run(main, Environment.GetEnvironmentVariable("READER_SELFTEST")!);
            }
            catch (Exception e)
            {
                Log(new() { ["error"] = e.ToString() });
            }
            if (Environment.GetEnvironmentVariable("READER_SELFTEST_EXIT") == "1") main.Close();
        });
    }

    private static async Task<bool> WaitFor(Func<bool> cond, double timeoutSeconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static string OutDir => Environment.GetEnvironmentVariable("READER_SELFTEST_OUT") is { Length: > 0 } d
        ? d
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LogPath!)) ?? ".", "selftest-png");

    private static async Task Run(MainWindow main, string step)
    {
        var graph = AppGraph.Shared;
        Trace($"selftest {step} started pid={Environment.ProcessId}");
        await AccountSteps(graph);
        if (Environment.GetEnvironmentVariable("READER_SELFTEST_IMPORT") is { Length: > 0 } import)
        {
            var (added, failures) = await graph.ImportAsync(import.Split(';', StringSplitOptions.RemoveEmptyEntries));
            Log(new() { ["imported"] = added.Select(a => a.Title).ToList(), ["failures"] = failures });
            main.Library.Reload();
        }
        if (graph.Account.IsSignedIn)
        {
            await graph.LibrarySync.SyncAsync();
            await WaitFor(() => !graph.LibrarySync.IsSyncing, 120);
            main.Library.Reload();
            Log(new()
            {
                ["what"] = "library",
                ["titles"] = main.Library.Items.Select(i => i.Title).ToList(),
                ["progress"] = main.Library.Items.Select(i => i.Progress).ToList(),
                ["lastError"] = graph.LibrarySync.LastError,
                ["status"] = main.Library.StatusShown,
            });
        }
        Directory.CreateDirectory(OutDir);
        await Task.Delay(300);
        SnapshotWindow(main, "00-library");
        if (step == "library") return;

        var rows = graph.Documents.Library();
        var docIndex = int.TryParse(Environment.GetEnvironmentVariable("READER_SELFTEST_DOC"), out var n) ? n : 0;
        if (docIndex >= rows.Count)
        {
            Log(new() { ["step"] = step, ["error"] = "no book" });
            return;
        }
        // Reopen the same book each run (rows are ordered by last opened).
        var id = rows.OrderBy(r => r.Id, StringComparer.Ordinal).ElementAt(docIndex).Id;
        main.OpenBook(id);
        var reader = main.Reader!;
        var model = reader.Model;
        if (!await WaitFor(() => model.State.Phase.IsReading && (reader.Flow is not null || reader.Pages is not null)))
        {
            Log(new() { ["step"] = step, ["error"] = "book didn't open", ["phase"] = model.State.Phase.ToString() });
            return;
        }
        await Task.Delay(500);

        void Record(string what, Dictionary<string, object?>? extra = null)
        {
            var m = reader.Flow is { } f ? (f.MeasurePosition().Page, f.MeasurePosition().Fraction) :
                reader.Pages is { } p ? (p.MeasurePosition().Page, p.MeasurePosition().Fraction) : (-1, -1f);
            var saved = graph.Positions.Load(id);
            var fields = new Dictionary<string, object?>
            {
                ["step"] = step,
                ["what"] = what,
                ["page"] = m.Item1,
                ["fraction"] = (double)m.Item2,
                ["mode"] = model.State.Mode.RawValue(),
                ["paged"] = graph.Settings.PageByPage,
                ["twoPages"] = graph.Settings.TwoPages,
                ["columns"] = reader.Flow?.Columns,
                ["theme"] = graph.Settings.Theme.ToString(),
                ["zoom"] = (double)model.State.Zoom,
                ["textScale"] = (double)model.State.TextScale,
                ["width"] = (int)main.ActualWidth,
                ["savedPage"] = saved?.PageIndex ?? -1,
                ["savedFraction"] = (double)(saved?.PageOffset ?? -1),
                ["notice"] = reader.NoticeShown,
                ["ocrBanner"] = reader.OcrBannerShown,
                ["phase"] = model.State.Phase.GetType().Name,
            };
            if (extra is not null) foreach (var kv in extra) fields[kv.Key] = kv.Value;
            Log(fields);
        }

        Record("opened", new()
        {
            ["title"] = model.State.Title,
            ["pageCount"] = model.PageCount,
            ["telemetry"] = string.Join(",", graph.Telemetry.Counts().Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")),
        });
        SnapshotWindow(main, "01-opened");

        async Task Settle(int after)
        {
            await WaitFor(() => model.Controller.RestoreToken > after && model.State.Phase.IsReading);
            await Task.Delay(700); // pages loading above must not move the reader
        }

        if (step is "read")
        {
            var target = int.TryParse(Environment.GetEnvironmentVariable("READER_SELFTEST_PAGE"), out var t) ? t : 186;
            var token = model.Controller.RestoreToken;
            model.GoToPage(target);
            await Settle(token);
            await Task.Delay(900); // the debounce elapses; no flush is called
            Record("read");
        }

        if (step is "all" or "turns" or "spreads" or "themes")
        {
            if (model.Pdf is not null && !model.IsTextMode)
            {
                var token = model.Controller.RestoreToken;
                model.SetMode(ReadingMode.Text);
                await Settle(token);
            }
            if (!graph.Settings.PageByPage) reader.SetPaged(true);
            if (graph.Settings.TwoPages) reader.SetTwoPages(false);
            await Task.Delay(400);
            SnapshotWindow(main, "10-flow-single");
            var flow = reader.Flow!;
            var starts = new List<(int, double)>();
            starts.Add(Pos(flow));
            for (var i = 0; i < 3; i++)
            {
                flow.Turn(forward: true);
                await Task.Delay(120);
                SnapshotWindow(main, $"11-turn{i + 1}-mid-animation");
                await Task.Delay(400);
                starts.Add(Pos(flow));
                SnapshotWindow(main, $"12-turn{i + 1}");
                Record($"turnForward{i + 1}", new() { ["animating"] = reader.Animator.IsAnimating });
            }
            var backOk = true;
            for (var i = 2; i >= 0; i--)
            {
                flow.Turn(forward: false);
                await Task.Delay(450);
                backOk &= Pos(flow) == starts[i];
            }
            Record("turnedBack", new() { ["returnedExactly"] = backOk });
            // Turn during a turn: the first finishes at once, the second still animates.
            flow.Turn(true);
            await Task.Delay(60);
            flow.Turn(true);
            await Task.Delay(30);
            Record("doubleTurn", new() { ["animating"] = reader.Animator.IsAnimating });
            await Task.Delay(600);
            Record("doubleTurnDone", new() { ["overlayCleared"] = !reader.Animator.IsAnimating });
            SaveSnapshot(flow.Snapshot(), "13-flow-snapshot");
        }

        if (step is "all" or "spreads")
        {
            var flow = reader.Flow!;
            if (main.ActualWidth < 900) main.Width = 1200;
            await Task.Delay(300);
            var token = model.Controller.RestoreToken;
            reader.SetTwoPages(true);
            await Settle(token);
            Record("spreadOn", new() { ["columns"] = flow.Columns });
            SnapshotWindow(main, "20-flow-spread");
            var before = Pos(flow);
            flow.Turn(true);
            await Task.Delay(450);
            SnapshotWindow(main, "21-flow-spread-turned");
            flow.Turn(false);
            await Task.Delay(450);
            Record("spreadBack", new() { ["returnedExactly"] = Pos(flow) == before });
            token = model.Controller.RestoreToken;
            reader.SetTwoPages(false);
            await Settle(token);
            Record("spreadOff");
        }

        if (step is "all" or "themes")
        {
            foreach (var theme in new[] { ReaderTheme.Dark, ReaderTheme.Black, ReaderTheme.Sepia })
            {
                AppGraph.ApplyTheme(theme);
                await Task.Delay(300);
                SnapshotWindow(main, $"30-theme-{theme}");
                Record("theme");
            }
            var token = model.Controller.RestoreToken;
            reader.SetPaged(false);
            await Settle(token);
            reader.Flow?.ScrollBy(500);
            await Task.Delay(700);
            Record("continuousScrolled");
            SnapshotWindow(main, "31-continuous");
            token = model.Controller.RestoreToken;
            reader.SetPaged(true);
            await Settle(token);
            token = model.Controller.RestoreToken;
            model.ZoomIn();
            model.ZoomIn();
            await Settle(token);
            Record("largerText");
            SnapshotWindow(main, "32-larger-text");
            token = model.Controller.RestoreToken;
            model.SetTextScale(1);
            await Settle(token);
        }

        if ((step is "all" or "pages") && model.Pdf is not null)
        {
            var token = model.Controller.RestoreToken;
            model.SetMode(ReadingMode.Pages);
            await Settle(token);
            await Task.Delay(1500); // pages render
            Record("originalPages");
            SnapshotWindow(main, "40-pages");
            token = model.Controller.RestoreToken;
            model.ZoomIn();
            model.ZoomIn();
            await Settle(token);
            await Task.Delay(1000);
            Record("zoomedIn");
            SnapshotWindow(main, "41-pages-zoomed");
            token = model.Controller.RestoreToken;
            model.ActualSize();
            await Settle(token);
            token = model.Controller.RestoreToken;
            reader.SetTwoPages(true);
            await Settle(token);
            await Task.Delay(1500);
            Record("pagesSpread");
            SnapshotWindow(main, "42-pages-spread");
            token = model.Controller.RestoreToken;
            reader.SetTwoPages(false);
            await Settle(token);
        }

        if (step is "all")
        {
            foreach (var width in new[] { 560.0, 1300.0, 900.0 })
            {
                var token = model.Controller.RestoreToken;
                main.Width = width;
                await Settle(token);
                Record("resized");
            }
        }

        if (step is "observe")
        {
            // A late answer from the server lands after the initial restore.
            await Task.Delay(3000);
            Record("observed", new()
            {
                ["telemetry"] = string.Join(",", graph.Telemetry.Counts().Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")),
            });
        }

        // READER_SELFTEST_LINGER: stay open like a reader would, so background uploads run.
        var linger = double.TryParse(Environment.GetEnvironmentVariable("READER_SELFTEST_LINGER"), out var l) ? l : 1;
        await Task.Delay(TimeSpan.FromSeconds(linger));
        Log(new()
        {
            ["step"] = step,
            ["what"] = "done",
            ["pending"] = graph.Positions.Pending().Count,
            ["account"] = graph.Account.Email,
        });
    }

    private static (int, double) Pos(FlowView flow)
    {
        var m = flow.MeasurePosition();
        return (m.Page, Math.Round(m.Fraction, 4));
    }

    /// <summary>READER_SELFTEST_SIGNUP / READER_SELFTEST_SIGNIN = "email:password": what Settings does.</summary>
    private static async Task AccountSteps(AppGraph graph)
    {
        var signup = Environment.GetEnvironmentVariable("READER_SELFTEST_SIGNUP");
        var signin = Environment.GetEnvironmentVariable("READER_SELFTEST_SIGNIN");
        var value = signup ?? signin;
        if (string.IsNullOrEmpty(value)) return;
        var i = value.IndexOf(':');
        var (email, password) = (value[..i], value[(i + 1)..]);
        try
        {
            if (signup is not null) await graph.Account.CreateAccountAsync(email, password);
            else await graph.Account.SignInAsync(email, password);
            Log(new() { ["account"] = graph.Account.Email, ["signedIn"] = graph.Account.IsSignedIn });
        }
        catch (Exception e)
        {
            Log(new() { ["account"] = "error", ["error"] = e.Message });
        }
    }

    /// <summary>The whole window, overlays included, as a PNG, with a check that it isn't one flat colour.</summary>
    public static void SnapshotWindow(Window window, string name)
    {
        if (window.Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        SaveSnapshot(rtb, name);
    }

    public static void SaveSnapshot(BitmapSource bitmap, string name)
    {
        var path = Path.Combine(OutDir, name + ".png");
        try
        {
            Directory.CreateDirectory(OutDir);
            SavePng(bitmap, path);
            var stats = PixelStats.Of(bitmap);
            Log(new()
            {
                ["png"] = path,
                ["width"] = bitmap.PixelWidth,
                ["height"] = bitmap.PixelHeight,
                ["distinctColors"] = stats.DistinctColors,
                ["inkFraction"] = Math.Round(stats.InkFraction, 4),
                ["flat"] = stats.IsFlat,
            });
        }
        catch (Exception e)
        {
            Log(new() { ["png"] = path, ["error"] = e.Message });
        }
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}

/// <summary>How much is on a picture: distinct colours and the share of pixels unlike the most common one.</summary>
internal readonly record struct PixelStats(int DistinctColors, double InkFraction)
{
    /// <summary>One flat colour (an empty or blank snapshot).</summary>
    public bool IsFlat => DistinctColors <= 1 || InkFraction < 0.002;

    public static PixelStats Of(BitmapSource source)
    {
        BitmapSource bmp = source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var c = pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        var total = pixels.Length / 4;
        var most = counts.Count == 0 ? 0 : counts.Values.Max();
        return new PixelStats(counts.Count, total == 0 ? 0 : 1 - (double)most / total);
    }
}
