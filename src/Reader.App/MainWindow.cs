using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Reader.App.Library;
using Reader.App.Platform;
using Reader.App.Reading;
using Reader.Core.App;

namespace Reader.App;

/// <summary>
/// The single main window. It always opens on the Library; a book opens inside the same window,
/// on top of the library, and the back arrow (Ctrl+[ or Alt+Left) closes it, saving its
/// position, and returns to the library.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly AppGraph _graph;
    private readonly Grid _root = new();
    public LibraryView Library { get; }
    public ReaderView? Reader { get; private set; }
    private SettingsWindow? _settings;

    public MainWindow(AppGraph graph)
    {
        _graph = graph;
        Title = "Reader";
        MinWidth = 420;
        MinHeight = 360;
        Width = 1040;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Reader.ico")); } catch { }
        SetResourceReference(BackgroundProperty, "BgBrush");
        UseLayoutRounding = true;

        Library = new LibraryView();
        Library.OpenRequested += OpenBook;
        Library.OpenFileRequested += () => _ = ShowOpenDialog();
        Library.AccountRequested += ShowSettings;
        Library.Attach();
        _root.Children.Add(Library);
        Content = _root;

        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Library.ShowDropHint(false);
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) =>
        {
            // Back in the app: an open book may have been read further on another device.
            Reader?.Model.Controller.RefreshFromServer();
            _graph.SyncLibrary();
        };
        Deactivated += (_, _) => _graph.FlushAll();
        Closing += (_, _) =>
        {
            _graph.FlushAll();
            SavePlacement();
        };
        Closed += (_, _) => Reader?.Close();
        RestorePlacement();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ThemeManager.ApplyChrome(this);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    /// <summary>Horizontal wheel and touchpad swipes (WM_MOUSEHWHEEL), which WPF doesn't route.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEHWHEEL && Reader is not null)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            handled = Reader.HandleWheel(delta, horizontal: true, ctrl: false);
            return handled ? (IntPtr)1 : IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    // Books

    /// <summary>Opens a book on top of the library (or brings the open one forward).</summary>
    public void OpenBook(string documentId)
    {
        if (Reader?.Model.DocumentId == documentId) return;
        if (Reader is not null) CloseBook();
        var model = new ReaderModel(documentId, _graph);
        var view = new ReaderView(model, _graph);
        view.BackRequested += CloseBook;
        Reader = view;
        _root.Children.Add(view);
        Library.Visibility = Visibility.Collapsed;
        Title = "Reader";
        model.Controller.Changed += UpdateTitle;
        Dispatcher.BeginInvoke(view.FocusContent, DispatcherPriority.Input);
    }

    private void UpdateTitle()
    {
        if (Reader is { } r && !string.IsNullOrEmpty(r.Model.State.Title)) Title = $"{r.Model.State.Title} — Reader";
    }

    /// <summary>Back to the library: closes the book and saves its position.</summary>
    public void CloseBook()
    {
        if (Reader is not { } r) return;
        r.Model.Controller.Changed -= UpdateTitle;
        r.Close();
        _root.Children.Remove(r);
        Reader = null;
        Title = "Reader";
        Library.Visibility = Visibility.Visible;
        Library.Reload();
        _graph.SyncLibrary();
        Dispatcher.BeginInvoke(Library.FocusList, DispatcherPriority.Input);
    }

    /// <summary>Files from the command line, Explorer or a second launch: import and open the first.</summary>
    public async Task HandleArgsAsync(string[] args)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();
        if (files.Count > 0) await ImportAndOpen(files);
    }

    public async Task ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose PDF or EPUB books to add to your library",
            Filter = "Books (*.pdf;*.epub)|*.pdf;*.epub|PDF (*.pdf)|*.pdf|EPUB (*.epub)|*.epub",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true) await ImportAndOpen(dialog.FileNames);
    }

    public async Task ImportAndOpen(IReadOnlyList<string> files)
    {
        var (added, failures) = await _graph.ImportAsync(files);
        Library.Reload();
        if (failures.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", failures),
                failures.Count == 1 ? "Couldn't open this file" : "Couldn't open these files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (added.Count > 0) OpenBook(added[0].Id);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (Reader is null) Library.ShowDropHint(ok);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        Library.ShowDropHint(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await ImportAndOpen(files);
    }

    // Settings

    public void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_graph) { Owner = this };
        _settings.SignedIn += () =>
        {
            // Straight to the library, where the account's books start arriving.
            _settings?.Close();
            CloseBook();
            _graph.SyncLibrary();
        };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    // Keys and wheel

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && key == Key.O)
        {
            _ = ShowOpenDialog();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && key == Key.OemComma)
        {
            ShowSettings();
            e.Handled = true;
            return;
        }
        if (Reader is null) return;
        if ((mods == ModifierKeys.Control && key == Key.OemOpenBrackets) || (mods == ModifierKeys.Alt && key == Key.Left) ||
            (key == Key.BrowserBack))
        {
            CloseBook();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        if (Reader.HandleKey(e)) e.Handled = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Reader is null) return;
        if (e.OriginalSource is DependencyObject d && IsInside<System.Windows.Controls.Primitives.ScrollBar>(d)) return;
        if (Reader.HandleWheel(e.Delta, horizontal: false, ctrl: Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) e.Handled = true;
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
        {
            if (p is T) return true;
        }
        return false;
    }

    // Window placement

    private void RestorePlacement()
    {
        if (_graph.Settings.Window is not { } w) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(w.Left, w.Top, Math.Max(MinWidth, w.Width), Math.Max(MinHeight, w.Height));
        // Only if it's still (mostly) on a screen: monitors change.
        var visible = Rect.Intersect(screen, rect);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 150) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0) return;
        _graph.Settings.Window = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        _graph.Settings.Save();
    }
}
