using System.IO;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Edit: adds new text, images, a watermark, and header / footer / page numbers to the pages.
/// Uses PdfContentOps. Existing text is not edited in place (out of scope).
/// </summary>
public partial class EditPanel : UserControl, IToolPanel
{
    private enum Placing { None, Text, Image }

    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private Placing _placing;
    private bool _busy;

    public EditPanel()
    {
        InitializeComponent();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Edit";

    public string Description => "Add text, images, watermark, header and footer.";

    public string IconKey => "Icon.EditText";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;

        if (_viewer is not null)
        {
            _viewer.PageClick += OnPageClick;
            _viewer.PageRectangleSelected += OnPageRectangle;
        }

        SetPlacing(Placing.None);
        UpdateState();
    }

    public void Detach()
    {
        if (_viewer is not null)
        {
            _viewer.PageClick -= OnPageClick;
            _viewer.PageRectangleSelected -= OnPageRectangle;
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        _placing = Placing.None;
        PlaceTextToggle.IsChecked = false;
        PlaceImageToggle.IsChecked = false;
    }

    /// <summary>
    /// Ticks "Page numbers" and scrolls to it, ready for "Add to every page" (the suggestion bar shown
    /// after merging pages uses this).
    /// </summary>
    public void ShowPageNumbersOption()
    {
        PageNumbersCheck.IsChecked = true;
        PageNumbersCheck.BringIntoView();
        PageNumbersCheck.Focus();
        _host?.SetStatus("Page numbers ticked: press 'Add to every page' in the Edit tool.");
    }

    // ---- Operations (public for tests and automation) ----------------------

    public async Task<bool> AddTextAsync(int page, float x, float y, string text, float fontSize)
    {
        var session = _session;
        if (session is null || _busy || string.IsNullOrWhiteSpace(text)) return false;
        var password = session.Password;
        var content = text.Replace("\r\n", "\n");

        return await Run(session, (input, output) =>
            PdfContentOps.AddText(input, output, page, x, y, content, fontSize, password), "Add text");
    }

    public async Task<bool> AddImageAsync(int page, string imagePath, float left, float bottom, float width, float height)
    {
        var session = _session;
        if (session is null || _busy) return false;
        var password = session.Password;

        return await Run(session, (input, output) =>
            PdfContentOps.AddImage(input, output, page, imagePath, left, bottom, width, height, password), "Add image");
    }

    public async Task<bool> AddWatermarkAsync(string text, float opacity, float rotation, float fontSize)
    {
        var session = _session;
        if (session is null || _busy || string.IsNullOrWhiteSpace(text)) return false;
        var password = session.Password;

        return await Run(session, (input, output) =>
            PdfContentOps.AddWatermark(input, output, text, opacity, rotation, fontSize, password), "Add watermark");
    }

    public async Task<bool> AddHeaderFooterAsync(string? header, string? footer, bool pageNumbers)
    {
        var session = _session;
        if (session is null || _busy) return false;
        if (string.IsNullOrWhiteSpace(header) && string.IsNullOrWhiteSpace(footer) && !pageNumbers) return false;
        var password = session.Password;
        var h = string.IsNullOrWhiteSpace(header) ? null : header;
        var f = string.IsNullOrWhiteSpace(footer) ? null : footer;

        return await Run(session, (input, output) =>
            PdfContentOps.AddHeaderFooter(input, output, h, f, pageNumbers, password), "Add header and footer");
    }

    private async Task<bool> Run(DocumentSession session, Action<string, string> operation, string description)
    {
        _busy = true;
        try
        {
            return await ToolUi.ApplyAsync(_host, this, session, operation, description);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- Placing on the page -----------------------------------------------

    private void SetPlacing(Placing placing)
    {
        _placing = placing;
        PlaceTextToggle.IsChecked = placing == Placing.Text;
        PlaceImageToggle.IsChecked = placing == Placing.Image;

        if (_viewer is null) return;
        _viewer.InteractionMode = placing switch
        {
            Placing.Text => InteractionMode.PointClick,
            Placing.Image => InteractionMode.RectangleDrag,
            _ => InteractionMode.Browse
        };

        if (placing == Placing.Text) _host?.SetStatus("Add text: click on the page where the text should start.");
        if (placing == Placing.Image) _host?.SetStatus("Add image: drag a box on the page.");
    }

    private void OnPlaceTextToggled(object sender, RoutedEventArgs e)
    {
        if (PlaceTextToggle.IsChecked == true && string.IsNullOrWhiteSpace(AddTextBox.Text))
        {
            ToolUi.ShowInfo(_host, "Type the text first.");
            PlaceTextToggle.IsChecked = false;
            AddTextBox.Focus();
            return;
        }

        SetPlacing(PlaceTextToggle.IsChecked == true ? Placing.Text : Placing.None);
    }

    private void OnPlaceImageToggled(object sender, RoutedEventArgs e)
    {
        if (PlaceImageToggle.IsChecked == true && !File.Exists(ImagePathBox.Text))
        {
            PlaceImageToggle.IsChecked = false;
            if (!BrowseImage()) return;
            PlaceImageToggle.IsChecked = true;
        }

        SetPlacing(PlaceImageToggle.IsChecked == true ? Placing.Image : Placing.None);
    }

    private async void OnPageClick(object? sender, PageMouseEventArgs e)
    {
        if (_placing != Placing.Text || _busy) return;
        var size = ToolUi.ParseFloat(TextSizeBox.Text, 12f, 2f, 400f);
        if (await AddTextAsync(e.PageNumber, (float)e.X, (float)e.Y, AddTextBox.Text, size))
        {
            SetPlacing(Placing.None);
        }
    }

    private async void OnPageRectangle(object? sender, PageRectEventArgs e)
    {
        if (_placing != Placing.Image || _busy) return;
        var path = ImagePathBox.Text;
        if (!File.Exists(path)) return;

        if (await AddImageAsync(e.PageNumber, path, (float)e.Left, (float)e.Bottom, (float)e.Width, (float)e.Height))
        {
            SetPlacing(Placing.None);
        }
    }

    private void OnBrowseImage(object sender, RoutedEventArgs e) => BrowseImage();

    private bool BrowseImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(_host?.Window) != true) return false;
        ImagePathBox.Text = dialog.FileName;
        return true;
    }

    // ---- Whole-document operations -----------------------------------------

    private async void OnWatermarkClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WatermarkText.Text))
        {
            ToolUi.ShowInfo(_host, "Type the watermark text first.");
            return;
        }

        var opacity = (float)(OpacitySlider.Value / 100.0);
        var rotation = ToolUi.ParseFloat(RotationBox.Text, 45f, -360f, 360f);
        var size = ToolUi.ParseFloat(WatermarkSizeBox.Text, 60f, 4f, 400f);
        await AddWatermarkAsync(WatermarkText.Text.Trim(), opacity, rotation, size);
    }

    private async void OnHeaderFooterClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HeaderText.Text) && string.IsNullOrWhiteSpace(FooterText.Text) &&
            PageNumbersCheck.IsChecked != true)
        {
            ToolUi.ShowInfo(_host, "Type a header or footer, or tick page numbers.");
            return;
        }

        await AddHeaderFooterAsync(HeaderText.Text, FooterText.Text, PageNumbersCheck.IsChecked == true);
    }

    private void UpdateState()
    {
        var hasDocument = _session is not null;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        foreach (var child in Body.Children.OfType<FrameworkElement>())
        {
            if (!ReferenceEquals(child, NoDocumentText)) child.IsEnabled = hasDocument;
        }
    }
}
