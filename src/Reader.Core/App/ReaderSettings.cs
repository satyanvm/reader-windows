using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reader.Core.App;

/// <summary>Where the main window was, so it comes back there.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Device-wide reading preferences (as on Android and the Mac: they apply to every book on this
/// device and aren't synced), kept in <c>settings.json</c>.
/// </summary>
public sealed class ReaderSettings
{
    public ReaderTheme Theme { get; set; } = ReaderTheme.Sepia;
    public float TextScale { get; set; } = 1;
    /// <summary>Flowing text one page at a time (the default) rather than continuous scroll.</summary>
    public bool PageByPage { get; set; } = true;
    /// <summary>Two pages side by side, in both modes.</summary>
    public bool TwoPages { get; set; }
    public WindowPlacement? Window { get; set; }
    public bool FileAssociationsRegistered { get; set; }

    public static readonly float[] TextSteps = [0.7f, 0.8f, 0.9f, 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f];
    public static readonly float[] ZoomSteps = [1f, 1.25f, 1.5f, 2f, 3f, 4f];

    private string? _path;

    public static ReaderSettings Load(string path)
    {
        ReaderSettings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch
        {
            s = new();
        }
        if (!(s.TextScale > 0)) s.TextScale = 1;
        s._path = path;
        return s;
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Preferences are a convenience; reading goes on without them.
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static float NextStep(float[] steps, float current) => steps.FirstOrDefault(s => s > current + 0.001f, current);
    public static float PreviousStep(float[] steps, float current) => steps.LastOrDefault(s => s < current - 0.001f, current);
}
