using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Reader.Core.App;
using Reader.Core.Reader;
using Reader.Core.Text;

namespace Reader.App.Reading;

/// <summary>
/// One open book, shown inside the main window on top of the library: the toolbar, the pages or
/// the flowing text, the notice pill, the scanned-page banner, Find, and the scrubber.
/// </summary>
internal sealed class ReaderView : DockPanel
{
    public ReaderModel Model { get; }
    public event Action? BackRequested;

    private readonly AppGraph _graph;
    private readonly Grid _content = new();
    private readonly Canvas _overlay = new();
    public PageTurnAnimator Animator { get; }
    public FlowView? Flow { get; private set; }
    public PagesView? Pages { get; private set; }

    private readonly TextBlock _title = new();
    private readonly RadioButton _pagesSegment;
    private readonly RadioButton _textSegment;
    private readonly Border _modeControl;
    private readonly ToggleButton _spreadToggle;
    private readonly TextBlock _spreadGlyph;
    private readonly Button _zoomOut;
    private readonly Button _zoomIn;
    private readonly Button _settingsButton;
    private readonly Button _moreButton;
    private readonly Slider _scrubber = new();
    private readonly TextBlock _scrubLabel = new();
    private readonly Border _bottomBar;
    private readonly Border _notice;
    private readonly TextBlock _noticeText = new();
    private readonly Button _noticeAction;
    private readonly Border _ocrBanner;
    private readonly Border _findBar;
    private readonly TextBox _findBox = new();
    private readonly TextBlock _findStatus = new();
    private readonly Border _status;
    private readonly TextBlock _statusTitle = new();
    private readonly TextBlock _statusDetail = new();
    private readonly DispatcherTimer _noticeTimer;
    private Notice? _shownNotice;
    private ReadingMode? _shownMode;
    private bool _settingScrubber;
    private bool _scrubbing;
    private bool _closed;

    public ReaderView(ReaderModel model, AppGraph graph)
    {
        Model = model;
        _graph = graph;
        SetResourceReference(BackgroundProperty, "BgBrush");
        Focusable = false;
        Animator = new PageTurnAnimator(_overlay);

        // Toolbar
        var back = IconButton("", "Library (Ctrl+[ or Alt+Left)", () => BackRequested?.Invoke());
        _title.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(6, 0, 12, 0);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

        _pagesSegment = Segment("Original Pages", "Show the PDF's own pages (Ctrl+1)", () => Model.SetMode(ReadingMode.Pages));
        _textSegment = Segment("Flowing Text", "Reflow the text like an ebook (Ctrl+2)", () => Model.SetMode(ReadingMode.Text));
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        segments.Children.Add(_pagesSegment);
        segments.Children.Add(_textSegment);
        _modeControl = new Border { CornerRadius = new CornerRadius(7), Child = segments, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _modeControl.SetResourceReference(Border.BackgroundProperty, "HoverBrush");

        _spreadGlyph = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16 };
        _spreadToggle = new ToggleButton { Style = (Style)Application.Current.Resources["ToolToggleStyle"], Content = _spreadGlyph };
        _spreadToggle.Click += (_, _) => SetTwoPages(_spreadToggle.IsChecked == true);

        _zoomOut = IconButton("", "Zoom Out (Ctrl+-)", () => Model.ZoomOut());
        _zoomIn = IconButton("", "Zoom In (Ctrl+=)", () => Model.ZoomIn());
        _settingsButton = IconButton("", "Reading Settings: text size, theme, page by page or continuous", ShowSettingsMenu);
        _moreButton = IconButton("", "More", ShowMoreMenu);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var c in new UIElement[] { _modeControl, _spreadToggle, _zoomOut, _zoomIn, _settingsButton, _moreButton }) right.Children.Add(c);
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(back, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(back);
        bar.Children.Add(right);
        bar.Children.Add(_title);
        var toolbar = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        toolbar.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // Bottom bar: the scrubber previews "Page N / M" while dragging and jumps on release.
        _scrubber.Focusable = false;
        _scrubber.IsMoveToPointEnabled = true;
        _scrubber.SmallChange = 1;
        _scrubber.LargeChange = 10;
        _scrubber.VerticalAlignment = VerticalAlignment.Center;
        _scrubber.ValueChanged += OnScrubberChanged;
        _scrubber.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _scrubbing = true));
        _scrubber.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _scrubbing = false;
            Model.GoToPage((int)Math.Round(_scrubber.Value));
        }));
        _scrubLabel.MinWidth = 120;
        _scrubLabel.TextAlignment = TextAlignment.Right;
        _scrubLabel.VerticalAlignment = VerticalAlignment.Center;
        _scrubLabel.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _scrubLabel.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        _scrubLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        var bottom = new DockPanel { Margin = new Thickness(16, 6, 16, 8) };
        DockPanel.SetDock(_scrubLabel, Dock.Right);
        bottom.Children.Add(_scrubLabel);
        bottom.Children.Add(_scrubber);
        _bottomBar = new Border { Child = bottom };
        _bottomBar.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        SetDock(_bottomBar, Dock.Bottom);
        Children.Add(_bottomBar);

        // Notice pill.
        _noticeText.VerticalAlignment = VerticalAlignment.Center;
        _noticeText.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _noticeText.FontSize = 13;
        _noticeText.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _noticeAction = new Button { Style = (Style)Application.Current.Resources["LinkButton"], Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _noticeAction.Click += (_, _) => PerformNotice();
        var dismiss = IconButton("", "Dismiss", () => Model.Controller.DismissNotice());
        dismiss.MinWidth = 26;
        dismiss.MinHeight = 26;
        dismiss.Margin = new Thickness(8, 0, -6, 0);
        ((TextBlock)dismiss.Content).FontSize = 10;
        var noticeRow = new StackPanel { Orientation = Orientation.Horizontal };
        noticeRow.Children.Add(_noticeText);
        noticeRow.Children.Add(_noticeAction);
        noticeRow.Children.Add(dismiss);
        _notice = new Border
        {
            Child = noticeRow,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 6, 12, 6),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.25 },
        };
        _notice.SetResourceReference(Border.BackgroundProperty, "PillBrush");
        _noticeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            if (Model.State.Notice is { } n && n == _shownNotice) Model.Controller.DismissNotice();
        };

        // Scanned-page banner.
        var ocrText = new TextBlock
        {
            Text = "Text recognized from a scanned page, so it may contain mistakes.",
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
        };
        ocrText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _ocrBanner = new Border { Child = ocrText, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
        _ocrBanner.SetResourceReference(Border.BackgroundProperty, "BgBrush");

        // Find.
        _findBox.Width = 220;
        _findBox.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        _findBox.FontSize = 13;
        _findBox.Padding = new Thickness(4, 3, 4, 3);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunFind(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFind();
                e.Handled = true;
            }
        };
        _findStatus.Margin = new Thickness(8, 0, 4, 0);
        _findStatus.VerticalAlignment = VerticalAlignment.Center;
        _findStatus.FontSize = 12;
        _findStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var findRow = new StackPanel { Orientation = Orientation.Horizontal };
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_findStatus);
        findRow.Children.Add(IconButton("", "Previous (Shift+Enter)", () => RunFind(false)));
        findRow.Children.Add(IconButton("", "Next (Enter)", () => RunFind(true)));
        findRow.Children.Add(IconButton("", "Close (Esc)", CloseFind));
        _findBar = new Border
        {
            Child = findRow,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 4, 4),
            Margin = new Thickness(0, 10, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.25 },
        };
        _findBar.SetResourceReference(Border.BackgroundProperty, "PillBrush");

        // Opening / couldn't open.
        _statusTitle.FontSize = 18;
        _statusTitle.FontWeight = FontWeights.SemiBold;
        _statusTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _statusTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _statusDetail.Margin = new Thickness(0, 6, 0, 0);
        _statusDetail.HorizontalAlignment = HorizontalAlignment.Center;
        _statusDetail.TextWrapping = TextWrapping.Wrap;
        _statusDetail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420 };
        statusStack.Children.Add(_statusTitle);
        statusStack.Children.Add(_statusDetail);
        _status = new Border { Child = statusStack };
        _status.SetResourceReference(Border.BackgroundProperty, "BgBrush");

        _content.Children.Add(_overlay);
        _content.Children.Add(_ocrBanner);
        _content.Children.Add(_notice);
        _content.Children.Add(_findBar);
        _content.Children.Add(_status);
        Children.Add(_content);

        Model.Controller.Changed += OnChanged;
        _graph.Settings.Save();
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Normal);

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["ToolButtonStyle"],
            Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 15 },
            ToolTip = tip,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private RadioButton Segment(string text, string tip, Action click)
    {
        var r = new RadioButton { Content = text, ToolTip = tip, GroupName = "mode-" + GetHashCode(), Style = (Style)Application.Current.Resources["SegmentStyle"] };
        r.Click += (_, _) => click();
        return r;
    }

    /// <summary>Brings the chrome and the content view in line with the controller's state.</summary>
    public void Refresh()
    {
        if (_closed) return;
        var state = Model.State;
        _title.Text = string.IsNullOrEmpty(state.Title) ? "Reader" : state.Title;
        switch (state.Phase)
        {
            case Phase.Loading:
                _status.Visibility = Visibility.Visible;
                _statusTitle.Text = "Opening…";
                _statusDetail.Text = "";
                break;
            case Phase.Failed f:
                _status.Visibility = Visibility.Visible;
                _statusTitle.Text = "Can't open this book";
                _statusDetail.Text = f.Message;
                break;
            default:
                _status.Visibility = Visibility.Collapsed;
                EnsureContentView();
                break;
        }

        var isPdf = Model.Pdf is not null;
        var hasDoc = state.Document is not null;
        var textMode = state.Mode == ReadingMode.Text;
        _modeControl.Visibility = isPdf ? Visibility.Visible : Visibility.Collapsed;
        _pagesSegment.IsChecked = !textMode;
        _textSegment.IsChecked = textMode;
        _zoomIn.Visibility = _zoomOut.Visibility = hasDoc && !textMode ? Visibility.Visible : Visibility.Collapsed;
        _zoomIn.IsEnabled = Model.CanZoomIn;
        _zoomOut.IsEnabled = Model.CanZoomOut;
        _spreadToggle.IsChecked = _graph.Settings.TwoPages;
        _spreadGlyph.Text = _graph.Settings.TwoPages ? "" : "";
        _spreadToggle.ToolTip = _graph.Settings.TwoPages ? "Two Pages (Ctrl+Alt+2). Click for one page (Ctrl+Alt+1)" : "One Page (Ctrl+Alt+1). Click for two pages (Ctrl+Alt+2)";
        _settingsButton.IsEnabled = hasDoc;
        _moreButton.IsEnabled = hasDoc;
        _bottomBar.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;

        UpdateScrubber();
        UpdateNotice(state.Notice);
        UpdateOcrBanner();
        if (!textMode && _findBar.Visibility == Visibility.Visible) CloseFind();
    }

    private void EnsureContentView()
    {
        var mode = Model.State.Mode;
        if (_shownMode == mode && (Flow is not null || Pages is not null)) return;
        Animator.FinishNow();
        if (Flow is not null)
        {
            Flow.Detach();
            _content.Children.Remove(Flow);
            Flow = null;
        }
        if (Pages is not null)
        {
            Pages.Detach();
            _content.Children.Remove(Pages);
            Pages = null;
        }
        _shownMode = mode;
        FrameworkElement view;
        if (mode == ReadingMode.Text && Model.FlowSource() is { } source)
        {
            Flow = new FlowView(Model, source, _graph) { Animator = Animator };
            Flow.Moved += () =>
            {
                UpdateOcrBanner();
                UpdateScrubber();
            };
            view = Flow;
        }
        else if (Model.Pdf is { } pdf)
        {
            Pages = new PagesView(Model, pdf, _graph);
            Pages.Moved += UpdateScrubber;
            view = Pages;
        }
        else
        {
            return;
        }
        _content.Children.Insert(0, view);
        Dispatcher.BeginInvoke(() =>
        {
            Flow?.Sync();
            Pages?.Sync();
            FocusContent();
        }, DispatcherPriority.Loaded);
    }

    public void FocusContent()
    {
        if (_findBox.IsKeyboardFocusWithin) return;
        (Flow as IInputElement ?? Pages)?.Focus();
        if (Flow is not null) Keyboard.Focus(Flow);
        else if (Pages is not null) Keyboard.Focus(Pages);
    }

    // Scrubber

    private string Unit => Model.Epub is not null ? "Chapter" : "Page";

    private void UpdateScrubber()
    {
        var count = Model.PageCount;
        _settingScrubber = true;
        _scrubber.Minimum = 0;
        _scrubber.Maximum = Math.Max(1, count - 1);
        _scrubber.IsEnabled = count > 1;
        if (!_scrubbing) _scrubber.Value = Math.Clamp(Model.State.PageIndex, 0, Math.Max(0, count - 1));
        _settingScrubber = false;
        _scrubLabel.Text = $"{Unit} {(int)Math.Round(_scrubber.Value) + 1} / {count}";
    }

    private void OnScrubberChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var shown = (int)Math.Round(_scrubber.Value);
        _scrubLabel.Text = $"{Unit} {shown + 1} / {Model.PageCount}";
        if (_settingScrubber || _scrubbing) return;
        // A click on the track: jump there.
        if (shown != Model.State.PageIndex) Model.GoToPage(shown);
    }

    // Notice

    private void UpdateNotice(Notice? notice)
    {
        if (notice == _shownNotice) return;
        _shownNotice = notice;
        _noticeTimer.Stop();
        if (notice is null)
        {
            _notice.Visibility = Visibility.Collapsed;
            return;
        }
        var unit = Model.Epub is not null ? "chapter" : "page";
        var (message, action) = notice switch
        {
            Notice.Resumed r => ($"Resumed on {unit} {r.PageIndex + 1}", "Start over"),
            Notice.SyncedFromOtherDevice s => ($"Moved to {unit} {s.PageIndex + 1}, where you left off on another device", (string?)null),
            Notice.OtherDeviceAvailable o => ($"You reached {unit} {o.Position.PageIndex + 1} on another device", "Go there"),
            _ => ("", null),
        };
        _noticeText.Text = message;
        _noticeAction.Content = action ?? "";
        _noticeAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        _notice.Visibility = Visibility.Visible;
        _noticeTimer.Interval = TimeSpan.FromSeconds(action is null ? 4 : 10);
        _noticeTimer.Start();
    }

    /// <summary>The notice's text and action as shown (self-test).</summary>
    public string? NoticeShown => _notice.Visibility == Visibility.Visible
        ? _noticeText.Text + (_noticeAction.Visibility == Visibility.Visible ? " · " + _noticeAction.Content : "")
        : null;

    private void PerformNotice()
    {
        switch (Model.State.Notice)
        {
            case Notice.Resumed: Model.RestartFromBeginning(); break;
            case Notice.OtherDeviceAvailable o: Model.Controller.Jump(o.Position); break;
            default: Model.Controller.DismissNotice(); break;
        }
    }

    private void UpdateOcrBanner()
    {
        var recognized = Model.IsTextMode && Flow is not null && Model.FlowSource() is { } source && source.IsRecognized(Flow.TopUnit);
        _ocrBanner.Visibility = recognized ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool OcrBannerShown => _ocrBanner.Visibility == Visibility.Visible;

    // Menus

    private void ShowSettingsMenu()
    {
        var s = _graph.Settings;
        var textMode = Model.IsTextMode;
        var menu = new ContextMenu { PlacementTarget = _settingsButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = $"Text Size · {(int)Math.Round(Model.State.TextScale * 100)}%", IsEnabled = false });
        menu.Items.Add(Item("Larger", "Ctrl+=", () => Model.ZoomIn(), textMode && Model.CanZoomIn));
        menu.Items.Add(Item("Smaller", "Ctrl+-", () => Model.ZoomOut(), textMode && Model.CanZoomOut));
        menu.Items.Add(Item("Default Size", "Ctrl+0", () => Model.SetTextScale(1), textMode));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Theme", IsEnabled = false });
        foreach (var theme in new[] { ReaderTheme.Dark, ReaderTheme.Sepia, ReaderTheme.Black })
        {
            var item = Item(theme.Label(), null, () => AppGraph.ApplyTheme(theme), true);
            item.IsChecked = s.Theme == theme;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var paged = Item("Page by Page", null, () => SetPaged(true), true);
        paged.IsChecked = s.PageByPage;
        var continuous = Item("Continuous Scroll", null, () => SetPaged(false), true);
        continuous.IsChecked = !s.PageByPage;
        menu.Items.Add(paged);
        menu.Items.Add(continuous);
        menu.IsOpen = true;
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _moreButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Start from the Beginning", null, () => Model.RestartFromBeginning(), true));
        menu.Items.Add(Item("Find…", "Ctrl+F", OpenFind, Model.IsTextMode));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string? gesture, Action click, bool enabled)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    public void SetPaged(bool paged)
    {
        if (_graph.Settings.PageByPage == paged) return;
        _graph.Settings.PageByPage = paged;
        _graph.Settings.Save();
        Animator.FinishNow();
        Flow?.Sync();
        Refresh();
    }

    /// <summary>One page or two (a spread), for every book on this device.</summary>
    public void SetTwoPages(bool two)
    {
        _graph.Settings.TwoPages = two;
        _graph.Settings.Save();
        Animator.FinishNow();
        Flow?.Sync();
        Pages?.Sync();
        Refresh();
    }

    // Find

    public void OpenFind()
    {
        if (!Model.IsTextMode) return;
        _findBar.Visibility = Visibility.Visible;
        _findStatus.Text = "";
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        Flow?.ClearFind();
        FocusContent();
    }

    private void RunFind(bool forward)
    {
        if (Flow is null) return;
        var found = Flow.Find(_findBox.Text, forward);
        _findStatus.Text = found ? "" : "No matches";
    }

    // Keys and wheel (routed here by the main window)

    public bool HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        if (_findBox.IsKeyboardFocusWithin) return false;
        if (ctrl && alt)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1: SetTwoPages(false); return true;
                case Key.D2 or Key.NumPad2: SetTwoPages(true); return true;
                case Key.Up: PreviousPage(); return true;
                case Key.Down: NextPage(); return true;
            }
            return false;
        }
        if (ctrl)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1: Model.SetMode(ReadingMode.Pages); return true;
                case Key.D2 or Key.NumPad2: Model.SetMode(ReadingMode.Text); return true;
                case Key.OemPlus or Key.Add: Model.ZoomIn(); return true;
                case Key.OemMinus or Key.Subtract: Model.ZoomOut(); return true;
                case Key.D0 or Key.NumPad0: Model.ActualSize(); return true;
                case Key.F: OpenFind(); return true;
            }
            return false;
        }
        if (alt) return false;
        switch (key)
        {
            case Key.Home: Model.GoToPage(0); return true;
            case Key.End: Model.GoToPage(Math.Max(0, Model.PageCount - 1)); return true;
            case Key.Escape when _notice.Visibility == Visibility.Visible: Model.Controller.DismissNotice(); return true;
        }
        if (Flow is not null) return Flow.HandleKey(key, mods);
        if (Pages is not null) return Pages.HandleKey(key, mods);
        return false;
    }

    private double _zoomAccum;

    public bool HandleWheel(int delta, bool horizontal, bool ctrl)
    {
        if (Flow is not null)
        {
            if (ctrl)
            {
                // Ctrl+wheel and touchpad pinch change the text size, a step per notch.
                _zoomAccum += delta;
                if (Math.Abs(_zoomAccum) >= 60)
                {
                    if (_zoomAccum > 0) Model.ZoomIn(); else Model.ZoomOut();
                    _zoomAccum = 0;
                }
                return true;
            }
            return Flow.HandleWheel(delta, horizontal);
        }
        return Pages?.HandleWheel(delta, horizontal, ctrl) ?? false;
    }

    /// <summary>The next source page (two in an original-pages spread).</summary>
    public void NextPage()
    {
        if (Pages is not null) Pages.NextPage();
        else Model.GoToPage(Model.State.PageIndex + 1);
    }

    public void PreviousPage()
    {
        if (Pages is not null) Pages.PreviousPage();
        else Model.GoToPage(Model.State.PageIndex - 1);
    }

    /// <summary>Back to the library: saves the position and releases the book.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _noticeTimer.Stop();
        Animator.FinishNow();
        Model.Controller.Changed -= OnChanged;
        Flow?.Detach();
        Pages?.Detach();
        Model.Close();
    }
}
