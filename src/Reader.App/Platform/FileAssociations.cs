using Microsoft.Win32;

namespace Reader.App.Platform;

/// <summary>
/// Registers Reader for .pdf and .epub for the current user (HKCU, no administrator rights): it
/// appears in "Open with", becomes the handler for .epub if nothing else is, and double-clicking
/// a file passes it to Reader.exe. Windows keeps the user's choice of default PDF app; Reader
/// can be picked from Open with → Choose another app.
/// </summary>
internal static class FileAssociations
{
    public const string ProgId = "Reader.Document";

    public static void Register(string exePath)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue("", "Book");
                prog.SetValue("FriendlyTypeName", "Book");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exePath}\",0");
                using var command = prog.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exePath}\" \"%1\"");
            }
            foreach (var ext in new[] { ".pdf", ".epub" })
            {
                using var key = classes.CreateSubKey(ext);
                using (var openWith = key.CreateSubKey("OpenWithProgids")) openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                if (ext == ".epub" && string.IsNullOrEmpty(key.GetValue("") as string)) key.SetValue("", ProgId);
            }
            using (var app = classes.CreateSubKey(@"Applications\Reader.exe"))
            {
                app.SetValue("FriendlyAppName", "Reader");
                using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exePath}\" \"%1\"");
                using var types = app.CreateSubKey("SupportedTypes");
                types.SetValue(".pdf", "");
                types.SetValue(".epub", "");
            }
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Associations are a convenience; Open and drag and drop work without them.
        }
    }
}
