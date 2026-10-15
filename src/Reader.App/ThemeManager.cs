using System.Windows;
using System.Windows.Media;
using Reader.App.Platform;
using Reader.Core.App;

namespace Reader.App;

/// <summary>Applies a reading theme to the whole app: page background, text, chrome and the title bar.</summary>
internal static class ThemeManager
{
    public static ReaderTheme Current { get; private set; } = ReaderTheme.Sepia;
    public static event Action? Changed;

    public static Color ToColor(uint rgb, byte alpha = 255) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static ThemeColors Colors => ThemeColors.For(Current);

    public static void Apply(ReaderTheme theme)
    {
        Current = theme;
        var c = ThemeColors.For(theme);
        var res = Application.Current.Resources;
        void Set(string key, Color color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            res[key] = b;
        }
        var bg = ToColor(c.Background);
        var fg = ToColor(c.Text);
        Set("BgBrush", bg);
        Set("FgBrush", fg);
        Set("MutedBrush", ToColor(c.Muted));
        Set("ChromeBrush", c.IsDark ? Mix(bg, Colors_.White, theme == ReaderTheme.Black ? 0.06 : 0.04) : Mix(bg, Colors_.Black, 0.035));
        Set("LineBrush", Color.FromArgb(0x26, fg.R, fg.G, fg.B));
        Set("HoverBrush", Color.FromArgb(0x16, fg.R, fg.G, fg.B));
        Set("PressedBrush", Color.FromArgb(0x28, fg.R, fg.G, fg.B));
        Set("SelectedBrush", Color.FromArgb(0x2E, fg.R, fg.G, fg.B));
        Set("PillBrush", c.IsDark ? Color.FromArgb(0xF2, 0x38, 0x38, 0x3C) : theme == ReaderTheme.White ? Color.FromArgb(0xF5, 0xF4, 0xF4, 0xF6) : Color.FromArgb(0xF5, 0xFF, 0xFB, 0xF3));
        Set("AccentBrush", c.IsDark ? Color.FromRgb(0xD9, 0xA4, 0x65) : theme == ReaderTheme.White ? Color.FromRgb(0x2F, 0x6B, 0xB0) : Color.FromRgb(0x8A, 0x5A, 0x2B));
        Set("ErrorBrush", c.IsDark ? Color.FromRgb(0xFF, 0x8A, 0x80) : Color.FromRgb(0xB3, 0x26, 0x1E));
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
        Changed?.Invoke();
    }

    public static void ApplyChrome(Window w)
    {
        var c = ThemeColors.For(Current);
        Native.SetDarkTitleBar(w, c.IsDark);
        Native.SetCaptionColor(w, c.Background);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static class Colors_
    {
        public static readonly Color White = Color.FromRgb(255, 255, 255);
        public static readonly Color Black = Color.FromRgb(0, 0, 0);
    }
}
