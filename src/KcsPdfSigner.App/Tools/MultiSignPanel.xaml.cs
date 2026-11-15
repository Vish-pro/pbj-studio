using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Multi-Sign (DSC): finds every signing spot in the active document, shows them on the pages,
/// and signs them all with one certificate in a single all-or-nothing run.
/// </summary>
public partial class MultiSignPanel : UserControl, IToolPanel
{
    private static readonly Brush SpotFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)));
    private static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    private readonly MultiSignViewModel _viewModel = new();
    private PdfViewer? _viewer;
    private IStudioHost? _host;

    public MultiSignPanel()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.PlacementsChanged += (_, _) => RedrawOverlay();
        _viewModel.Placements.CollectionChanged += OnPlacementsCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FocusRequested += (_, page) => _viewer?.GoToPage(page);
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Multi-Sign (DSC)";

    public string Description => "Sign one PDF at many spots with a USB DSC token.";

    public string IconKey => "Icon.MultiSign";

    public FrameworkElement View => this;

    public MultiSignViewModel ViewModel => _viewModel;

    public void Initialize(IStudioHost host)
    {
        _host = host;
        _viewModel.Host = host;
        host.DocumentClosed += (_, session) => _viewModel.Forget(session);
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();

        _viewModel.SetSession(session);
        _viewer = viewer;

        if (_viewer is not null)
        {
            _viewer.PageClick += OnPageClick;
            _viewer.OverlayLayoutChanged += OnOverlayLayoutChanged;
        }

        UpdateInteractionMode();
        RedrawOverlay();
    }

    public void Detach()
    {
        if (_viewer is null) return;

        _viewer.PageClick -= OnPageClick;
        _viewer.OverlayLayoutChanged -= OnOverlayLayoutChanged;
        _viewer.InteractionMode = InteractionMode.Browse;
        _viewer.ClearOverlay(this);
        _viewer = null;
    }

    // ---- Viewer wiring -----------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MultiSignViewModel.SelectedMode) or nameof(MultiSignViewModel.Session))
        {
            UpdateInteractionMode();
        }
    }

    private void UpdateInteractionMode()
    {
        if (_viewer is null) return;
        _viewer.InteractionMode = _viewModel.IsManualMode && _viewModel.Session is not null
            ? InteractionMode.PointClick
            : InteractionMode.Browse;
        if (_viewModel.IsManualMode && _viewModel.Session is not null)
        {
            _host?.SetStatus("Place by hand: click on a page to drop a signature there.");
        }
    }

    private void OnPageClick(object? sender, PageMouseEventArgs e)
    {
        var session = _viewModel.Session;
        if (!_viewModel.IsManualMode || session is null || _viewModel.IsBusy) return;
        if (e.PageNumber < 1 || e.PageNumber > session.Pages.Count) return;

        _viewModel.AddManualPlacement(session.Pages[e.PageNumber - 1], (float)e.X, (float)e.Y);
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) => RedrawOverlay();

    private void OnPlacementsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Toggling the Sign checkbox must restyle the rectangle on the page.
        foreach (var row in e.NewItems?.OfType<PlacementRow>() ?? [])
        {
            row.PropertyChanged -= OnPlacementRowChanged;
            row.PropertyChanged += OnPlacementRowChanged;
        }

        foreach (var row in e.OldItems?.OfType<PlacementRow>() ?? [])
        {
            row.PropertyChanged -= OnPlacementRowChanged;
        }

        RedrawOverlay();
    }

    private void OnPlacementRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlacementRow.Enabled)) RedrawOverlay();
    }

    /// <summary>
    /// Draws every placement on its page's overlay. Each spot gets a white halo under a black
    /// outline so it reads on light and dark page content alike; a switched-off spot is dashed.
    /// </summary>
    private void RedrawOverlay()
    {
        var index = 0;
        foreach (var row in _viewModel.Placements) row.Index = ++index;

        if (_viewer is null) return;
        _viewer.ClearOverlay(this);

        var selected = PlacementGrid.SelectedItem as PlacementRow;

        foreach (var row in _viewModel.Placements)
        {
            var overlay = _viewer.GetOverlay(row.Page);
            if (overlay is null) continue;

            var p = row.Placement;
            var box = _viewer.PdfRectToOverlay(row.Page, p.Left, p.Bottom, p.Width, p.Height);
            if (box.IsEmpty) continue;

            var isSelected = ReferenceEquals(row, selected);
            var width = Math.Max(box.Width, 2);
            var height = Math.Max(box.Height, 2);

            var halo = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = HaloBrush,
                StrokeThickness = isSelected ? 6 : 4,
                IsHitTestVisible = false,
                Tag = this
            };

            var outline = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = Brushes.Black,
                StrokeThickness = isSelected ? 3 : 2,
                Fill = row.Enabled ? SpotFill : null,
                IsHitTestVisible = false,
                Tag = this
            };

            if (!row.Enabled) outline.StrokeDashArray = [3, 2];

            foreach (var shape in new[] { halo, outline })
            {
                Canvas.SetLeft(shape, box.Left);
                Canvas.SetTop(shape, box.Top);
                overlay.Children.Add(shape);
            }

            var caption = new Border
            {
                Background = row.Enabled ? Brushes.Black : Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 0, 4, 1),
                IsHitTestVisible = false,
                Tag = this,
                Child = new TextBlock
                {
                    Text = row.Enabled ? row.Index.ToString() : row.Index + " off",
                    Foreground = row.Enabled ? Brushes.White : Brushes.Black,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                }
            };

            Canvas.SetLeft(caption, box.Left);
            Canvas.SetTop(caption, Math.Max(box.Top - 18, 0));
            overlay.Children.Add(caption);
        }
    }

    // ---- Grid and log ------------------------------------------------------

    private void OnRemoveSelectedPlacement(object sender, RoutedEventArgs e)
    {
        if (PlacementGrid.SelectedItem is PlacementRow row) _viewModel.RemovePlacement(row);
    }

    private void OnPlacementSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RedrawOverlay();
        if (PlacementGrid.SelectedItem is not PlacementRow row || _viewer is null) return;

        var p = row.Placement;
        _viewer.BringIntoView(row.Page, p.Left, p.Bottom + p.Height);
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
