#if WINDOWS_APP_TESTS
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Reader.App;
using Reader.App.Reading;
using Reader.Core.Reader;
using Xunit;

namespace Reader.Core.Tests.App;

/// <summary>
/// The real WPF views, on an STA thread with a dispatcher (Windows only): snapshots contain the
/// text, spreads show two different pages, and the page-turn overlay always goes away.
/// </summary>
[Collection("wpf")]
public class WpfTests
{
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "reader-wpf-tests");

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        return true;
    }

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        PumpUntil(() => DateTime.UtcNow >= until, ms + 1000);
    }

    /// <summary>An EPUB long enough for several pages.</summary>
    private static string LongEpub()
    {
        var paragraphs = string.Concat(Enumerable.Range(1, 120).Select(i =>
            $"<p>Paragraph {i}. Be not uneasy, discouraged, or out of humour, because practice falls short of precept in some particulars. If you happen to be beaten, come on again, and be glad if most of your actions are worthy of human nature.</p>"));
        var files = EpubTests.Book.ToArray();
        files[4] = ("OEBPS/text/one.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Book One</h1>{paragraphs}</body></html>");
        return EpubTests.Epub(files);
    }

    private static (MainWindowHost Host, FlowView Flow, ReaderModel Model) OpenFlow(bool twoPages, double width = 900, double height = 700)
    {
        Environment.SetEnvironmentVariable("READER_SUPPORT_DIR", T.TempDir());
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var graph = AppGraph.Create();
        graph.Settings.PageByPage = true;
        graph.Settings.TwoPages = twoPages;
        var doc = graph.Documents.ImportFile(LongEpub());
        var model = new ReaderModel(doc.Id, graph);
        Assert.True(PumpUntil(() => model.State.Document is not null), "the book opened");
        var flow = new FlowView(model, model.FlowSource()!, graph);
        var host = new MainWindowHost(flow, width, height);
        Assert.True(PumpUntil(() => model.State.Phase.IsReading), $"restored: {model.State.Phase}");
        Pump(200);
        return (host, flow, model);
    }

    private static string Save(BitmapSource bmp, string name)
    {
        Directory.CreateDirectory(Out);
        var path = Path.Combine(Out, name + ".png");
        SelfTest.SavePng(bmp, path);
        return path;
    }

    private static PixelStats Stats(string png)
    {
        var decoder = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return PixelStats.Of(decoder.Frames[0]);
    }

    [Fact]
    public void SnapshotsOfFlowingTextIncludeTheText() => Sta(() =>
    {
        var (host, flow, _) = OpenFlow(twoPages: false);
        var first = Save(flow.Snapshot(), "flow-page1");
        var stats = Stats(first);
        Assert.False(stats.IsFlat, $"{first}: {stats}");
        Assert.True(stats.InkFraction > 0.01, $"{first}: text drawn ({stats})");

        Assert.True(flow.Turn(forward: true));
        var second = Save(flow.Snapshot(), "flow-page2");
        Assert.False(Stats(second).IsFlat);
        Assert.NotEqual(File.ReadAllBytes(first), File.ReadAllBytes(second));
        host.Close();
    });

    [Fact]
    public void ASpreadShowsTwoDifferentPagesWithTheFoldBetween() => Sta(() =>
    {
        var (host, flow, _) = OpenFlow(twoPages: true, width: 1300);
        Assert.Equal(2, flow.Columns);
        var bmp = flow.Snapshot();
        Save(bmp, "flow-spread");
        var half = bmp.PixelWidth / 2;
        var left = new CroppedBitmap(bmp, new Int32Rect(0, 0, half - 20, bmp.PixelHeight));
        var right = new CroppedBitmap(bmp, new Int32Rect(half + 20, 0, bmp.PixelWidth - half - 20, bmp.PixelHeight));
        Assert.False(PixelStats.Of(left).IsFlat, "left page has text");
        Assert.False(PixelStats.Of(right).IsFlat, "right page has text");
        host.Close();
    });

    [Fact]
    public void ThePageTurnOverlayAnimatesAndIsAlwaysRemoved() => Sta(() =>
    {
        var (host, flow, _) = OpenFlow(twoPages: false);
        var animator = new PageTurnAnimator(host.Overlay) { ReduceMotionOverride = false };
        flow.Animator = animator;
        Assert.True(flow.Turn(true));
        Assert.True(animator.IsAnimating);
        Assert.Equal(2, host.Overlay.Children.Count);
        Pump(100);
        Save(host.Snapshot(), "turn-mid-animation");
        // A new turn during an animation finishes the current one at once.
        Assert.True(flow.Turn(true));
        Assert.True(animator.IsAnimating);
        Assert.True(PumpUntil(() => !animator.IsAnimating, 2000), "the overlay is removed");
        Assert.Empty(host.Overlay.Children);

        animator.ReduceMotionOverride = true; // "Show animations" off: a cross-fade
        Assert.True(flow.Turn(false));
        Assert.True(animator.IsAnimating);
        Assert.True(PumpUntil(() => !animator.IsAnimating, 1000));
        host.Close();
    });

    /// <summary>A window holding the view and the overlay canvas, the way ReaderView arranges them.</summary>
    private sealed class MainWindowHost
    {
        private readonly Window _window;
        public Canvas Overlay { get; } = new();

        public MainWindowHost(FrameworkElement view, double width, double height)
        {
            var grid = new Grid();
            grid.Children.Add(view);
            grid.Children.Add(Overlay);
            _window = new Window
            {
                Content = grid,
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = 0,
                Top = 0,
            };
            _window.Show();
        }

        public BitmapSource Snapshot()
        {
            var root = (FrameworkElement)_window.Content;
            var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            return rtb;
        }

        public void Close() => _window.Close();
    }
}
#endif
