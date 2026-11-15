using System.IO;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Export: pages as PNG or JPG, the text as .txt, and Optimize (rewrite smaller, undoable) with
/// the size before and after.
/// </summary>
public partial class ExportPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private string? _lastOptimizeResult;

    public ExportPanel()
    {
        InitializeComponent();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Export";

    public string Description => "Save pages as images or text, or make the file smaller.";

    public string IconKey => "Icon.Export";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        if (_session is not null)
        {
            _session.DocumentChanged += OnDocumentChanged;
            if (string.IsNullOrWhiteSpace(FolderBox.Text)) FolderBox.Text = PanelHelpers.DefaultFolder(_session);
        }

        UpdateState();
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
        _lastOptimizeResult = null;
        UpdateState();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        var session = _session;
        NoDocumentText.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        Sections.IsEnabled = session is not null;

        if (session is null)
        {
            SizeText.Text = "";
            return;
        }

        long size = 0;
        try { size = new FileInfo(session.CurrentFile).Length; } catch { /* shown as 0 */ }
        SizeText.Text = _lastOptimizeResult ?? "Current size: " + PanelHelpers.FormatSize(size);
    }

    // ---- Images ------------------------------------------------------------

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Folder for the page images",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : PanelHelpers.DefaultFolder(_session)
        };
        if (dialog.ShowDialog(_host?.Window) == true) FolderBox.Text = dialog.FolderName;
    }

    private async void OnExportImages(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        List<int> pages;
        try
        {
            pages = PanelHelpers.ParsePageRange(PagesBox.Text, session.PageCount);
        }
        catch (FormatException ex)
        {
            PanelHelpers.ShowInfo(_host, "Export images", ex.Message);
            return;
        }

        if (!int.TryParse(DpiBox.Text.Trim(), out var dpi) || dpi < 10 || dpi > 1200)
        {
            PanelHelpers.ShowInfo(_host, "Export images", "DPI must be a whole number from 10 to 1200.");
            return;
        }

        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            PanelHelpers.ShowInfo(_host, "Export images", "Choose a folder for the images.");
            return;
        }

        var format = FormatBox.SelectedIndex == 1 ? "jpg" : "png";
        var password = session.Password;
        var scratch = PanelHelpers.NewScratchFolder("export");
        List<string> written = [];
        try
        {
            // Named copy so the images are called "Report_page001.png", not "004_page001.png".
            var copy = PanelHelpers.NamedCopy(session, scratch);
            var ok = await PanelHelpers.RunBusyAsync(_host, this, "Export images",
                async () => written = await Task.Run(() =>
                    PdfExportOps.ExportImages(copy, folder, format, dpi, pages, password)));
            if (!ok) return;
        }
        finally
        {
            PanelHelpers.TryDeleteFolder(scratch);
        }

        _host?.SetStatus($"Exported {written.Count} image(s).");
        PanelHelpers.ShowInfo(_host, "Export images", $"Wrote {written.Count} {format.ToUpperInvariant()} file(s) to:\n{folder}");
    }

    // ---- Text --------------------------------------------------------------

    private async void OnExportText(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export text to",
            Filter = "Text files (*.txt)|*.txt",
            InitialDirectory = PanelHelpers.DefaultFolder(session),
            FileName = Path.GetFileNameWithoutExtension(session.FileName) + ".txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var target = dialog.FileName;
        var file = session.CurrentFile;
        var password = session.Password;
        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Export text",
            () => Task.Run(() => PdfExportOps.ExportText(file, target, password)));
        if (ok) _host?.SetStatus("Text exported to " + Path.GetFileName(target) + ".");
    }

    // ---- Optimize ----------------------------------------------------------

    private async void OnOptimize(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var before = new FileInfo(session.CurrentFile).Length;
        var password = session.Password;
        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Optimize",
            () => session.ApplyAsync((input, output) => PdfExportOps.Optimize(input, output, password), "Optimize"));
        if (!ok || !ReferenceEquals(session, _session)) return;

        long after;
        try
        {
            after = new FileInfo(session.CurrentFile).Length;
        }
        catch (IOException)
        {
            return; // The document was closed meanwhile.
        }

        var change = before > 0 ? (before - after) * 100.0 / before : 0;
        _lastOptimizeResult = $"Before: {PanelHelpers.FormatSize(before)}\nAfter: {PanelHelpers.FormatSize(after)} " +
                              (change >= 0 ? $"({change:0.#}% smaller)" : $"({-change:0.#}% larger; Ctrl+Z undoes it)");
        UpdateState();
        _lastOptimizeResult = null;
        _host?.SetStatus($"Optimized: {PanelHelpers.FormatSize(before)} -> {PanelHelpers.FormatSize(after)}.");
    }
}
