using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Combine Files: joins PDFs and images, in the listed order, into one new PDF that then opens in
/// a new tab. Works with no document open.
/// </summary>
public partial class CombinePanel : UserControl, IToolPanel
{
    internal static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    private readonly ObservableCollection<CombineEntry> _entries = [];
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;

    public CombinePanel()
    {
        InitializeComponent();
        FileList.ItemsSource = _entries;
        _entries.CollectionChanged += (_, _) => UpdateState();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Combine Files";

    public string Description => "Join PDFs and images into one PDF.";

    public string IconKey => "Icon.Combine";

    public FrameworkElement View => this;

    /// <summary>The files in the list, in order (for tests and automation).</summary>
    internal IReadOnlyList<CombineEntry> Entries => _entries;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        UpdateState();
    }

    public void Detach()
    {
        if (_viewer is not null)
        {
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        UpdateState();
    }

    // ---- List editing ------------------------------------------------------

    /// <summary>Adds files, skipping anything that is not a PDF or a supported image.</summary>
    internal int AddFiles(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var isPdf = extension == ".pdf";
            if (!isPdf && !ImageExtensions.Contains(extension)) continue;

            _entries.Add(new CombineEntry(Path.GetFullPath(file), isPdf, null, Path.GetFileName(file)));
            added++;
        }

        return added;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files to combine",
            Filter = "PDFs and images|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|PDF files (*.pdf)|*.pdf|" +
                     "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = PanelHelpers.DefaultFolder(_session ?? _host?.ActiveSession)
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var added = AddFiles(dialog.FileNames);
        if (added < dialog.FileNames.Length)
            _host?.SetStatus($"Skipped {dialog.FileNames.Length - added} file(s) that are not PDFs or supported images.");
    }

    private void OnAddCurrent(object sender, RoutedEventArgs e)
    {
        var session = _session ?? _host?.ActiveSession;
        if (session is null) return;

        try
        {
            // Snapshot the working file so later edits in the tab do not change what gets combined.
            var folder = PanelHelpers.NewScratchFolder("combine-src");
            var copy = PanelHelpers.NamedCopy(session, folder);
            _entries.Add(new CombineEntry(copy, true, session.Password, session.FileName) { ScratchFolder = folder });
        }
        catch (Exception ex)
        {
            PanelHelpers.ShowError(_host, "Combine Files", ex.Message);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        foreach (var entry in FileList.SelectedItems.OfType<CombineEntry>().ToList())
        {
            _entries.Remove(entry);
            PanelHelpers.TryDeleteFolder(entry.ScratchFolder);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries) PanelHelpers.TryDeleteFolder(entry.ScratchFolder);
        _entries.Clear();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int step)
    {
        var selected = FileList.SelectedItems.OfType<CombineEntry>().ToList();
        if (selected.Count == 0) return;

        var indices = selected.Select(s => _entries.IndexOf(s)).OrderBy(i => i).ToList();
        if (step < 0)
        {
            if (indices[0] == 0) return;
            foreach (var i in indices) _entries.Move(i, i - 1);
        }
        else
        {
            if (indices[^1] == _entries.Count - 1) return;
            for (var k = indices.Count - 1; k >= 0; k--) _entries.Move(indices[k], indices[k] + 1);
        }

        FileList.SelectedItems.Clear();
        foreach (var entry in selected) FileList.SelectedItems.Add(entry);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        AddFiles(files);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (!IsInitialized || CombineButton is null) return;

        var hasSelection = FileList.SelectedItems.Count > 0;
        RemoveButton.IsEnabled = hasSelection;
        UpButton.IsEnabled = hasSelection;
        DownButton.IsEnabled = hasSelection;
        ClearButton.IsEnabled = _entries.Count > 0;
        AddCurrentButton.IsEnabled = (_session ?? _host?.ActiveSession) is not null;
        CombineButton.IsEnabled = _entries.Count > 0;

        var pdfs = _entries.Count(x => x.IsPdf);
        var images = _entries.Count - pdfs;
        SummaryText.Text = _entries.Count == 0
            ? "Add at least one file."
            : $"{pdfs} PDF(s), {images} image(s).";
    }

    // ---- Combine -----------------------------------------------------------

    private async void OnCombine(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) return;

        // Passwords are asked for here, on the UI thread, before the background work starts.
        var passwords = new Dictionary<CombineEntry, string?>();
        foreach (var entry in _entries.Where(x => x.IsPdf))
        {
            var password = entry.Password;
            try
            {
                if (password is null && PdfInfoReader.NeedsPassword(entry.Path))
                {
                    password = MessageDialog.Prompt(_host?.Window,
                        $"'{entry.Name}' is password protected. Enter its permissions (owner) password so its pages can be copied.",
                        "Password required", isPassword: true);
                    if (password is null) return;
                }
            }
            catch (Exception ex)
            {
                PanelHelpers.ShowError(_host, "Combine Files", $"'{entry.Name}': {ex.Message}");
                return;
            }

            passwords[entry] = password;
        }

        var first = _entries[0];
        var dialog = new SaveFileDialog
        {
            Title = "Save the combined PDF as",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = first.ScratchFolder is null
                ? Path.GetDirectoryName(first.Path) ?? PanelHelpers.DefaultFolder(null)
                : PanelHelpers.DefaultFolder(_session ?? _host?.ActiveSession),
            FileName = "Combined.pdf",
            AddExtension = true,
            DefaultExt = ".pdf"
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var output = dialog.FileName;
        var entries = _entries.ToList();
        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Combine files",
            () => Task.Run(() => CombineTo(entries, passwords, output)));
        if (!ok) return;

        _host?.SetStatus($"Combined {entries.Count} file(s) into {Path.GetFileName(output)}.");
        // Marked as a fresh merge: the suggestion bar then offers page numbers.
        _host?.OpenDocument(output)?.MarkOrigin(KcsPdfSigner.Core.Documents.DocumentOrigin.Merged);
    }

    /// <summary>
    /// Converts each run of consecutive images to one PDF, decrypts protected PDFs into a scratch
    /// folder (Core's Merge takes no passwords), then merges everything in order.
    /// </summary>
    internal static void CombineTo(IList<CombineEntry> entries, IReadOnlyDictionary<CombineEntry, string?> passwords, string output)
    {
        var scratch = PanelHelpers.NewScratchFolder("combine");
        try
        {
            var inputs = new List<string>();
            var images = new List<string>();
            var counter = 0;

            void FlushImages()
            {
                if (images.Count == 0) return;
                var file = Path.Combine(scratch, $"images{++counter:000}.pdf");
                PdfPageOps.ImagesToPdf(images, file);
                inputs.Add(file);
                images.Clear();
            }

            foreach (var entry in entries)
            {
                if (!entry.IsPdf)
                {
                    images.Add(entry.Path);
                    continue;
                }

                FlushImages();
                passwords.TryGetValue(entry, out var password);
                if (!string.IsNullOrEmpty(password))
                {
                    var plain = Path.Combine(scratch, $"pdf{++counter:000}.pdf");
                    PdfSecurityOps.Decrypt(entry.Path, plain, password);
                    inputs.Add(plain);
                }
                else
                {
                    inputs.Add(entry.Path);
                }
            }

            FlushImages();

            // Write next to the target first so a failure never leaves a half-written file.
            var staging = Path.Combine(scratch, "combined.pdf");
            PdfPageOps.Merge(inputs, staging);
            File.Copy(staging, output, overwrite: true);
        }
        finally
        {
            PanelHelpers.TryDeleteFolder(scratch);
        }
    }

    /// <summary>One file in the combine list.</summary>
    public sealed class CombineEntry(string path, bool isPdf, string? password, string name)
    {
        public string Path { get; } = path;

        public bool IsPdf { get; } = isPdf;

        /// <summary>Known password (for a snapshot of an open encrypted document). Never shown.</summary>
        public string? Password { get; } = password;

        public string Name { get; } = name;

        public string Folder => ScratchFolder is null ? System.IO.Path.GetDirectoryName(Path) ?? "" : "(open document)";

        public string Kind => IsPdf ? "PDF" : "Image";

        /// <summary>Scratch folder holding a snapshot copy; deleted when the entry is removed.</summary>
        public string? ScratchFolder { get; init; }
    }
}
