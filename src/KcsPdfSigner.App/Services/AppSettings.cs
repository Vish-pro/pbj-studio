using System.IO;
using System.Text.Json;
using System.Windows;
using KcsPdfSigner.App.Dialogs;

namespace KcsPdfSigner.App.Services;

public enum TouchMode
{
    Auto,
    On,
    Off
}

public enum ScreenScaling
{
    /// <summary>Follow the Windows display scale (sharp, the default).</summary>
    Auto,

    /// <summary>Draw the interface at 100% regardless of the Windows display scale.</summary>
    None
}

/// <summary>
/// User preferences, kept in %APPDATA%\KcsPdfStudio\settings.json. Every property here is read
/// somewhere that changes behaviour; see the Preferences dialog for the matching checkbox.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StorePath => Path.Combine(App.DataFolder, "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Raised after <see cref="Apply"/> stores new values.</summary>
    public static event EventHandler? Changed;

    // Startup and documents
    public bool ReopenLastSession { get; set; }
    public List<string> LastSession { get; set; } = [];
    public bool PromptBeforeClosingMultiple { get; set; }
    public bool CreateLinksFromUrls { get; set; } = true;
    public bool DetectFormFields { get; set; } = true;

    // Tools
    public bool SingleKeyAccelerators { get; set; }
    public bool QuickActionsOnSelection { get; set; } = true;
    public bool MouseWheelZoom { get; set; }

    // Snapshot
    public bool SnapshotFixedResolution { get; set; }
    public int SnapshotPixelsPerInch { get; set; } = 150;
    public bool SnapshotOpenForMarkup { get; set; }

    // Display
    public TouchMode Touch { get; set; } = TouchMode.Auto;
    public ScreenScaling Scaling { get; set; } = ScreenScaling.Auto;

    // Warnings
    public bool HideEditWarnings { get; set; }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();

    /// <summary>Makes <paramref name="updated"/> the current settings, saves them and tells listeners.</summary>
    public static void Apply(AppSettings updated)
    {
        updated.SnapshotPixelsPerInch = Math.Clamp(updated.SnapshotPixelsPerInch, 30, 1200);
        Current = updated;
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Saves the current values without raising <see cref="Changed"/> (for things like the last session).</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            App.LogError("Saving settings", ex);
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StorePath)) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            App.LogError("Reading settings", ex);
        }

        return new AppSettings();
    }

    /// <summary>
    /// Confirmation before a change that cannot be undone from the document (redaction, flattening).
    /// Skipped when the user ticked "Do not show edit warnings".
    /// </summary>
    public static bool ConfirmEdit(Window? owner, string message, string title)
    {
        if (Current.HideEditWarnings) return true;
        return MessageDialog.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
               == MessageBoxResult.OK;
    }
}
