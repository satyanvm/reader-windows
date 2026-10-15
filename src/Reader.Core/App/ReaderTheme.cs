namespace Reader.Core.App;

/// <summary>The three reading themes, used for the page background, text, the progress bar and the window chrome.</summary>
public enum ReaderTheme { Dark, Sepia, Black }

public readonly record struct ThemeColors(uint Background, uint Text, uint Muted, bool IsDark)
{
    public static ThemeColors For(ReaderTheme theme) => theme switch
    {
        ReaderTheme.Dark => new(0x242427, 0xDCDCDE, 0x8A8A8E, true),
        ReaderTheme.Black => new(0x000000, 0xC4C4C4, 0x6E6E6E, true),
        _ => new(0xF5ECD9, 0x4A3A2A, 0x9C8B75, false),
    };
}

public static class ReaderThemes
{
    public static string Label(this ReaderTheme t) => t switch
    {
        ReaderTheme.Dark => "Dark",
        ReaderTheme.Black => "Black",
        _ => "Sepia",
    };
}
