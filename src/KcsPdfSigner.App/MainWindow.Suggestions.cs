using System.Runtime.CompilerServices;
using System.Windows;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Suggestions;
using KcsPdfSigner.App.Tools;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App;

/// <summary>
/// Suggestion bars under the toolbar (like Acrobat's): at most two at a time for the active document.
/// The facts come from Core's <see cref="DocumentAdvisor"/>, read once per document version off the UI
/// thread (<see cref="DocumentAdvice"/>); stale results (another tab, a newer version) are ignored.
/// Each button runs an existing feature. Other features add bars through <see cref="SuggestionRegistry"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>At most this many bars at once.</summary>
    public const int MaxSuggestionBars = 2;

    private int _suggestionGeneration;

    /// <summary>Suggestions dismissed per document for this session ("Dismiss").</summary>
    private readonly ConditionalWeakTable<DocumentSession, HashSet<string>> _dismissedSuggestions = new();

    private void AttachSuggestions(DocumentTab tab)
    {
        tab.Session.DocumentChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, _active)) UpdateSuggestions();
        };
        tab.Session.PropertyChanged += (_, e) =>
        {
            // Set by Multiple PDFs, Combine Files and Multi-Sign right after they open their result.
            if (e.PropertyName == nameof(DocumentSession.Origin) && ReferenceEquals(tab, _active)) UpdateSuggestions();
        };
    }

    /// <summary>The suggestions currently shown (for tests).</summary>
    public IReadOnlyList<Suggestion> VisibleSuggestions =>
        SuggestionHost.Children.OfType<SuggestionBar>().Select(b => b.Suggestion).ToList();

    private long LargeFileBytes => (long)(AppSettings.Current.LargeFileMegabytes * 1024 * 1024);

    private bool HasTool(string title) =>
        _tools.Any(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows the bars for the active document once its analysis has finished.</summary>
    private async void UpdateSuggestions()
    {
        var generation = ++_suggestionGeneration;
        var tab = _active;

        if (tab is null || !AppSettings.Current.ShowSuggestions)
        {
            ShowSuggestionBars([]);
            return;
        }

        // A different document: its bars must not linger while the new one is analysed.
        if (SuggestionHost.Tag is not null && !ReferenceEquals(SuggestionHost.Tag, tab)) ShowSuggestionBars([]);

        var file = tab.Session.CurrentFile;
        DocumentAnalysis analysis;
        try
        {
            analysis = await DocumentAdvice.GetAsync(tab.Session);
        }
        catch
        {
            return; // Inspect reports problems in the analysis itself; nothing to show.
        }

        if (generation != _suggestionGeneration || !ReferenceEquals(tab, _active) || file != tab.Session.CurrentFile) return;

        SuggestionHost.Tag = tab;
        ShowSuggestionBars(BuildSuggestions(tab, analysis));
    }

    /// <summary>Every suggestion for the tab, filtered (dismissed, turned off) and sorted, before taking the top two.</summary>
    private List<Suggestion> BuildSuggestions(DocumentTab tab, DocumentAnalysis analysis)
    {
        var session = tab.Session;
        var options = new AdvisorOptions { LargeFileBytes = LargeFileBytes, Origin = session.Origin };
        var all = new List<Suggestion>();

        foreach (var finding in DocumentAdvisor.BuildFindings(analysis, options))
        {
            // OCR is offered through its provider, only once an OCR tool exists.
            if (finding.ActionId == SuggestionActions.Ocr) continue;

            all.Add(new Suggestion
            {
                Id = finding.Id,
                Priority = finding.Priority,
                Message = finding.Message,
                ActionLabel = string.IsNullOrEmpty(finding.ActionLabel) ? null : finding.ActionLabel,
                Run = ActionFor(finding, tab, analysis)
            });
        }

        var context = new SuggestionContext { Session = session, Analysis = analysis, Host = this, HasTool = HasTool };
        foreach (var provider in SuggestionRegistry.Providers)
        {
            try
            {
                all.AddRange(provider.GetSuggestions(context));
            }
            catch (Exception ex)
            {
                App.LogError("Suggestion provider " + provider.GetType().Name, ex);
            }
        }

        var never = AppSettings.Current.DismissedSuggestions;
        var dismissed = _dismissedSuggestions.GetOrCreateValue(session);
        return all
            .Where(s => !never.Contains(s.Id) && !dismissed.Contains(s.Id))
            .GroupBy(s => s.Id).Select(g => g.First())
            .OrderByDescending(s => s.Priority)
            .Take(MaxSuggestionBars)
            .ToList();
    }

    private void ShowSuggestionBars(IReadOnlyList<Suggestion> suggestions)
    {
        SuggestionHost.Children.Clear();
        foreach (var suggestion in suggestions)
        {
            var bar = new SuggestionBar(suggestion);
            bar.ActionClicked += (_, _) => RunSuggestion(suggestion);
            bar.Dismissed += (_, _) => DismissSuggestion(suggestion.Id, forever: false);
            bar.NeverAgain += (_, _) => DismissSuggestion(suggestion.Id, forever: true);
            SuggestionHost.Children.Add(bar);
        }

        SuggestionHost.Visibility = SuggestionHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Dismiss: hidden for this document in this session. Forever: stored in the settings for every document.</summary>
    private void DismissSuggestion(string id, bool forever)
    {
        if (_active is null) return;

        if (forever)
        {
            if (!AppSettings.Current.DismissedSuggestions.Contains(id)) AppSettings.Current.DismissedSuggestions.Add(id);
            AppSettings.Save();
            SetStatus("That suggestion will not be shown again. Preferences > Reset suggestions brings it back.");
        }
        else
        {
            _dismissedSuggestions.GetOrCreateValue(_active.Session).Add(id);
        }

        UpdateSuggestions();
    }

    private async void RunSuggestion(Suggestion suggestion)
    {
        if (suggestion.Run is null) return;
        try
        {
            await suggestion.Run();
        }
        catch (SignedChangeDeclinedException declined)
        {
            SetStatus(declined.Message);
        }
        catch (Exception ex)
        {
            App.LogError("Suggestion " + suggestion.Id, ex);
            MessageDialog.Show(this, "That did not work.\n\n" + ex.Message, App.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ======================================================================
    // Actions: each one runs an existing feature
    // ======================================================================

    private Func<Task>? ActionFor(DocumentFinding finding, DocumentTab tab, DocumentAnalysis analysis)
    {
        var session = tab.Session;
        Task Done() => Task.CompletedTask;

        return finding.ActionId switch
        {
            SuggestionActions.RemovePasswordCopy => () =>
            {
                if (RemovePasswordCopyCommand.CanExecute(null)) RemovePasswordCopyCommand.Execute(null);
                return Done();
            },
            SuggestionActions.ReduceSize => () =>
            {
                OpenTool("Export");
                SetStatus("Press Optimize in the Export tool: it shows the size before and after, and Ctrl+Z undoes it.");
                return Done();
            },
            SuggestionActions.SignAtSpots => () =>
            {
                StartSigningAtSpots();
                return Done();
            },
            SuggestionActions.FillForm => () =>
            {
                OpenTool("Fill Forms");
                return Done();
            },
            SuggestionActions.AddPageNumbers => () =>
            {
                if (OpenTool("Edit") && _openTool?.Panel is EditPanel edit) edit.ShowPageNumbersOption();
                return Done();
            },
            SuggestionActions.RemoveHiddenInfo => () => RemoveHiddenInfoAsync(session, analysis.HiddenInfo),
            SuggestionActions.StraightenPages => () => StraightenPagesAsync(session, analysis.SidewaysPages),
            SuggestionActions.SaveCleanCopy => () => SaveCleanCopyAsync(session),
            SuggestionActions.SendCheck => () => ShowSendCheckAsync(session),
            _ => null
        };
    }

    /// <summary>Opens Multi-Sign with text detection, and detects the spots on the active document.</summary>
    private void StartSigningAtSpots()
    {
        if (_tools.FirstOrDefault(t => t.Panel is MultiSignPanel) is not { } entry) return;
        OpenToolEntry(entry);
        var model = ((MultiSignPanel)entry.Panel).ViewModel;
        model.IsAnchorMode = true;
        if (model.DetectCommand.CanExecute(null)) model.DetectCommand.Execute(null);
        SetStatus("Review the signing spots in Multi-Sign (DSC), then choose the certificate and sign.");
    }

    private async Task RemoveHiddenInfoAsync(DocumentSession session, HiddenInfoReport report)
    {
        var items = report.RemovableItems();
        if (items.Count == 0 && report.Attachments.Count == 0) return;

        var message = "This removes the following from the file (nothing visible on the pages changes):\n\n" +
                      string.Join("\n", items.Select(i => "- " + i)) +
                      "\n\nThe creation and change dates stay, and the program that saves the file records itself as Producer." +
                      "\nForm entries and text on the pages are not checked or changed.\n\nCtrl+Z undoes it. Continue?";
        if (MessageDialog.Show(this, message, "Remove hidden information", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var removeAttachments = false;
        if (report.Attachments.Count > 0)
        {
            removeAttachments = MessageDialog.Show(this,
                $"The file also has {report.Attachments.Count} attached file(s):\n\n" +
                string.Join("\n", report.Attachments.Take(10).Select(a => "- " + a)) +
                "\n\nRemove the attachments too?", "Attachments", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;
        }

        var password = session.Password;
        await session.ApplyAsync((input, output) => PdfSanitizeOps.RemoveHiddenInfo(input, output, password, removeAttachments),
            "Remove hidden information");
        SetStatus("Hidden information removed. Ctrl+Z undoes it; save to keep it.");
    }

    private async Task StraightenPagesAsync(DocumentSession session, IReadOnlyList<PageTurn> turns)
    {
        if (turns.Count == 0) return;
        var password = session.Password;
        var list = turns.ToList();
        // One change for all pages, so one Ctrl+Z undoes it.
        await session.ApplyAsync((input, output) => DocumentAdvisor.StraightenPages(input, output, list, password),
            "Straighten pages");
        SetStatus($"Straightened {list.Count} page(s). Ctrl+Z undoes it.");
    }

    /// <summary>Rewrites the damaged file cleanly (undoable), then offers Save As under a new name.</summary>
    private async Task SaveCleanCopyAsync(DocumentSession session)
    {
        var password = session.Password;
        await session.ApplyAsync((input, output) => DocumentAdvisor.WriteCleanCopy(input, output, password), "Repair file");
        SetStatus("The file structure was rebuilt. Choose where to save the clean copy.");
        if (ReferenceEquals(_active?.Session, session)) SaveAs();
    }

    private async Task ShowSendCheckAsync(DocumentSession session)
    {
        SetStatus("Checking the signed file...");
        var analysis = await DocumentAdvice.GetAsync(session);
        var verification = await SignatureChecks.GetAsync(session);
        SetStatus("Send check ready.");
        new SendCheckDialog(session.FileName, analysis, verification, session.IsEncrypted, LargeFileBytes) { Owner = this }.ShowDialog();
    }
}
