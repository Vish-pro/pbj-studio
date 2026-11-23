using KcsPdfSigner.App.Documents;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Services;

/// <summary>
/// Runs <see cref="DocumentAdvisor.Inspect"/> once per document version, off the UI thread, and keeps
/// the result per working file (each version of a document is its own file) and password, like
/// <see cref="SignatureChecks"/>. The suggestion bars and the Send check dialog share it.
/// </summary>
public static class DocumentAdvice
{
    private static readonly Dictionary<string, Task<DocumentAnalysis>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<DocumentAnalysis> GetAsync(DocumentSession session)
    {
        var file = session.CurrentFile;
        var password = session.Password;
        var key = file + "|" + (password ?? "");

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var existing) && !existing.IsFaulted) return existing;

            // Inspect never throws; a file it cannot read gives an analysis with Error set and no findings.
            var task = Task.Run(() => DocumentAdvisor.Inspect(file, password));
            Cache[key] = task;
            if (Cache.Count > 64)
            {
                foreach (var stale in Cache.Keys.Where(k => k != key).Take(Cache.Count - 64).ToList()) Cache.Remove(stale);
            }

            return task;
        }
    }
}
