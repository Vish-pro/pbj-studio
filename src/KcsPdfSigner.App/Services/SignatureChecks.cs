using KcsPdfSigner.App.Documents;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Services;

/// <summary>
/// Runs the signature check for a document version once, off the UI thread, and shares the result
/// between the banner, the Signatures pane, the page badges and the properties dialog. A result is
/// kept per working file (each version of a document is its own file) and trust-list version.
/// </summary>
public static class SignatureChecks
{
    private static readonly Dictionary<string, Task<DocumentVerification>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The finished result for a file, or null while it is still running (or never ran).</summary>
    public static DocumentVerification? Peek(DocumentSession session)
    {
        lock (Cache)
        {
            return Cache.TryGetValue(Key(session.CurrentFile, session.Password), out var task) && task.IsCompletedSuccessfully
                ? task.Result
                : null;
        }
    }

    /// <summary>
    /// The check result for the session's current version. <paramref name="force"/> runs it again
    /// (the "Validate" buttons), e.g. after going online for revocation checks.
    /// </summary>
    public static Task<DocumentVerification> GetAsync(DocumentSession session, bool force = false)
    {
        var file = session.CurrentFile;
        var password = session.Password;
        var key = Key(file, password);

        lock (Cache)
        {
            if (!force && Cache.TryGetValue(key, out var existing) && !existing.IsFaulted) return existing;

            var trusted = TrustedCertificates.Certificates();
            var task = Task.Run(() =>
            {
                try
                {
                    return SignatureVerifier.Verify(file, password, new VerificationOptions { TrustedCertificates = trusted });
                }
                catch (Exception ex)
                {
                    // The verifier reports problems itself; this is a last guard so a view never hangs.
                    return new DocumentVerification { DocumentError = "The signatures could not be checked: " + ex.Message };
                }
            });

            Cache[key] = task;
            if (Cache.Count > 64)
            {
                foreach (var stale in Cache.Keys.Where(k => k != key).Take(Cache.Count - 64).ToList()) Cache.Remove(stale);
            }

            return task;
        }
    }

    private static string Key(string file, string? password) => file + "|" + (password ?? "") + "|" + TrustedCertificates.Version;
}
