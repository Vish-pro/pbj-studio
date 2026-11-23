using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Navigation;

/// <summary>A row in the Bookmarks tree.</summary>
public sealed class NavNode
{
    public required string Title { get; init; }
    public int PageNumber { get; init; }
    public List<NavNode> Children { get; init; } = [];
}

/// <summary>A row in the Signatures or Comments list.</summary>
public sealed class NavItem
{
    public required string Title { get; init; }
    public string Detail { get; init; } = string.Empty;
    public int PageNumber { get; init; }

    /// <summary>Optional area to bring into view (unrotated PDF points).</summary>
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>Whatever the producer wants to keep with the row (e.g. a Core info object).</summary>
    public object? Data { get; init; }

    /// <summary>Status glyph shown before the row (signatures only).</summary>
    public System.Windows.Media.Geometry? Glyph { get; init; }
}

/// <summary>Page thumbnail, rendered lazily the first time the list shows it.</summary>
public sealed class ThumbItem : INotifyPropertyChanged
{
    private readonly Action<ThumbItem> _request;
    private BitmapSource? _image;
    private bool _requested;

    public ThumbItem(PdfPageInfo page, double width, double height, Action<ThumbItem> request)
    {
        Page = page;
        Width = width;
        Height = height;
        _request = request;
    }

    public PdfPageInfo Page { get; }
    public int PageNumber => Page.PageNumber;
    public double Width { get; }
    public double Height { get; }

    public BitmapSource? Image
    {
        get
        {
            if (_image is null && !_requested)
            {
                _requested = true;
                _request(this);
            }

            return _image;
        }
        set
        {
            _image = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Left navigation pane: Pages (thumbnails), Bookmarks, Signatures, Comments.
/// Each tab has its own Load method; MainWindow calls <see cref="LoadAll"/> when the active
/// document or its content changes.
/// </summary>
public partial class NavigationPane : UserControl
{
    private const double ThumbWidth = 116;

    private readonly ObservableCollection<ThumbItem> _thumbs = [];
    private readonly Queue<ThumbItem> _thumbQueue = new();
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private int _generation;
    private bool _pumping;
    private bool _syncing;

    public NavigationPane()
    {
        InitializeComponent();
        ThumbList.ItemsSource = _thumbs;
    }

    /// <summary>Raised when the user picks a page in any tab.</summary>
    public event EventHandler<int>? PageRequested;

    /// <summary>Shows a document (or nothing) and fills every tab.</summary>
    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        if (_viewer is not null) _viewer.CurrentPageChanged -= OnViewerPageChanged;
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;

        _session = session;
        _viewer = viewer;

        if (_viewer is not null) _viewer.CurrentPageChanged += OnViewerPageChanged;
        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;

        LoadAll();
    }

    public void LoadAll()
    {
        LoadPages();
        LoadBookmarks();
        LoadSignatures();
        LoadComments();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => LoadAll();

    // ---- Pages -------------------------------------------------------------

    /// <summary>Rebuilds the thumbnail list; images render on demand as rows come into view.</summary>
    public void LoadPages()
    {
        _generation++;
        _thumbQueue.Clear();
        _thumbs.Clear();
        if (_session is null) return;

        foreach (var page in _session.Pages)
        {
            var (width, height) = PreviewGeometry.RotatedSize(page);
            var thumbHeight = width > 0 ? ThumbWidth * height / width : ThumbWidth * 1.414;
            _thumbs.Add(new ThumbItem(page, ThumbWidth, Math.Round(thumbHeight), RequestThumb));
        }

        SyncSelection();
    }

    private void RequestThumb(ThumbItem item)
    {
        _thumbQueue.Enqueue(item);
        // Bindings call this during layout; start the worker afterwards.
        Dispatcher.BeginInvoke(PumpThumbs, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void PumpThumbs()
    {
        if (_pumping) return;
        _pumping = true;

        try
        {
            while (_thumbQueue.Count > 0)
            {
                var item = _thumbQueue.Dequeue();
                var bytes = _viewer?.DocumentBytes;
                var password = _viewer?.DocumentPassword;
                if (bytes is null) break;

                var generation = _generation;
                var (width, _) = PreviewGeometry.RotatedSize(item.Page);
                var scale = VisualTreeHelperDpi();
                var dpi = (int)Math.Max(8, Math.Round(ThumbWidth * scale / Math.Max(width / 72.0, 0.1)));
                var pageNumber = item.PageNumber;

                try
                {
                    var bitmap = await Task.Run(() => PageRenderer.Render(bytes, pageNumber, password, dpi));
                    if (generation == _generation) item.Image = bitmap;
                }
                catch (Exception ex)
                {
                    // A stale request (the document changed while it was queued) is expected.
                    if (generation == _generation) App.LogError($"Thumbnail of page {pageNumber}", ex);
                }
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private double VisualTreeHelperDpi()
    {
        try { return System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX; }
        catch { return 1.0; }
    }

    private void OnViewerPageChanged(object? sender, EventArgs e) => SyncSelection();

    private void SyncSelection()
    {
        if (_viewer is null || _viewer.CurrentPage < 1 || _viewer.CurrentPage > _thumbs.Count) return;
        _syncing = true;
        try
        {
            ThumbList.SelectedIndex = _viewer.CurrentPage - 1;
            if (ThumbList.SelectedItem is not null) ThumbList.ScrollIntoView(ThumbList.SelectedItem);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThumbList.SelectedItem is not ThumbItem item) return;
        PageRequested?.Invoke(this, item.PageNumber);
    }

    // ---- Bookmarks ---------------------------------------------------------

    private int _bookmarkGeneration;
    private int _signatureGeneration;
    private int _commentGeneration;

    /// <summary>
    /// Fills the Bookmarks tree from Core's PdfInfoReader.ReadOutline, read off the UI thread.
    /// </summary>
    public async void LoadBookmarks()
    {
        var generation = ++_bookmarkGeneration;
        if (_session is null)
        {
            ShowBookmarks([], "No document open.");
            return;
        }

        var file = _session.CurrentFile;
        var password = _session.Password;
        ShowBookmarks([], "Reading bookmarks...");

        try
        {
            var outline = await Task.Run(() => PdfInfoReader.ReadOutline(file, password));
            if (generation != _bookmarkGeneration) return;
            ShowBookmarks(outline.Select(ToNode).ToList());
        }
        catch (Exception ex)
        {
            if (generation != _bookmarkGeneration) return;
            App.LogError("Read bookmarks", ex);
            ShowBookmarks([], "Could not read the bookmarks: " + ex.Message);
        }
    }

    private static NavNode ToNode(OutlineItem item) => new()
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title.Trim(),
        PageNumber = item.PageNumber,
        Children = item.Children.Select(ToNode).ToList()
    };

    public void ShowBookmarks(IReadOnlyList<NavNode> nodes, string emptyMessage = "This document has no bookmarks.")
    {
        BookmarkTree.ItemsSource = nodes;
        BookmarksEmpty.Text = nodes.Count == 0 ? emptyMessage : string.Empty;
        BookmarksEmpty.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBookmarkSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavNode { PageNumber: > 0 } node) PageRequested?.Invoke(this, node.PageNumber);
    }

    // ---- Signatures --------------------------------------------------------

    private DocumentVerification? _verification;

    /// <summary>Raised after "Validate all" started a fresh check, so the banner and badges follow.</summary>
    public event EventHandler? SignaturesRechecked;

    /// <summary>The last finished check shown in the Signatures tab (null while checking).</summary>
    public DocumentVerification? Verification => _verification;

    /// <summary>
    /// Fills the Signatures list from Core's SignatureVerifier (shared, cached per document
    /// version by <see cref="SignatureChecks"/>), off the UI thread: status glyph, signer, status
    /// and the facts that decided it, then the signature fields not signed yet.
    /// </summary>
    public async void LoadSignatures()
    {
        var generation = ++_signatureGeneration;
        _verification = null;
        SignatureSummary.Visibility = Visibility.Collapsed;
        if (_session is null)
        {
            ShowSignatures([], "No document open.");
            return;
        }

        var session = _session;
        ShowSignatures([], "Checking signatures...");

        try
        {
            var result = await SignatureChecks.GetAsync(session);
            if (generation != _signatureGeneration) return;
            _verification = result;

            var items = result.Signatures.Select(ToSignatureItem).ToList();
            items.AddRange(result.UnsignedFields.Select(field => new NavItem
            {
                Title = "Not signed yet: " + field.FieldName,
                Detail = field.PageNumber > 0 ? $"Empty signature field on page {field.PageNumber}" : "Empty signature field (not on a page)",
                PageNumber = field.PageNumber,
                X = field.Left,
                Y = field.Bottom + field.Height,
                Glyph = TryFindResource("Icon.SigField") as System.Windows.Media.Geometry,
                Data = field
            }));

            SignatureSummary.Text = string.IsNullOrEmpty(result.BannerText) ? "" : result.BannerText + "\n" + result.BannerDetail;
            SignatureSummary.Visibility = string.IsNullOrEmpty(result.BannerText) ? Visibility.Collapsed : Visibility.Visible;
            ShowSignatures(items, string.IsNullOrEmpty(result.DocumentError) ? "This document is not signed." : result.DocumentError);
        }
        catch (Exception ex)
        {
            if (generation != _signatureGeneration) return;
            App.LogError("Check signatures", ex);
            ShowSignatures([], "Could not check the signatures: " + ex.Message);
        }
    }

    private NavItem ToSignatureItem(SignatureVerification info)
    {
        var lines = new List<string> { SignatureUi.StatusWord(info.Status) + (info.IsDocumentTimestamp ? " (document timestamp)" : "") };

        lines.Add(info.Changes switch
        {
            _ when !info.IntegrityOk => "Changed or damaged since signing",
            ChangesAfterSigning.None => "Not modified since signing",
            ChangesAfterSigning.AllowedOnly => "Later changes are allowed ones (signatures, form fill-in, comments)",
            ChangesAfterSigning.NotAllowed => "Changed after signing in a way that is not allowed",
            _ => "Changed after signing; changes could not be classified"
        });

        if (info.IntegrityOk && info.Certificate is not null)
            lines.Add(info.IdentityTrusted ? "Signer's identity is valid" : "Signer's identity is unknown");
        if (info.TimestampTime is { } stamp && !info.IsDocumentTimestamp)
            lines.Add($"Timestamped {stamp:dd MMM yyyy HH:mm}" + (info.TimestampTrusted ? "" : " (authority not trusted)"));
        else if (info.ClaimedSigningTime is { } date)
            lines.Add("Signed " + date.ToString("dd MMM yyyy HH:mm") + " (signer's computer clock)");
        if (info.CertificationLevel > 0) lines.Add("Certified: " + DocumentVerification.CertificationDescription(info.CertificationLevel));
        if (!string.IsNullOrWhiteSpace(info.Reason)) lines.Add("Reason: " + info.Reason);
        lines.Add(info.PageNumber > 0 ? $"Field {info.FieldName}, page {info.PageNumber}" : $"Field {info.FieldName} (invisible)");

        return new NavItem
        {
            Title = (string.IsNullOrWhiteSpace(info.SignerName) ? "(unknown signer)" : info.SignerName),
            Detail = string.Join("\n", lines),
            PageNumber = info.PageNumber,
            X = info.Left,
            Y = info.Bottom + info.Height,
            Glyph = TryFindResource(SignatureUi.GlyphKey(info.Status)) as System.Windows.Media.Geometry,
            Data = info
        };
    }

    public void ShowSignatures(IReadOnlyList<NavItem> items, string emptyMessage = "This document is not signed.")
    {
        SignatureList.ItemsSource = items;
        SignaturesEmpty.Text = items.Count == 0 ? emptyMessage : string.Empty;
        SignaturesEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Brings the Signatures tab to the front.</summary>
    public void ShowSignaturesTab() => Tabs.SelectedItem = SignaturesTab;

    /// <summary>Checks every signature again (fresh, not from the cache) and refreshes the list.</summary>
    public void ValidateAll()
    {
        if (_session is null) return;
        _ = SignatureChecks.GetAsync(_session, force: true);
        LoadSignatures();
        SignaturesRechecked?.Invoke(this, EventArgs.Empty);
    }

    private void OnValidateAll(object sender, RoutedEventArgs e) => ValidateAll();

    private void OnTrustedCertificates(object sender, RoutedEventArgs e) =>
        new TrustedCertificatesDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OnSignatureDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelectedSignature();

    private void OnSignatureKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        OpenSelectedSignature();
        e.Handled = true;
    }

    private void OpenSelectedSignature()
    {
        if (_session is null || _verification is null || SignatureList.SelectedItem is not NavItem { Data: SignatureVerification signature }) return;
        OpenSignatureProperties(signature.FieldName);
    }

    /// <summary>Opens the properties of one signature of the current document.</summary>
    public void OpenSignatureProperties(string fieldName)
    {
        if (_session is null || _verification is null) return;
        var dialog = new SignaturePropertiesDialog(_session, _verification, fieldName) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    // ---- Comments ----------------------------------------------------------

    /// <summary>
    /// Fills the Comments list from Core's PdfAnnotationOps.List, off the UI thread.
    /// </summary>
    public async void LoadComments()
    {
        var generation = ++_commentGeneration;
        if (_session is null)
        {
            ShowComments([], "No document open.");
            return;
        }

        var file = _session.CurrentFile;
        var password = _session.Password;
        ShowComments([], "Reading comments...");

        try
        {
            var comments = await Task.Run(() => PdfAnnotationOps.List(file, password));
            if (generation != _commentGeneration) return;
            ShowComments(comments.Select(c =>
            {
                var text = c.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length > 120) text = text[..120] + "...";
                var who = string.IsNullOrWhiteSpace(c.Author) ? "" : " - " + c.Author;
                return new NavItem
                {
                    Title = $"Page {c.PageNumber}: {(c.Kind == AnnotationKind.Ink ? "Freehand" : c.Kind.ToString())}{who}",
                    Detail = text,
                    PageNumber = c.PageNumber,
                    X = c.Left,
                    Y = c.Bottom + c.Height,
                    Data = c
                };
            }).ToList());
        }
        catch (Exception ex)
        {
            if (generation != _commentGeneration) return;
            App.LogError("Read comments", ex);
            ShowComments([], "Could not read the comments: " + ex.Message);
        }
    }

    public void ShowComments(IReadOnlyList<NavItem> items, string emptyMessage = "This document has no comments.")
    {
        CommentList.ItemsSource = items;
        CommentsEmpty.Text = items.Count == 0 ? emptyMessage : string.Empty;
        CommentsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NavItem { PageNumber: > 0 } item })
        {
            if (_viewer is not null && (item.X != 0 || item.Y != 0))
                _viewer.BringIntoView(item.PageNumber, item.X, item.Y);
            else
                PageRequested?.Invoke(this, item.PageNumber);
        }
    }

    /// <summary>Currently open document, for Phase C loaders.</summary>
    public DocumentSession? Session => _session;
}
