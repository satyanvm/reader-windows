namespace Reader.Core.App;

/// <summary>
/// A CSS-style <c>cubic-bezier(x1, y1, x2, y2)</c> timing curve from (0,0) to (1,1): progress
/// for a given fraction of the duration. The page turn uses (0.25, 0.8, 0.25, 1), a soft ease-out.
/// </summary>
public readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    public static readonly CubicBezier PageTurn = new(0.25, 0.8, 0.25, 1);

    private static double Curve(double a, double b, double s) =>
        3 * (1 - s) * (1 - s) * s * a + 3 * (1 - s) * s * s * b + s * s * s;

    private static double Slope(double a, double b, double s) =>
        3 * (1 - s) * (1 - s) * a + 6 * (1 - s) * s * (b - a) + 3 * s * s * (1 - b);

    /// <summary>Progress (0–1) at time <paramref name="t"/> (0–1).</summary>
    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(s) = t: Newton's method, with bisection as the fallback.
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Curve(X1, X2, s) - t;
            if (Math.Abs(x) < 1e-7) return Curve(Y1, Y2, s);
            var d = Slope(X1, X2, s);
            if (Math.Abs(d) < 1e-6) break;
            s -= x / d;
        }
        double lo = 0, hi = 1;
        s = t;
        for (var i = 0; i < 50; i++)
        {
            var x = Curve(X1, X2, s);
            if (Math.Abs(x - t) < 1e-7) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Curve(Y1, Y2, s);
    }
}
