using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reader.App.Platform;
using Reader.Core.Sync;

namespace Reader.App;

/// <summary>
/// Settings (Ctrl+,): the Reader account that syncs your books and your place in them across
/// devices. Signed out: sign in or create an account (email and password; Google when this
/// build has a client id and the server allows it). With email codes on, a 6-digit code confirms
/// a new account or sets a new password. Signed in: status, waiting uploads, Sync Now, Sign Out.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppGraph _graph;
    private readonly SyncAccount _account;
    private readonly StackPanel _body = new() { Margin = new Thickness(28, 22, 28, 24) };
    private AccountStage? _shownStage;
    private bool _createMode;
    private bool _working;
    private string? _error;
    private bool _resent;

    // Kept across re-renders of the same stage.
    private readonly TextBox _email = Field<TextBox>();
    private readonly PasswordBox _password = Field<PasswordBox>();
    private readonly TextBox _code = Field<TextBox>();
    private readonly PasswordBox _newPassword = Field<PasswordBox>();

    /// <summary>Signed in from this window: the app closes it and shows the library.</summary>
    public event Action? SignedIn;

    public SettingsWindow(AppGraph graph)
    {
        _graph = graph;
        _account = graph.Account;
        Title = "Settings";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgBrush");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body, MaxHeight = 760 };
        _code.FontSize = 18;
        _code.MaxLength = 8;
        SourceInitialized += (_, _) => ThemeManager.ApplyChrome(this);
        _account.Changed += Render;
        Closed += (_, _) => _account.Changed -= Render;
        Loaded += async (_, _) =>
        {
            _account.RefreshPendingCount();
            await _account.LoadProvidersAsync();
            Render();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Render();
    }

    private static T Field<T>() where T : Control, new()
    {
        var c = new T { Style = (Style)Application.Current.Resources["FieldStyle"] };
        return c;
    }

    private void Render()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }
        if (_shownStage is not null && _shownStage.GetType() != _account.Stage.GetType())
        {
            _error = null;
            _resent = false;
        }
        _shownStage = _account.Stage;
        _body.Children.Clear();
        switch (_account.Stage)
        {
            case AccountStage.SignedIn s: RenderSignedIn(s.Email); break;
            case AccountStage.AwaitingCode c: RenderCode(c.Email, c.Purpose); break;
            default: RenderSignIn(); break;
        }
    }

    // Pieces

    private TextBlock Heading(string text) => Text(text, 18, FontWeights.SemiBold, "FgBrush", new Thickness(0, 0, 0, 4));

    private static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = "FgBrush", Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static Label Caption(string text, UIElement target)
    {
        var l = new Label { Content = text, Target = target, Padding = new Thickness(0, 4, 0, 2), FontSize = 12.5 };
        l.SetResourceReference(ForegroundProperty, "MutedBrush");
        return l;
    }

    private Button Primary(string text, bool enabled, Func<Task> action)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["PrimaryButton"], IsEnabled = enabled && !_working, IsDefault = true, MinWidth = 120 };
        b.Click += async (_, _) => await Run(action);
        return b;
    }

    private Button Secondary(string text, Action action)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["SecondaryButton"], Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private Button Link(string text, bool enabled, Func<Task> action)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["LinkButton"], IsEnabled = enabled && !_working, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Click += async (_, _) => await Run(action);
        return b;
    }

    private void Error()
    {
        if (_error is not null) _body.Children.Add(Text(_error, 12.5, null, "ErrorBrush"));
    }

    /// <summary>Runs an account action, showing progress and the server's message if it fails.</summary>
    private async Task Run(Func<Task> action)
    {
        if (_working) return;
        _working = true;
        _error = null;
        Render();
        var wasSignedIn = _account.IsSignedIn;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // The person closed the browser tab.
        }
        catch (Exception e)
        {
            _error = e.Message;
        }
        _working = false;
        Render();
        if (!wasSignedIn && _account.IsSignedIn) SignedIn?.Invoke();
    }

    // Stages

    private void RenderSignIn()
    {
        _body.Children.Add(Heading("Account"));
        _body.Children.Add(Text("Keep your books and your place in them in sync on all your devices.", 13, null, "MutedBrush", new Thickness(0, 0, 0, 14)));
        if (_account.LastError == SyncAccount.SignedOutMessage)
        {
            _body.Children.Add(Text(_account.LastError, 12.5, null, "ErrorBrush"));
        }

        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        var signIn = new RadioButton { Content = "Sign In", GroupName = "auth", IsChecked = !_createMode, Style = (Style)Application.Current.Resources["SegmentStyle"] };
        var create = new RadioButton { Content = "Create Account", GroupName = "auth", IsChecked = _createMode, Style = (Style)Application.Current.Resources["SegmentStyle"] };
        signIn.Click += (_, _) => { _createMode = false; _error = null; Render(); };
        create.Click += (_, _) => { _createMode = true; _error = null; Render(); };
        segments.Children.Add(signIn);
        segments.Children.Add(create);
        var segmentBox = new Border { Child = segments, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        segmentBox.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        _body.Children.Add(segmentBox);

        _body.Children.Add(Caption("_Email", _email));
        _body.Children.Add(Detach(_email));
        _body.Children.Add(Caption(_createMode ? "_Password (at least 8 characters)" : "_Password", _password));
        _body.Children.Add(Detach(_password));
        Error();
        if (_createMode && _account.Providers.EmailCodes)
        {
            _body.Children.Add(Text("We'll email you a code to confirm the address is yours.", 12.5, null, "MutedBrush"));
        }

        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var submit = Primary(_createMode ? "Create Account" : "Sign In", true, async () =>
        {
            if (string.IsNullOrWhiteSpace(_email.Text) || _password.Password.Length == 0)
            {
                throw new AccountException("form", "Enter your email and password.");
            }
            if (_createMode) await _account.CreateAccountAsync(_email.Text, _password.Password);
            else await _account.SignInAsync(_email.Text, _password.Password);
        });
        DockPanel.SetDock(submit, Dock.Right);
        row.Children.Add(submit);
        // Resetting a password needs an emailed code.
        if (!_createMode && _account.Providers.EmailCodes)
        {
            var forgot = Link("Forgot password?", true, async () =>
            {
                if (string.IsNullOrWhiteSpace(_email.Text)) throw new AccountException("form", "Enter your email first.");
                await _account.ForgotPasswordAsync(_email.Text);
            });
            forgot.ToolTip = "We'll email you a code to set a new password";
            row.Children.Add(forgot);
        }
        if (_working) row.Children.Add(Text("Working…", 12.5, null, "MutedBrush", new Thickness(0, 6, 0, 0)));
        _body.Children.Add(row);

        if (GoogleSignIn.ClientId is not null && _account.Providers.Google)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 14) };
            sep.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            _body.Children.Add(sep);
            var google = new Button { Style = (Style)Application.Current.Resources["SecondaryButton"], IsEnabled = !_working, HorizontalAlignment = HorizontalAlignment.Stretch };
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = "G", FontWeight = FontWeights.Bold, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4)), Margin = new Thickness(0, 0, 10, 0) });
            content.Children.Add(new TextBlock { Text = "Continue with Google", FontSize = 13 });
            google.Content = content;
            google.Click += async (_, _) => await Run(async () =>
            {
                var token = await GoogleSignIn.IdTokenAsync(CancellationToken.None);
                await _account.SignInWithProviderAsync("google", token);
            });
            _body.Children.Add(google);
            _body.Children.Add(Text("A Google account with the same email signs in to the same Reader account.", 12, null, "MutedBrush", new Thickness(0, 8, 0, 0)));
        }
        Dispatcher.BeginInvoke(() => { if (_email.Text.Length == 0) _email.Focus(); else _password.Focus(); });
    }

    private void RenderCode(string email, CodePurpose purpose)
    {
        _body.Children.Add(Heading(purpose == CodePurpose.ConfirmEmail ? "Confirm your email" : "Set a new password"));
        _body.Children.Add(Text($"We sent a 6-digit code to {email}. It expires in 10 minutes.", 13, null, "MutedBrush", new Thickness(0, 0, 0, 12)));
        _body.Children.Add(Caption("_Code", _code));
        _body.Children.Add(Detach(_code));
        if (purpose == CodePurpose.ResetPassword)
        {
            _body.Children.Add(Caption("_New password (at least 8 characters)", _newPassword));
            _body.Children.Add(Detach(_newPassword));
        }
        if (_resent) _body.Children.Add(Text("A new code is on its way.", 12.5, null, "MutedBrush"));
        Error();
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var submit = Primary(purpose == CodePurpose.ConfirmEmail ? "Confirm" : "Set Password", true, async () =>
        {
            if (_code.Text.Trim().Length < 6) throw new AccountException("form", "Enter the 6-digit code.");
            if (purpose == CodePurpose.ConfirmEmail) await _account.ConfirmAsync(_code.Text);
            else await _account.ResetPasswordAsync(_code.Text, _newPassword.Password);
        });
        DockPanel.SetDock(submit, Dock.Right);
        row.Children.Add(submit);
        row.Children.Add(Link("Use a different email", true, () =>
        {
            _account.CancelCode();
            return Task.CompletedTask;
        }));
        row.Children.Add(Link("Send a new code", true, async () =>
        {
            await _account.ResendCodeAsync();
            _resent = true;
        }));
        _body.Children.Add(row);
        Dispatcher.BeginInvoke(() => _code.Focus());
    }

    private void RenderSignedIn(string email)
    {
        _body.Children.Add(Heading("Account"));
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void RowOf(int i, string label, string value, string brush = "FgBrush")
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, 13, null, "MutedBrush", new Thickness(0, 0, 16, 8));
            var v = Text(value, 13, null, brush);
            Grid.SetRow(l, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }
        RowOf(0, "Signed in as", email);
        RowOf(1, "Status", _account.StatusText, _account.LastError is null || _account.IsSyncing ? "FgBrush" : "ErrorBrush");
        var books = _account.PendingCount;
        RowOf(2, "Waiting to upload", books == 1 ? "1 book" : $"{books} books");
        _body.Children.Add(grid);
        _body.Children.Add(Text("Your books and your place in each are saved on this PC first, then synced. Sign in with the same account on your other devices to pick up where you left off.",
            12.5, null, "MutedBrush", new Thickness(0, 0, 0, 16)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var sync = Secondary("Sync Now", () => _account.SyncNow());
        sync.IsEnabled = !_account.IsSyncing;
        row.Children.Add(sync);
        row.Children.Add(Secondary("Sign Out", () => _account.SignOut()));
        _body.Children.Add(row);
    }

    /// <summary>Kept fields move between renders; take them out of their old parent first.</summary>
    private static T Detach<T>(T element) where T : FrameworkElement
    {
        if (element.Parent is Panel p) p.Children.Remove(element);
        return element;
    }
}
