using System.IO;
using Reader.Core.App;
using Reader.Core.Flow;
using Reader.Core.Reader;
using Reader.Core.Text;

namespace Reader.App.Reading;

/// <summary>
/// One open book: the document, its restore/track controller and, for PDFs in flowing-text mode,
/// the text pipeline. A port of the Mac's ReaderModel (Android's ReaderViewModel).
/// </summary>
internal sealed class ReaderModel
{
    public string DocumentId { get; }
    public ReaderController Controller { get; }
    /// <summary>Flowing-text view of a PDF; created the first time the reader switches to text mode.</summary>
    public TextDocument? Text { get; private set; }
    public string Format { get; private set; } = "PDF";
    public string? PdfPath { get; private set; }
    private readonly AppGraph _graph;
    private bool _closed;
    private IFlowSource? _flowSource;

    public ReaderModel(string documentId, AppGraph graph)
    {
        DocumentId = documentId;
        _graph = graph;
        Controller = new ReaderController(documentId, LoadAsync, graph.Positions, graph.SyncEngine, graph.Telemetry, graph.Scheduler);
        graph.OpenReader = this;
        Controller.Start();
    }

    public ReaderUiState State => Controller.State;
    public PdfBook? Pdf => State.Document as PdfBook;
    public EpubBook? Epub => State.Document as EpubBook;
    public bool IsTextMode => State.Mode == ReadingMode.Text;
    public ReaderSettings Settings => _graph.Settings;
    public int PageCount => State.Document?.PageCount ?? 0;

    private async Task<LoadedDocument> LoadAsync()
    {
        var doc = _graph.Documents.Get(DocumentId) ?? throw new ReaderException("This book is no longer in your library");
        try { _graph.Documents.Touch(DocumentId); } catch { }
        if (!File.Exists(doc.LocalPath)) throw new ReaderException("The book's file is missing");
        Format = doc.Format;
        if (doc.Format == "EPUB")
        {
            // EPUBs are text already: always flowing text. Parsing runs off the UI thread.
            var book = await Task.Run(() => EpubBook.Open(doc.LocalPath));
            return new LoadedDocument(doc.Title, book, ReadingMode.Text, _graph.Settings.TextScale);
        }
        PdfPath = doc.LocalPath;
        var pdf = await Task.Run(() => new PdfBook(doc.LocalPath));
        var mode = ReadingModes.Parse(doc.ReadingMode) ?? ReadingMode.Pages;
        if (mode == ReadingMode.Text)
        {
            // Build around the saved page first, so the restore target is ready soonest.
            EnsureText(pdf, _graph.Positions.Load(DocumentId)?.PageIndex ?? 0);
        }
        return new LoadedDocument(doc.Title, pdf, mode, _graph.Settings.TextScale);
    }

    // Commands

    public void SetMode(ReadingMode mode)
    {
        if (Pdf is not { } pdf || mode == State.Mode) return; // EPUBs have no original pages
        if (mode == ReadingMode.Text) EnsureText(pdf, State.PageIndex);
        Controller.SetMode(mode);
        try { _graph.Documents.SetReadingMode(DocumentId, mode); } catch { }
    }

    public void ZoomIn()
    {
        if (IsTextMode) SetTextScale(ReaderSettings.NextStep(ReaderSettings.TextSteps, State.TextScale));
        else Controller.SetZoom(ReaderSettings.NextStep(ReaderSettings.ZoomSteps, State.Zoom));
    }

    public void ZoomOut()
    {
        if (IsTextMode) SetTextScale(ReaderSettings.PreviousStep(ReaderSettings.TextSteps, State.TextScale));
        else Controller.SetZoom(ReaderSettings.PreviousStep(ReaderSettings.ZoomSteps, State.Zoom));
    }

    public void ActualSize()
    {
        if (IsTextMode) SetTextScale(1); else Controller.SetZoom(1);
    }

    public bool CanZoomIn => IsTextMode ? State.TextScale < ReaderSettings.TextSteps[^1] : State.Zoom < ReaderSettings.ZoomSteps[^1];
    public bool CanZoomOut => IsTextMode ? State.TextScale > ReaderSettings.TextSteps[0] : State.Zoom > 1;

    public void SetTextScale(float scale)
    {
        Controller.SetTextScale(scale);
        _graph.Settings.TextScale = Controller.State.TextScale;
        _graph.Settings.Save();
    }

    public void GoToPage(int index) => Controller.GoToPage(index);
    public void RestartFromBeginning() => Controller.RestartFromBeginning();

    /// <summary>The page at the top of the window was read by OCR, so it may contain mistakes.</summary>
    public bool TopPageRecognized => IsTextMode && Text?.LoadedPage(State.PageIndex)?.Recognized == true;

    /// <summary>What the flowing-text view shows: the EPUB, or the PDF's text once text mode is on.</summary>
    public IFlowSource? FlowSource()
    {
        if (_flowSource is not null) return _flowSource;
        if (Epub is { } epub) _flowSource = new EpubFlowSource(epub);
        else if (Text is { } text && Pdf is not null) _flowSource = new PdfFlowSource(text);
        return _flowSource;
    }

    // Lifecycle

    public void Flush() => Controller.Flush();

    /// <summary>Back to the library: save now and release the document.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Controller.Flush();
        Text?.Close();
        State.Document?.Close();
        if (_graph.OpenReader == this) _graph.OpenReader = null;
    }

    private void EnsureText(PdfBook pdf, int fromPage)
    {
        if (Text is not null) return;
        var doc = new TextDocument(pdf.Path, TextDocument.CacheDirFor(_graph.TextCacheBase, DocumentId), pdf.PageCount,
            _graph.Ocr.Available ? _graph.Ocr : null, _graph.Pdf);
        Text = doc;
        doc.Prefetch(fromPage);
    }
}
