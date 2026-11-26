using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Services;

/// <summary>
/// "Make this your default PDF app?". Windows does not let a program set itself as the default; it can
/// only check, register itself as a choice, and open the Windows page where the user confirms with one
/// click. Works for both the installed EXE and the Microsoft Store (packaged) copy.
/// </summary>
public static class DefaultPdfApp
{
    private const string ProgId = "PBJStudio.pdf";
    private const string RegisteredName = "PBJStudio";

    /// <summary>The app user model id of the Store copy, or null when running as a normal program.</summary>
    public static string? PackagedAppId()
    {
        var length = 256;
        var buffer = new StringBuilder(length);
        return GetCurrentApplicationUserModelId(ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    /// <summary>True when double-clicking a PDF opens this app.</summary>
    public static bool IsDefault()
    {
        try
        {
            var current = CurrentProgId();
            if (current.Length == 0) return false;
            var packaged = PackagedAppId();
            if (packaged is null)
                return current.Equals(ProgId, StringComparison.OrdinalIgnoreCase)
                       || current.Equals(@"Applications\PBJStudio.exe", StringComparison.OrdinalIgnoreCase);

            using var key = Registry.ClassesRoot.OpenSubKey(current + @"\Application");
            return string.Equals(key?.GetValue("AppUserModelID") as string, packaged, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.LogError("Checking the default PDF app", ex);
            return false;
        }
    }

    /// <summary>The Windows Settings address that shows this app's "Set default" button.</summary>
    public static string SettingsAddress()
    {
        var packaged = PackagedAppId();
        if (packaged is not null) return "ms-settings:defaultapps?registeredAUMID=" + Uri.EscapeDataString(packaged);
        using var machine = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications");
        var scope = machine?.GetValue(RegisteredName) is not null ? "registeredAppMachine" : "registeredAppUser";
        return $"ms-settings:defaultapps?{scope}={RegisteredName}";
    }

    /// <summary>Registers the app as a PDF choice if needed and opens the Windows page to confirm it.</summary>
    public static void OpenWindowsChoice()
    {
        if (PackagedAppId() is null) RegisterForCurrentUser();
        Process.Start(new ProcessStartInfo(SettingsAddress()) { UseShellExecute = true });
    }

    /// <summary>The same per-user entries the installer's "PDF files" option writes; nothing when already there.</summary>
    private static void RegisterForCurrentUser()
    {
        using (var machine = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications"))
            if (machine?.GetValue(RegisteredName) is not null) return;
        using (var user = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
            if (user?.GetValue(RegisteredName) is not null) return;
        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith("PBJStudio.exe", StringComparison.OrdinalIgnoreCase)) return;

        void Set(string path, string? name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue(name, value);
        }

        Set($@"Software\Classes\{ProgId}", null, "PDF Document (PB&J Studio)");
        Set($@"Software\Classes\{ProgId}\DefaultIcon", null, exe + ",0");
        Set($@"Software\Classes\{ProgId}\shell\open\command", null, $"\"{exe}\" \"%1\"");
        Set(@"Software\Classes\.pdf\OpenWithProgids", ProgId, "");
        Set(@"Software\PBJStudio\Capabilities", "ApplicationName", App.ProductName);
        Set(@"Software\PBJStudio\Capabilities", "ApplicationDescription", "View, fill, merge, protect and sign PDFs");
        Set(@"Software\PBJStudio\Capabilities\FileAssociations", ".pdf", ProgId);
        Set(@"Software\RegisteredApplications", RegisteredName, @"Software\PBJStudio\Capabilities");
    }

    private static string CurrentProgId()
    {
        const int AssocStrProgId = 20;
        uint length = 512;
        var buffer = new StringBuilder((int)length);
        return AssocQueryString(0, AssocStrProgId, ".pdf", null, buffer, ref length) == 0 ? buffer.ToString() : "";
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(ref int length, StringBuilder buffer);
}
