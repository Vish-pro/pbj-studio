using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Tools;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Suggestions;

/*
 * Suggestion bars: how to add one from a new feature (for example OCR)
 * ---------------------------------------------------------------------
 * The main window shows at most two bars under the toolbar for the active document. The built-in
 * bars come from Core's DocumentAdvisor (KcsPdfSigner.Core.Documents). A feature that is not part
 * of the framework adds its own bars without touching it:
 *
 * 1. Implement ISuggestionProvider. GetSuggestions runs on the UI thread, after the document has
 *    been analysed off the UI thread; read context.Analysis (facts about the file) and return
 *    Suggestion objects. Keep it quick: no file reading here.
 * 2. Register it once: add it to SuggestionRegistry's list below, or call
 *    SuggestionRegistry.Register(new MyProvider()) at start-up.
 * 3. Give each suggestion a stable Id: "Don't show this again" is stored by Id in settings.json.
 *
 * 4. Facts that take long to find (reading page orientation with OCR) are worked out in the
 *    background by the provider itself; when they are ready it calls SuggestionRegistry.RequestRefresh()
 *    and the bars are built again (see ScannedSidewaysSuggestionProvider).
 *
 * OCR uses both: OcrSuggestionProvider shows "This looks like a scan. Make it searchable (OCR)?"
 * because the "OCR" tool is registered in ToolRegistry, using Core's scan detection
 * (DocumentAnalysis.Scan); ScannedSidewaysSuggestionProvider reads the orientation of picture
 * pages (Tesseract's orientation detection) and offers to straighten them.
 */

/// <summary>One bar: a message, optionally one action button.</summary>
public sealed class Suggestion
{
    /// <summary>Stable id; also the key for "Don't show this again".</summary>
    public required string Id { get; init; }

    /// <summary>Higher shows first (built-in bars use 40 to 95).</summary>
    public int Priority { get; init; }

    public required string Message { get; init; }

    /// <summary>Button text; null for an information-only bar.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>What the button does; null for an information-only bar. Runs on the UI thread.</summary>
    public Func<Task>? Run { get; init; }
}

/// <summary>What a provider can look at.</summary>
public sealed class SuggestionContext
{
    public required DocumentSession Session { get; init; }

    /// <summary>Facts read from the current version of the document.</summary>
    public required DocumentAnalysis Analysis { get; init; }

    public required IStudioHost Host { get; init; }

    /// <summary>True when a tool panel with this title is registered (e.g. "OCR").</summary>
    public required Func<string, bool> HasTool { get; init; }
}

/// <summary>Adds suggestion bars for a feature outside the framework.</summary>
public interface ISuggestionProvider
{
    IEnumerable<Suggestion> GetSuggestions(SuggestionContext context);
}

/// <summary>The registered providers. The built-in bars from Core are not listed here.</summary>
public static class SuggestionRegistry
{
    private static readonly List<ISuggestionProvider> List = [new OcrSuggestionProvider(), new ScannedSidewaysSuggestionProvider()];

    public static IReadOnlyList<ISuggestionProvider> Providers => List;

    public static void Register(ISuggestionProvider provider)
    {
        if (!List.Contains(provider)) List.Add(provider);
    }

    /// <summary>Raised (on any thread) when a provider has new facts; the main window rebuilds its bars.</summary>
    public static event EventHandler? RefreshRequested;

    public static void RequestRefresh() => RefreshRequested?.Invoke(null, EventArgs.Empty);
}

/// <summary>
/// "This looks like a scan. Make it searchable (OCR)?" Shown when a tool titled "OCR" is registered
/// and the file can be changed (an owner-restricted file cannot be rewritten, so it is not offered).
/// </summary>
public sealed class OcrSuggestionProvider : ISuggestionProvider
{
    public const string OcrToolTitle = "OCR";

    public IEnumerable<Suggestion> GetSuggestions(SuggestionContext context)
    {
        if (!context.HasTool(OcrToolTitle)) yield break;
        if (context.Analysis.Permissions?.IsRestricted == true) yield break;

        var finding = DocumentAdvisor.BuildFindings(context.Analysis, new AdvisorOptions { LargeFileBytes = 0 })
            .FirstOrDefault(f => f.Id == SuggestionIds.Scanned);
        if (finding is null) yield break;

        var host = context.Host;
        yield return new Suggestion
        {
            Id = finding.Id,
            Priority = finding.Priority,
            Message = finding.Message,
            ActionLabel = finding.ActionLabel,
            Run = () =>
            {
                host.OpenTool(OcrToolTitle);
                return Task.CompletedTask;
            }
        };
    }
}
