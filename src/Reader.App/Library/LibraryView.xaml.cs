using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reader.Core.Data;

namespace Reader.App.Library;

/// <summary>A row in the library list.</summary>
public sealed record LibraryItem(string Id, string Title, string Progress, string Format)
{
    /// <summary>An icon for PDF or EPUB.</summary>
    public string Glyph => Format == "EPUB" ? "" : "";
}

/// <summary>
/// The home page: every book, most recently opened first, with its progress. Enter or a
/// double-click opens a book; the context menu opens or removes it.
/// </summary>
public partial class LibraryView : UserControl
{
    /// <summary>Open this book.</summary>
    public event Action<string>? OpenRequested;
    public event Action? OpenFileRequested;
    public event Action? AccountRequested;

    private AppGraph Graph => AppGraph.Shared;

    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            Graph.SyncLibrary();
        };
    }

    public void Attach()
    {
        Graph.LibraryChanged += Reload;
        Graph.LibrarySyncChanged += UpdateAccount;
        Graph.Account.Changed += UpdateAccount;
        UpdateAccount();
    }

    public IReadOnlyList<LibraryItem> Items => (IReadOnlyList<LibraryItem>?)List.ItemsSource ?? Array.Empty<LibraryItem>();

    public void Reload()
    {
        List<LibraryRow> rows;
        try { rows = Graph.Documents.Library(); }
        catch { rows = new(); }
        var selected = (List.SelectedItem as LibraryItem)?.Id;
        var items = rows.Select(r => new LibraryItem(r.Id, r.Title, r.ProgressLabel, r.Format)).ToList();
        List.ItemsSource = items;
        if (selected is not null) List.SelectedItem = items.FirstOrDefault(i => i.Id == selected);
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateAccount()
    {
        var account = Graph.Account;
        AccountText.Text = account.IsSignedIn ? account.Email ?? "Signed in" : "Sign In to Sync";
        AccountGlyph.Text = account.IsSignedIn ? "" : "";
        AccountButton.ToolTip = account.IsSignedIn
            ? "Your books and your place in them sync to your other devices"
            : "Sign in to keep your books and your place on all your devices";
        var sync = Graph.LibrarySync;
        if (!account.IsSignedIn)
        {
            StatusStrip.Visibility = Visibility.Collapsed;
        }
        else if (sync.IsSyncing)
        {
            StatusStrip.Visibility = Visibility.Visible;
            StatusText.Text = "Syncing your library…";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            RetryButton.Visibility = Visibility.Collapsed;
        }
        else if (sync.LastError is { } error)
        {
            StatusStrip.Visibility = Visibility.Visible;
            StatusText.Text = error;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            RetryButton.Visibility = Visibility.Visible;
        }
        else
        {
            StatusStrip.Visibility = Visibility.Collapsed;
        }
        RemoveItem.Header = account.IsSignedIn ? "Remove from Library on All Devices" : "Remove from Library";
    }

    /// <summary>What the status strip shows (self-test).</summary>
    public string? StatusShown => StatusStrip.Visibility == Visibility.Visible ? StatusText.Text : null;

    public void ShowDropHint(bool show) => DropHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    public void FocusList()
    {
        if (List.Items.Count == 0) return;
        if (List.SelectedIndex < 0) List.SelectedIndex = 0;
        (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
    }

    private void OnAccount(object sender, RoutedEventArgs e) => AccountRequested?.Invoke();
    private void OnOpen(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();
    private void OnRetry(object sender, RoutedEventArgs e) => Graph.SyncLibrary();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: LibraryItem item })
        {
            OpenRequested?.Invoke(item.Id);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is LibraryItem item)
        {
            OpenRequested?.Invoke(item.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && List.SelectedItem is LibraryItem toRemove)
        {
            Remove(toRemove);
            e.Handled = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem container)
        {
            container.IsSelected = true;
        }
        if (List.SelectedItem is null) e.Handled = true;
        UpdateAccount();
    }

    private void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is LibraryItem item) OpenRequested?.Invoke(item.Id);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is LibraryItem item) Remove(item);
    }

    private void Remove(LibraryItem item)
    {
        Graph.Remove(item.Id);
        Reload();
    }
}
