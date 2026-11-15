using System.IO;
using System.Text.Json;

namespace KcsPdfSigner.App.Services;

/// <summary>Most-recently-opened PDFs, kept in %APPDATA%\KcsPdfStudio\recent.json.</summary>
public static class RecentFiles
{
    private const int MaxEntries = 12;

    public static string StorePath => Path.Combine(App.DataFolder, "recent.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? [];
            return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        Save(list.Take(MaxEntries).ToList());
    }

    public static void Remove(string path)
    {
        var list = Load();
        if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save(list);
    }

    public static void Clear() => Save([]);

    private static void Save(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.LogError("Saving recent files", ex);
        }
    }
}
