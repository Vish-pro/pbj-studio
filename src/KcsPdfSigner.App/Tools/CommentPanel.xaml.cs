using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>A row in the comments list.</summary>
public sealed class CommentRow
{
    public required AnnotationInfo Info { get; init; }
    public int PageNumber => Info.PageNumber;
    public string Kind => Info.Kind == AnnotationKind.Ink ? "Freehand" : Info.Kind.ToString();
    public string Author => Info.Author;

    public string ShortText
    {
        get
        {
            var text = Info.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 80 ? text[..80] + "..." : text;
        }
    }
}

/// <summary>
/// Comment: adds highlights, underlines, strikeouts, notes, free text, shapes, freehand ink and
/// stamps, and lists the document's comments with go-to and delete. Uses PdfAnnotationOps.
/// </summary>
public partial class CommentPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private AnnotationKind? _tool;
    private int _listGeneration;
    private bool _busy;

    public CommentPanel()
    {
        InitializeComponent();
        AuthorBox.Text = Environment.UserName;
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Comment";

    public string Description => "Highlight, notes, shapes, freehand and stamps.";

    public string IconKey => "Icon.Comment";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;

        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.PageClick += OnPageClick;
            _viewer.PageRectangleSelected += OnPageRectangle;
            _viewer.PageFreehandCompleted += OnPageFreehand;
            _viewer.OverlayLayoutChanged += OnOverlayLayoutChanged;
        }

        UpdateInteractionMode();
        UpdateState();
        _ = LoadCommentsAsync();
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.PageClick -= OnPageClick;
            _viewer.PageRectangleSelected -= OnPageRectangle;
            _viewer.PageFreehandCompleted -= OnPageFreehand;
            _viewer.OverlayLayoutChanged -= OnOverlayLayoutChanged;
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        _listGeneration++;
    }

    // ---- Public for tests and automation ----------------------------------

    /// <summary>The tool in use, or null when browsing.</summary>
    public AnnotationKind? CurrentTool
    {
        get => _tool;
        set
        {
            _tool = value;
            foreach (var button in ToolButtons.Children.OfType<ToggleButton>())
                button.IsChecked = value is not null && Equals(button.Tag as string, value.ToString());
            UpdateInteractionMode();
        }
    }

    /// <summary>Comments currently listed.</summary>
    public IReadOnlyList<CommentRow> Rows => CommentList.ItemsSource as IReadOnlyList<CommentRow> ?? [];

    /// <summary>Adds one comment to the active document (undoable).</summary>
    public async Task<bool> AddAsync(AnnotationSpec spec)
    {
        var session = _session;
        if (session is null || _busy) return false;

        if (string.IsNullOrEmpty(spec.Author)) spec.Author = AuthorBox.Text.Trim();
        ApplyColour(spec);

        _busy = true;
        try
        {
            var password = session.Password;
            return await ToolUi.ApplyAsync(_host, this, session,
                (input, output) => PdfAnnotationOps.Add(input, output, [spec], password),
                "Add " + KindName(spec.Kind).ToLowerInvariant());
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Reloads the comment list from the current working file (off the UI thread).</summary>
    public async Task LoadCommentsAsync()
    {
        var generation = ++_listGeneration;
        var session = _session;
        if (session is null)
        {
            CommentList.ItemsSource = null;
            ListStatus.Text = "";
            return;
        }

        var file = session.CurrentFile;
        var password = session.Password;
        ListStatus.Text = "Reading comments...";

        try
        {
            var items = await Task.Run(() => PdfAnnotationOps.List(file, password));
            if (generation != _listGeneration) return;

            CommentList.ItemsSource = items.Select(i => new CommentRow { Info = i }).ToList();
            ListStatus.Text = items.Count == 0 ? "No comments yet." : items.Count + " comment(s). Select one to go to it.";
        }
        catch (Exception ex)
        {
            if (generation != _listGeneration) return;
            App.LogError("List comments", ex);
            CommentList.ItemsSource = null;
            ListStatus.Text = "Could not read the comments: " + ex.Message;
        }

        RedrawOverlay();
    }

    // ---- Tools -------------------------------------------------------------

    private void OnToolClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } button) return;
        var kind = Enum.Parse<AnnotationKind>(tag);
        CurrentTool = button.IsChecked == true ? kind : null;
    }

    private void UpdateInteractionMode()
    {
        if (_viewer is null) return;

        _viewer.InteractionMode = _session is null || _tool is null
            ? InteractionMode.Browse
            : _tool switch
            {
                AnnotationKind.Note => InteractionMode.PointClick,
                AnnotationKind.Ink or AnnotationKind.Line => InteractionMode.Freehand,
                _ => InteractionMode.RectangleDrag
            };

        ToolHint.Text = _tool switch
        {
            null => "Pick a tool, then work on the page. Esc cancels a drag.",
            AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut =>
                "Drag a box over the text on the page.",
            AnnotationKind.Note => "Click on the page where the note should go.",
            AnnotationKind.FreeText => "Drag a box on the page, then type the text.",
            AnnotationKind.Line => "Drag from the start of the line to its end.",
            AnnotationKind.Ink => "Draw on the page with the mouse. Each stroke is saved as it ends.",
            AnnotationKind.Stamp => "Drag a box on the page for the stamp.",
            _ => "Drag a box on the page."
        };

        if (_tool is not null) _host?.SetStatus(KindName(_tool.Value) + ": " + ToolHint.Text);
    }

    private async void OnPageClick(object? sender, PageMouseEventArgs e)
    {
        if (_tool != AnnotationKind.Note || _session is null || _busy) return;

        var text = MessageDialog.Prompt(_host?.Window, "Note text:", "Add note", NoteTextBox.Text);
        if (text is null) return;

        await AddAsync(new AnnotationSpec
        {
            Kind = AnnotationKind.Note,
            PageNumber = e.PageNumber,
            Left = (float)e.X,
            Bottom = (float)e.Y - 20f,
            Width = 20f,
            Height = 20f,
            Text = text
        });
    }

    private async void OnPageRectangle(object? sender, PageRectEventArgs e)
    {
        if (_tool is not { } kind || _session is null || _busy) return;
        if (kind is AnnotationKind.Note or AnnotationKind.Ink or AnnotationKind.Line) return;

        var text = NoteTextBox.Text;
        if (kind == AnnotationKind.FreeText)
        {
            var typed = MessageDialog.Prompt(_host?.Window, "Text for the box:", "Free text", NoteTextBox.Text);
            if (string.IsNullOrWhiteSpace(typed)) return;
            text = typed;
        }
        else if (kind == AnnotationKind.Stamp)
        {
            text = string.IsNullOrWhiteSpace(StampCombo.Text) ? "APPROVED" : StampCombo.Text.Trim();
        }

        await AddAsync(new AnnotationSpec
        {
            Kind = kind,
            PageNumber = e.PageNumber,
            Left = (float)e.Left,
            Bottom = (float)e.Bottom,
            Width = (float)e.Width,
            Height = (float)e.Height,
            Text = text
        });
    }

    private async void OnPageFreehand(object? sender, PageInkEventArgs e)
    {
        if (_tool is not { } kind || _session is null || _busy || e.PdfPoints.Count < 2) return;

        if (kind == AnnotationKind.Line)
        {
            var a = e.PdfPoints[0];
            var b = e.PdfPoints[^1];
            if (Math.Abs(b.X - a.X) < 1 && Math.Abs(b.Y - a.Y) < 1) return;

            await AddAsync(new AnnotationSpec
            {
                Kind = AnnotationKind.Line,
                PageNumber = e.PageNumber,
                Left = (float)a.X,
                Bottom = (float)a.Y,
                Width = (float)(b.X - a.X),
                Height = (float)(b.Y - a.Y),
                Text = NoteTextBox.Text
            });
        }
        else if (kind == AnnotationKind.Ink)
        {
            await AddAsync(new AnnotationSpec
            {
                Kind = AnnotationKind.Ink,
                PageNumber = e.PageNumber,
                InkPoints = [e.PdfPoints.Select(p => new AnnotationPoint((float)p.X, (float)p.Y)).ToList()],
                Text = NoteTextBox.Text
            });
        }
    }

    private void ApplyColour(AnnotationSpec spec)
    {
        if (spec.ColorR is not null) return;

        (float R, float G, float B)? colour = ColourCombo.SelectedIndex switch
        {
            1 => (0.85f, 0.1f, 0.1f),
            2 => (0.1f, 0.3f, 0.85f),
            3 => (0.1f, 0.6f, 0.2f),
            4 => (1f, 0.9f, 0f),
            5 => (0f, 0f, 0f),
            _ => null
        };

        if (colour is not { } c) return;
        spec.ColorR = c.R;
        spec.ColorG = c.G;
        spec.ColorB = c.B;
    }

    private static string KindName(AnnotationKind kind) => kind switch
    {
        AnnotationKind.StrikeOut => "Strikeout",
        AnnotationKind.FreeText => "Free text",
        AnnotationKind.Ink => "Freehand",
        _ => kind.ToString()
    };

    // ---- List --------------------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e) => _ = LoadCommentsAsync();

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) => RedrawOverlay();

    private void OnCommentSelected(object sender, SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = CommentList.SelectedItem is CommentRow && _session is not null;
        RedrawOverlay();

        if (CommentList.SelectedItem is CommentRow row && _viewer is not null)
        {
            _viewer.BringIntoView(row.PageNumber, row.Info.Left, row.Info.Bottom + row.Info.Height);
        }
    }

    private void RedrawOverlay()
    {
        if (_viewer is null) return;
        _viewer.ClearOverlay(this);

        if (CommentList.SelectedItem is CommentRow row)
        {
            var i = row.Info;
            ToolUi.DrawBox(_viewer, this, i.PageNumber, i.Left - 2, i.Bottom - 2, i.Width + 4, i.Height + 4, dashed: true);
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    /// <summary>Removes the selected comment (undoable).</summary>
    public async Task<bool> DeleteSelectedAsync()
    {
        if (CommentList.SelectedItem is not CommentRow row || _session is null || _busy) return false;

        var session = _session;
        var password = session.Password;
        var page = row.Info.PageNumber;
        var index = row.Info.IndexOnPage;

        _busy = true;
        try
        {
            return await ToolUi.ApplyAsync(_host, this, session,
                (input, output) => PdfAnnotationOps.Remove(input, output, page, index, password),
                "Delete comment");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = LoadCommentsAsync();

    private void UpdateState()
    {
        var hasDocument = _session is not null;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        ToolButtons.IsEnabled = hasDocument;
        DeleteButton.IsEnabled = hasDocument && CommentList.SelectedItem is CommentRow;
    }
}
