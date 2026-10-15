using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Reader.Core.App;
using Reader.Core.Flow;
using Reader.Core.Reader;
using Reader.Core.Text;

namespace Reader.App.Reading;

/// <summary>
/// Flowing text (a PDF's reflowed text, or an EPUB). Page by page (the default) shows one page at
/// a time, or two side by side as a spread; continuous scroll is the alternative.
///
/// Positions are anchored to a unit (a PDF page, an EPUB block) and an offset into it, so text
/// above changing height as PDF pages finish building never moves what the reader sees: those
/// shifts are undone by construction, never reported. Only movement the reader causes (turns,
/// scrolling, Find) is reported to the controller and saved.
/// </summary>
internal sealed class FlowView : FrameworkElement
{
    public const double MaxTextWidth = 680;
    public const double MinMargin = 24;
    /// <summary>Top and bottom margins of a page.</summary>
    public const double PageMargin = 24;
    /// <summary>Below this width a spread shows one page.</summary>
    public const double SpreadMinWidth = 760;
    /// <summary>Space above the first unit when scrolling continuously.</summary>
    private const double ContinuousTopInset = 16;

    private readonly ReaderModel _model;
    private readonly ReaderController _controller;
    private readonly IFlowSource _source;
    private readonly AppGraph _graph;

    private FlowStyle? _style;
    private WpfUnitMeasurer? _measurer;
    private FlowLayout? _layout;
    private FlowPager? _pager;
    private (double Width, float Scale, ReaderTheme Theme, bool Paged, int Columns, double Dpi)? _built;
    private FlowPos _anchor;
    private int? _appliedToken;
    private int? _restoreInFlight;
    private LayoutKey? _lastKeySent;
    private bool _detached;
    private readonly HashSet<int> _pendingUnits = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Dictionary<string, ImageSource?> _epubImages = new();
    private readonly Dictionary<int, ImageSource?> _pageImages = new();
    private readonly HashSet<int> _pageImagesLoading = new();
    private (int Unit, int Start, int Length)? _highlight;

    public PageTurnAnimator? Animator { get; set; }

    /// <summary>Raised after the reader moved (a turn, a scroll, Find), for the chrome to update.</summary>
    public event Action? Moved;

    public FlowView(ReaderModel model, IFlowSource source, AppGraph graph)
    {
        _model = model;
        _controller = model.Controller;
        _source = source;
        _graph = graph;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _flushTimer!.Stop();
            FlushPendingUnits();
        }, Dispatcher);
        _flushTimer.Stop();
        _source.OnUnitsLoaded = units =>
        {
            foreach (var u in units) _pendingUnits.Add(u);
            // Pages arrive in bursts while prefetching; apply them together.
            if (!_flushTimer.IsEnabled) _flushTimer.Start();
        };
        SizeChanged += (_, _) => Sync();
        IsVisibleChanged += (_, _) => Sync();
        _controller.Changed += OnControllerChanged;
        ThemeManager.Changed += Sync;
    }

    public void Detach()
    {
        _detached = true;
        _controller.Changed -= OnControllerChanged;
        ThemeManager.Changed -= Sync;
        _source.OnUnitsLoaded = null;
        _flushTimer.Stop();
        Animator?.FinishNow();
        _measurer?.Dispose();
        _measurer = null;
    }

    private void OnControllerChanged() => Dispatcher.BeginInvoke(Sync, DispatcherPriority.Normal);

    public bool Paged => _graph.Settings.PageByPage;
    private bool IsActive => !_detached && _controller.State.Mode == ReadingMode.Text && IsVisible;

    public int Columns => Paged && _graph.Settings.TwoPages && ActualWidth >= SpreadMinWidth ? 2 : 1;

    /// <summary>The text column's width: at most 680, with margins of at least 24.</summary>
    public double ColumnWidth
    {
        get
        {
            var pageWidth = Columns == 2 ? ActualWidth / 2 : ActualWidth;
            return Math.Max(40, Math.Min(MaxTextWidth, pageWidth - MinMargin * 2));
        }
    }

    private double ColumnLeft(int column)
    {
        var pageWidth = Columns == 2 ? ActualWidth / 2 : ActualWidth;
        return Math.Round(column * pageWidth + (pageWidth - ColumnWidth) / 2);
    }

    private double PageTextHeight => Math.Max(40, ActualHeight - PageMargin * 2);

    /// <summary>Keyed to this view's own mode, so a view being replaced can never answer for its successor.</summary>
    public LayoutKey CurrentKey => _controller.State.LayoutKeyFor((int)Math.Round(ColumnWidth), Paged) with { Mode = ReadingMode.Text };

    /// <summary>Brings the layout, the controller's idea of the layout, and any pending restore in line.</summary>
    public void Sync()
    {
        if (!IsActive || ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureLayout();
        var key = CurrentKey;
        if (key != _lastKeySent)
        {
            _lastKeySent = key;
            _controller.OnLayout(key);
        }
        if (_controller.State.Phase.AsRestoring is { } r && r.Token != _appliedToken && r.Token != _restoreInFlight)
        {
            ApplyRestore(r.Token);
        }
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var want = (Math.Round(ColumnWidth), _controller.State.TextScale, ThemeManager.Current, Paged, Columns, dpi);
        if (_built == want && _layout is not null)
        {
            _pager?.Configure(Columns, PageTextHeight);
            return;
        }
        // Keep the reader's place across a rebuild the controller doesn't re-apply (theme, spreads at the same width).
        (int Unit, float Fraction)? keep = _layout is null ? null : _layout.UnitFraction(Paged && _pager is not null ? _pager.Start : _anchor);
        _measurer?.Dispose();
        _style = new FlowStyle(_controller.State.TextScale, ThemeManager.Current, dpi);
        _measurer = new WpfUnitMeasurer(_source, _style, Math.Round(ColumnWidth), Paged ? 0 : ContinuousTopInset,
            PageSizeOf, EpubImage, PageImage);
        _layout = new FlowLayout(_measurer);
        _pager = new FlowPager(_layout);
        _pager.Configure(Columns, PageTextHeight);
        _built = want;
        if (keep is { } k) Place(_layout.PosOf(k.Unit, k.Fraction));
    }

    /// <summary>Puts the top of the view at <paramref name="pos"/> (aligned to a whole line when paged). Not reported.</summary>
    private void Place(FlowPos pos)
    {
        if (_layout is null || _pager is null) return;
        if (Paged) _pager.Reset(pos);
        else _anchor = ClampContinuous(pos);
    }

    private FlowPos Top => Paged && _pager is not null ? _pager.Start : _anchor;

    private async void ApplyRestore(int token)
    {
        if (_controller.State.Phase.AsRestoring is not { } r || r.Token != token) return;
        _restoreInFlight = token;
        var page = r.Target?.PageIndex ?? 0;
        var fraction = r.Target?.PageOffset ?? 0;
        try
        {
            await _source.PrepareAsync(page);
        }
        catch
        {
            // Restore anyway; the unit shows a placeholder until its text arrives.
        }
        if (!IsActive || _controller.State.Phase.AsRestoring?.Token != token)
        {
            if (_restoreInFlight == token) _restoreInFlight = null;
            return;
        }
        FlushPendingUnits();
        EnsureLayout();
        if (_layout is null) return;
        Animator?.FinishNow();
        var (unit, within) = _source.ToUnit(page, fraction);
        _highlight = null;
        Place(_layout.PosOf(unit, within));
        var m = MeasurePosition();
        _appliedToken = token;
        _restoreInFlight = null;
        SelfTest.Trace($"flow restore token={token} target={page}/{fraction} unit={unit}/{within} landed={m.Page}/{m.Fraction}");
        _controller.OnRestoreApplied(token, CurrentKey, m.Page, m.Fraction, 0, m.AtEnd);
        InvalidateVisual();
        Moved?.Invoke();
    }

    /// <summary>Where the top of the (first) page is, in device-independent terms.</summary>
    public (int Page, float Fraction, bool AtEnd) MeasurePosition()
    {
        if (_layout is null || _source.UnitCount == 0) return (0, 0, true);
        var (unit, f) = _layout.UnitFraction(Top);
        var (page, fraction) = _source.FromUnit(unit, f);
        var atEnd = Paged && _pager is not null ? _pager.AtEnd : _layout.ContentBelow(_anchor, ActualHeight + 1) <= ActualHeight + 1;
        return (page, fraction, atEnd);
    }

    /// <summary>The unit at the top, for the OCR banner and the scrubber.</summary>
    public int TopUnit => _layout is null ? 0 : _layout.UnitFraction(Top).Unit;

    private void Report()
    {
        if (!IsActive || !_controller.State.Phase.IsReading) return;
        var m = MeasurePosition();
        _controller.OnScrolled(CurrentKey, m.Page, m.Fraction, 0);
        Moved?.Invoke();
    }

    // Loading pages (PDF flowing text)

    /// <summary>Replaces placeholders with real text. Positions are anchored per unit, so nothing the reader sees moves.</summary>
    private void FlushPendingUnits()
    {
        if (_layout is null || _measurer is null || _pendingUnits.Count == 0)
        {
            _pendingUnits.Clear();
            return;
        }
        var top = _layout.UnitFraction(Top).Unit;
        var topChanged = false;
        foreach (var u in _pendingUnits)
        {
            _layout.Invalidate(u);
            _measurer.Forget(u);
            if (u == top) topChanged = true;
        }
        SelfTest.Trace($"flow flushed {_pendingUnits.Count} units topChanged={topChanged}");
        _pendingUnits.Clear();
        if (topChanged && _controller.State.Phase.AsRestoring is null && _controller.Current is { } current)
        {
            // The page under the reader got its text: put the reader back on their place in it.
            var (unit, within) = _source.ToUnit(current.PageIndex, current.PageOffset);
            Place(_layout.PosOf(unit, within));
        }
        InvalidateVisual();
        Moved?.Invoke();
    }

    // Navigation

    /// <summary>Turns one page (two in a spread) with the page-turn animation. False at either end.</summary>
    public bool Turn(bool forward)
    {
        if (_layout is null || _pager is null || !_controller.State.Phase.IsReading) return false;
        if (!Paged)
        {
            ScrollBy((forward ? 1 : -1) * Math.Max(48, ActualHeight - 48));
            return true;
        }
        Animator?.FinishNow();
        var before = Animator is null ? null : Snapshot();
        var moved = forward ? _pager.Next() : _pager.Previous();
        if (!moved) return false;
        _highlight = null;
        InvalidateVisual();
        Report();
        if (Animator is not null && before is not null) Animator.Play(before, Snapshot(), forward);
        return true;
    }

    public void ScrollBy(double dy)
    {
        if (_layout is null || Paged || !_controller.State.Phase.IsReading) return;
        var next = ClampContinuous(_layout.Advance(_anchor, dy));
        if (_layout.Same(next, _anchor)) return;
        _anchor = next;
        InvalidateVisual();
        Report();
    }

    private FlowPos ClampContinuous(FlowPos p)
    {
        if (_layout is null || _layout.UnitCount == 0) return p;
        p = _layout.Normalize(p);
        var h = Math.Max(1, ActualHeight);
        var below = _layout.ContentBelow(p, h + 1);
        if (below < h - 24)
        {
            var end = new FlowPos(_layout.UnitCount - 1, _layout.Height(_layout.UnitCount - 1));
            p = _layout.Advance(end, -(h - 24));
        }
        return p;
    }

    // Input

    private long _lastNotchTurn;
    private long _lastTouchpadEvent;
    private double _touchpadAccum;
    private bool _gestureTurned;

    /// <summary>
    /// Mouse wheel and touchpad. Paged: one wheel notch turns one page (with a 250 ms lockout); a
    /// touchpad swipe turns one page once 24 px have accumulated, and its momentum is ignored.
    /// Continuous: scrolls.
    /// </summary>
    public bool HandleWheel(int delta, bool horizontal)
    {
        var now = Environment.TickCount64;
        var notch = !horizontal && delta != 0 && delta % 120 == 0;
        if (!Paged)
        {
            var dy = horizontal ? 0 : -delta / 120.0 * SystemParameters.WheelScrollLines * 16;
            if (dy != 0) ScrollBy(dy);
            return true;
        }
        if (notch)
        {
            if (now - _lastNotchTurn < 250) return true;
            _lastNotchTurn = now;
            Turn(forward: delta < 0);
            return true;
        }
        if (now - _lastTouchpadEvent > 180)
        {
            _touchpadAccum = 0;
            _gestureTurned = false;
        }
        _lastTouchpadEvent = now;
        if (_gestureTurned) return true;
        // A wheel delta of 120 is about 48 px of scrolling.
        _touchpadAccum += delta * 0.4;
        if (Math.Abs(_touchpadAccum) >= 24)
        {
            _gestureTurned = true;
            var forward = horizontal ? _touchpadAccum > 0 : _touchpadAccum < 0;
            Turn(forward);
        }
        return true;
    }

    /// <summary>Reading keys. Paged: Space, →, ↓, PageDown forward; Shift+Space, ←, ↑, PageUp back. Continuous: scroll, ←/→ by page.</summary>
    public bool HandleKey(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return false;
        var shift = mods == ModifierKeys.Shift;
        if (Paged)
        {
            switch (key)
            {
                case Key.Space: Turn(!shift); return true;
                case Key.Right or Key.Down or Key.PageDown when !shift: Turn(true); return true;
                case Key.Left or Key.Up or Key.PageUp when !shift: Turn(false); return true;
            }
            return false;
        }
        var page = Math.Max(48, ActualHeight - 48);
        switch (key)
        {
            case Key.Space: ScrollBy(shift ? -page : page); return true;
            case Key.PageDown: ScrollBy(page); return true;
            case Key.PageUp: ScrollBy(-page); return true;
            case Key.Down: ScrollBy(48); return true;
            case Key.Up: ScrollBy(-48); return true;
            case Key.Right: _controller.GoToPage(_controller.State.PageIndex + 1); return true;
            case Key.Left: _controller.GoToPage(_controller.State.PageIndex - 1); return true;
        }
        return false;
    }

    // Find

    /// <summary>Finds <paramref name="query"/> after (or before) the current match or page, and goes there. False if not found.</summary>
    public bool Find(string query, bool forward)
    {
        if (_layout is null || _measurer is null || string.IsNullOrWhiteSpace(query) || !_controller.State.Phase.IsReading) return false;
        var count = _source.UnitCount;
        var startUnit = _highlight?.Unit ?? _layout.UnitFraction(Top).Unit;
        var after = _highlight is { } h ? h.Start + (forward ? 1 : -1) : (forward ? -1 : int.MaxValue);
        for (var step = 0; step <= count; step++)
        {
            var u = ((forward ? startUnit + step : startUnit - step) % count + count) % count;
            if (_source.Content(u) is not FlowContent.Blocks blocks) continue;
            var matches = new List<(int Start, int Length)>();
            var unitChar = 0;
            foreach (var block in blocks.Items)
            {
                if (block.Kind == BlockKind.Image)
                {
                    unitChar += 1;
                    continue;
                }
                var text = BlockText.Of(block, hyphenate: block.Kind != BlockKind.Heading);
                var plain = block.Text;
                var i = plain.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
                while (i >= 0)
                {
                    var start = unitChar + text.Map[i];
                    var end = unitChar + text.Map[Math.Min(plain.Length, i + query.Length)];
                    matches.Add((start, Math.Max(1, end - start)));
                    i = plain.IndexOf(query, i + 1, StringComparison.CurrentCultureIgnoreCase);
                }
                unitChar += text.Display.Length + 1;
            }
            (int Start, int Length)? hit = null;
            if (step == 0)
            {
                hit = forward ? matches.FirstOrDefault(m => m.Start > after) : matches.LastOrDefault(m => m.Start < after);
                if (hit == default((int, int))) hit = null;
            }
            else if (matches.Count > 0)
            {
                hit = forward ? matches[0] : matches[^1];
            }
            if (hit is not { } found) continue;
            _highlight = (u, found.Start, found.Length);
            var pos = _layout.PosOf(new FlowChar(u, found.Start));
            Animator?.FinishNow();
            if (Paged) _pager!.Reset(pos);
            else _anchor = ClampContinuous(_layout.Advance(pos, -Math.Min(120, ActualHeight / 3)));
            InvalidateVisual();
            Report();
            return true;
        }
        return false;
    }

    public void ClearFind()
    {
        _highlight = null;
        InvalidateVisual();
    }

    // Drawing

    protected override void OnRender(DrawingContext dc) => Draw(dc, new Size(ActualWidth, ActualHeight));

    /// <summary>A picture of what the view shows now (for the page-turn animation and the self-test).</summary>
    public BitmapSource Snapshot()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }


    public void Draw(DrawingContext dc, Size size)
    {
        var bg = new SolidColorBrush(ThemeManager.ToColor(ThemeManager.Colors.Background));
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(size));
        if (_layout is null || _measurer is null || _style is null || _pager is null) return;
        if (Paged)
        {
            var pages = _pager.Pages();
            for (var c = 0; c < pages.Count && c < Columns; c++)
            {
                var slice = pages[c];
                if (slice.VisibleHeight <= 0) continue;
                DrawLines(dc, slice.Start, ColumnLeft(c), PageMargin, slice.VisibleHeight,
                    new Rect(ColumnLeft(c) - 8, PageMargin, ColumnWidth + 16, slice.VisibleHeight + 3));
            }
            if (Columns == 2) DrawFold(dc, size);
        }
        else
        {
            DrawLines(dc, _anchor, ColumnLeft(0), 0, size.Height, new Rect(0, 0, size.Width, size.Height));
        }
    }

    private void DrawFold(DrawingContext dc, Size size)
    {
        var x = Math.Round(size.Width / 2) + 0.5;
        var muted = ThemeManager.Colors.Muted;
        var line = new Pen(new SolidColorBrush(ThemeManager.ToColor(muted, 0x50)), 1);
        line.Freeze();
        dc.DrawLine(line, new Point(x, PageMargin), new Point(x, size.Height - PageMargin));
        var shade = new LinearGradientBrush(ThemeManager.ToColor(0, ThemeManager.Colors.IsDark ? (byte)0x30 : (byte)0x14),
            ThemeManager.ToColor(0, 0), 0);
        var shadeLeft = new LinearGradientBrush(ThemeManager.ToColor(0, 0), ThemeManager.ToColor(0, ThemeManager.Colors.IsDark ? (byte)0x30 : (byte)0x14), 0);
        dc.DrawRectangle(shade, null, new Rect(x, 0, 14, size.Height));
        dc.DrawRectangle(shadeLeft, null, new Rect(x - 14, 0, 14, size.Height));
    }

    private void DrawLines(DrawingContext dc, FlowPos start, double x, double y0, double height, Rect clip)
    {
        dc.PushClip(new RectangleGeometry(clip));
        var highlight = new SolidColorBrush(ThemeManager.Colors.IsDark ? Color.FromArgb(0x66, 0xD9, 0xA4, 0x65) : Color.FromArgb(0x55, 0xF2, 0xC0, 0x5C));
        foreach (var (unit, index, _, top, _) in _layout!.LinesFrom(start, height))
        {
            var rendered = _measurer!.Rendered(unit);
            if (index >= rendered.Items.Count) continue;
            var item = rendered.Items[index];
            var y = y0 + top;
            switch (item)
            {
                case Drawn.TextLineItem t:
                    if (_highlight is { } hl && hl.Unit == unit)
                    {
                        var lineStart = t.CharStart;
                        var lineEnd = t.CharStart + t.Line.Length;
                        var from = Math.Max(hl.Start, lineStart);
                        var to = Math.Min(hl.Start + hl.Length, lineEnd);
                        if (to > from)
                        {
                            foreach (var b in t.Line.GetTextBounds(from - t.BlockCharStart, to - from))
                            {
                                var r = b.Rectangle;
                                dc.DrawRectangle(highlight, null, new Rect(x + r.X, y + r.Y, r.Width, r.Height));
                            }
                        }
                    }
                    t.Line.Draw(dc, new Point(x, y), System.Windows.Media.TextFormatting.InvertAxes.None);
                    break;
                case Drawn.ImageItem img:
                    var rect = new Rect(x + img.X, y, img.Width, img.Bottom - img.Top);
                    if (img.Source() is { } src) dc.DrawImage(src, rect);
                    else dc.DrawRectangle(Brushes.White, null, rect);
                    break;
                case Drawn.PlaceholderItem p:
                    var dots = new FormattedText("…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(_style!.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), _style.BodySize,
                        _style.Muted, _style.PixelsPerDip);
                    dc.DrawText(dots, new Point(x + (ColumnWidth - dots.Width) / 2, y + (p.Bottom - p.Top - dots.Height) / 2));
                    break;
            }
        }
        dc.Pop();
    }

    // Images

    private WpfUnitMeasurer.PageSizeInfo? PageSizeOf(int page) =>
        _model.Pdf is { } pdf && page >= 0 && page < pdf.PageSizes.Count
            ? new WpfUnitMeasurer.PageSizeInfo(pdf.PageSizes[page].Width, pdf.PageSizes[page].Height)
            : null;

    private ImageSource? EpubImage(string path)
    {
        if (_epubImages.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        if (_model.Epub?.ImageBytes(path) is { } bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            catch
            {
                image = null;
            }
        }
        _epubImages[path] = image;
        return image;
    }

    /// <summary>A figure page in a PDF's flowing text: rendered at the column width, drawn when ready.</summary>
    private ImageSource? PageImage(int page)
    {
        if (_pageImages.TryGetValue(page, out var cached)) return cached;
        if (_model.PdfPath is not { } path || !_pageImagesLoading.Add(page)) return null;
        var width = (int)Math.Ceiling(ColumnWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        _ = LoadPageImage(path, page, width);
        return null;
    }

    private async Task LoadPageImage(string path, int page, int width)
    {
        var bmp = await _graph.Pdf.RenderBitmapAsync(path, page, Math.Max(200, width));
        _pageImages[page] = bmp;
        _pageImagesLoading.Remove(page);
        if (!_detached) InvalidateVisual();
    }
}
