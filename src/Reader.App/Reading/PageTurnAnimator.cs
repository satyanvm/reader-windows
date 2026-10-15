using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Reader.Core.App;

namespace Reader.App.Reading;

/// <summary>WPF easing from a CSS cubic-bezier curve.</summary>
internal sealed class CubicBezierEase : EasingFunctionBase
{
    public CubicBezier Curve { get; init; } = CubicBezier.PageTurn;

    public CubicBezierEase() => EasingMode = EasingMode.EaseIn; // EaseInCore is used as is

    protected override double EaseInCore(double normalizedTime) => Curve.Ease(normalizedTime);

    protected override Freezable CreateInstanceCore() => new CubicBezierEase { Curve = Curve };
}

/// <summary>
/// The page-turn animation, drawn over the live view from two pictures (the old page and the new
/// one), so the reader's input is never blocked and the view underneath is already on the new page:
///  - forward: the new page slides in from the right edge over the old one, with a soft shadow on
///    its leading edge, while the old page drifts left by 30% and dims to 18% black;
///  - back: the reverse;
///  - 0.38 s, ease-out cubic-bezier(0.25, 0.8, 0.25, 1); a 0.2 s cross-fade instead when Windows'
///    "Show animations" is off.
/// A new turn during an animation finishes the current one at once, and a timer always removes
/// the overlay, so it can never get stuck.
///
/// It also dissolves the view from a picture of what was there before (<see cref="FadeFrom"/>), so
/// text that arrives, a new text size, a theme, one or two pages, or the other reading mode comes
/// in smoothly instead of popping. A turn ends a running dissolve.
/// </summary>
internal sealed class PageTurnAnimator
{
    public static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(0.38);
    public static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.2);
    public static readonly TimeSpan DissolveDuration = TimeSpan.FromSeconds(0.26);
    private const double Drift = 0.3;
    private const double Dim = 0.18;
    private const double ShadowWidth = 28;

    private readonly Canvas _overlay;
    private readonly DispatcherTimer _fallback;
    private readonly DispatcherTimer _dissolveFallback;
    private readonly List<UIElement> _turn = new();
    private UIElement? _dissolve;
    private int _generation;
    private int _dissolveGeneration;

    public bool IsAnimating { get; private set; }
    public bool IsDissolving => _dissolve is not null;

    /// <summary>For tests and the self-test: animations on or off, regardless of the system setting.</summary>
    public bool? ReduceMotionOverride { get; set; }

    public PageTurnAnimator(Canvas overlay)
    {
        _overlay = overlay;
        _overlay.IsHitTestVisible = false;
        _overlay.ClipToBounds = true;
        _fallback = new DispatcherTimer(DispatcherPriority.Normal, overlay.Dispatcher);
        _fallback.Tick += (_, _) => FinishNow();
        _dissolveFallback = new DispatcherTimer(DispatcherPriority.Normal, overlay.Dispatcher);
        _dissolveFallback.Tick += (_, _) => FinishDissolve();
    }

    private bool ReduceMotion => ReduceMotionOverride ?? !SystemParameters.ClientAreaAnimation;

    public void Play(BitmapSource oldPage, BitmapSource newPage, bool forward)
    {
        FinishNow();
        FinishDissolve();
        var w = _overlay.ActualWidth;
        var h = _overlay.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var generation = ++_generation;
        IsAnimating = true;

        FrameworkElement Picture(BitmapSource source) => new Image
        {
            Source = source,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
        };

        var duration = ReduceMotion ? FadeDuration : TurnDuration;
        _fallback.Interval = duration + TimeSpan.FromMilliseconds(250);
        _fallback.Start();

        if (ReduceMotion)
        {
            var under = Picture(oldPage);
            var over = Picture(newPage);
            over.Opacity = 0;
            AddTurn(under);
            AddTurn(over);
            var fade = new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (_, _) => Done(generation);
            over.BeginAnimation(UIElement.OpacityProperty, fade);
            return;
        }

        // The sheet on top moves across the full width; the one underneath drifts and dims.
        var (bottomSource, topSource) = forward ? (oldPage, newPage) : (newPage, oldPage);
        var bottom = new Grid { Width = w, Height = h };
        bottom.Children.Add(Picture(bottomSource));
        var dim = new Rectangle { Fill = Brushes.Black, Opacity = forward ? 0 : Dim };
        bottom.Children.Add(dim);
        var bottomMove = new TranslateTransform(forward ? 0 : -Drift * w, 0);
        bottom.RenderTransform = bottomMove;

        var top = new Canvas { Width = w, Height = h };
        var shadow = new Rectangle
        {
            Width = ShadowWidth,
            Height = h,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x48, 0, 0, 0), 0),
        };
        Canvas.SetLeft(shadow, -ShadowWidth);
        top.Children.Add(shadow);
        top.Children.Add(Picture(topSource));
        var topMove = new TranslateTransform(forward ? w : 0, 0);
        top.RenderTransform = topMove;

        AddTurn(bottom);
        AddTurn(top);

        DoubleAnimation Anim(double from, double to) => new(from, to, duration)
        {
            EasingFunction = new CubicBezierEase(),
            FillBehavior = FillBehavior.HoldEnd,
        };
        var slide = Anim(forward ? w : 0, forward ? 0 : w);
        slide.Completed += (_, _) => Done(generation);
        topMove.BeginAnimation(TranslateTransform.XProperty, slide);
        bottomMove.BeginAnimation(TranslateTransform.XProperty, Anim(forward ? 0 : -Drift * w, forward ? -Drift * w : 0));
        dim.BeginAnimation(UIElement.OpacityProperty, Anim(forward ? 0 : Dim, forward ? Dim : 0));
    }

    private void AddTurn(UIElement e)
    {
        _turn.Add(e);
        _overlay.Children.Add(e);
    }

    private void Done(int generation)
    {
        if (generation == _generation) FinishNow();
    }

    /// <summary>Ends any running turn now: the overlay goes, the live page underneath is already the new one.</summary>
    public void FinishNow()
    {
        _fallback.Stop();
        foreach (var e in _turn) _overlay.Children.Remove(e);
        _turn.Clear();
        IsAnimating = false;
    }

    /// <summary>
    /// Covers the live view with <paramref name="before"/> (a picture of it a moment ago) and fades
    /// that out, so whatever the view draws next comes in as a soft dissolve. Doesn't stop a running
    /// turn; a turn started meanwhile ends it.
    /// </summary>
    public void FadeFrom(BitmapSource before, TimeSpan? duration = null)
    {
        FinishDissolve();
        var w = _overlay.ActualWidth;
        var h = _overlay.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var generation = ++_dissolveGeneration;
        var length = duration ?? DissolveDuration;
        var picture = new Image { Source = before, Width = w, Height = h, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        _dissolve = picture;
        // Below any turn, so a turn always shows on top.
        _overlay.Children.Insert(0, picture);
        _dissolveFallback.Interval = length + TimeSpan.FromMilliseconds(250);
        _dissolveFallback.Start();
        var fade = new DoubleAnimation(1, 0, length) { EasingFunction = new CubicBezierEase(), FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (generation == _dissolveGeneration) FinishDissolve();
        };
        picture.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Ends a running dissolve now.</summary>
    public void FinishDissolve()
    {
        _dissolveFallback.Stop();
        if (_dissolve is not null) _overlay.Children.Remove(_dissolve);
        _dissolve = null;
    }

    /// <summary>Ends everything (closing the book).</summary>
    public void Stop()
    {
        FinishNow();
        FinishDissolve();
    }
}
