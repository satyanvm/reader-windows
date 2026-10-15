using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Reader.App.Platform;

/// <summary>
/// One Reader per library: a second launch (double-clicking another book in Explorer) hands its
/// files to the running app over a named pipe and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    public bool IsFirst { get; }

    public SingleInstance(string dataDir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant())))[..16];
        _pipeName = $"Reader-{Environment.UserName}-{hash}";
        _mutex = new Mutex(true, $"Local\\Reader-{hash}", out var created);
        IsFirst = created;
    }

    /// <summary>Sends <paramref name="args"/> to the running instance. True if it got them.</summary>
    public bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            using var w = new StreamWriter(client, Encoding.UTF8);
            w.WriteLine(args.Length == 0 ? "\u0001activate" : string.Join('\u0000', args));
            w.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for other launches; <paramref name="onArgs"/> is called on a background thread.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var r = new StreamReader(server, Encoding.UTF8);
                    var line = await r.ReadLineAsync() ?? "";
                    onArgs(line == "\u0001activate" ? [] : line.Split('\u0000', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
