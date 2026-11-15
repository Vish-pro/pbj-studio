using System.IO;
using System.Windows;
using System.Windows.Input;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Helpers shared by the Organize, Combine, Protect, Export, Properties and Read Aloud panels.
/// </summary>
internal static class PanelHelpers
{
    /// <summary>Shows an error in the app's own black dialog.</summary>
    public static void ShowError(IStudioHost? host, string title, string message)
        => MessageDialog.Show(host?.Window, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void ShowInfo(IStudioHost? host, string title, string message)
        => MessageDialog.Show(host?.Window, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(IStudioHost? host, string title, string message)
        => MessageDialog.Show(host?.Window, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="panel"/> disabled and a wait cursor, and
    /// shows any exception in a dialog. Returns true when it finished without an exception.
    /// </summary>
    public static async Task<bool> RunBusyAsync(
        IStudioHost? host, FrameworkElement panel, string description, Func<Task> work)
    {
        panel.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        host?.SetStatus(description + "...");
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            host?.SetStatus(description + " failed.");
            Mouse.OverrideCursor = null;
            ShowError(host, description, description + " failed:\n\n" + ex.Message);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            panel.IsEnabled = true;
        }
    }

    /// <summary>Like <see cref="RunBusyAsync"/> but leaves error handling to <paramref name="work"/>.</summary>
    public static async Task RunBusyQuietAsync(FrameworkElement panel, Func<Task> work)
    {
        panel.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await work();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            panel.IsEnabled = true;
        }
    }

    /// <summary>
    /// Parses a page list such as "1-3, 5, 8-" against a document of <paramref name="pageCount"/>
    /// pages. Empty text or "all" means every page. Throws <see cref="FormatException"/> with a
    /// readable message on bad input.
    /// </summary>
    public static List<int> ParsePageRange(string? text, int pageCount)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(1, pageCount).ToList();

        var pages = new List<int>();
        foreach (var raw in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            int from, to;
            if (dash < 0)
            {
                from = to = ParsePage(part, pageCount);
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                from = left.Length == 0 ? 1 : ParsePage(left, pageCount);
                to = right.Length == 0 ? pageCount : ParsePage(right, pageCount);
                if (to < from) throw new FormatException($"'{part}' runs backwards.");
            }

            for (var p = from; p <= to; p++)
                if (!pages.Contains(p)) pages.Add(p);
        }

        if (pages.Count == 0) throw new FormatException("No pages given.");
        pages.Sort();
        return pages;
    }

    private static int ParsePage(string text, int pageCount)
    {
        if (!int.TryParse(text, out var page))
            throw new FormatException($"'{text}' is not a page number.");
        if (page < 1 || page > pageCount)
            throw new FormatException($"Page {page} does not exist (the document has {pageCount} page(s)).");
        return page;
    }

    /// <summary>"1.2 MB" style size.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " bytes";
        double value = bytes;
        string[] units = ["KB", "MB", "GB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0") + " " + units[unit];
    }

    /// <summary>A fresh scratch folder under the studio temp root. Delete it with <see cref="TryDeleteFolder"/>.</summary>
    public static string NewScratchFolder(string prefix)
    {
        var folder = Path.Combine(DocumentSession.TempRoot, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Copies the session's current working file into <paramref name="folder"/> under the
    /// document's own name, so Core operations that name their output after the input file
    /// (Split, ExportImages) produce "Report_p1-3.pdf" instead of "004_p1-3.pdf".
    /// </summary>
    public static string NamedCopy(DocumentSession session, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(session.FileName);
        if (string.IsNullOrWhiteSpace(name)) name = "document";
        var copy = Path.Combine(folder, name + ".pdf");
        File.Copy(session.CurrentFile, copy, overwrite: true);
        File.SetAttributes(copy, FileAttributes.Normal);
        return copy;
    }

    public static void TryDeleteFolder(string? folder)
    {
        try
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Scratch only; stale folders are cleaned at start-up.
        }
    }

    /// <summary>Folder of the document's original file, or Documents when it has none.</summary>
    public static string DefaultFolder(DocumentSession? session)
    {
        try
        {
            var folder = session is null ? null : Path.GetDirectoryName(session.OriginalPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
        }
        catch
        {
            // Fall through.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
