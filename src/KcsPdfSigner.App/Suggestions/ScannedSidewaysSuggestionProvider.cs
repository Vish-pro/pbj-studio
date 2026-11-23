using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Suggestions;

/// <summary>
/// "N scanned pages look sideways or upside down. Straighten them?" for picture pages (no text, so the
/// built-in text-direction check cannot see them). Tesseract's orientation detection reads them in the
/// background, once per document version, at 150 dpi and for at most <see cref="MaxPages"/> pages; the
/// bar appears when that finishes. Conservative: only pages whose orientation confidence reaches
/// <see cref="PdfOcrOps.SidewaysSuggestionConfidence"/> count, because a false alarm is worse than a miss.
/// </summary>
public sealed class ScannedSidewaysSuggestionProvider : ISuggestionProvider
{
    public const string Id = "scanned-sideways";

    /// <summary>Picture pages checked per document (the first ones); orientation reading costs about 0.2 s a page.</summary>
    public const int MaxPages = 60;

    private static readonly Lazy<bool> EngineReady = new(() => PdfOcrOps.CheckEngine().Length == 0);

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Suggestion> GetSuggestions(SuggestionContext context)
    {
        var analysis = context.Analysis;
        if (!context.HasTool(OcrSuggestionProvider.OcrToolTitle)) return [];
        if (analysis.Permissions?.IsRestricted == true || analysis.Scan.ScannedPages.Count == 0) return [];
        if (!EngineReady.Value || !OcrLanguages.FindInstalled().ContainsKey(OcrLanguages.OsdCode)) return [];

        var session = context.Session;
        var file = session.CurrentFile;
        var password = session.Password;
        var key = file + "|" + (password ?? "");

        Job job;
        lock (_gate)
        {
            // Only the document on screen matters: stop reading any other version.
            foreach (var (other, running) in _jobs.Where(j => j.Key != key && !j.Value.Task.IsCompleted).ToList())
            {
                running.Cancel.Cancel();
                _jobs.Remove(other);
            }

            if (!_jobs.TryGetValue(key, out job!))
            {
                var pages = analysis.Scan.ScannedPages.Take(MaxPages).ToList();
                var cancel = new CancellationTokenSource();
                var task = Task.Run(() =>
                {
                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* ignore */ }
                    try
                    {
                        return PdfOcrOps.DetectOrientation(file, password, pages, cancellationToken: cancel.Token);
                    }
                    finally
                    {
                        try { Thread.CurrentThread.Priority = ThreadPriority.Normal; } catch { /* ignore */ }
                    }
                }, cancel.Token);
                job = new Job(task, cancel);
                _jobs[key] = job;
                if (_jobs.Count > 32) _jobs.Remove(_jobs.Keys.First(k => k != key));
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted) App.LogError("Orientation of scanned pages", t.Exception);
                    if (t.IsCompletedSuccessfully) SuggestionRegistry.RequestRefresh();
                }, TaskScheduler.Default);
            }
        }

        if (!job.Task.IsCompletedSuccessfully) return [];

        var turns = job.Task.Result
            .Where(o => o.TurnClockwise != 0 && o.Confidence >= PdfOcrOps.SidewaysSuggestionConfidence)
            .Select(o => new PageTurn(o.PageNumber, o.TurnClockwise))
            .ToList();
        if (turns.Count == 0) return [];

        var host = context.Host;
        var n = turns.Count;
        return
        [
            new Suggestion
            {
                Id = Id,
                Priority = 79,
                Message = n == 1
                    ? $"Scanned page {turns[0].PageNumber} looks sideways or upside down. Straighten it?"
                    : $"{n} scanned pages look sideways or upside down. Straighten them?",
                ActionLabel = n == 1 ? "Straighten page" : "Straighten pages",
                Run = async () =>
                {
                    // One change for all pages (PdfPageOps.RotatePages per turn), so one Ctrl+Z undoes it.
                    await session.ApplyAsync((input, output) => DocumentAdvisor.StraightenPages(input, output, turns, password),
                        "Straighten scanned pages");
                    host.SetStatus($"Straightened {n} scanned page(s). Ctrl+Z undoes it.");
                }
            }
        ];
    }

    private sealed record Job(Task<List<PageOrientation>> Task, CancellationTokenSource Cancel);
}
