using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>A marked (not yet applied) redaction area.</summary>
public sealed class RedactMark
{
    public required RedactArea Area { get; init; }
    public int Number { get; set; }
    public string Label => $"{Number}. Page {Area.PageNumber}  ({Area.Width:0} x {Area.Height:0} pt)";
}

/// <summary>
/// Redact: drag boxes on the pages, review them, then apply. Applying runs PdfContentOps.Redact
/// (iText pdfSweep), which removes the content under the boxes from the working copy.
/// </summary>
public partial class RedactPanel : UserControl, IToolPanel
{
    private readonly ObservableCollection<RedactMark> _marks = [];
    private IStudioHost? _host;
    private DocumentSession? _session;
    private DocumentSession? _marksSession;
    private PdfViewer? _viewer;

    public RedactPanel()
    {
        InitializeComponent();
        AreaList.ItemsSource = _marks;
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Redact";

    public string Description => "Permanently remove text and images in marked areas.";

    public string IconKey => "Icon.Redact";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host)
    {
        _host = host;
        host.DocumentClosed += (_, closed) =>
        {
            if (ReferenceEquals(closed, _marksSession)) ClearMarks();
        };
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;

        // Marks belong to one document; keep them while the same document comes back.
        if (!ReferenceEquals(session, _marksSession))
        {
            _marks.Clear();
            _marksSession = session;
        }

        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.PageRectangleSelected += OnPageRectangle;
            _viewer.OverlayLayoutChanged += OnOverlayLayoutChanged;
        }

        UpdateInteractionMode();
        UpdateState();
        RedrawOverlay();
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.PageRectangleSelected -= OnPageRectangle;
            _viewer.OverlayLayoutChanged -= OnOverlayLayoutChanged;
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
    }

    // ---- Public for tests and automation ----------------------------------

    public IReadOnlyList<RedactMark> Marks => _marks;

    public void AddMark(RedactArea area)
    {
        _marks.Add(new RedactMark { Area = area });
        Renumber();
        UpdateState();
        RedrawOverlay();
    }

    /// <summary>Applies all marks. With <paramref name="confirm"/> the user is asked first.</summary>
    public async Task<bool> ApplyAsync(bool confirm = true)
    {
        var session = _session;
        if (session is null || _marks.Count == 0) return false;

        if (confirm)
        {
            var pages = _marks.Select(m => m.Area.PageNumber).Distinct().Count();
            var message =
                $"Apply {_marks.Count} redaction(s) on {pages} page(s)?\n\n" +
                "All text, images and drawings inside the boxes are removed from the working copy permanently " +
                "(not just covered), and the boxes are painted black. Comments overlapping the boxes are removed too.\n\n" +
                "While the document is open, Undo (Ctrl+Z) brings back the earlier working copy. " +
                "Once you save, the removed content is gone from the saved file.";

            if (!AppSettings.ConfirmEdit(_host?.Window, message, "Apply redactions"))
            {
                return false;
            }
        }

        var areas = _marks.Select(m => m.Area).ToList();
        var password = session.Password;
        var ok = await ToolUi.ApplyAsync(_host, this, session,
            (input, output) => PdfContentOps.Redact(input, output, areas, password),
            areas.Count == 1 ? "Redact 1 area" : $"Redact {areas.Count} areas");

        if (ok) ClearMarks();
        return ok;
    }

    // ---- Events ------------------------------------------------------------

    private void OnPageRectangle(object? sender, PageRectEventArgs e)
    {
        if (MarkToggle.IsChecked != true || _session is null) return;
        AddMark(new RedactArea(e.PageNumber, (float)e.Left, (float)e.Bottom, (float)e.Width, (float)e.Height));
        _host?.SetStatus($"Marked area {_marks.Count} on page {e.PageNumber}. Apply redactions when ready.");
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // Pages can disappear (undo of an insert, delete pages): drop marks that no longer fit.
        if (_session is null) return;
        var count = _session.PageCount;
        foreach (var stale in _marks.Where(m => m.Area.PageNumber > count).ToList()) _marks.Remove(stale);
        Renumber();
        UpdateState();
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) => RedrawOverlay();

    private void OnMarkToggled(object sender, RoutedEventArgs e) => UpdateInteractionMode();

    private void OnAreaSelected(object sender, SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled = AreaList.SelectedItem is RedactMark;
        RedrawOverlay();
        if (AreaList.SelectedItem is RedactMark mark && _viewer is not null)
        {
            _viewer.BringIntoView(mark.Area.PageNumber, mark.Area.Left, mark.Area.Bottom + mark.Area.Height);
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (AreaList.SelectedItem is not RedactMark mark) return;
        _marks.Remove(mark);
        Renumber();
        UpdateState();
        RedrawOverlay();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e) => ClearMarks();

    private async void OnApplyClicked(object sender, RoutedEventArgs e) => await ApplyAsync();

    // ---- Helpers -----------------------------------------------------------

    private void ClearMarks()
    {
        _marks.Clear();
        UpdateState();
        RedrawOverlay();
    }

    private void Renumber()
    {
        var number = 0;
        foreach (var mark in _marks) mark.Number = ++number;
        AreaList.Items.Refresh();
    }

    private void UpdateInteractionMode()
    {
        if (_viewer is null) return;
        var marking = MarkToggle.IsChecked == true && _session is not null;
        _viewer.InteractionMode = marking ? InteractionMode.RectangleDrag : InteractionMode.Browse;
        if (marking) _host?.SetStatus("Redact: drag boxes over the content to remove.");
    }

    private void UpdateState()
    {
        var hasDocument = _session is not null;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        MarkToggle.IsEnabled = hasDocument;
        ClearButton.IsEnabled = _marks.Count > 0;
        ApplyButton.IsEnabled = hasDocument && _marks.Count > 0;
        RemoveButton.IsEnabled = AreaList.SelectedItem is RedactMark;
        CountText.Text = _marks.Count == 0
            ? "None yet."
            : $"{_marks.Count} area(s) on {_marks.Select(m => m.Area.PageNumber).Distinct().Count()} page(s).";
    }

    private void RedrawOverlay()
    {
        if (_viewer is null) return;
        _viewer.ClearOverlay(this);

        var selected = AreaList.SelectedItem as RedactMark;
        foreach (var mark in _marks)
        {
            var a = mark.Area;
            ToolUi.DrawBox(_viewer, this, a.PageNumber, a.Left, a.Bottom, a.Width, a.Height,
                dashed: !ReferenceEquals(mark, selected), filled: true, caption: "R" + mark.Number,
                thickness: ReferenceEquals(mark, selected) ? 3 : 2);
        }
    }
}
