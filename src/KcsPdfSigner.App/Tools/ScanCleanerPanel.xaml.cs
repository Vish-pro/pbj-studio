using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using KcsPdfSigner.Core.Scanning;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Scan Cleaner: photos of pages become clean A4 pages in one new PDF (page edges found and flattened,
/// straightened, background whitened, noise reduced, sharpened; colour, grey or black and white; optionally
/// searchable). The pictures are cleaned one at a time off the UI thread, with progress and Cancel. Works
/// with no document open; the finished PDF opens in a new tab.
/// </summary>
public partial class ScanCleanerPanel : UserControl, IToolPanel
{
    internal static readonly string[] PictureExtensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif"];

    private readonly ObservableCollection<ScanPicture> _pictures = [];
    private readonly List<OcrPanel.LanguageChoice> _languages = [];
    private IStudioHost? _host;
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _previewCancel;
    private int _previewVersion;
    private bool _loading = true;

    static ScanCleanerPanel()
    {
        // SkiaSharp cannot read TIFF or HEIC; Windows can (HEIC needs Microsoft's HEIF extension).
        ScanImageOps.ExternalDecoder = DecodeWithWindows;
    }

    public ScanCleanerPanel()
    {
        InitializeComponent();
        PictureList.ItemsSource = _pictures;
        LanguageList.ItemsSource = _languages;
        _pictures.CollectionChanged += (_, _) => UpdateState();
        _loading = false;
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public const string ScanCleanerTitle = "Scan Cleaner";

    public string Title => ScanCleanerTitle;

    public string Description => "Turn photos of pages into clean scanned PDF pages.";

    public string IconKey => "Icon.ScanClean";

    public FrameworkElement View => this;

    /// <summary>The pictures in the list, in order (for tests and automation).</summary>
    internal IReadOnlyList<ScanPicture> Pictures => _pictures;

    /// <summary>True while a run is going.</summary>
    public bool IsRunning => _cancel is not null;

    /// <summary>What the last run did; null before the first run.</summary>
    public ScanRunResult? LastResult { get; private set; }

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer) => UpdateState();

    public void Detach()
    {
        // A run keeps going in the background; the finished PDF opens when it is done.
    }

    // ---- Pictures ----------------------------------------------------------

    internal int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            _pictures.Add(new ScanPicture(path));
            added++;
        }

        if (added > 0 && PictureList.SelectedItem is null) PictureList.SelectedIndex = 0;
        return added;
    }

    private void OnAddPictures(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add pictures of pages",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.webp;*.heic;*.heif|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = PanelHelpers.DefaultFolder(_host?.ActiveSession)
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;
        var added = AddFiles(dialog.FileNames);
        if (added < dialog.FileNames.Length)
            _host?.SetStatus($"Skipped {dialog.FileNames.Length - added} file(s) that are not pictures.");
    }

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        foreach (var item in PictureList.SelectedItems.OfType<ScanPicture>().ToList()) _pictures.Remove(item);
    }

    private void OnClear(object sender, RoutedEventArgs e) => _pictures.Clear();

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int direction)
    {
        var selected = PictureList.SelectedItems.OfType<ScanPicture>().OrderBy(p => _pictures.IndexOf(p)).ToList();
        if (selected.Count == 0) return;
        if (direction > 0) selected.Reverse();
        foreach (var item in selected)
        {
            var index = _pictures.IndexOf(item);
            var target = index + direction;
            if (target < 0 || target >= _pictures.Count) break;
            if (selected.Contains(_pictures[target])) continue;
            _pictures.Move(index, target);
        }

        PictureList.Focus();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        e.Handled = true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
        _ = ShowPreviewAsync();
    }

    // ---- Options ------------------------------------------------------------

    private ScanCleanOptions BuildOptions(bool withOcr) => new()
    {
        Mode = BwRadio.IsChecked == true ? ScanColorMode.BlackAndWhite : GreyRadio.IsChecked == true ? ScanColorMode.Grey : ScanColorMode.Colour,
        FindPageEdges = EdgesCheck.IsChecked == true,
        Straighten = StraightenCheck.IsChecked == true,
        CleanBackground = BackgroundCheck.IsChecked == true,
        MakeSearchable = withOcr && OcrCheck.IsChecked == true,
        CropFor = CropSnapshot(),
        OcrLanguages = _languages.Where(l => l.IsSelected).Select(l => l.Code).DefaultIfEmpty(OcrLanguages.EnglishCode).ToList(),
    };

    /// <summary>The crop chosen for each picture, copied now so a background run can read it.</summary>
    private Func<string, int> CropSnapshot()
    {
        var choices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var picture in _pictures) choices.TryAdd(picture.Path, picture.CropIndex);
        return path => choices.TryGetValue(path, out var index) ? index : 0;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _ = ShowPreviewAsync();
    }

    private void OnOcrChanged(object sender, RoutedEventArgs e)
    {
        if (OcrCheck.IsChecked == true) LoadLanguages();
        UpdateState();
    }

    private void LoadLanguages()
    {
        var ticked = _languages.Count == 0
            ? new HashSet<string>([OcrLanguages.EnglishCode])
            : _languages.Where(l => l.IsSelected).Select(l => l.Code).ToHashSet();
        var installed = OcrLanguages.FindInstalled();
        var codes = OcrLanguages.ReadingLanguages.Select(l => l.Code).Where(installed.ContainsKey).ToList();
        _languages.Clear();
        foreach (var code in codes)
            _languages.Add(new OcrPanel.LanguageChoice(code, OcrLanguages.Find(code)?.Name ?? code) { IsSelected = ticked.Contains(code) });
        if (_languages.Count > 0 && !_languages.Any(l => l.IsSelected)) _languages[0].IsSelected = true;
        LanguageList.ItemsSource = null;
        LanguageList.ItemsSource = _languages;
    }

    private void OnLanguages(object sender, RoutedEventArgs e)
    {
        new OcrLanguagesDialog { Owner = _host?.Window }.ShowDialog();
        LoadLanguages();
    }

    // ---- Preview ------------------------------------------------------------

    /// <summary>Shows the first selected picture before and after cleaning (reduced size, off the UI thread).</summary>
    internal async Task ShowPreviewAsync()
    {
        _previewCancel?.Cancel();
        var picture = PictureList.SelectedItems.OfType<ScanPicture>().FirstOrDefault();
        var version = ++_previewVersion;
        if (picture is null)
        {
            BeforeImage.Source = null;
            AfterImage.Source = null;
            PreviewText.Text = _pictures.Count == 0 ? "Add pictures, then select one to see the clean-up." : "Select a picture to see the clean-up.";
            return;
        }

        var cancel = _previewCancel = new CancellationTokenSource();
        var options = BuildOptions(withOcr: false);
        PreviewText.Text = "Preparing the preview...";
        try
        {
            // Short pause: dragging through the list or ticking boxes quickly does not start a clean-up each time.
            await Task.Delay(150, cancel.Token);
            var cropIndex = picture.CropIndex;
            var (before, after, note, count, cropName) = await Task.Run(() =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                // The picture is read once and kept while you swipe through its crops.
                var cache = _previewCache;
                if (cache is null || !string.Equals(cache.Path, picture.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var source = ScanImageOps.Load(picture.Path, 2600);
                    cache = new PreviewCache(picture.Path, source, ScanImageOps.ToPng(ScanImageOps.DownscaleToFit(source, 700, out _)));
                    _previewCache = cache;
                }

                cancel.Token.ThrowIfCancellationRequested();
                var cleaned = ScanCleaner.Clean(cache.Source, options, 80, cropIndex);
                var afterPng = ScanImageOps.ToPng(cleaned.Image);
                var text = (cleaned.EdgesFound ? "Page edges found and flattened. " : "No page edge found; the whole picture is used. ")
                           + (Math.Abs(cleaned.SkewDegrees) >= 0.15 ? $"Straightened by {Math.Abs(cleaned.SkewDegrees):0.#} degrees." : "Text was already straight.");
                return (cache.BeforePng, afterPng, text, cleaned.CropCount, cleaned.CropName);
            }, cancel.Token);
            if (version != _previewVersion) return;
            BeforeImage.Source = ToImage(before);
            AfterImage.Source = ToImage(after);
            PreviewText.Text = note;
            picture.CropCount = count;
            picture.CropIndex = Math.Clamp(picture.CropIndex, 0, count - 1);
            CropText.Text = $"Crop {picture.CropIndex + 1} of {count}: {cropName}";
            CropBar.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            CropHint.Visibility = CropBar.Visibility;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _previewVersion) return;
            BeforeImage.Source = null;
            AfterImage.Source = null;
            PreviewText.Text = "This picture cannot be read: " + ex.Message;
        }
    }

    private sealed record PreviewCache(string Path, Raster Source, byte[] BeforePng);

    private PreviewCache? _previewCache;

    private void OnCropPrev(object sender, RoutedEventArgs e) => StepCrop(-1);

    private void OnCropNext(object sender, RoutedEventArgs e) => StepCrop(1);

    /// <summary>Shows the next or previous crop of the selected picture(s); the shown one is the one kept.</summary>
    private void StepCrop(int direction)
    {
        var selected = PictureList.SelectedItems.OfType<ScanPicture>().ToList();
        if (selected.Count == 0) return;
        var count = Math.Max(1, selected[0].CropCount);
        var next = ((selected[0].CropIndex + direction) % count + count) % count;
        foreach (var picture in selected) picture.CropIndex = next;
        _ = ShowPreviewAsync();
    }

    private System.Windows.Point _swipeStart;

    private void OnAfterDown(object sender, MouseButtonEventArgs e)
    {
        _swipeStart = e.GetPosition(AfterBorder);
        AfterBorder.Focus();
    }

    private void OnAfterUp(object sender, MouseButtonEventArgs e)
    {
        var dx = e.GetPosition(AfterBorder).X - _swipeStart.X;
        if (Math.Abs(dx) >= 30) StepCrop(dx < 0 ? 1 : -1);
    }

    private void OnAfterWheel(object sender, MouseWheelEventArgs e)
    {
        StepCrop(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnAfterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Right or Key.Down) StepCrop(1);
        else if (e.Key is Key.Left or Key.Up) StepCrop(-1);
        else return;
        e.Handled = true;
    }

    private static BitmapImage ToImage(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static byte[]? DecodeWithWindows(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Run ----------------------------------------------------------------

    private async void OnRun(object sender, RoutedEventArgs e) => await RunAsync(null);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Stopping after the current picture...";
    }

    /// <summary>Cleans every listed picture into one PDF. <paramref name="outputPath"/> null asks where to save. Returns true when a PDF was made.</summary>
    internal async Task<bool> RunAsync(string? outputPath)
    {
        if (IsRunning || _pictures.Count == 0) return false;
        var options = BuildOptions(withOcr: true);
        if (options.MakeSearchable)
        {
            var engine = PdfOcrOps.CheckEngine();
            if (engine.Length > 0)
            {
                PanelHelpers.ShowError(_host, ScanCleanerTitle, engine);
                return false;
            }
        }

        var files = _pictures.Select(p => p.Path).ToList();
        if (outputPath is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save the scanned PDF as",
                Filter = "PDF files (*.pdf)|*.pdf",
                InitialDirectory = Path.GetDirectoryName(files[0]) ?? PanelHelpers.DefaultFolder(null),
                FileName = "Scanned pages.pdf",
                AddExtension = true,
                DefaultExt = ".pdf"
            };
            if (dialog.ShowDialog(_host?.Window) != true) return false;
            outputPath = dialog.FileName;
        }

        if (files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
        {
            PanelHelpers.ShowInfo(_host, ScanCleanerTitle, "Choose a different file name: the PDF cannot replace one of the pictures.");
            return false;
        }

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        LastResult = null;
        ResultText.Visibility = Visibility.Collapsed;
        Progress.Maximum = files.Count;
        Progress.Value = 0;
        ProgressText.Text = $"Picture 1 of {files.Count}...";
        UpdateState();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Stage == "reading")
            {
                ProgressText.Text = $"Reading the text on page {p.Done + 1} of {p.Total}...";
                return;
            }

            Progress.Value = p.Done;
            if (p.Done < p.Total)
            {
                ProgressText.Text = $"Picture {p.Done + 1} of {p.Total}: {Path.GetFileName(p.File)}";
                _host?.SetStatus($"Scan Cleaner: picture {p.Done + 1} of {p.Total}...");
            }
        });

        var made = false;
        var output = outputPath;
        try
        {
            var result = await Task.Run(() => ScanCleaner.CreatePdf(files, output, options, progress, token), token);
            LastResult = result;
            made = true;
            ShowResult(Describe(result, output));
            _host?.SetStatus($"Scan Cleaner: {result.PagesWritten} page(s) saved to {Path.GetFileName(output)}.");
            _host?.OpenDocument(output);
        }
        catch (OperationCanceledException)
        {
            _host?.SetStatus("Scan Cleaner cancelled. No PDF was made.");
            ShowResult("Cancelled. No PDF was made.");
        }
        catch (Exception ex)
        {
            App.LogError("Scan Cleaner", ex);
            _host?.SetStatus("Scan Cleaner failed.");
            ShowResult("The PDF could not be made.");
            PanelHelpers.ShowError(_host, ScanCleanerTitle, "The PDF could not be made:\n\n" + ex.Message);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            UpdateState();
        }

        return made;
    }

    private static string Describe(ScanRunResult r, string output)
    {
        var lines = new List<string>
        {
            $"Made {Path.GetFileName(output)}: {r.PagesWritten} page(s), {PanelHelpers.FormatSize(r.OutputBytes)}.",
            r.PagesWithEdges == r.PagesWritten ? "The page edges were found in every picture."
                : $"The page edges were found in {r.PagesWithEdges} of {r.PagesWritten} pictures; the others were cleaned as a whole picture. Look at those pages.",
        };
        if (r.Failed.Count > 0)
            lines.Add($"{r.Failed.Count} picture(s) could not be read and were left out: " +
                      string.Join(", ", r.Failed.Take(5).Select(f => Path.GetFileName(f.File))) + (r.Failed.Count > 5 ? " and more." : "."));
        if (r.Ocr is not null)
            lines.Add(r.Ocr.PagesRead.Count == 0 ? "No text was found to make searchable."
                : $"Searchable: {r.Ocr.Words:N0} words read, average confidence {r.Ocr.MeanConfidence:0}%. Check names, amounts and dates.");
        return string.Join("\n", lines);
    }

    private void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.Visibility = Visibility.Visible;
    }

    private void UpdateState()
    {
        var running = IsRunning;
        var has = _pictures.Count > 0;
        var selected = PictureList.SelectedItems.Count > 0;
        RunButton.IsEnabled = has && !running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = selected && !running;
        UpButton.IsEnabled = selected && !running;
        DownButton.IsEnabled = selected && !running;
        ClearButton.IsEnabled = has && !running;
        PictureList.IsEnabled = !running;
        OcrLanguagesPanel.Visibility = OcrCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var engine = OcrCheck.IsChecked == true ? PdfOcrOps.CheckEngine() : "";
        EngineText.Text = engine;
        EngineText.Visibility = engine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = has ? $"{_pictures.Count} picture(s) = {_pictures.Count} page(s), in this order." : "No pictures yet.";
    }

    /// <summary>One picture in the list.</summary>
    internal sealed class ScanPicture
    {
        public ScanPicture(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
            }

            SizeText = PanelHelpers.FormatSize(size);
        }

        public string Path { get; }

        /// <summary>Which crop is kept for this picture (0 is the best guess).</summary>
        public int CropIndex { get; set; }

        /// <summary>How many crops were on offer when last looked at.</summary>
        public int CropCount { get; set; } = 1;

        public string Name { get; }

        public string SizeText { get; }
    }
}
