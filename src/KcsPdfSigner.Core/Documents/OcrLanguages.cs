using System.Net.Http;
using System.Security.Cryptography;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>One OCR language pack (a Tesseract "traineddata" file from tessdata_fast).</summary>
public sealed class OcrLanguage
{
    public OcrLanguage(string code, string name, long sizeBytes, string sha256, bool bundled)
    {
        Code = code;
        Name = name;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Bundled = bundled;
    }

    /// <summary>Tesseract code, e.g. "hin".</summary>
    public string Code { get; }

    /// <summary>English name, e.g. "Hindi".</summary>
    public string Name { get; }

    /// <summary>Exact size of the official file.</summary>
    public long SizeBytes { get; }

    /// <summary>SHA-256 of the official file (pinned when this version was built).</summary>
    public string Sha256 { get; }

    /// <summary>Always installed with the program (English and orientation detection).</summary>
    public bool Bundled { get; }

    public string FileName => Code + ".traineddata";

    /// <summary>Official download address (tessdata_fast, tag 4.1.0).</summary>
    public string Url => OcrLanguages.DownloadBase + FileName;

    public override string ToString() => Name;
}

/// <summary>Where a language was found.</summary>
public enum OcrLanguageSource
{
    NotInstalled,

    /// <summary>In the program's own tessdata folder (put there by the installer).</summary>
    WithProgram,

    /// <summary>In the user's tessdata folder (added from the Languages dialog).</summary>
    AddedByUser
}

/// <summary>
/// The OCR language packs: the catalogue (English and orientation detection always come with the
/// program, ten Indian languages are optional), where they are looked for, and how a downloaded or
/// copied file is checked before it is used.
/// </summary>
/// <remarks>
/// Two folders count: the program's "tessdata" folder (installer) and %APPDATA%\KcsPdfStudio\tessdata
/// (added later by the user, no administrator rights needed). A language found in either is installed.
/// Files are from https://github.com/tesseract-ocr/tessdata_fast (Apache-2.0), tag 4.1.0; their sizes
/// and SHA-256 values below were taken from those files, so a changed or damaged file is refused.
/// </remarks>
public static class OcrLanguages
{
    public const string DownloadBase = "https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/";

    public const string OsdCode = "osd";
    public const string EnglishCode = "eng";

    /// <summary>Every pack the program knows: English, orientation detection, then the ten optional languages.</summary>
    public static IReadOnlyList<OcrLanguage> Catalog { get; } =
    [
        new("eng", "English", 4113088, "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2", true),
        new("osd", "Page orientation detection", 10562727, "9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff", true),
        new("hin", "Hindi", 1122751, "4c73ffc59d497c186b19d1e90f5d721d678ea6b2e277b719bee4e2af12271825", false),
        new("kan", "Kannada", 3608331, "bd31e6b6ae93271e3bcf5383d306d8eefbb91542937cd6d735a5930c970e61d8", false),
        new("mar", "Marathi", 2118233, "0ba3f2d116972e72fe9e176bc84c38e81dfb6670f4ed1f7f6c8e16a27da7cb61", false),
        new("tam", "Tamil", 3237963, "d02fbec24be4b07e32e80d0ccfc3b6b67a3c5d61c9d0a7c8532677990912c6ec", false),
        new("tel", "Telugu", 2769654, "d10691fddd5b67802e1c12800ebb321d3b8bcd8d24a2ac3ff206f93188c04ab5", false),
        new("mal", "Malayalam", 5275996, "bd05cbf1b197e7810d2903419aedb06f9ef77bfedf50b358673c1d18d707cdb4", false),
        new("guj", "Gujarati", 1418394, "fa69658614b4946a9afae8853d67e0689838803dfa3d12c2e35ec53ee6f8df34", false),
        new("ben", "Bengali", 855841, "31163084c279aaebd376216f0c3d5c17ad4b5fee8db49dae79c20000b5de5964", false),
        new("pan", "Punjabi", 497721, "1ec0907fc3534065ea9ae190c6bb7ec9e5c74fd9d2fa996aaec7407f11ad8131", false),
        new("urd", "Urdu", 1398718, "62e8250ce2a994106e313a82e26a516a39e2cf159d0ce3c5b5008387fd0d555f", false),
    ];

    /// <summary>The languages a user can read a scan in (the catalogue without orientation detection).</summary>
    public static IEnumerable<OcrLanguage> ReadingLanguages => Catalog.Where(l => l.Code != OsdCode);

    public static OcrLanguage? Find(string code) =>
        Catalog.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The program's own language folder (next to the exe).</summary>
    public static string ProgramFolder => Path.Combine(AppContext.BaseDirectory, "tessdata");

    /// <summary>The user's language folder, for packs added later.</summary>
    public static string UserFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KcsPdfStudio", "tessdata");

    /// <summary>Both folders, program first.</summary>
    public static IReadOnlyList<string> DefaultFolders => [ProgramFolder, UserFolder];

    /// <summary>
    /// Every language file found in <paramref name="folders"/> (code to full path), by file name and a
    /// valid traineddata header. When a language is in more than one folder, the first folder wins.
    /// Languages outside the catalogue are included too (a user may copy any Tesseract pack).
    /// </summary>
    public static Dictionary<string, string> FindInstalled(IEnumerable<string>? folders = null)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders ?? DefaultFolders)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.traineddata"))
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    if (code.Length == 0 || found.ContainsKey(code)) continue;
                    if (HasTrainedDataHeader(file, out _)) found[code] = file;
                }
            }
            catch
            {
                // A folder that cannot be read simply offers no languages.
            }
        }

        return found;
    }

    /// <summary>Where a language is installed (program folder first), with the default folders.</summary>
    public static OcrLanguageSource SourceOf(string code, IReadOnlyDictionary<string, string> installed)
    {
        if (!installed.TryGetValue(code, out var path)) return OcrLanguageSource.NotInstalled;
        var folder = Path.GetFullPath(Path.GetDirectoryName(path) ?? "");
        return string.Equals(folder.TrimEnd('\\'), Path.GetFullPath(UserFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? OcrLanguageSource.AddedByUser
            : OcrLanguageSource.WithProgram;
    }

    /// <summary>
    /// True when the file starts like a Tesseract traineddata file: an entry count (1 to 64) followed by
    /// that many offsets, each -1 (absent) or inside the file.
    /// </summary>
    public static bool HasTrainedDataHeader(string path, out string problem)
    {
        problem = "";
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;
            using var reader = new BinaryReader(stream);
            if (length < 12)
            {
                problem = "The file is too small to be a language pack.";
                return false;
            }

            var count = reader.ReadInt32();
            if (count < 1 || count > 64 || length < 4 + 8L * count)
            {
                problem = "The file is not a Tesseract language pack (.traineddata).";
                return false;
            }

            var header = 4 + 8L * count;
            var any = false;
            for (var i = 0; i < count; i++)
            {
                var offset = reader.ReadInt64();
                if (offset == -1) continue;
                if (offset < header || offset >= length)
                {
                    problem = "The file is not a Tesseract language pack, or it is damaged.";
                    return false;
                }

                any = true;
            }

            if (!any) problem = "The language pack is empty.";
            return any;
        }
        catch (Exception ex)
        {
            problem = "The file could not be read: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Checks a language file: its header, and when the expected values are given, its exact size and
    /// SHA-256. Returns "" when it is good, otherwise a sentence for the user.
    /// </summary>
    public static string Verify(string path, long expectedSize = 0, string? expectedSha256 = null)
    {
        if (!File.Exists(path)) return "The file was not found.";
        var size = new FileInfo(path).Length;
        if (expectedSize > 0 && size != expectedSize)
            return $"The file has the wrong size ({size:N0} bytes instead of {expectedSize:N0}). It may be incomplete or a different version.";
        if (!HasTrainedDataHeader(path, out var problem)) return problem;

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            var actual = Sha256Of(path);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return "The file's checksum does not match the official file, so it was not used.";
        }

        return "";
    }

    /// <summary>Checks a file against the catalogue entry it claims to be.</summary>
    public static string Verify(string path, OcrLanguage language) => Verify(path, language.SizeBytes, language.Sha256);

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Copies a language file chosen by the user (for PCs without internet) into
    /// <paramref name="targetFolder"/> (the user folder by default) after checking it. A file named like a
    /// catalogue language must be the official file (size and checksum); another *.traineddata file only
    /// needs a valid header. Returns the language code. Throws <see cref="InvalidOperationException"/>
    /// with a readable message when the file is refused.
    /// </summary>
    public static string InstallFromFile(string sourcePath, string? targetFolder = null)
    {
        if (!File.Exists(sourcePath)) throw new InvalidOperationException("The file was not found.");
        var name = Path.GetFileName(sourcePath);
        if (!name.EndsWith(".traineddata", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a Tesseract language file: its name ends in .traineddata, for example hin.traineddata.");

        var code = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (code == OsdCode)
            throw new InvalidOperationException("Orientation detection always comes with the program; it cannot be replaced.");
        var known = Find(code);
        var problem = known is null ? Verify(sourcePath) : Verify(sourcePath, known);
        if (problem.Length > 0) throw new InvalidOperationException(problem);

        var folder = targetFolder ?? UserFolder;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, code + ".traineddata");
        var temp = target + ".part";
        File.Copy(sourcePath, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
        return code;
    }

    /// <summary>
    /// Downloads <paramref name="language"/> from its official address into <paramref name="targetFolder"/>
    /// (the user folder by default), reporting the bytes received. The file is written as .part, checked
    /// (size, header, SHA-256) and only then renamed; a refused or cancelled download leaves nothing.
    /// Pass <paramref name="client"/> to use another HttpClient (tests use a local fake). Throws
    /// <see cref="InvalidOperationException"/> with a readable message (offline, refused file).
    /// </summary>
    public static async Task<string> DownloadAsync(
        OcrLanguage language, string? targetFolder = null, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        var folder = targetFolder ?? UserFolder;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, language.FileName);
        var temp = target + ".part";
        var ownClient = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(language.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    "Could not reach github.com, where the language files are kept. Check the internet connection and try again, " +
                    "or download " + language.FileName + " on another PC and use \"Add from a file...\".\n\n(" + ex.Message + ")", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The download timed out. Check the internet connection and try again.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"The download failed: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        total += read;
                        progress?.Report(total);
                        if (total > language.SizeBytes + 1024)
                            throw new InvalidOperationException("The download is larger than the official file, so it was stopped.");
                    }
                }
            }

            var problem = Verify(temp, language);
            if (problem.Length > 0) throw new InvalidOperationException(problem);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            if (ownClient) client.Dispose();
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* leftover .part files are ignored */ }
        }
    }

    /// <summary>Deletes a language the user added. Languages that came with the program are not removed.</summary>
    public static void RemoveUserLanguage(string code, string? userFolder = null)
    {
        var path = Path.Combine(userFolder ?? UserFolder, code + ".traineddata");
        if (File.Exists(path)) File.Delete(path);
    }
}
