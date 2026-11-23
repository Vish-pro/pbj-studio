using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KcsPdfSigner.App.Services;

/// <summary>One certificate the user chose to trust for checking signatures (stored as DER, base64).</summary>
public sealed class TrustedCertificateEntry
{
    public string Thumbprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string ValidTo { get; set; } = "";
    public string Added { get; set; } = "";
    public string Certificate { get; set; } = "";
}

/// <summary>
/// The app's own list of certificates trusted for signature checks, kept in
/// %APPDATA%\KcsPdfStudio\trusted-certs.json. Used in addition to the Windows certificate store,
/// which is never changed. Nothing is ever added without the user asking for it.
/// </summary>
public static class TrustedCertificates
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static List<TrustedCertificateEntry>? _entries;

    public static string StorePath => Path.Combine(App.DataFolder, "trusted-certs.json");

    /// <summary>Raised after the list changes (signatures are then checked again).</summary>
    public static event EventHandler? Changed;

    /// <summary>Goes up by one on every change, so cached results can tell they are stale.</summary>
    public static int Version { get; private set; }

    public static IReadOnlyList<TrustedCertificateEntry> Entries => _entries ??= Load();

    /// <summary>The trusted certificates, ready for the verifier.</summary>
    public static List<X509Certificate2> Certificates()
    {
        var list = new List<X509Certificate2>();
        foreach (var entry in Entries)
        {
            try
            {
                list.Add(new X509Certificate2(Convert.FromBase64String(entry.Certificate)));
            }
            catch
            {
                // A damaged entry is simply not used.
            }
        }

        return list;
    }

    public static bool Contains(string thumbprint) =>
        Entries.Any(e => string.Equals(e.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a certificate (DER bytes). Returns false when it was already in the list.</summary>
    public static bool Add(byte[] der)
    {
        var certificate = new X509Certificate2(der);
        if (Contains(certificate.Thumbprint)) return false;

        var entries = Entries.ToList();
        entries.Add(new TrustedCertificateEntry
        {
            Thumbprint = certificate.Thumbprint,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            ValidTo = certificate.NotAfter.ToString("yyyy-MM-dd"),
            Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Certificate = Convert.ToBase64String(certificate.RawData)
        });
        Store(entries);
        return true;
    }

    public static void Remove(string thumbprint)
    {
        var entries = Entries.Where(e => !string.Equals(e.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)).ToList();
        Store(entries);
    }

    /// <summary>
    /// Reads a certificate file: .cer/.crt/.der (binary or PEM text) or .p7b (first certificate).
    /// Throws with a readable message when the file holds no certificate.
    /// </summary>
    public static byte[] ReadCertificateFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start >= 0)
        {
            var stop = text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop > start)
            {
                var body = text[(start + begin.Length)..stop];
                bytes = Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            }
        }

        try
        {
            return new X509Certificate2(bytes).RawData;
        }
        catch
        {
            // A PKCS#7 bundle (.p7b): take the self-signed root if there is one, else the first.
            var collection = new X509Certificate2Collection();
            try
            {
                collection.Import(bytes);
            }
            catch
            {
                throw new InvalidOperationException("The file does not contain a certificate.");
            }

            if (collection.Count == 0) throw new InvalidOperationException("The file does not contain a certificate.");
            var root = collection.FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData));
            return (root ?? collection[0]).RawData;
        }
    }

    private static List<TrustedCertificateEntry> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<TrustedCertificateEntry>>(File.ReadAllText(StorePath)) ?? [];
        }
        catch (Exception ex)
        {
            App.LogError("Reading trusted certificates", ex);
        }

        return [];
    }

    private static void Store(List<TrustedCertificateEntry> entries)
    {
        Directory.CreateDirectory(App.DataFolder);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(entries, JsonOptions));
        _entries = entries;
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Forgets the in-memory copy so the next read comes from disk (tests restore the file).</summary>
    public static void Reload()
    {
        _entries = null;
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
