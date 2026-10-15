using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Reader.Core.App;
using Reader.Core.Reader;
using Reader.Core.Text;

namespace Reader.App.Reading;

/// <summary>
/// Original pages, top to bottom (or in pairs as a spread). Pages are laid out at the window's
/// width (<see cref="PageLayout"/>, the same integer math as Android and the Mac); zoom is a
/// magnification of that layout, so zooming never changes the layout's coordinates. Pages are
/// rendered lazily by Windows.Data.Pdf and cached; page heights come from the crop boxes, so a
/// restore never waits for rendering.
///
/// Next and previous page (←/→, and a pair at a time in a spread) slide with the same page-turn
/// animation as flowing text.
/// </summary>
internal sealed class PagesView : FrameworkElement
{
    public const int Gap = 12;
    public const int Inset = 12;
    public const int Gutter = 12;

    private readonly ReaderModel _model;
    private readonly PdfBook _book;
    private readonly ReaderController _controller;
    private readonly AppGraph _graph;
    private readonly VisualCollection _children;
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical, Width = 12, Opacity = 0.75 };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal, Height = 12, Opacity = 0.75 };

    private PageLayout? _single;
    private SpreadLayout? _spread;
    private int _builtWidth = -1;
    private bool _builtSpread;
    private double _docY;
    private double _docX;
    private int? _appliedToken;
    private LayoutKey? _lastKeySent;
    private bool _detached;
    private bool _settingBars;

    private readonly Dictionary<int, (int Width, BitmapSource Bitmap)> _bitmaps = new();
    private readonly LinkedList<int> _bitmapOrder = new();
    private readonly HashSet<(int Page, int Width)> _rendering = new();
    private const int KeepBitmaps = 24;

    public PageTurnAnimator? Animator { get; set; }

    /// <summary>A turn waiting for its restore to land: the picture before it, and its direction.</summary>
    private (BitmapSource Before, bool Forward, long At)? _pendingTurn;

    public event Action? Moved;

    public PagesView(ReaderModel model, PdfBook book, AppGraph graph)
    {
        _model = model;
        _book = book;
        _controller = model.Controller;
        _graph = graph;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _children = new VisualCollection(this) { _vbar, _hbar };
        _vbar.Scroll += (_, e) => { if (!_settingBars) ScrollTo(_docX, e.NewValue, report: true); };
        _hbar.Scroll += (_, e) => { if (!_settingBars) ScrollTo(e.NewValue, _docY, report: true); };
        SizeChanged += (_, _) => Sync();
        IsVisibleChanged += (_, _) => Sync();
        _controller.Changed += OnControllerChanged;
        ThemeManager.Changed += InvalidateVisual;
        IsManipulationEnabled = true;
    }

    public void Detach()
    {
        _detached = true;
        _controller.Changed -= OnControllerChanged;
        ThemeManager.Changed -= InvalidateVisual;
        _pendingTurn = null;
        Animator?.Stop();
    }

    private void OnControllerChanged() => Dispatcher.BeginInvoke(Sync, DispatcherPriority.Normal);

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        _vbar.Measure(availableSize);
        _hbar.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _vbar.Arrange(new Rect(Math.Max(0, finalSize.Width - _vbar.Width), 0, _vbar.Width, Math.Max(0, finalSize.Height - (_hbar.Visibility == Visibility.Visible ? _hbar.Height : 0))));
        _hbar.Arrange(new Rect(0, Math.Max(0, finalSize.Height - _hbar.Height), Math.Max(0, finalSize.Width - _vbar.Width), _hbar.Height));
        return finalSize;
    }

    private bool IsActive => !_detached && _controller.State.Mode == ReadingMode.Pages && IsVisible;
    private float Zoom => _controller.State.Zoom;
    private bool SpreadMode => _graph.Settings.TwoPages && ActualWidth >= SpreadLayout.MinSpreadWidth;
    private int ViewportWidth => Math.Max(1, (int)Math.Round(ActualWidth));
    private double VisibleDocHeight => ActualHeight / Zoom;
    private double VisibleDocWidth => ActualWidth / Zoom;
    private int TotalHeight => _spread?.TotalHeight ?? _single?.TotalHeight ?? 0;
    private int LayoutWidth => ViewportWidth;
    private double MaxY => Math.Max(0, TotalHeight - VisibleDocHeight);
    private double MaxX => Math.Max(0, LayoutWidth - VisibleDocWidth);

    /// <summary>The width pages are laid out at: the window, or one column of a spread.</summary>
    private int KeyWidth => _spread?.ColumnWidth ?? ViewportWidth;

    /// <summary>Keyed to this view's own mode, so a view being replaced can never answer for its successor.</summary>
    public LayoutKey CurrentKey => _controller.State.LayoutKeyFor(KeyWidth) with { Mode = ReadingMode.Pages };

    public void Sync()
    {
        if (!IsActive || ActualWidth <= 0 || ActualHeight <= 0) return;
        RelayoutIfNeeded();
        var key = CurrentKey;
        if (key != _lastKeySent)
        {
            _lastKeySent = key;
            _controller.OnLayout(key);
        }
        if (_controller.State.Phase.AsRestoring is { } r && r.Token != _appliedToken) ApplyRestore(r);
        UpdateBars();
        InvalidateVisual();
    }

    private void RelayoutIfNeeded()
    {
        var width = ViewportWidth;
        var spread = SpreadMode;
        if (width == _builtWidth && spread == _builtSpread && (_single is not null || _spread is not null)) return;
        _builtWidth = width;
        _builtSpread = spread;
        if (spread)
        {
            _spread = new SpreadLayout(_book.PageSizes, width, Gutter, Gap, Inset);
            _single = null;
        }
        else
        {
            _single = new PageLayout(_book.PageSizes, width, Gap, Inset);
            _spread = null;
        }
    }

    private void ApplyRestore(Phase.Restoring r)
    {
        var target = r.Target;
        var page = target?.PageIndex ?? 0;
        var fraction = target?.PageOffset ?? 0;
        var y = _spread?.Y(page, fraction) ?? _single!.Y(page, fraction);
        _docY = Math.Clamp(y, 0, MaxY);
        _docX = Math.Clamp((target?.HorizontalOffset ?? 0) * MaxX, 0, MaxX);
        var m = MeasurePosition();
        _appliedToken = r.Token;
        SelfTest.Trace($"pages restore token={r.Token} target={page}/{fraction} landed={m.Page}/{m.Fraction} y={_docY}");
        _controller.OnRestoreApplied(r.Token, CurrentKey, m.Page, m.Fraction, m.Horizontal, m.AtEnd);
        // The turn that asked for this restore slides the new page in (only if it's recent: a
        // turn at either end never restores, and mustn't animate a later, unrelated jump).
        if (_pendingTurn is { } turn && Environment.TickCount64 - turn.At < 1500 && Animator is not null)
        {
            Animator.Play(turn.Before, Snapshot(), turn.Forward);
        }
        _pendingTurn = null;
        Moved?.Invoke();
    }

    /// <summary>Where the viewport top is, in device-independent terms.</summary>
    public (int Page, float Fraction, float Horizontal, bool AtEnd) MeasurePosition()
    {
        int page;
        float fraction;
        if (_spread is { } s)
        {
            (page, fraction) = s.PositionAt(_docY);
        }
        else if (_single is { } l)
        {
            var slot = l.Slot(_docY);
            (page, fraction) = (slot.Index, l.Fraction(slot.Index, slot.Offset));
        }
        else
        {
            return (0, 0, 0, true);
        }
        var maxX = MaxX;
        var horizontal = maxX > 0.5 ? (float)Math.Clamp(_docX / maxX, 0, 1) : 0f;
        var atEnd = _docY >= TotalHeight - VisibleDocHeight - 1;
        return (page, fraction, horizontal, atEnd);
    }

    private void ScrollTo(double x, double y, bool report)
    {
        var nx = Math.Clamp(x, 0, MaxX);
        var ny = Math.Clamp(y, 0, MaxY);
        if (Math.Abs(nx - _docX) < 0.01 && Math.Abs(ny - _docY) < 0.01) return;
        _docX = nx;
        _docY = ny;
        UpdateBars();
        InvalidateVisual();
        if (report && IsActive && _controller.State.Phase.IsReading)
        {
            var m = MeasurePosition();
            _controller.OnScrolled(CurrentKey, m.Page, m.Fraction, m.Horizontal);
            Moved?.Invoke();
        }
    }

    /// <summary>Scrolls by screen pixels.</summary>
    public void ScrollBy(double dx, double dy) => ScrollTo(_docX + dx / Zoom, _docY + dy / Zoom, report: true);

    private void UpdateBars()
    {
        _settingBars = true;
        _vbar.Minimum = 0;
        _vbar.Maximum = MaxY;
        _vbar.ViewportSize = VisibleDocHeight;
        _vbar.LargeChange = VisibleDocHeight * 0.9;
        _vbar.SmallChange = 48;
        _vbar.Value = _docY;
        _vbar.Visibility = MaxY > 0 ? Visibility.Visible : Visibility.Collapsed;
        _hbar.Minimum = 0;
        _hbar.Maximum = MaxX;
        _hbar.ViewportSize = VisibleDocWidth;
        _hbar.LargeChange = VisibleDocWidth * 0.9;
        _hbar.SmallChange = 48;
        _hbar.Value = _docX;
        _hbar.Visibility = MaxX > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        _settingBars = false;
    }

    // Input

    public bool HandleWheel(int delta, bool horizontal, bool ctrl)
    {
        if (ctrl && !horizontal)
        {
            // Ctrl+wheel and touchpad pinch (which Windows sends as Ctrl+wheel).
            _zoomAccum += delta;
            if (Math.Abs(_zoomAccum) >= 60)
            {
                if (_zoomAccum > 0) _model.ZoomIn(); else _model.ZoomOut();
                _zoomAccum = 0;
            }
            return true;
        }
        var px = delta / 120.0 * SystemParameters.WheelScrollLines * 16;
        if (horizontal) ScrollBy(px, 0);
        else ScrollBy(0, -px);
        return true;
    }

    private double _zoomAccum;

    public bool HandleKey(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return false;
        var shift = mods == ModifierKeys.Shift;
        var page = Math.Max(48, ActualHeight - 48);
        switch (key)
        {
            case Key.Space: ScrollBy(0, shift ? -page : page); return true;
            case Key.PageDown: ScrollBy(0, page); return true;
            case Key.PageUp: ScrollBy(0, -page); return true;
            case Key.Down: ScrollBy(0, 48); return true;
            case Key.Up: ScrollBy(0, -48); return true;
            case Key.Right when MaxX > 0.5 && shift: ScrollBy(48, 0); return true;
            case Key.Left when MaxX > 0.5 && shift: ScrollBy(-48, 0); return true;
            case Key.Right: NextPage(); return true;
            case Key.Left: PreviousPage(); return true;
        }
        return false;
    }

    /// <summary>Next page; in a spread, the next pair. Animated like a page turn.</summary>
    public void NextPage()
    {
        var p = _controller.State.PageIndex;
        TurnTo(_spread is not null ? SpreadLayout.LeftPageOf(p) + 2 : p + 1, forward: true);
    }

    public void PreviousPage()
    {
        var p = _controller.State.PageIndex;
        TurnTo(_spread is not null ? SpreadLayout.LeftPageOf(p) - 2 : p - 1, forward: false);
    }

    private void TurnTo(int page, bool forward)
    {
        var count = _book.PageSizes.Count;
        if (count == 0) return;
        var target = Math.Clamp(page, 0, count - 1);
        // Nothing further that way (the last page, or already at the top of the first): no turn.
        var (current, fraction, _, _) = MeasurePosition();
        if (target == current && (forward || fraction <= 0.0001f)) return;
        Animator?.FinishNow();
        _pendingTurn = Animator is not null && _controller.State.Phase.IsReading && IsLoaded ? (Snapshot(), forward, Environment.TickCount64) : null;
        _controller.GoToPage(target);
    }

    private Point? _dragFrom;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is { } from && e.LeftButton == MouseButtonState.Pressed && Zoom > 1)
        {
            var p = e.GetPosition(this);
            ScrollBy(from.X - p.X, from.Y - p.Y);
            _dragFrom = p;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    private double _pinch = 1;

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        _pinch = 1;
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        base.OnManipulationStarting(e);
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        _pinch *= e.DeltaManipulation.Scale.X;
        var t = e.DeltaManipulation.Translation;
        if (t.X != 0 || t.Y != 0) ScrollBy(-t.X, -t.Y);
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        if (Math.Abs(_pinch - 1) > 0.05) _controller.SetZoom((float)Math.Clamp(Zoom * _pinch, 1, 4));
        _pinch = 1;
        e.Handled = true;
    }

    // Drawing

    protected override void OnRender(DrawingContext dc) => Draw(dc, request: true);

    /// <summary>
    /// The page background: the theme's, except White, where white pages need a soft grey around
    /// them to show where they end.
    /// </summary>
    private static Color Backdrop => ThemeManager.Current == ReaderTheme.White
        ? Color.FromRgb(0xEE, 0xEE, 0xF0)
        : ThemeManager.ToColor(ThemeManager.Colors.Background);

    /// <summary>Draws the visible pages; <paramref name="request"/> also asks for missing or blurry bitmaps.</summary>
    private void Draw(DrawingContext dc, bool request)
    {
        var bg = new SolidColorBrush(Backdrop);
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_single is null && _spread is null) return;
        var zoom = Zoom;
        var dpi = VisualTreeHelper.GetDpi(this);
        var top = _docY;
        var bottom = _docY + VisibleDocHeight;
        var shadow = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
        shadow.Freeze();
        foreach (var (index, rect) in VisiblePages(top, bottom))
        {
            var screen = new Rect((rect.X - _docX) * zoom, (rect.Y - _docY) * zoom, rect.Width * zoom, rect.Height * zoom);
            dc.DrawRectangle(shadow, null, new Rect(screen.X + 1, screen.Y + 2, screen.Width, screen.Height));
            dc.DrawRectangle(Brushes.White, null, screen);
            var wantWidth = BucketWidth(screen.Width * dpi.DpiScaleX);
            if (_bitmaps.TryGetValue(index, out var cached))
            {
                dc.DrawImage(cached.Bitmap, screen);
                if (request && cached.Width < wantWidth * 0.8) Request(index, wantWidth);
                Touch(index);
            }
            else if (request)
            {
                Request(index, wantWidth);
            }
        }
        if (!request) return;
        // Prefetch the page just below the window.
        foreach (var (index, rect) in VisiblePages(bottom, bottom + VisibleDocHeight * 0.5))
        {
            if (!_bitmaps.ContainsKey(index)) Request(index, BucketWidth(rect.Width * zoom * dpi.DpiScaleX));
        }
    }

    private static int BucketWidth(double px) => Math.Clamp((int)Math.Ceiling(px / 128.0) * 128, 128, 4096);

    private IEnumerable<(int Index, Rect Rect)> VisiblePages(double top, double bottom)
    {
        if (_spread is { } s)
        {
            var row = Math.Max(0, s.PositionAt(top).PageIndex / 2);
            for (; row < s.RowCount && s.RowTops[row] <= bottom; row++)
            {
                for (var i = row * 2; i <= row * 2 + 1 && i < s.PageCount; i++)
                {
                    // A page alone in the last row sits in the middle.
                    var alone = i == s.PageCount - 1 && i % 2 == 0;
                    var x = alone ? (ViewportWidth - s.ColumnWidth) / 2 : s.X(i);
                    var y = s.RowTops[row] + (s.RowHeights[row] - s.PageHeights[i]) / 2;
                    yield return (i, new Rect(x, y, s.ColumnWidth, s.PageHeights[i]));
                }
            }
        }
        else if (_single is { } l)
        {
            for (var i = l.Slot(top).Index; i < l.PageCount && l.PageTops[i] <= bottom; i++)
            {
                yield return (i, new Rect(0, l.PageTops[i], l.ContentWidth, l.PageHeights[i]));
            }
        }
    }

    private void Touch(int index)
    {
        _bitmapOrder.Remove(index);
        _bitmapOrder.AddFirst(index);
    }

    private async void Request(int index, int width)
    {
        if (_detached || !_rendering.Add((index, width))) return;
        try
        {
            var bmp = await _graph.Pdf.RenderBitmapAsync(_book.Path, index, width);
            if (bmp is null || _detached) return;
            if (_bitmaps.TryGetValue(index, out var existing) && existing.Width >= width) return;
            _bitmaps[index] = (width, bmp);
            Touch(index);
            while (_bitmapOrder.Count > KeepBitmaps)
            {
                var old = _bitmapOrder.Last!.Value;
                _bitmapOrder.RemoveLast();
                _bitmaps.Remove(old);
            }
            InvalidateVisual();
        }
        finally
        {
            _rendering.Remove((index, width));
        }
    }

    /// <summary>A picture of what the view shows now, drawn fresh (for the page-turn animation and the self-test).</summary>
    public BitmapSource Snapshot()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, request: false);
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
