using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Organize Pages: a thumbnail grid of the active document with multi-select. Rotate, delete,
/// extract, reorder (buttons or drag), insert pages from another PDF or a blank page, and split.
/// Every change goes through <see cref="DocumentSession.ApplyAsync"/>, so Ctrl+Z undoes it.
/// </summary>
public partial class OrganizePagesPanel : UserControl, IToolPanel
{
    private const string DragFormat = "KcsPdfStudio.OrganizePages";

    private readonly ObservableCollection<ThumbItem> _items = [];

    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private RenderJob? _job;
    private List<int>? _pendingSelection;
    private bool _suppressNavigate;

    private Point _dragStart;
    private ThumbItem? _dragItem;
    private ThumbItem? _deferredSingleSelect;
    private bool _dragging;

    public OrganizePagesPanel()
    {
        InitializeComponent();
        ThumbList.ItemsSource = _items;
        SizeChanged += (_, _) => ScheduleVisibleUpdate();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Organize Pages";

    public string Description => "Rotate, delete, reorder, insert, extract and split pages.";

    public string IconKey => "Icon.Pages";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        Rebuild();
    }

    public void Detach()
    {
        StopRendering();
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        _items.Clear();
        UpdateState();
    }

    // ---- Thumbnails --------------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>Rebuilds the grid for the session's current file and restarts lazy rendering.</summary>
    private void Rebuild()
    {
        StopRendering();
        _items.Clear();

        var session = _session;
        if (session is not null)
        {
            foreach (var page in session.Pages) _items.Add(new ThumbItem(page));

            var job = new RenderJob();
            _job = job;
            var file = session.CurrentFile;
            var password = session.Password;
            _ = Task.Run(() => RenderLoop(job, file, password));
        }

        var reselect = _pendingSelection;
        _pendingSelection = null;
        if (reselect is not null && reselect.Count > 0)
        {
            _suppressNavigate = true;
            try
            {
                ThumbList.SelectedItems.Clear();
                foreach (var item in _items.Where(i => reselect.Contains(i.PageNumber))) ThumbList.SelectedItems.Add(item);
                var first = _items.FirstOrDefault(i => reselect.Contains(i.PageNumber));
                if (first is not null) ThumbList.ScrollIntoView(first);
            }
            finally
            {
                _suppressNavigate = false;
            }
        }

        UpdateState();
        ScheduleVisibleUpdate();
    }

    private void StopRendering()
    {
        _job?.Cancel();
        _job = null;
    }

    /// <summary>Background loop: renders whatever the UI queued (the visible thumbnails), one at a time.</summary>
    private async Task RenderLoop(RenderJob job, string file, string? password)
    {
        var token = job.Token;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file, token);
        }
        catch
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            ThumbItem? next;
            lock (job.Queue) job.Queue.TryDequeue(out next);

            if (next is null)
            {
                try
                {
                    await job.Signal.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            BitmapSource? image = null;
            try
            {
                image = PageRenderer.Render(bytes, next.PageNumber, password, next.Dpi);
            }
            catch
            {
                // A page that will not render keeps its empty frame.
            }

            if (token.IsCancellationRequested) return;
            var item = next;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested) item.Image = image;
            }, DispatcherPriority.Background);
        }
    }

    private void OnThumbScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleVisibleUpdate();

    private bool _visibleUpdatePending;

    private void ScheduleVisibleUpdate()
    {
        if (_visibleUpdatePending) return;
        _visibleUpdatePending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _visibleUpdatePending = false;
            QueueVisibleThumbnails();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Queues the thumbnails in view (plus one screen ahead) that are not rendered yet.</summary>
    private void QueueVisibleThumbnails()
    {
        var job = _job;
        if (job is null || _items.Count == 0 || !IsVisible) return;

        var viewportHeight = ThumbList.ActualHeight;
        var added = false;

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.Requested) continue;
            if (ThumbList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            if (!container.IsVisible) continue;

            var top = container.TranslatePoint(new Point(0, 0), ThumbList).Y;
            if (top + container.ActualHeight < -viewportHeight * 0.5 || top > viewportHeight * 2) continue;

            item.Requested = true;
            lock (job.Queue) job.Queue.Enqueue(item);
            added = true;
        }

        if (added && job.Signal.CurrentCount == 0) job.Signal.Release();
    }

    // ---- Selection ---------------------------------------------------------

    private List<int> SelectedPages() =>
        ThumbList.SelectedItems.OfType<ThumbItem>().Select(i => i.PageNumber).OrderBy(p => p).ToList();

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
        if (_suppressNavigate || _viewer is null) return;
        if (e.AddedItems.Count == 1 && e.AddedItems[0] is ThumbItem item) _viewer.GoToPage(item.PageNumber);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ThumbList.SelectAll();

    private void UpdateState()
    {
        var hasDocument = _session is not null;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        ActionsPanel.IsEnabled = hasDocument;
        ThumbList.IsEnabled = hasDocument;

        var selected = ThumbList.SelectedItems.Count;
        var hasSelection = hasDocument && selected > 0;
        RotateLeftButton.IsEnabled = hasSelection;
        RotateRightButton.IsEnabled = hasSelection;
        MoveUpButton.IsEnabled = hasSelection;
        MoveDownButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        ExtractButton.IsEnabled = hasSelection;

        SelectionText.Text = hasDocument
            ? $"Pages ({_items.Count}, {selected} selected)"
            : "Pages";
        InfoText.Text = hasDocument ? _session!.FileName : "";
    }

    private void OnThumbKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ThumbList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            OnDelete(sender, e);
        }
    }

    // ---- Drag to reorder ---------------------------------------------------

    private ThumbItem? ItemFromSource(object source, out ListBoxItem? container)
    {
        container = source is DependencyObject element
            ? ItemsControl.ContainerFromElement(ThumbList, element) as ListBoxItem
            : null;
        return container?.DataContext as ThumbItem;
    }

    private void OnThumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ThumbList);
        _dragItem = ItemFromSource(e.OriginalSource, out var container);
        _deferredSingleSelect = null;
        _dragging = false;

        // Keep a multi-selection intact when the user presses on it to drag it.
        if (_dragItem is not null && container is { IsSelected: true } && ThumbList.SelectedItems.Count > 1 &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredSingleSelect = _dragItem;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnThumbMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSingleSelect is not null && !_dragging)
        {
            var item = _deferredSingleSelect;
            ThumbList.SelectedItems.Clear();
            ThumbList.SelectedItem = item;
        }

        _deferredSingleSelect = null;
        _dragItem = null;
    }

    private void OnThumbMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed || _session is null) return;

        var position = e.GetPosition(ThumbList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!ThumbList.SelectedItems.Contains(_dragItem))
        {
            ThumbList.SelectedItems.Clear();
            ThumbList.SelectedItem = _dragItem;
        }

        _dragging = true;
        var pages = SelectedPages().ToArray();
        _dragItem = null;
        _deferredSingleSelect = null;

        try
        {
            DragDrop.DoDragDrop(ThumbList, new DataObject(DragFormat, pages), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void OnThumbDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnThumbDrop(object sender, DragEventArgs e)
    {
        if (_session is null || e.Data.GetData(DragFormat) is not int[] pages || pages.Length == 0) return;
        e.Handled = true;

        // Insertion point: before the item under the mouse, or after it if dropped on its right half.
        var count = _items.Count;
        var target = ItemFromSource(e.OriginalSource, out var container);
        var insertAt = count;
        if (target is not null && container is not null)
        {
            insertAt = _items.IndexOf(target);
            if (e.GetPosition(container).X > container.ActualWidth / 2) insertAt++;
        }

        var order = PageMath.MoveTo(count, pages, insertAt, out var newPositions);
        if (order is null) return;
        await ReorderAsync(order, newPositions, "Move pages");
    }

    // ---- Actions -----------------------------------------------------------

    private async Task<bool> ApplyAsync(Action<string, string> operation, string description, List<int>? reselect)
    {
        var session = _session;
        if (session is null) return false;

        _pendingSelection = reselect;
        var ok = await PanelHelpers.RunBusyAsync(_host, this, description,
            () => session.ApplyAsync(operation, description));
        _pendingSelection = null;
        if (ok) _host?.SetStatus(description + " - done. Ctrl+Z undoes it.");
        return ok;
    }

    private bool RequireSelection(out List<int> pages)
    {
        pages = SelectedPages();
        if (_session is not null && pages.Count > 0) return true;
        _host?.SetStatus("Select one or more pages first.");
        return false;
    }

    private async void OnRotateLeft(object sender, RoutedEventArgs e) => await RotateAsync(-90);

    private async void OnRotateRight(object sender, RoutedEventArgs e) => await RotateAsync(90);

    private async Task RotateAsync(int degrees)
    {
        if (!RequireSelection(out var pages)) return;
        var password = _session!.Password;
        await ApplyAsync((input, output) => PdfPageOps.RotatePages(input, output, pages, degrees, password),
            degrees < 0 ? "Rotate pages left" : "Rotate pages right", pages);
    }

    private async void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var pages)) return;
        var order = PageMath.MoveByOne(_items.Count, pages, up: true, out var newPositions);
        if (order is null) return;
        await ReorderAsync(order, newPositions, "Move pages up");
    }

    private async void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var pages)) return;
        var order = PageMath.MoveByOne(_items.Count, pages, up: false, out var newPositions);
        if (order is null) return;
        await ReorderAsync(order, newPositions, "Move pages down");
    }

    private async Task ReorderAsync(List<int> order, List<int> newPositions, string description)
    {
        var password = _session?.Password;
        await ApplyAsync((input, output) => PdfPageOps.ReorderPages(input, output, order, password),
            description, newPositions);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var pages)) return;
        if (pages.Count >= _items.Count)
        {
            PanelHelpers.ShowInfo(_host, "Delete pages", "A document must keep at least one page.");
            return;
        }

        var what = pages.Count == 1 ? $"page {pages[0]}" : $"{pages.Count} pages";
        if (!PanelHelpers.Confirm(_host, "Delete pages", $"Delete {what}? Ctrl+Z brings them back.")) return;

        var password = _session!.Password;
        await ApplyAsync((input, output) => PdfPageOps.DeletePages(input, output, pages, password),
            pages.Count == 1 ? "Delete page" : "Delete pages", null);
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var pages)) return;
        var session = _session!;

        var dialog = new SaveFileDialog
        {
            Title = "Extract pages to",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = PanelHelpers.DefaultFolder(session),
            FileName = Path.GetFileNameWithoutExtension(session.FileName) + "_extract.pdf",
            AddExtension = true,
            DefaultExt = ".pdf"
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var target = dialog.FileName;
        var file = session.CurrentFile;
        var password = session.Password;
        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Extract pages",
            () => Task.Run(() => PdfPageOps.ExtractPages(file, target, pages, password)));
        if (!ok) return;

        _host?.SetStatus($"Extracted {pages.Count} page(s) to {Path.GetFileName(target)}.");
        _host?.OpenDocument(target);
    }

    private int InsertAfter()
    {
        var pages = SelectedPages();
        return pages.Count > 0 ? pages.Max() : _items.Count;
    }

    private async void OnInsertFromFile(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Insert pages from",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = PanelHelpers.DefaultFolder(session)
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var other = dialog.FileName;
        string? otherPassword = null;
        int otherPages;
        try
        {
            if (PdfInfoReader.NeedsPassword(other))
            {
                otherPassword = MessageDialog.Prompt(_host?.Window,
                    $"'{Path.GetFileName(other)}' is password protected. Enter its password.",
                    "Password required", isPassword: true);
                if (otherPassword is null) return;
            }

            otherPages = PdfInfoReader.Read(other, otherPassword).PageCount;
        }
        catch (Exception ex)
        {
            PanelHelpers.ShowError(_host, "Insert pages", ex.Message);
            return;
        }

        var after = InsertAfter();
        var password = session.Password;
        var reselect = Enumerable.Range(after + 1, otherPages).ToList();
        await ApplyAsync((input, output) => PdfPageOps.InsertPages(input, output, other, after, password, otherPassword),
            "Insert pages", reselect);
    }

    private async void OnInsertBlank(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var after = InsertAfter();
        var password = session.Password;
        await ApplyAsync((input, output) => PdfPageOps.InsertBlankPage(input, output, after, password),
            "Insert blank page", [after + 1]);
    }

    private async void OnSplit(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        if (!int.TryParse(SplitSizeBox.Text.Trim(), out var size) || size < 1)
        {
            PanelHelpers.ShowInfo(_host, "Split", "Pages per file must be a whole number of 1 or more.");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Folder for the split files",
            InitialDirectory = PanelHelpers.DefaultFolder(session)
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var folder = dialog.FolderName;
        var password = session.Password;
        var scratch = PanelHelpers.NewScratchFolder("split");
        List<string> written = [];
        try
        {
            var copy = PanelHelpers.NamedCopy(session, scratch);
            var ok = await PanelHelpers.RunBusyAsync(_host, this, "Split",
                async () => written = await Task.Run(() => PdfPageOps.Split(copy, folder, size, password)));
            if (!ok) return;
        }
        finally
        {
            PanelHelpers.TryDeleteFolder(scratch);
        }

        _host?.SetStatus($"Split into {written.Count} file(s).");
        PanelHelpers.ShowInfo(_host, "Split", $"Wrote {written.Count} file(s) to:\n{folder}");
    }

    // ---- Item --------------------------------------------------------------

    /// <summary>One rendering pass over one version of the document; replaced on every change.</summary>
    private sealed class RenderJob
    {
        private readonly CancellationTokenSource _cts = new();

        public Queue<ThumbItem> Queue { get; } = new();

        public SemaphoreSlim Signal { get; } = new(0);

        public CancellationToken Token => _cts.Token;

        public void Cancel() => _cts.Cancel();
    }

    /// <summary>One thumbnail. Image arrives later from the render loop.</summary>
    public sealed class ThumbItem : INotifyPropertyChanged
    {
        private BitmapSource? _image;

        public ThumbItem(KcsPdfSigner.Core.PdfPageInfo page)
        {
            PageNumber = page.PageNumber;
            Label = page.PageNumber.ToString();

            var rotated = ((page.Rotation % 360) + 360) % 360 is 90 or 270;
            var width = Math.Max(1.0, rotated ? page.Height : page.Width);
            var height = Math.Max(1.0, rotated ? page.Width : page.Height);
            var scale = (BoxSize - 2) / Math.Max(width, height);
            FrameWidth = Math.Max(8, width * scale);
            FrameHeight = Math.Max(8, height * scale);

            // Render at about 1.5x the box so thumbnails stay sharp on scaled displays.
            Dpi = (int)Math.Clamp(Math.Round(BoxSize * 1.5 * 72 / Math.Max(width, height)), 8, 150);
        }

        public double BoxSize => 104;

        public int PageNumber { get; }

        public string Label { get; }

        public double FrameWidth { get; }

        public double FrameHeight { get; }

        public int Dpi { get; }

        /// <summary>Queued for rendering (UI thread only).</summary>
        public bool Requested { get; set; }

        public BitmapSource? Image
        {
            get => _image;
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
}

/// <summary>Pure page-order arithmetic used by Organize Pages (kept separate so it can be tested).</summary>
internal static class PageMath
{
    /// <summary>
    /// Moves the selected pages one place earlier (up) or later as a block-aware shuffle. Returns
    /// the new order (1-based page numbers in their new sequence) and where the selected pages end
    /// up, or null when nothing would move.
    /// </summary>
    public static List<int>? MoveByOne(int pageCount, IEnumerable<int> selected, bool up, out List<int> newPositions)
    {
        var set = selected.ToHashSet();
        var order = Enumerable.Range(1, pageCount).ToList();

        if (up)
        {
            for (var i = 1; i < order.Count; i++)
                if (set.Contains(order[i]) && !set.Contains(order[i - 1]))
                    (order[i], order[i - 1]) = (order[i - 1], order[i]);
        }
        else
        {
            for (var i = order.Count - 2; i >= 0; i--)
                if (set.Contains(order[i]) && !set.Contains(order[i + 1]))
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
        }

        newPositions = PositionsOf(order, set);
        return IsIdentity(order) ? null : order;
    }

    /// <summary>
    /// Moves the selected pages, as a block in page order, so they sit before position
    /// <paramref name="insertAt"/> (0-based index into the current order; pageCount = the end).
    /// </summary>
    public static List<int>? MoveTo(int pageCount, IEnumerable<int> selected, int insertAt, out List<int> newPositions)
    {
        var set = selected.Where(p => p >= 1 && p <= pageCount).ToHashSet();
        var order = Enumerable.Range(1, pageCount).ToList();
        insertAt = Math.Clamp(insertAt, 0, pageCount);

        var rest = order.Where(p => !set.Contains(p)).ToList();
        var position = order.Take(insertAt).Count(p => !set.Contains(p));
        var moved = order.Where(set.Contains).ToList();

        var result = rest.Take(position).Concat(moved).Concat(rest.Skip(position)).ToList();
        newPositions = PositionsOf(result, set);
        return IsIdentity(result) ? null : result;
    }

    private static List<int> PositionsOf(List<int> order, HashSet<int> set)
        => order.Select((page, index) => (page, index)).Where(t => set.Contains(t.page)).Select(t => t.index + 1).ToList();

    private static bool IsIdentity(List<int> order)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] != i + 1) return false;
        return true;
    }
}
