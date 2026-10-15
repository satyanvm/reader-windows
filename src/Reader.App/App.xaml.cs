using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Reader.App.Platform;
using Reader.Core.App;

namespace Reader.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppGraph? _graph;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var dataDir = AppPaths.DataDir();
        _single = new SingleInstance(dataDir);
        if (!_single.IsFirst && !SelfTest.Requested && _single.Forward(e.Args))
        {
            // The running Reader opens the files.
            Shutdown();
            return;
        }

        _graph = AppGraph.Create();
        ThemeManager.Apply(_graph.Settings.Theme);
        var main = new MainWindow(_graph);
        MainWindow = main;
        main.Show();

        _single.Listen(args => Dispatcher.BeginInvoke(async () => await main.HandleArgsAsync(args)));
        RegisterFileTypes(_graph);

        // Saves are debounced and written within half a second anyway; these are prompt extras.
        SystemEvents.PowerModeChanged += (_, a) =>
        {
            if (a.Mode == PowerModes.Suspend) Dispatcher.Invoke(_graph.FlushAll);
            if (a.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _graph.SyncScheduler.RequestSync(TimeSpan.Zero));
        };
        SystemEvents.SessionEnding += (_, _) => Dispatcher.Invoke(_graph.FlushAll);

        Dispatcher.BeginInvoke(async () => await main.HandleArgsAsync(e.Args), DispatcherPriority.Loaded);
        SelfTest.RunIfRequested(main);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _graph?.FlushAll();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _graph?.FlushAll();
        _graph?.SyncScheduler.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// .pdf and .epub open in Reader from Explorer: registered for this user at first run, and
    /// again if Reader.exe moved. Scratch libraries (READER_SUPPORT_DIR) and self-tests don't touch
    /// the registry.
    /// </summary>
    private static void RegisterFileTypes(AppGraph graph)
    {
        if (SelfTest.Requested || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("READER_SUPPORT_DIR"))) return;
        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith("Reader.exe", StringComparison.OrdinalIgnoreCase)) return;
        var marker = Path.Combine(graph.DataDir, "associations");
        var registered = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        if (graph.Settings.FileAssociationsRegistered && string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return;
        Task.Run(() =>
        {
            FileAssociations.Register(exe);
            try { File.WriteAllText(marker, exe); } catch { }
        });
        graph.Settings.FileAssociationsRegistered = true;
        graph.Settings.Save();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = AppPaths.Logs(AppPaths.DataDir());
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
        }
        catch { }
        SelfTest.Trace("unhandled: " + e.Exception);
        // Keep reading: saving and sync don't depend on whatever failed.
        e.Handled = true;
    }
}
