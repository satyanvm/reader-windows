using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Reader.Core.Sync;

namespace Reader.App.Platform;

/// <summary>
/// The signed-in session, encrypted with DPAPI for the current Windows user
/// (<see cref="DataProtectionScope.CurrentUser"/>), in <c>session.bin</c> in the data folder.
/// Another user, or the file copied to another PC, can't read the token.
/// </summary>
internal sealed class DpapiSessionStore(string path) : ISessionStore
{
    private static readonly byte[] Entropy = "Reader.SyncSession.v1"u8.ToArray();

    private sealed record Saved(string Token, string Email);

    public AccountSessionInfo? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var saved = JsonSerializer.Deserialize<Saved>(plain);
            return saved is null || string.IsNullOrEmpty(saved.Token) ? null : new AccountSessionInfo(saved.Token, saved.Email);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AccountSessionInfo? session)
    {
        if (session is null)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Saved(session.Token, session.Email));
        var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, sealedBytes);
        File.Move(tmp, path, overwrite: true);
    }
}
