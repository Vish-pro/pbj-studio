using System.IO;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>Properties: file facts from <see cref="PdfInfoReader.Read"/> and editable metadata.</summary>
public partial class PropertiesPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private DocumentInfo? _info;
    private bool _loading;

    public PropertiesPanel()
    {
        InitializeComponent();
        Reload();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Properties";

    public string Description => "File details, and title, author, subject and keywords.";

    public string IconKey => "Icon.Info";

    public FrameworkElement View => this;

    /// <summary>The facts shown, as label/value pairs (for tests and automation).</summary>
    internal List<(string Label, string Value)> Facts { get; private set; } = [];

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        Reload();
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        Reload();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => Reload();

    // ---- Display -----------------------------------------------------------

    private void Reload()
    {
        var session = _session;
        _info = null;
        Facts = [];

        if (session is not null)
        {
            try
            {
                _info = PdfInfoReader.Read(session.CurrentFile, session.Password);
            }
            catch (Exception ex)
            {
                Facts = [("Error", ex.Message)];
            }
        }

        NoDocumentText.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        Sections.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;

        if (session is not null && _info is not null)
        {
            var info = _info;
            Facts =
            [
                ("File", session.OriginalPath),
                ("Size", PanelHelpers.FormatSize(info.FileSizeBytes) + (session.IsDirty ? " (with unsaved changes)" : "")),
                ("Pages", info.PageCount.ToString()),
                ("PDF version", string.IsNullOrEmpty(info.PdfVersion) ? "-" : info.PdfVersion),
                ("Encrypted", info.IsEncrypted ? "Yes" : "No"),
                ("Producer", Or(info.Producer)),
                ("Creator", Or(info.Creator)),
                ("Created", info.Created?.ToString("dd MMM yyyy HH:mm") ?? "-"),
                ("Modified", info.Modified?.ToString("dd MMM yyyy HH:mm") ?? "-")
            ];
        }

        FillGrid();
        ResetFields();
    }

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private void FillGrid()
    {
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();

        for (var row = 0; row < Facts.Count; row++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = Facts[row].Label,
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 12, 2)
            };
            var value = new TextBox
            {
                Text = Facts[row].Value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };

            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            InfoGrid.Children.Add(label);
            InfoGrid.Children.Add(value);
        }
    }

    private void ResetFields()
    {
        _loading = true;
        try
        {
            TitleBox.Text = _info?.Title ?? "";
            AuthorBox.Text = _info?.Author ?? "";
            SubjectBox.Text = _info?.Subject ?? "";
            KeywordsBox.Text = _info?.Keywords ?? "";
        }
        finally
        {
            _loading = false;
        }

        UpdateButtons();
    }

    private bool HasEdits =>
        _info is not null &&
        (TitleBox.Text != _info.Title || AuthorBox.Text != _info.Author ||
         SubjectBox.Text != _info.Subject || KeywordsBox.Text != _info.Keywords);

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = HasEdits;
        ResetButton.IsEnabled = HasEdits;
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateButtons();
    }

    private void OnReset(object sender, RoutedEventArgs e) => ResetFields();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || !HasEdits) return;

        var title = TitleBox.Text;
        var author = AuthorBox.Text;
        var subject = SubjectBox.Text;
        var keywords = KeywordsBox.Text;
        var password = session.Password;

        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Edit properties",
            () => session.ApplyAsync(
                (input, output) => PdfInfoReader.SetMetadata(input, output, title, author, subject, keywords, password),
                "Edit properties"));
        if (ok) _host?.SetStatus("Properties updated. Save the document to keep them; Ctrl+Z undoes it.");
    }
}
