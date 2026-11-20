using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.Core;

namespace KcsPdfSigner.App.Viewer;

/// <summary>
/// Continuous vertical PDF viewer. Pages are laid out top to bottom; only pages near the viewport
/// get a bitmap, rendered on a worker thread at a resolution that matches the zoom.
/// </summary>
/// <remarks>
/// <para>Coordinates. Each page has an overlay <see cref="Canvas"/> sized to the page as displayed
/// (page /Rotate applied, view rotation not applied: the whole frame is rotated for that). Convert
/// with <see cref="PdfRectToOverlay"/>, <see cref="PdfPointToOverlay"/> and <see cref="OverlayToPdf"/>;
/// PDF coordinates are unrotated user-space points, origin bottom-left, as used by Core.</para>
/// <para>Overlays. Tools draw on <see cref="GetOverlay"/> and tag every element with
/// <c>Tag = owner</c> so <see cref="ClearOverlay"/> can remove just theirs. Overlay positions are in
/// screen units, so redraw on <see cref="OverlayLayoutChanged"/> (raised after zoom, rotation and
/// document reloads).</para>
/// <para>Mouse. Set <see cref="InteractionMode"/>, then listen to <see cref="PageClick"/>,
/// <see cref="PageRectangleSelected"/> or <see cref="PageFreehandCompleted"/>. Put it back to
/// <see cref="InteractionMode.Browse"/> when the tool is done.</para>
/// </remarks>
public sealed class PdfViewer : UserControl
{
    private const double PageGap = 14;
    private const double OuterMargin = 16;
    private const double PointsToDips = 96.0 / 72.0;
    public const double MinZoom = 0.10;
    public const double MaxZoom = 8.00;

    private static readonly double[] ZoomSteps =
        [0.10, 0.25, 0.33, 0.50, 0.67, 0.75, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00, 6.00, 8.00];

    private static readonly Brush PlaceholderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush HitFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
    private static readonly Brush CurrentHitFill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)));

    private readonly ScrollViewer _scroll;
    private readonly Canvas _host;
    private readonly TextBlock _emptyText;
    private readonly List<PageSlot> _slots = [];
    private readonly DispatcherTimer _renderTimer;

    private byte[]? _bytes;
    private string? _password;
    private int _generation;
    private bool _pumping;

    private double _zoom = 1.0;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private int _viewRotation;
    private int _currentPage;
    private InteractionMode _interactionMode = InteractionMode.Browse;

    private List<PageHighlight> _highlights = [];
    private PageHighlight? _currentHighlight;

    // Mouse gesture state.
    private PageSlot? _gestureSlot;
    private bool _panning;
    private Point _panStart;
    private Point _panOffset;
    private Point _dragStart;
    private Rectangle? _rubberBand;
    private Rectangle? _rubberBandHalo;
    private Polyline? _inkLine;
    private List<Point>? _inkPoints;

    public PdfViewer()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Black;

        _host = new Canvas { Background = Brushes.Black };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Focusable = false,
            Background = Brushes.Black,
            Content = _host
        };

        _emptyText = new TextBlock
        {
            Text = "Open a PDF with File > Open, Ctrl+O, or drop it here.",
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var root = new Grid();
        root.Children.Add(_scroll);
        root.Children.Add(_emptyText);
        Content = root;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            PumpRendering();
        };

        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.SizeChanged += OnViewportSizeChanged;
        _scroll.PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseDown += (_, _) => Focus();
    }

    // ======================================================================
    // Events
    // ======================================================================

    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ZoomChanged;

    /// <summary>Overlay geometry changed (zoom, rotation, reload). Tools redraw their overlay items.</summary>
    public event EventHandler? OverlayLayoutChanged;

    /// <summary>Raised before the viewer handles a press; set Handled to take it over.</summary>
    public event EventHandler<PageMouseEventArgs>? PageMouseDown;
    public event EventHandler<PageMouseEventArgs>? PageMouseMove;
    public event EventHandler<PageMouseEventArgs>? PageMouseUp;

    /// <summary>Left click on a page in <see cref="InteractionMode.PointClick"/>.</summary>
    public event EventHandler<PageMouseEventArgs>? PageClick;

    /// <summary>Completed box drag in <see cref="InteractionMode.RectangleDrag"/>.</summary>
    public event EventHandler<PageRectEventArgs>? PageRectangleSelected;

    /// <summary>Completed stroke in <see cref="InteractionMode.Freehand"/>.</summary>
    public event EventHandler<PageInkEventArgs>? PageFreehandCompleted;

    // ======================================================================
    // Document
    // ======================================================================

    /// <summary>The session shown, if <see cref="ShowSession"/> was used.</summary>
    public DocumentSession? Session { get; private set; }

    /// <summary>Raw bytes of the document shown (for thumbnails and printing). Do not modify.</summary>
    public byte[]? DocumentBytes => _bytes;

    public string? DocumentPassword => _password;

    public IReadOnlyList<PdfPageInfo> Pages { get; private set; } = [];

    public int PageCount => Pages.Count;

    /// <summary>Shows a session and follows its changes (undo, redo, apply) automatically.</summary>
    public void ShowSession(DocumentSession session)
    {
        if (Session is not null) Session.DocumentChanged -= OnSessionChanged;
        Session = session;
        session.DocumentChanged += OnSessionChanged;
        LoadDocument(session.CurrentFile, session.Password, session.Pages, keepPosition: false);
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (Session is null) return;
        LoadDocument(Session.CurrentFile, Session.Password, Session.Pages, keepPosition: true);
    }

    /// <summary>Loads a PDF file. With <paramref name="keepPosition"/> the page and zoom stay.</summary>
    public void LoadDocument(string pdfPath, string? password, IReadOnlyList<PdfPageInfo> pages, bool keepPosition)
    {
        var page = keepPosition ? Math.Max(1, _currentPage) : 1;

        _generation++;
        _bytes = File.ReadAllBytes(pdfPath);
        _password = password;
        Pages = pages;

        BuildSlots();
        _emptyText.Visibility = pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyZoomMode();
        Relayout();
        _scroll.UpdateLayout();

        _currentPage = 0;
        GoToPage(Math.Min(page, Math.Max(1, pages.Count)));
        UpdateCurrentPage();
        OverlayLayoutChanged?.Invoke(this, EventArgs.Empty);
        PumpRendering();
    }

    /// <summary>Empties the viewer.</summary>
    public void Clear()
    {
        if (Session is not null) Session.DocumentChanged -= OnSessionChanged;
        Session = null;
        _generation++;
        _bytes = null;
        _password = null;
        Pages = [];
        _highlights = [];
        _currentHighlight = null;
        BuildSlots();
        Relayout();
        _emptyText.Visibility = Visibility.Visible;
        _currentPage = 0;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======================================================================
    // Navigation
    // ======================================================================

    /// <summary>1-based page at the reading line of the viewport; 0 when empty.</summary>
    public int CurrentPage => _currentPage;

    public void GoToPage(int pageNumber)
    {
        if (_slots.Count == 0) return;
        pageNumber = Math.Clamp(pageNumber, 1, _slots.Count);
        var slot = _slots[pageNumber - 1];
        _scroll.ScrollToVerticalOffset(Math.Max(0, slot.Top - OuterMargin / 2));
        SetCurrentPage(pageNumber);
    }

    public void NextPage() => GoToPage(_currentPage + 1);

    public void PreviousPage() => GoToPage(_currentPage - 1);

    public void FirstPage() => GoToPage(1);

    public void LastPage() => GoToPage(_slots.Count);

    /// <summary>Scrolls so a PDF-space point on a page is in view (used for search hits, placements).</summary>
    public void BringIntoView(int pageNumber, double pdfX, double pdfY)
    {
        if (pageNumber < 1 || pageNumber > _slots.Count) return;
        var slot = _slots[pageNumber - 1];
        var local = PdfPointToOverlay(pageNumber, pdfX, pdfY);
        var onHost = slot.Surface.TranslatePoint(local, _host);
        _scroll.ScrollToVerticalOffset(Math.Max(0, onHost.Y - _scroll.ViewportHeight / 3));
        if (onHost.X < _scroll.HorizontalOffset || onHost.X > _scroll.HorizontalOffset + _scroll.ViewportWidth)
        {
            _scroll.ScrollToHorizontalOffset(Math.Max(0, onHost.X - _scroll.ViewportWidth / 2));
        }
    }

    // ======================================================================
    // Zoom and rotation
    // ======================================================================

    /// <summary>1.0 = actual size. Setting it switches to <see cref="ZoomMode.Custom"/>.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoomMode = ZoomMode.Custom;
            SetZoom(value, null);
        }
    }

    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set
        {
            _zoomMode = value;
            if (value == ZoomMode.Custom) return;
            var page = _currentPage;
            ApplyZoomMode();
            Relayout();
            _scroll.UpdateLayout();
            if (page > 0) GoToPage(page);
            AfterZoom();
        }
    }

    public void ZoomIn() => Zoom = ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, MaxZoom);

    public void ZoomOut() => Zoom = ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, MinZoom);

    public void ActualSize() => Zoom = 1.0;

    public void FitWidth() => ZoomMode = ZoomMode.FitWidth;

    public void FitPage() => ZoomMode = ZoomMode.FitPage;

    /// <summary>View-only rotation in degrees (0, 90, 180, 270). The file is not changed.</summary>
    public int ViewRotation
    {
        get => _viewRotation;
        set
        {
            var normalised = ((value % 360) + 360) % 360 / 90 * 90;
            if (normalised == _viewRotation) return;
            var page = _currentPage;
            _viewRotation = normalised;
            foreach (var slot in _slots) slot.Frame.LayoutTransform = RotationTransform();
            ApplyZoomMode();
            Relayout();
            _scroll.UpdateLayout();
            if (page > 0) GoToPage(page);
            OverlayLayoutChanged?.Invoke(this, EventArgs.Empty);
            ScheduleRendering();
        }
    }

    public void RotateClockwise() => ViewRotation += 90;

    public void RotateCounterClockwise() => ViewRotation -= 90;

    private void SetZoom(double value, Point? anchorInViewport)
    {
        value = Math.Clamp(value, MinZoom, MaxZoom);
        if (Math.Abs(value - _zoom) < 0.0001 && _slots.Count > 0) return;

        var anchor = anchorInViewport ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 3);
        var captured = CaptureAnchor(anchor);

        _zoom = value;
        Relayout();
        _scroll.UpdateLayout();
        RestoreAnchor(captured, anchor);

        AfterZoom();
    }

    private void AfterZoom()
    {
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        OverlayLayoutChanged?.Invoke(this, EventArgs.Empty);
        ScheduleRendering();
    }

    private void ApplyZoomMode()
    {
        if (_zoomMode == ZoomMode.Custom || _slots.Count == 0) return;

        var viewportWidth = (_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth) - 2 * OuterMargin - 2;
        var viewportHeight = (_scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight) - 2 * OuterMargin - 2;
        if (viewportWidth <= 20 || viewportHeight <= 20) return;

        double zoom;
        if (_zoomMode == ZoomMode.FitWidth)
        {
            var widest = _slots.Max(s => ViewSizeAtZoomOne(s).Width);
            // Leave room for the vertical scroll bar that a fitted page will usually need.
            zoom = (viewportWidth - (_scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 0 : 12)) / widest;
        }
        else
        {
            var slot = _slots[Math.Clamp(_currentPage, 1, _slots.Count) - 1];
            var size = ViewSizeAtZoomOne(slot);
            zoom = Math.Min(viewportWidth / size.Width, viewportHeight / size.Height);
        }

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    private Size ViewSizeAtZoomOne(PageSlot slot)
    {
        var (width, height) = PreviewGeometry.RotatedSize(slot.Info);
        width *= PointsToDips;
        height *= PointsToDips;
        return _viewRotation is 90 or 270 ? new Size(height, width) : new Size(width, height);
    }

    private (PageSlot Slot, double Fx, double Fy)? CaptureAnchor(Point viewportPoint)
    {
        if (_slots.Count == 0) return null;
        var hostY = _scroll.VerticalOffset + viewportPoint.Y;
        var hostX = _scroll.HorizontalOffset + viewportPoint.X;
        var slot = SlotAtHostY(hostY);
        if (slot is null) return null;
        var fx = slot.FrameWidth > 0 ? (hostX - slot.Left) / slot.FrameWidth : 0.5;
        var fy = slot.FrameHeight > 0 ? (hostY - slot.Top) / slot.FrameHeight : 0;
        return (slot, fx, fy);
    }

    private void RestoreAnchor((PageSlot Slot, double Fx, double Fy)? captured, Point viewportPoint)
    {
        if (captured is null) return;
        var (slot, fx, fy) = captured.Value;
        _scroll.ScrollToVerticalOffset(Math.Max(0, slot.Top + fy * slot.FrameHeight - viewportPoint.Y));
        _scroll.ScrollToHorizontalOffset(Math.Max(0, slot.Left + fx * slot.FrameWidth - viewportPoint.X));
    }

    // ======================================================================
    // Interaction
    // ======================================================================

    public InteractionMode InteractionMode
    {
        get => _interactionMode;
        set
        {
            CancelGesture();
            _interactionMode = value;
            var cursor = value == InteractionMode.Browse ? Cursors.Hand : Cursors.Cross;
            foreach (var slot in _slots) slot.Input.Cursor = cursor;
        }
    }

    /// <summary>Drops any drag in progress (Esc).</summary>
    public void CancelGesture()
    {
        if (_gestureSlot is not null)
        {
            if (_rubberBand is not null) _gestureSlot.Input.Children.Remove(_rubberBand);
            if (_rubberBandHalo is not null) _gestureSlot.Input.Children.Remove(_rubberBandHalo);
            if (_inkLine is not null) _gestureSlot.Input.Children.Remove(_inkLine);
            if (_gestureSlot.Input.IsMouseCaptured) _gestureSlot.Input.ReleaseMouseCapture();
        }

        _gestureSlot = null;
        _panning = false;
        _rubberBand = null;
        _rubberBandHalo = null;
        _inkLine = null;
        _inkPoints = null;
    }

    // ======================================================================
    // Overlay helpers
    // ======================================================================

    /// <summary>The tool overlay of a page (1-based), or null when out of range.</summary>
    public Canvas? GetOverlay(int pageNumber)
        => pageNumber >= 1 && pageNumber <= _slots.Count ? _slots[pageNumber - 1].Overlay : null;

    /// <summary>Removes overlay elements whose Tag equals <paramref name="owner"/>; null removes all.</summary>
    public void ClearOverlay(object? owner = null)
    {
        foreach (var slot in _slots)
        {
            if (owner is null)
            {
                slot.Overlay.Children.Clear();
                continue;
            }

            for (var i = slot.Overlay.Children.Count - 1; i >= 0; i--)
            {
                if (slot.Overlay.Children[i] is FrameworkElement element && Equals(element.Tag, owner))
                {
                    slot.Overlay.Children.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>Size of a page's overlay canvas in device-independent pixels.</summary>
    public Size GetOverlaySize(int pageNumber)
        => pageNumber >= 1 && pageNumber <= _slots.Count
            ? new Size(_slots[pageNumber - 1].Width, _slots[pageNumber - 1].Height)
            : Size.Empty;

    /// <summary>PDF rectangle (unrotated points, origin bottom-left) to overlay coordinates.</summary>
    public Rect PdfRectToOverlay(int pageNumber, double left, double bottom, double width, double height)
    {
        if (pageNumber < 1 || pageNumber > _slots.Count) return Rect.Empty;
        var slot = _slots[pageNumber - 1];

        var probe = new SignaturePlacement
        {
            PageNumber = pageNumber,
            Left = (float)left,
            Bottom = (float)bottom,
            Width = (float)width,
            Height = (float)height,
            Source = PlacementSource.Manual,
            PageWidth = slot.Info.Width,
            PageHeight = slot.Info.Height,
            Rotation = slot.Info.Rotation
        };

        var box = PreviewGeometry.ToPreview(probe, slot.Info, slot.Width, slot.Height);
        return new Rect(box.Left, box.Top, box.Width, box.Height);
    }

    public Point PdfPointToOverlay(int pageNumber, double x, double y)
    {
        var rect = PdfRectToOverlay(pageNumber, x, y, 0, 0);
        return rect.IsEmpty ? new Point() : rect.TopLeft;
    }

    /// <summary>Overlay coordinates to unrotated PDF points (origin bottom-left).</summary>
    public Point OverlayToPdf(int pageNumber, Point overlayPoint)
    {
        if (pageNumber < 1 || pageNumber > _slots.Count) return new Point();
        var slot = _slots[pageNumber - 1];
        var (x, y) = PreviewGeometry.FromPreview(overlayPoint.X, overlayPoint.Y, slot.Info, slot.Width, slot.Height);
        return new Point(x, y);
    }

    // ======================================================================
    // Search highlights
    // ======================================================================

    /// <summary>Shows search hits; <paramref name="current"/> is drawn darker and scrolled to.</summary>
    public void SetSearchHighlights(IEnumerable<PageHighlight> hits, PageHighlight? current = null)
    {
        _highlights = hits.ToList();
        _currentHighlight = current;
        DrawHighlights();

        if (current is { } hit) BringIntoView(hit.PageNumber, hit.Left, hit.Bottom + hit.Height);
    }

    public void ClearSearchHighlights()
    {
        _highlights = [];
        _currentHighlight = null;
        DrawHighlights();
    }

    private void DrawHighlights()
    {
        foreach (var slot in _slots) slot.Highlights.Children.Clear();

        foreach (var hit in _highlights)
        {
            if (hit.PageNumber < 1 || hit.PageNumber > _slots.Count) continue;
            var rect = PdfRectToOverlay(hit.PageNumber, hit.Left, hit.Bottom, hit.Width, hit.Height);
            var isCurrent = _currentHighlight is { } c && c == hit;

            var shape = new Rectangle
            {
                Width = Math.Max(rect.Width, 2),
                Height = Math.Max(rect.Height, 2),
                Fill = isCurrent ? CurrentHitFill : HitFill,
                Stroke = isCurrent ? Brushes.Black : null,
                StrokeThickness = 1
            };
            Canvas.SetLeft(shape, rect.Left);
            Canvas.SetTop(shape, rect.Top);
            _slots[hit.PageNumber - 1].Highlights.Children.Add(shape);
        }
    }

    // ======================================================================
    // Layout
    // ======================================================================

    private sealed class PageSlot
    {
        public required PdfPageInfo Info { get; init; }
        public required Border Frame { get; init; }
        public required Grid Surface { get; init; }
        public required Image Image { get; init; }
        public required Canvas Highlights { get; init; }
        public required Canvas Overlay { get; init; }
        public required Canvas Input { get; init; }
        public required TextBlock Label { get; init; }

        /// <summary>Displayed page size (page rotation applied, view rotation not), in DIPs.</summary>
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Frame rectangle on the host canvas (view rotation applied).</summary>
        public double Left { get; set; }
        public double Top { get; set; }
        public double FrameWidth { get; set; }
        public double FrameHeight { get; set; }

        public int RenderedPixelWidth { get; set; }
        public bool Failed { get; set; }
    }

    private void BuildSlots()
    {
        CancelGesture();
        _host.Children.Clear();
        _slots.Clear();

        var cursor = _interactionMode == InteractionMode.Browse ? Cursors.Hand : Cursors.Cross;

        foreach (var info in Pages)
        {
            var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var label = new TextBlock
            {
                Text = info.PageNumber.ToString(),
                Foreground = Brushes.White,
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var highlights = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
            var input = new Canvas { Background = Brushes.Transparent, Cursor = cursor };

            var surface = new Grid { Background = PlaceholderBrush, ClipToBounds = true };
            surface.Children.Add(label);
            surface.Children.Add(image);
            surface.Children.Add(highlights);
            surface.Children.Add(overlay);
            surface.Children.Add(input);

            var frame = new Border
            {
                BorderBrush = FrameBrush,
                BorderThickness = new Thickness(1),
                Child = surface,
                LayoutTransform = RotationTransform(),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var slot = new PageSlot
            {
                Info = info,
                Frame = frame,
                Surface = surface,
                Image = image,
                Highlights = highlights,
                Overlay = overlay,
                Input = input,
                Label = label
            };

            input.MouseLeftButtonDown += (_, e) => OnInputDown(slot, e);
            input.MouseMove += (_, e) => OnInputMove(slot, e);
            input.MouseLeftButtonUp += (_, e) => OnInputUp(slot, e);
            input.LostMouseCapture += (_, _) =>
            {
                if (_gestureSlot == slot && _panning) _panning = false;
            };

            _slots.Add(slot);
            _host.Children.Add(frame);
        }
    }

    private Transform? RotationTransform() => _viewRotation == 0 ? null : new RotateTransform(_viewRotation);

    private void Relayout()
    {
        var scale = _zoom * PointsToDips;
        var deviceScale = Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
        var top = OuterMargin;
        var widest = 0.0;

        foreach (var slot in _slots)
        {
            var (width, height) = PreviewGeometry.RotatedSize(slot.Info);
            // Whole device pixels, so the page bitmap lands exactly on the screen grid (sharp text).
            slot.Width = Math.Max(1 / deviceScale, Math.Round(width * scale * deviceScale) / deviceScale);
            slot.Height = Math.Max(1 / deviceScale, Math.Round(height * scale * deviceScale) / deviceScale);
            slot.Surface.Width = slot.Width;
            slot.Surface.Height = slot.Height;

            var sideways = _viewRotation is 90 or 270;
            slot.FrameWidth = (sideways ? slot.Height : slot.Width) + 2;
            slot.FrameHeight = (sideways ? slot.Width : slot.Height) + 2;
            slot.Top = top;
            top += slot.FrameHeight + PageGap;
            widest = Math.Max(widest, slot.FrameWidth);
        }

        var viewportWidth = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth;
        var hostWidth = Math.Max(widest + 2 * OuterMargin, viewportWidth);
        var hostHeight = _slots.Count == 0 ? 0 : top - PageGap + OuterMargin;

        foreach (var slot in _slots)
        {
            slot.Left = Math.Round((hostWidth - slot.FrameWidth) / 2);
            Canvas.SetLeft(slot.Frame, slot.Left);
            Canvas.SetTop(slot.Frame, slot.Top);
        }

        _host.Width = hostWidth;
        _host.Height = hostHeight;

        DrawHighlights();
    }

    private PageSlot? SlotAtHostY(double y)
    {
        if (_slots.Count == 0) return null;

        int low = 0, high = _slots.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_slots[mid].Top <= y) low = mid;
            else high = mid - 1;
        }

        return _slots[low];
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_slots.Count == 0) return;
        var page = _currentPage;

        if (_zoomMode != ZoomMode.Custom)
        {
            var before = _zoom;
            ApplyZoomMode();
            Relayout();
            _scroll.UpdateLayout();
            if (page > 0 && Math.Abs(before - _zoom) > 0.0001)
            {
                GoToPage(page);
                AfterZoom();
                return;
            }
        }
        else
        {
            Relayout();
        }

        ScheduleRendering();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0 && e.ViewportHeightChange == 0) return;
        UpdateCurrentPage();
        PumpRendering();
    }

    private void UpdateCurrentPage()
    {
        if (_slots.Count == 0) return;

        // At the very end of the document, the last page is current even if short.
        if (_scroll.VerticalOffset >= _scroll.ScrollableHeight - 1 && _scroll.ScrollableHeight > 0)
        {
            SetCurrentPage(_slots.Count);
            return;
        }

        var slot = SlotAtHostY(_scroll.VerticalOffset + _scroll.ViewportHeight * 0.3);
        if (slot is not null) SetCurrentPage(slot.Info.PageNumber);
    }

    private void SetCurrentPage(int page)
    {
        if (page == _currentPage) return;
        _currentPage = page;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======================================================================
    // Rendering
    // ======================================================================

    private void ScheduleRendering()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async void PumpRendering()
    {
        if (_pumping || _bytes is null) return;
        _pumping = true;

        try
        {
            while (true)
            {
                var job = NextRenderJob();
                if (job is null) break;

                var (slot, pixelWidth, pixelHeight, screenScale) = job.Value;
                var generation = _generation;
                var bytes = _bytes;
                var password = _password;
                var pageNumber = slot.Info.PageNumber;

                BitmapSource? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => PageRenderer.RenderPixels(bytes!, pageNumber, password, pixelWidth, pixelHeight, screenScale));
                }
                catch (Exception ex)
                {
                    if (generation == _generation)
                    {
                        slot.Failed = true;
                        slot.Label.Text = $"Page {pageNumber} could not be shown.";
                        App.LogError($"Rendering page {pageNumber}", ex);
                    }
                }

                if (generation != _generation || bitmap is null) continue;

                slot.Image.Source = bitmap;
                slot.RenderedPixelWidth = pixelWidth;
                ReleaseFarBitmaps();
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private (PageSlot Slot, int PixelWidth, int PixelHeight, double ScreenScale)? NextRenderJob()
    {
        if (_slots.Count == 0 || _bytes is null) return null;

        var viewportTop = _scroll.VerticalOffset;
        var viewportHeight = Math.Max(_scroll.ViewportHeight, 1);
        var centre = viewportTop + viewportHeight / 2;
        var from = viewportTop - viewportHeight * 0.5;
        var to = viewportTop + viewportHeight * 1.5;

        var deviceScale = Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);

        PageSlot? best = null;
        var bestDistance = double.MaxValue;
        var bestWidth = 0;
        var bestHeight = 0;

        var first = SlotAtHostY(from);
        var start = first is null ? 0 : first.Info.PageNumber - 1;

        for (var i = start; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Top > to) break;
            if (slot.Top + slot.FrameHeight < from || slot.Failed) continue;

            // The exact number of screen pixels this page covers; keep it under the memory cap.
            var pixelWidth = (double)Math.Max(8, (int)Math.Round(slot.Width * deviceScale));
            var pixelHeight = (double)Math.Max(8, (int)Math.Round(slot.Height * deviceScale));
            if (pixelWidth * pixelHeight > PageRenderer.MaxPixels)
            {
                var shrink = Math.Sqrt(PageRenderer.MaxPixels / (pixelWidth * pixelHeight));
                pixelWidth = Math.Floor(pixelWidth * shrink);
                pixelHeight = Math.Floor(pixelHeight * shrink);
            }

            var needs = slot.RenderedPixelWidth != (int)pixelWidth;
            if (!needs) continue;

            var distance = Math.Abs(slot.Top + slot.FrameHeight / 2 - centre);
            if (distance < bestDistance)
            {
                best = slot;
                bestDistance = distance;
                bestWidth = (int)pixelWidth;
                bestHeight = (int)pixelHeight;
            }
        }

        return best is null ? null : (best, bestWidth, bestHeight, deviceScale);
    }

    /// <summary>Keeps memory bounded: bitmaps far outside the viewport are dropped.</summary>
    private void ReleaseFarBitmaps()
    {
        var viewportHeight = Math.Max(_scroll.ViewportHeight, 1);
        var keepFrom = _scroll.VerticalOffset - viewportHeight * 3;
        var keepTo = _scroll.VerticalOffset + viewportHeight * 4;

        foreach (var slot in _slots)
        {
            if (slot.RenderedPixelWidth == 0) continue;
            if (slot.Top + slot.FrameHeight >= keepFrom && slot.Top <= keepTo) continue;
            slot.Image.Source = null;
            slot.RenderedPixelWidth = 0;
        }
    }

    // ======================================================================
    // Mouse and keyboard
    // ======================================================================

    private PageMouseEventArgs MakeArgs(PageSlot slot, MouseEventArgs e, Point overlayPoint, int clickCount = 1)
    {
        var pdf = OverlayToPdf(slot.Info.PageNumber, overlayPoint);
        return new PageMouseEventArgs
        {
            PageNumber = slot.Info.PageNumber,
            X = pdf.X,
            Y = pdf.Y,
            OverlayPoint = overlayPoint,
            ClickCount = clickCount,
            Modifiers = Keyboard.Modifiers
        };
    }

    private void OnInputDown(PageSlot slot, MouseButtonEventArgs e)
    {
        Focus();
        var point = e.GetPosition(slot.Input);
        var args = MakeArgs(slot, e, point, e.ClickCount);
        PageMouseDown?.Invoke(this, args);
        if (args.Handled)
        {
            e.Handled = true;
            return;
        }

        switch (_interactionMode)
        {
            case InteractionMode.Browse:
                _gestureSlot = slot;
                _panning = true;
                _panStart = e.GetPosition(_scroll);
                _panOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
                slot.Input.CaptureMouse();
                break;

            case InteractionMode.PointClick:
                PageClick?.Invoke(this, args);
                break;

            case InteractionMode.RectangleDrag:
                _gestureSlot = slot;
                _dragStart = point;
                _rubberBandHalo = new Rectangle { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
                _rubberBand = new Rectangle
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 3],
                    IsHitTestVisible = false
                };
                PlaceRubberBand(point);
                slot.Input.Children.Add(_rubberBandHalo);
                slot.Input.Children.Add(_rubberBand);
                slot.Input.CaptureMouse();
                break;

            case InteractionMode.Freehand:
                _gestureSlot = slot;
                _inkPoints = [point];
                _inkLine = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _inkLine.Points.Add(point);
                slot.Input.Children.Add(_inkLine);
                slot.Input.CaptureMouse();
                break;
        }

        e.Handled = true;
    }

    private void OnInputMove(PageSlot slot, MouseEventArgs e)
    {
        var point = e.GetPosition(slot.Input);

        if (_panning && _gestureSlot == slot)
        {
            var now = e.GetPosition(_scroll);
            _scroll.ScrollToHorizontalOffset(_panOffset.X - (now.X - _panStart.X));
            _scroll.ScrollToVerticalOffset(_panOffset.Y - (now.Y - _panStart.Y));
            return;
        }

        if (_rubberBand is not null && _gestureSlot == slot)
        {
            PlaceRubberBand(Clamp(slot, point));
        }
        else if (_inkLine is not null && _inkPoints is not null && _gestureSlot == slot)
        {
            var clamped = Clamp(slot, point);
            _inkPoints.Add(clamped);
            _inkLine.Points.Add(clamped);
        }

        PageMouseMove?.Invoke(this, MakeArgs(slot, e, point));
    }

    private void OnInputUp(PageSlot slot, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(slot.Input);

        if (_gestureSlot == slot)
        {
            if (_rubberBand is not null)
            {
                var rect = new Rect(_dragStart, Clamp(slot, point));
                CancelGesture();

                if (rect.Width >= 3 && rect.Height >= 3)
                {
                    var a = OverlayToPdf(slot.Info.PageNumber, rect.TopLeft);
                    var b = OverlayToPdf(slot.Info.PageNumber, rect.BottomRight);
                    PageRectangleSelected?.Invoke(this, new PageRectEventArgs
                    {
                        PageNumber = slot.Info.PageNumber,
                        Left = Math.Min(a.X, b.X),
                        Bottom = Math.Min(a.Y, b.Y),
                        Width = Math.Abs(b.X - a.X),
                        Height = Math.Abs(b.Y - a.Y),
                        OverlayRect = rect
                    });
                }
            }
            else if (_inkPoints is not null)
            {
                var overlayPoints = _inkPoints;
                CancelGesture();

                if (overlayPoints.Count >= 2)
                {
                    PageFreehandCompleted?.Invoke(this, new PageInkEventArgs
                    {
                        PageNumber = slot.Info.PageNumber,
                        OverlayPoints = overlayPoints,
                        PdfPoints = overlayPoints.Select(p => OverlayToPdf(slot.Info.PageNumber, p)).ToList()
                    });
                }
            }
            else
            {
                CancelGesture();
            }
        }

        PageMouseUp?.Invoke(this, MakeArgs(slot, e, point));
        e.Handled = true;
    }

    private void PlaceRubberBand(Point current)
    {
        var rect = new Rect(_dragStart, current);
        foreach (var shape in new[] { _rubberBandHalo, _rubberBand })
        {
            if (shape is null) continue;
            shape.Width = rect.Width;
            shape.Height = rect.Height;
            Canvas.SetLeft(shape, rect.Left);
            Canvas.SetTop(shape, rect.Top);
        }
    }

    private static Point Clamp(PageSlot slot, Point point)
        => new(Math.Clamp(point.X, 0, slot.Width), Math.Clamp(point.Y, 0, slot.Height));

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_slots.Count == 0) return;

        var wheelZoom = Services.AppSettings.Current.MouseWheelZoom;
        if (wheelZoom && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // With wheel zoom on, Shift + wheel scrolls the page.
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && !wheelZoom) return;

        var factor = Math.Pow(1.1, e.Delta / 120.0);
        _zoomMode = ZoomMode.Custom;
        SetZoom(_zoom * factor, e.GetPosition(_scroll));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || _slots.Count == 0) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.PageDown:
                NextPage();
                e.Handled = true;
                break;
            case Key.PageUp:
                PreviousPage();
                e.Handled = true;
                break;
            case Key.Home:
                FirstPage();
                e.Handled = true;
                break;
            case Key.End:
                LastPage();
                e.Handled = true;
                break;
            case Key.Down:
                _scroll.LineDown();
                e.Handled = true;
                break;
            case Key.Up:
                _scroll.LineUp();
                e.Handled = true;
                break;
            case Key.Escape when _gestureSlot is not null:
                CancelGesture();
                e.Handled = true;
                break;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
