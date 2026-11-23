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
 * The OCR hook already exists: OcrSuggestionProvider shows "This looks like a scan. Make it
 * searchable (OCR)?" as soon as a tool panel titled "OCR" is registered in ToolRegistry, using
 * Core's scan detection (DocumentAnalysis.Scan). For the OCR part of "pages look sideways"
 * (pages that are pictures, where text direction cannot be read), an OCR feature can add a
 * provider that reads context.Analysis.Scan.ScannedPages, finds each page's orientation and returns a
 * suggestion whose Run straightens them with session.ApplyAsync (see DocumentAdvisor.StraightenPages).
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
    private static readonly List<ISuggestionProvider> List = [new OcrSuggestionProvider()];

    public static IReadOnlyList<ISuggestionProvider> Providers => List;

    public static void Register(ISuggestionProvider provider)
    {
        if (!List.Contains(provider)) List.Add(provider);
    }
}

/// <summary>
/// "This looks like a scan. Make it searchable (OCR)?" Shown only when a tool titled "OCR" is
/// registered, so today (no OCR tool yet) it shows nothing.
/// </summary>
public sealed class OcrSuggestionProvider : ISuggestionProvider
{
    public const string OcrToolTitle = "OCR";

    public IEnumerable<Suggestion> GetSuggestions(SuggestionContext context)
    {
        if (!context.HasTool(OcrToolTitle)) yield break;

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
