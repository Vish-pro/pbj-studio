using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// OCR: makes scanned pages searchable (an invisible text layer from Tesseract; the pages look the same).
/// Pages (all, current, a range), languages (any installed ones, several together) and "skip pages that
/// already have text". The run is one <see cref="DocumentSession.ApplyAsync"/> change, so Ctrl+Z undoes it;
/// it runs off the UI thread with page-by-page progress and can be cancelled between pages.
/// </summary>
public partial class OcrPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private CancellationTokenSource? _cancel;
    private DocumentSession? _running;
    private readonly List<LanguageChoice> _languages = [];

    public OcrPanel()
    {
        InitializeComponent();
        LanguageList.ItemsSource = _languages;
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => OcrTitle;

    public const string OcrTitle = "OCR";

    public string Description => "Make scanned pages searchable: adds an invisible text layer.";

    public string IconKey => "Icon.Ocr";

    public FrameworkElement View => this;

    /// <summary>True while a run is going.</summary>
    public bool IsRunning => _running is not null;

    /// <summary>What the last run did (for the result line and tests); null before the first run.</summary>
    public OcrRunResult? LastResult { get; private set; }

    public void Initialize(IStudioHost host)
    {
        _host = host;
        // A document closed while it is being read: stop reading it.
        host.DocumentClosed += (_, closed) =>
        {
            if (ReferenceEquals(closed, _running)) _cancel?.Cancel();
        };
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        LoadLanguages();
        UpdateState();
    }

    public void Detach()
    {
        // A run keeps going in the background (its result lands in its own document).
        _session = null;
        _viewer = null;
        UpdateState();
    }

    // ---- Languages ---------------------------------------------------------

    /// <summary>Installed reading languages, catalogue order first; keeps what was ticked (English at first).</summary>
    private void LoadLanguages()
    {
        var ticked = _languages.Count == 0
            ? new HashSet<string>([OcrLanguages.EnglishCode])
            : _languages.Where(l => l.IsSelected).Select(l => l.Code).ToHashSet();

        var installed = OcrLanguages.FindInstalled();
        var codes = OcrLanguages.ReadingLanguages.Select(l => l.Code).Where(installed.ContainsKey)
            .Concat(installed.Keys.Where(k => k != OcrLanguages.OsdCode && OcrLanguages.Find(k) is null).OrderBy(k => k))
            .ToList();

        _languages.Clear();
        foreach (var code in codes)
            _languages.Add(new LanguageChoice(code, OcrLanguages.Find(code)?.Name ?? code) { IsSelected = ticked.Contains(code) });
        if (_languages.Count > 0 && !_languages.Any(l => l.IsSelected)) _languages[0].IsSelected = true;

        LanguageList.ItemsSource = null;
        LanguageList.ItemsSource = _languages;
    }

    /// <summary>The installed languages offered (for tests).</summary>
    public IReadOnlyList<LanguageChoice> Languages => _languages;

    private void OnLanguages(object sender, RoutedEventArgs e)
    {
        var dialog = new OcrLanguagesDialog { Owner = _host?.Window };
        dialog.ShowDialog();
        LoadLanguages();
    }

    // ---- Run -----------------------------------------------------------------

    private void OnRangeFocus(object sender, KeyboardFocusChangedEventArgs e) => RangeRadio.IsChecked = true;

    private async void OnRun(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Stopping after the current page...";
    }

    /// <summary>Reads the chosen pages of the active document. Returns true when the document was changed.</summary>
    internal async Task<bool> RunAsync()
    {
        var session = _session;
        if (session is null || IsRunning) return false;

        var engine = PdfOcrOps.CheckEngine();
        if (engine.Length > 0)
        {
            PanelHelpers.ShowError(_host, "OCR", engine);
            return false;
        }

        var languages = _languages.Where(l => l.IsSelected).Select(l => l.Code).ToList();
        if (languages.Count == 0)
        {
            PanelHelpers.ShowInfo(_host, "OCR", "Tick at least one language.");
            return false;
        }

        List<int>? pages;
        try
        {
            pages = CurrentPageRadio.IsChecked == true ? [Math.Max(1, _viewer?.CurrentPage ?? 1)]
                : RangeRadio.IsChecked == true ? PanelHelpers.ParsePageRange(RangeBox.Text, session.PageCount)
                : null;
        }
        catch (FormatException ex)
        {
            PanelHelpers.ShowInfo(_host, "OCR", ex.Message);
            return false;
        }

        var skip = SkipTextCheck.IsChecked == true;
        var file = session.CurrentFile;
        var password = session.Password;

        _running = session;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        LastResult = null;
        ResultText.Visibility = Visibility.Collapsed;
        UpdateState();
        ProgressText.Text = "Looking at the pages...";

        var changed = false;
        try
        {
            var toRead = await Task.Run(() => PdfOcrOps.PagesToRead(file, password, pages, skip), token);
            if (toRead.Count == 0)
            {
                ShowResult("Every chosen page already has text, so nothing was changed. Untick \"Skip pages that already have text\" to read them again.");
                return false;
            }

            Progress.Maximum = toRead.Count;
            Progress.Value = 0;
            ProgressText.Text = $"Page 1 of {toRead.Count}...";
            var progress = new Progress<OcrProgress>(p =>
            {
                Progress.Value = p.Done;
                if (p.Done < p.Total)
                {
                    ProgressText.Text = $"Page {p.Done + 1} of {p.Total} (page {p.PageNumber} of the document)...";
                    _host?.SetStatus($"OCR: page {p.Done + 1} of {p.Total}...");
                }
            });

            var options = new OcrOptions { Pages = pages, Languages = languages, SkipPagesWithText = skip };
            OcrRunResult? result = null;
            await session.ApplyAsync(
                (input, output) => result = PdfOcrOps.MakeSearchable(input, output, options, progress, token, password),
                "Make searchable (OCR)");
            changed = true;
            LastResult = result;
            ShowResult(Describe(result!));
            _host?.SetStatus($"OCR finished: {result!.PagesRead.Count} page(s) made searchable. Ctrl+Z undoes it.");
        }
        catch (SignedChangeDeclinedException declined)
        {
            _host?.SetStatus(declined.Message);
            ShowResult("Nothing was changed: the document's digital signatures were kept.");
        }
        catch (OperationCanceledException)
        {
            _host?.SetStatus("OCR cancelled. The document was not changed.");
            ShowResult("Cancelled. The document was not changed.");
        }
        catch (Exception ex)
        {
            App.LogError("OCR", ex);
            _host?.SetStatus("OCR failed.");
            ShowResult("OCR failed. The document was not changed.");
            PanelHelpers.ShowError(_host, "OCR", "OCR failed:\n\n" + ex.Message);
        }
        finally
        {
            _running = null;
            _cancel?.Dispose();
            _cancel = null;
            UpdateState();
        }

        return changed;
    }

    private static string Describe(OcrRunResult r)
    {
        var lines = new List<string>
        {
            r.PagesRead.Count == 0
                ? "No text was found on the chosen pages."
                : $"Made {r.PagesRead.Count} page(s) searchable: {r.Words:N0} words, average confidence {r.MeanConfidence:0}%, in {r.Elapsed.TotalSeconds:0} s."
        };
        if (r.PagesSkipped.Count > 0) lines.Add($"{r.PagesSkipped.Count} page(s) already had text and were left as they were.");
        if (r.PagesWithoutText.Count > 0)
            lines.Add($"No text found on {Pages(r.PagesWithoutText)} (blank, a photo, handwriting or a language that is not ticked).");
        if (r.TurnedForReading.Count > 0)
            lines.Add($"{Pages(r.TurnedForReading.Select(t => t.PageNumber).ToList())} looked sideways or upside down and were read turned upright. " +
                      "The pages themselves were not turned; use Organize Pages or the suggestion bar to turn them.");
        if (r.PagesRead.Count > 0)
        {
            if (r.MeanConfidence < 70) lines.Add("The confidence is low: the scan may be faint, blurred or in another language. Check the text before you rely on it.");
            lines.Add("Search (Ctrl+F) now finds words on these pages. Save to keep it; Ctrl+Z undoes it.");
        }

        return string.Join("\n", lines);
    }

    private static string Pages(IReadOnlyList<int> pages) =>
        (pages.Count == 1 ? "page " : "pages ") + (pages.Count <= 8 ? string.Join(", ", pages) : string.Join(", ", pages.Take(7)) + " and more");

    private void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.Visibility = Visibility.Visible;
    }

    private void UpdateState()
    {
        var hasDocument = _session is not null;
        var running = IsRunning;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        Options.IsEnabled = hasDocument && !running;
        RunButton.IsEnabled = hasDocument && !running && _languages.Count > 0;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        LanguagesButton.IsEnabled = !running;

        var engine = hasDocument ? PdfOcrOps.CheckEngine() : "";
        EngineText.Text = engine.Length > 0 ? engine
            : hasDocument && _languages.Count == 0 ? "No scan languages are installed. Use Languages... to add one." : "";
        EngineText.Visibility = EngineText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One installed language in the list.</summary>
    public sealed class LanguageChoice(string code, string name) : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Code { get; } = code;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? property = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
