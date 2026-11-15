using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>A row in the search results list.</summary>
public sealed class SearchResultRow
{
    public required SearchHit Hit { get; init; }
    public int Index { get; init; }
    public string PageLabel => $"Page {Hit.PageNumber}";
    public string Snippet => Hit.Snippet.Replace('\r', ' ').Replace('\n', ' ').Trim();
    public PageHighlight Highlight => new(Hit.PageNumber, Hit.Left, Hit.Bottom, Hit.Width, Hit.Height);
}

/// <summary>
/// Search: finds text with PdfTextSearch on a worker thread, lists the hits with page and snippet,
/// steps through them and highlights them in the viewer. Also answers the toolbar find box.
/// </summary>
public partial class SearchPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private List<SearchResultRow> _results = [];
    private string? _lastQuery;
    private bool _lastCase;
    private bool _lastWhole;
    private int _generation;
    private bool _searching;

    public SearchPanel()
    {
        InitializeComponent();
        UpdateButtons();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Search";

    public string Description => "Find text, step through the hits.";

    public string IconKey => "Icon.Search";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host)
    {
        _host = host;
        host.FindRequested += OnFindRequested;
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        var sameDocument = ReferenceEquals(session, _session) && session is not null;
        Detach();
        _session = session;
        _viewer = viewer;

        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;

        if (!sameDocument) ClearResults();
        else ShowHighlights();

        if (IsLoaded) QueryBox.Focus();
        else Loaded += FocusOnce;
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.ClearSearchHighlights();
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _generation++;
        _searching = false;
        _session = null;
        _viewer = null;
    }

    private void FocusOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= FocusOnce;
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    // ---- Public for tests and automation ----------------------------------

    public IReadOnlyList<SearchResultRow> Results => _results;

    public int CurrentIndex => ResultList.SelectedIndex;

    /// <summary>Searches the working file off the UI thread and shows the hits.</summary>
    public async Task SearchAsync(string query, bool? caseSensitive = null, bool? wholeWord = null)
    {
        if (caseSensitive is not null) CaseCheck.IsChecked = caseSensitive;
        if (wholeWord is not null) WholeWordCheck.IsChecked = wholeWord;
        QueryBox.Text = query;

        var session = _session;
        query = query.Trim();
        if (session is null || query.Length == 0)
        {
            ClearResults();
            CountText.Text = session is null ? "Open a PDF to search it." : "";
            return;
        }

        var generation = ++_generation;
        var file = session.CurrentFile;
        var password = session.Password;
        var matchCase = CaseCheck.IsChecked == true;
        var whole = WholeWordCheck.IsChecked == true;

        _searching = true;
        CountText.Text = "Searching...";
        UpdateButtons();

        try
        {
            var hits = await Task.Run(() => PdfTextSearch.Search(file, query, matchCase, whole, password));
            if (generation != _generation) return;

            _lastQuery = query;
            _lastCase = matchCase;
            _lastWhole = whole;
            _results = hits.Select((h, i) => new SearchResultRow { Hit = h, Index = i }).ToList();
            ResultList.ItemsSource = _results;

            if (_results.Count == 0)
            {
                CountText.Text = $"No matches for \"{query}\".";
                _viewer?.ClearSearchHighlights();
                _host?.SetStatus($"No matches for \"{query}\".");
            }
            else
            {
                var pages = _results.Select(r => r.Hit.PageNumber).Distinct().Count();
                _host?.SetStatus($"{_results.Count} match(es) on {pages} page(s).");
                var first = FirstFromCurrentPage();
                ResultList.SelectedIndex = first; // shows highlights via OnResultSelected
                if (first == 0) ShowHighlights();
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            App.LogError("Search", ex);
            ClearResults();
            CountText.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            if (generation == _generation) _searching = false;
            UpdateButtons();
        }
    }

    public void Next() => Step(+1);

    public void Previous() => Step(-1);

    // ---- Events ------------------------------------------------------------

    private async void OnFindRequested(object? sender, string query)
    {
        _host?.OpenTool(Title);
        if (_session is null)
        {
            _host?.SetStatus("Open a PDF to search it.");
            return;
        }

        await SearchAsync(query);
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e) => await SearchAsync(QueryBox.Text);

    private async void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (IsSameSearch() && _results.Count > 0)
        {
            if (shift) Previous(); else Next();
            return;
        }

        await SearchAsync(QueryBox.Text);
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_results.Count > 0 || _lastQuery is not null) _ = SearchAsync(QueryBox.Text);
    }

    private void OnNextClicked(object sender, RoutedEventArgs e) => Next();

    private void OnPreviousClicked(object sender, RoutedEventArgs e) => Previous();

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        ShowHighlights();
        if (ResultList.SelectedItem is not null) ResultList.ScrollIntoView(ResultList.SelectedItem);
        UpdateButtons();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // The text may have moved (redaction, page changes): run the same search again.
        if (_lastQuery is not null) _ = SearchAsync(_lastQuery);
        else ClearResults();
    }

    // ---- Helpers -----------------------------------------------------------

    private bool IsSameSearch()
        => _lastQuery is not null &&
           string.Equals(QueryBox.Text.Trim(), _lastQuery, StringComparison.Ordinal) &&
           _lastCase == (CaseCheck.IsChecked == true) &&
           _lastWhole == (WholeWordCheck.IsChecked == true);

    private void Step(int delta)
    {
        if (_results.Count == 0) return;
        var index = ResultList.SelectedIndex < 0
            ? (delta > 0 ? 0 : _results.Count - 1)
            : (ResultList.SelectedIndex + delta + _results.Count) % _results.Count;
        ResultList.SelectedIndex = index;
    }

    private int FirstFromCurrentPage()
    {
        var page = _viewer?.CurrentPage ?? 1;
        var index = _results.FindIndex(r => r.Hit.PageNumber >= page);
        return index < 0 ? 0 : index;
    }

    private void ShowHighlights()
    {
        if (_viewer is null) return;
        if (_results.Count == 0)
        {
            _viewer.ClearSearchHighlights();
            return;
        }

        PageHighlight? current = ResultList.SelectedItem is SearchResultRow row ? row.Highlight : null;
        _viewer.SetSearchHighlights(_results.Select(r => r.Highlight), current);

        if (ResultList.SelectedIndex >= 0)
        {
            CountText.Text = $"{ResultList.SelectedIndex + 1} of {_results.Count}";
        }
        else
        {
            CountText.Text = $"{_results.Count} match(es)";
        }
    }

    private void ClearResults()
    {
        _generation++;
        _searching = false;
        _results = [];
        _lastQuery = null;
        ResultList.ItemsSource = null;
        CountText.Text = "";
        _viewer?.ClearSearchHighlights();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasResults = _results.Count > 0 && !_searching;
        NextButton.IsEnabled = hasResults;
        PreviousButton.IsEnabled = hasResults;
        SearchButton.IsEnabled = !_searching;
    }
}
