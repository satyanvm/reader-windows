using System.Text.Json;

namespace Reader.Core.Telemetry;

/// <summary>Same names as Android and the Mac, so the analytics backend can treat all apps alike.</summary>
public enum RestoreEvent
{
    /// <summary>Opened with no saved position: started at the beginning, as intended.</summary>
    FirstOpen,
    RestoredLocal,
    RestoredRemote,
    /// <summary>A newer position from another device arrived after opening and was applied.</summary>
    RemoteAppliedLate,
    /// <summary>The reader landed somewhere other than the restore target.</summary>
    RestoreMismatch,
    UnexpectedReset,
    UserRestart,
}

public static class RestoreEvents
{
    public static string Name(this RestoreEvent e) => e switch
    {
        RestoreEvent.FirstOpen => "FIRST_OPEN",
        RestoreEvent.RestoredLocal => "RESTORED_LOCAL",
        RestoreEvent.RestoredRemote => "RESTORED_REMOTE",
        RestoreEvent.RemoteAppliedLate => "REMOTE_APPLIED_LATE",
        RestoreEvent.RestoreMismatch => "RESTORE_MISMATCH",
        RestoreEvent.UnexpectedReset => "UNEXPECTED_RESET",
        RestoreEvent.UserRestart => "USER_RESTART",
        _ => e.ToString(),
    };
}

/// <summary>
/// Restore outcomes, so unexpected resets are measurable. An <c>UNEXPECTED_RESET</c> means saved
/// progress existed but the reader ended up at the first page anyway. Treat any occurrence as a
/// high-priority defect.
/// </summary>
public interface IRestoreTelemetry
{
    void Record(RestoreEvent e, string detail = "");
}

/// <summary>
/// Logs every event to <c>restore-telemetry.log</c> in the data folder (one line each, with the
/// level: <c>UNEXPECTED_RESET</c> is <c>FAULT</c>, <c>RESTORE_MISMATCH</c> is <c>WARN</c>) and
/// keeps lifetime counters in <c>restore-telemetry.json</c>.
/// </summary>
public sealed class LocalRestoreTelemetry : IRestoreTelemetry
{
    private readonly string _logPath;
    private readonly string _countsPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _counts;

    public LocalRestoreTelemetry(string directory)
    {
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "restore-telemetry.log");
        _countsPath = Path.Combine(directory, "restore-telemetry.json");
        try
        {
            _counts = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_countsPath)) ?? new();
        }
        catch
        {
            _counts = new();
        }
    }

    public void Record(RestoreEvent e, string detail = "")
    {
        var level = e switch
        {
            RestoreEvent.UnexpectedReset => "FAULT",
            RestoreEvent.RestoreMismatch => "WARN",
            _ => "INFO",
        };
        var message = $"{e.Name()} {detail}".Trim();
        lock (_lock)
        {
            _counts[e.Name()] = _counts.GetValueOrDefault(e.Name()) + 1;
            try
            {
                File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {level} ReaderRestore {message}{Environment.NewLine}");
                File.WriteAllText(_countsPath, JsonSerializer.Serialize(_counts));
            }
            catch
            {
                // Telemetry must never break reading.
            }
        }
        System.Diagnostics.Trace.WriteLine($"ReaderRestore {level} {message}");
    }

    public Dictionary<string, int> Counts()
    {
        lock (_lock)
        {
            return Enum.GetValues<RestoreEvent>().ToDictionary(e => e.Name(), e => _counts.GetValueOrDefault(e.Name()));
        }
    }
}
