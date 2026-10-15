namespace Reader.Core.App;

/// <summary>
/// Where the library lives: <c>%LOCALAPPDATA%\Reader</c>, or <c>READER_SUPPORT_DIR</c> (tests and
/// self-test runs use a scratch library). Everything is in this folder: the database, the books,
/// the text cache, the session, settings and logs.
/// </summary>
public static class AppPaths
{
    public static string DataDir(string? overrideDir = null)
    {
        var custom = overrideDir ?? Environment.GetEnvironmentVariable("READER_SUPPORT_DIR");
        var dir = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reader");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Database(string dataDir) => Path.Combine(dataDir, "reader.sqlite");
    public static string Documents(string dataDir) => Path.Combine(dataDir, "documents");
    public static string LibrarySyncState(string dataDir) => Path.Combine(dataDir, "library-sync.json");
    public static string Session(string dataDir) => Path.Combine(dataDir, "session.bin");
    public static string Settings(string dataDir) => Path.Combine(dataDir, "settings.json");
    public static string Logs(string dataDir) => Path.Combine(dataDir, "logs");

    /// <summary>
    /// This library's device id: a random UUID kept next to its database, so a second library
    /// (another Windows user, or a test library) is a separate device.
    /// </summary>
    public static string DeviceId(string dataDir)
    {
        var file = Path.Combine(dataDir, "device-id");
        try
        {
            if (File.Exists(file))
            {
                var id = File.ReadAllText(file).Trim();
                if (id.Length > 0) return id;
            }
        }
        catch { }
        var fresh = Guid.NewGuid().ToString().ToLowerInvariant();
        try { File.WriteAllText(file, fresh); } catch { }
        return fresh;
    }
}
