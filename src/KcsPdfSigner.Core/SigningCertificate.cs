using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KcsPdfSigner.Core;

/// <summary>
/// A certificate from the Windows certificate store that is usable for signing,
/// together with the descriptive bits the UI needs to show.
/// </summary>
public sealed class SigningCertificate
{
    public SigningCertificate(X509Certificate2 certificate, StoreLocation location, string providerName)
    {
        Certificate = certificate;
        StoreLocation = location;
        ProviderName = providerName;
    }

    public X509Certificate2 Certificate { get; }

    public StoreLocation StoreLocation { get; }

    /// <summary>CSP/KSP backing the private key. Used to tell a USB token apart from a soft key.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// True when the private key is held by something other than Microsoft's software key store,
    /// i.e. almost certainly a USB DSC token (eMudhra / Capricorn / ProxKey / ePass and friends).
    /// </summary>
    public bool IsHardwareToken =>
        !string.IsNullOrEmpty(ProviderName) &&
        !ProviderName.Contains("Software Key Storage", StringComparison.OrdinalIgnoreCase) &&
        !ProviderName.Contains("Strong Cryptographic", StringComparison.OrdinalIgnoreCase) &&
        !ProviderName.Contains("Enhanced Cryptographic", StringComparison.OrdinalIgnoreCase) &&
        !ProviderName.Contains("Base Cryptographic Provider", StringComparison.OrdinalIgnoreCase);

    public string CommonName => ReadNamePart(Certificate.Subject, "CN") ?? Certificate.Subject;

    public string IssuerName => ReadNamePart(Certificate.Issuer, "CN") ?? Certificate.Issuer;

    public string SerialNumber => Certificate.SerialNumber;

    public DateTime ValidFrom => Certificate.NotBefore;

    public DateTime ValidTo => Certificate.NotAfter;

    public bool IsExpired => DateTime.Now > Certificate.NotAfter;

    public bool IsNotYetValid => DateTime.Now < Certificate.NotBefore;

    public string DisplayName
    {
        get
        {
            var suffix = IsExpired ? "  [EXPIRED]"
                : IsNotYetValid ? "  [NOT YET VALID]"
                : $"  (valid to {ValidTo:dd-MMM-yyyy})";
            var kind = IsHardwareToken ? "token" : "file/soft";
            return $"{CommonName}  -  {kind}{suffix}";
        }
    }

    /// <summary>
    /// Enumerates certificates that have a private key and are allowed to sign.
    /// Reads both the current-user and local-machine personal stores; DSC token drivers
    /// normally register into CurrentUser\My when the token is plugged in.
    /// </summary>
    public static List<SigningCertificate> Discover(bool includeExpired = false)
    {
        var found = new List<SigningCertificate>();
        var seenThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                // Store may not exist (common for LocalMachine\My on a personal PC).
                continue;
            }

            foreach (var cert in store.Certificates)
            {
                if (!cert.HasPrivateKey) continue;
                if (!AllowsSigning(cert)) continue;
                if (!includeExpired && DateTime.Now > cert.NotAfter) continue;
                if (!seenThumbprints.Add(cert.Thumbprint)) continue;

                found.Add(new SigningCertificate(cert, location, ReadProviderName(cert)));
            }
        }

        return found
            .OrderByDescending(c => !c.IsExpired)
            .ThenByDescending(c => c.IsHardwareToken)
            .ThenBy(c => c.CommonName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A certificate qualifies if it carries no key-usage restriction at all, or if it
    /// permits digitalSignature / nonRepudiation. DSCs issued in India set both.
    /// </summary>
    private static bool AllowsSigning(X509Certificate2 cert)
    {
        var usageExtensions = cert.Extensions.OfType<X509KeyUsageExtension>().ToList();
        if (usageExtensions.Count == 0) return true;

        const X509KeyUsageFlags signingFlags =
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation;

        return usageExtensions.Any(u => (u.KeyUsages & signingFlags) != 0);
    }

    /// <summary>
    /// Best-effort read of the provider backing the private key. Opening the key handle does
    /// not prompt for the token PIN on any driver we care about (only signing does), but the
    /// whole thing is guarded so an uncooperative CSP cannot break enumeration.
    /// </summary>
    private static string ReadProviderName(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng cng) return cng.Key.Provider?.Provider ?? string.Empty;
            if (rsa is RSACryptoServiceProvider csp) return csp.CspKeyContainerInfo.ProviderName ?? string.Empty;

            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa is ECDsaCng ecCng) return ecCng.Key.Provider?.Provider ?? string.Empty;
        }
        catch (Exception)
        {
            // Provider name is cosmetic; a failure here must not hide the certificate.
        }

        return string.Empty;
    }

    private static string? ReadNamePart(string distinguishedName, string key)
    {
        foreach (var part in distinguishedName.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return part[(key.Length + 1)..].Trim('"', ' ');
        }
        return null;
    }
}
