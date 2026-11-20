using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Multiple PDFs: take chosen pages from several PDFs, put them in the order you want (drag, move
/// buttons, rotate, remove) and save them as one new PDF. Thumbnails are rendered lazily on a
/// background thread, so PDFs with hundreds of pages stay responsive.
/// </summary>
public partial class PageBasketPanel : UserControl, IToolPanel
{
    private const string DragFormat = "KcsPdfStudio.PageBasket";

    private readonly ObservableCollection<SourceFile> _sources = [];
    private readonly ObservableCollection<PageThumb> _pageThumbs = [];
    private readonly ObservableCollection<BasketItem> _basket = [];
    private readonly List<PageThumb> _pickOrder = [];

    private readonly Queue<PageThumb> _renderQueue = new();
    private readonly SemaphoreSlim _renderSignal = new(0);
    private readonly CancellationTokenSource _renderStop = new();
    private bool _renderStarted;
    private bool _visibleUpdatePending;

    private IStudioHost? _host;
    private DocumentSession? _session;
    private bool _suppressSelection;

    private Point _dragStart;
    private BasketItem? _dragItem;
    private BasketItem? _deferredSingleSelect;
    private bool _dragging;

    public PageBasketPanel()
    {
        InitializeComponent();
        SourceList.ItemsSource = _sources;
        PageList.ItemsSource = _pageThumbs;
        BasketList.ItemsSource = _basket;
        SizeChanged += (_, _) => ScheduleVisibleUpdate();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Multiple PDFs";

    public string Description => "Pick pages from several PDFs, arrange them and save one new PDF.";

    public string IconKey => "Icon.Combine";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host)
    {
        _host = host;
        host.DocumentClosed += (_, _) => UpdateState();
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        _session = session;
        UpdateState();
        ScheduleVisibleUpdate();
    }

    public void Detach() => _session = null;

    // ---- Sources -----------------------------------------------------------

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add PDFs",
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true,
            InitialDirectory = PanelHelpers.DefaultFolder(_session)
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        foreach (var path in dialog.FileNames) await AddSourceAsync(path, null);
    }

    private async void OnAddOpen(object sender, RoutedEventArgs e)
    {
        var session = _session ?? _host?.ActiveSession;
        if (session is null)
        {
            _host?.SetStatus("Open a PDF first, or use Add PDFs.");
            return;
        }

        // A private copy: the working file disappears when the tab is closed.
        var folder = PanelHelpers.NewScratchFolder("basket");
        var copy = Path.Combine(folder, session.FileName);
        try
        {
            session.SaveCopy(copy);
        }
        catch (Exception ex)
        {
            PanelHelpers.ShowError(_host, "Multiple PDFs", ex.Message);
            return;
        }

        await AddSourceAsync(copy, session.Password);
    }

    private void OnSourcesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSourcesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        foreach (var file in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            await AddSourceAsync(file, null);
    }

    private async Task AddSourceAsync(string path, string? password)
    {
        var full = Path.GetFullPath(path);
        var existing = _sources.FirstOrDefault(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SourceList.SelectedItem = existing;
            return;
        }

        IReadOnlyList<PdfPageInfo> pages;
        try
        {
            if (password is null && PdfInfoReader.NeedsPassword(full))
            {
                password = MessageDialog.Prompt(_host?.Window,
                    $"'{Path.GetFileName(full)}' is password protected. Enter its owner password " +
                    "(the one that also lets you change the file) so its pages can be copied.",
                    "Password required", isPassword: true);
                if (password is null) return;
            }

            var pw = password;
            pages = await Task.Run(() => PdfPageReader.ReadPages(full, pw));
        }
        catch (Exception ex)
        {
            PanelHelpers.ShowError(_host, "Multiple PDFs", $"'{Path.GetFileName(full)}': {ex.Message}");
            return;
        }

        if (pages.Count == 0)
        {
            PanelHelpers.ShowInfo(_host, "Multiple PDFs", $"'{Path.GetFileName(full)}' has no pages.");
            return;
        }

        var source = new SourceFile(full, password, pages);
        _sources.Add(source);
        SourceList.SelectedItem = source;
        UpdateState();
    }

    private void OnRemoveSource(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceFile source) return;

        var used = _basket.Count(b => ReferenceEquals(b.Thumb.Source, source));
        if (used > 0 && !PanelHelpers.Confirm(_host, "Remove file",
                $"{used} page(s) from '{source.Name}' are in the new PDF and will be taken out. Continue?"))
            return;

        foreach (var item in _basket.Where(b => ReferenceEquals(b.Thumb.Source, source)).ToList()) _basket.Remove(item);
        _sources.Remove(source);
        Renumber();
        UpdateState();
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        _pageThumbs.Clear();
        _pickOrder.Clear();
        if (SourceList.SelectedItem is SourceFile source)
        {
            foreach (var thumb in source.Thumbs) _pageThumbs.Add(thumb);
            PickHeading.Text = $"2. Pick pages from {source.Name}";
        }
        else
        {
            PickHeading.Text = "2. Pick pages";
        }

        UpdateState();
        ScheduleVisibleUpdate();
    }

    // ---- Picking pages -----------------------------------------------------

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Remember the order the pages were clicked in: that is the order they are added in.
        foreach (var removed in e.RemovedItems.OfType<PageThumb>()) _pickOrder.Remove(removed);
        foreach (var added in e.AddedItems.OfType<PageThumb>()) if (!_pickOrder.Contains(added)) _pickOrder.Add(added);
        UpdateState();
    }

    private void OnSelectAllPages(object sender, RoutedEventArgs e) => PageList.SelectAll();

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (_pickOrder.Count == 0)
        {
            _host?.SetStatus("Click one or more pages first.");
            return;
        }

        AddToBasket(_pickOrder.ToList());
        PageList.SelectedItems.Clear();
    }

    private void OnAddAll(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is SourceFile source) AddToBasket(source.Thumbs);
    }

    private void OnPageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PageList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: PageThumb thumb })
        {
            AddToBasket([thumb]);
            e.Handled = true;
        }
    }

    private void OnRangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddRange(sender, e);
    }

    private void OnAddRange(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceFile source)
        {
            _host?.SetStatus("Add a PDF first.");
            return;
        }

        var numbers = ParseRangeInOrder(RangeBox.Text, source.Pages.Count, out var error);
        if (numbers is null)
        {
            PanelHelpers.ShowInfo(_host, "Multiple PDFs", error ?? "Could not read the page numbers.");
            return;
        }

        AddToBasket(numbers.Select(n => source.Thumbs[n - 1]).ToList());
        RangeBox.Clear();
    }

    /// <summary>"3-5, 9" becomes 3,4,5,9 in the order typed ("5-3" counts down). Null with a message when invalid.</summary>
    internal static List<int>? ParseRangeInOrder(string? text, int pageCount, out string? error)
    {
        error = null;
        var result = new List<int>();
        var parts = (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Type the pages you want, for example 3-5, 9.";
            return null;
        }

        foreach (var part in parts)
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from) ||
                !int.TryParse(bounds[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to))
            {
                error = $"'{part}' is not a page or a range such as 3-5.";
                return null;
            }

            if (from < 1 || to < 1 || from > pageCount || to > pageCount)
            {
                error = $"'{part}' is outside this file, which has {pageCount} page(s).";
                return null;
            }

            var step = from <= to ? 1 : -1;
            for (var n = from; n != to + step; n += step) result.Add(n);
        }

        return result;
    }

    private void AddToBasket(IReadOnlyList<PageThumb> thumbs)
    {
        if (thumbs.Count == 0) return;

        var added = new List<BasketItem>();
        foreach (var thumb in thumbs)
        {
            var item = new BasketItem(thumb);
            _basket.Add(item);
            added.Add(item);
        }

        Renumber();
        _suppressSelection = true;
        try
        {
            BasketList.SelectedItems.Clear();
            foreach (var item in added) BasketList.SelectedItems.Add(item);
        }
        finally
        {
            _suppressSelection = false;
        }

        BasketList.ScrollIntoView(added[^1]);
        UpdateState();
        ScheduleVisibleUpdate();
        _host?.SetStatus($"Added {added.Count} page(s). The new PDF has {_basket.Count} page(s).");
    }

    // ---- Arranging ---------------------------------------------------------

    private List<int> SelectedPositions() =>
        BasketList.SelectedItems.OfType<BasketItem>().Select(i => _basket.IndexOf(i) + 1).OrderBy(p => p).ToList();

    private bool RequireSelection(out List<int> positions)
    {
        positions = SelectedPositions();
        if (positions.Count > 0) return true;
        _host?.SetStatus("Select one or more pages in the new PDF first.");
        return false;
    }

    private void ApplyOrder(List<int> order, List<int> newPositions)
    {
        var items = order.Select(p => _basket[p - 1]).ToList();
        var selected = newPositions.Select(p => items[p - 1]).ToList();

        _suppressSelection = true;
        try
        {
            _basket.Clear();
            foreach (var item in items) _basket.Add(item);
            Renumber();

            BasketList.SelectedItems.Clear();
            foreach (var item in selected) BasketList.SelectedItems.Add(item);
            if (selected.Count > 0) BasketList.ScrollIntoView(selected[0]);
        }
        finally
        {
            _suppressSelection = false;
        }

        UpdateState();
        ScheduleVisibleUpdate();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var positions)) return;
        var order = PageMath.MoveByOne(_basket.Count, positions, up: true, out var now);
        if (order is not null) ApplyOrder(order, now);
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var positions)) return;
        var order = PageMath.MoveByOne(_basket.Count, positions, up: false, out var now);
        if (order is not null) ApplyOrder(order, now);
    }

    private void OnToTop(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var positions)) return;
        var order = PageMath.MoveTo(_basket.Count, positions, 0, out var now);
        if (order is not null) ApplyOrder(order, now);
    }

    private void OnToBottom(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var positions)) return;
        var order = PageMath.MoveTo(_basket.Count, positions, _basket.Count, out var now);
        if (order is not null) ApplyOrder(order, now);
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-90);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(int degrees)
    {
        if (!RequireSelection(out _)) return;
        foreach (var item in BasketList.SelectedItems.OfType<BasketItem>()) item.Rotation = (item.Rotation + degrees + 360) % 360;
    }

    private void OnRemovePages(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out _)) return;
        foreach (var item in BasketList.SelectedItems.OfType<BasketItem>().ToList()) _basket.Remove(item);
        Renumber();
        UpdateState();
    }

    private void OnClearBasket(object sender, RoutedEventArgs e)
    {
        if (_basket.Count == 0) return;
        if (!PanelHelpers.Confirm(_host, "Clear", $"Take all {_basket.Count} pages out of the new PDF?")) return;
        _basket.Clear();
        UpdateState();
    }

    private void OnBasketKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        OnRemovePages(sender, e);
    }

    private void OnBasketSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelection) UpdateState();
    }

    private void Renumber()
    {
        for (var i = 0; i < _basket.Count; i++) _basket[i].Position = i + 1;
    }

    // ---- Drag to reorder ---------------------------------------------------

    private BasketItem? ItemFromSource(object source, out ListBoxItem? container)
    {
        container = source is DependencyObject element
            ? ItemsControl.ContainerFromElement(BasketList, element) as ListBoxItem
            : null;
        return container?.DataContext as BasketItem;
    }

    private void OnBasketMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(BasketList);
        _dragItem = ItemFromSource(e.OriginalSource, out var container);
        _deferredSingleSelect = null;
        _dragging = false;

        // Keep a multi-selection intact when the user presses on it to drag it.
        if (_dragItem is not null && container is { IsSelected: true } && BasketList.SelectedItems.Count > 1 &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredSingleSelect = _dragItem;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnBasketMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSingleSelect is not null && !_dragging)
        {
            var item = _deferredSingleSelect;
            BasketList.SelectedItems.Clear();
            BasketList.SelectedItem = item;
        }

        _deferredSingleSelect = null;
        _dragItem = null;
    }

    private void OnBasketMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(BasketList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!BasketList.SelectedItems.Contains(_dragItem))
        {
            BasketList.SelectedItems.Clear();
            BasketList.SelectedItem = _dragItem;
        }

        _dragging = true;
        var positions = SelectedPositions().ToArray();
        _dragItem = null;
        _deferredSingleSelect = null;

        try
        {
            DragDrop.DoDragDrop(BasketList, new DataObject(DragFormat, positions), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void OnBasketDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBasketDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not int[] positions || positions.Length == 0) return;
        e.Handled = true;

        // Insertion point: before the page under the mouse, or after it when dropped on its right half.
        var insertAt = _basket.Count;
        var target = ItemFromSource(e.OriginalSource, out var container);
        if (target is not null && container is not null)
        {
            insertAt = _basket.IndexOf(target);
            if (e.GetPosition(container).X > container.ActualWidth / 2) insertAt++;
        }

        var order = PageMath.MoveTo(_basket.Count, positions, insertAt, out var now);
        if (order is not null) ApplyOrder(order, now);
    }

    // ---- Saving ------------------------------------------------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_basket.Count == 0)
        {
            _host?.SetStatus("Add some pages first.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save the new PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = PanelHelpers.DefaultFolder(_session),
            FileName = "Combined.pdf",
            AddExtension = true,
            DefaultExt = ".pdf"
        };
        if (dialog.ShowDialog(_host?.Window) != true) return;

        var target = dialog.FileName;
        var selection = _basket
            .Select(b => new PageSelection(b.Thumb.Source.Path, b.Thumb.PageNumber, b.Rotation, b.Thumb.Source.Password))
            .ToList();

        if (selection.Any(s => string.Equals(Path.GetFullPath(s.File), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)))
        {
            PanelHelpers.ShowInfo(_host, "Multiple PDFs", "Save to a different file name: the new PDF cannot replace one of its own sources.");
            return;
        }

        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Build the new PDF",
            () => Task.Run(() => PdfPageOps.BuildFromSelection(selection, target)));
        if (!ok) return;

        var files = _basket.Select(b => b.Thumb.Source).Distinct().Count();
        _host?.SetStatus($"Saved {selection.Count} page(s) from {files} file(s) to {Path.GetFileName(target)}.");
        if (OpenResultBox.IsChecked == true) _host?.OpenDocument(target);
    }

    // ---- State -------------------------------------------------------------

    private void UpdateState()
    {
        var hasSource = _sources.Count > 0;
        var hasPages = SourceList.SelectedItem is SourceFile;
        var selectedInBasket = BasketList.SelectedItems.Count;

        AddOpenButton.IsEnabled = _session is not null || _host?.ActiveSession is not null;
        RemoveSourceButton.IsEnabled = hasPages;
        PageList.IsEnabled = hasPages;
        AddSelectedButton.IsEnabled = hasPages && _pickOrder.Count > 0;
        AddAllButton.IsEnabled = hasPages;
        SelectAllButton.IsEnabled = hasPages;
        AddRangeButton.IsEnabled = hasPages;
        RangeBox.IsEnabled = hasPages;

        foreach (var button in new[] { TopButton, UpButton, DownButton, BottomButton, RotateLeftButton, RotateRightButton, RemovePagesButton })
            button.IsEnabled = selectedInBasket > 0;
        ClearButton.IsEnabled = _basket.Count > 0;
        SaveButton.IsEnabled = _basket.Count > 0;

        var files = _basket.Select(b => b.Thumb.Source).Distinct().Count();
        BasketHeading.Text = $"3. Your new PDF ({_basket.Count} page{(_basket.Count == 1 ? "" : "s")})";
        SummaryText.Text = _basket.Count == 0
            ? (hasSource ? "Nothing added yet." : "Add PDFs to start.")
            : $"{_basket.Count} page(s) from {files} file(s)" + (selectedInBasket > 0 ? $", {selectedInBasket} selected." : ".");
    }

    // ---- Lazy thumbnails ---------------------------------------------------

    private void OnListScrolled(object sender, ScrollChangedEventArgs e) => ScheduleVisibleUpdate();

    private void ScheduleVisibleUpdate()
    {
        if (_visibleUpdatePending) return;
        _visibleUpdatePending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _visibleUpdatePending = false;
            QueueVisible(PageList, o => (o as PageThumb));
            QueueVisible(BasketList, o => (o as BasketItem)?.Thumb);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Queues the thumbnails in view (plus a screen ahead) that are not rendered yet.</summary>
    private void QueueVisible(ListBox list, Func<object, PageThumb?> thumbOf)
    {
        if (!IsVisible || list.Items.Count == 0) return;

        var height = list.ActualHeight;
        var added = false;

        for (var i = 0; i < list.Items.Count; i++)
        {
            var thumb = thumbOf(list.Items[i]);
            if (thumb is null || thumb.Requested) continue;
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container || !container.IsVisible) continue;

            var top = container.TranslatePoint(new Point(0, 0), list).Y;
            if (top + container.ActualHeight < -height * 0.5 || top > height * 2) continue;

            thumb.Requested = true;
            lock (_renderQueue) _renderQueue.Enqueue(thumb);
            added = true;
        }

        if (!added) return;

        if (!_renderStarted)
        {
            _renderStarted = true;
            _ = Task.Run(RenderLoop);
        }

        if (_renderSignal.CurrentCount == 0) _renderSignal.Release();
    }

    private async Task RenderLoop()
    {
        var token = _renderStop.Token;
        while (!token.IsCancellationRequested)
        {
            PageThumb? next;
            lock (_renderQueue) _renderQueue.TryDequeue(out next);

            if (next is null)
            {
                try
                {
                    await _renderSignal.WaitAsync(token);
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
                var bytes = next.Source.GetBytes();
                image = PageRenderer.Render(bytes, next.PageNumber, next.Source.Password, next.Dpi);
            }
            catch
            {
                // A page that will not render keeps its empty frame.
            }

            var thumb = next;
            _ = Dispatcher.BeginInvoke(() => thumb.Image = image, DispatcherPriority.Background);
        }
    }

    // ---- Items -------------------------------------------------------------

    /// <summary>One PDF the pages come from.</summary>
    public sealed class SourceFile
    {
        private byte[]? _bytes;
        private readonly object _gate = new();

        public SourceFile(string path, string? password, IReadOnlyList<PdfPageInfo> pages)
        {
            Path = path;
            Password = password;
            Pages = pages;
            Name = System.IO.Path.GetFileName(path);
            Thumbs = pages.Select(p => new PageThumb(this, p)).ToList();
        }

        public string Path { get; }

        public string Name { get; }

        public string? Password { get; }

        public IReadOnlyList<PdfPageInfo> Pages { get; }

        public List<PageThumb> Thumbs { get; }

        public string PageText => Pages.Count == 1 ? "1 page" : $"{Pages.Count} pages";

        /// <summary>The file's bytes, read once on first use (called on the render thread).</summary>
        public byte[] GetBytes()
        {
            lock (_gate) return _bytes ??= File.ReadAllBytes(Path);
        }
    }

    /// <summary>One page of one source, shown as a thumbnail whose image arrives later.</summary>
    public sealed class PageThumb : INotifyPropertyChanged
    {
        private BitmapSource? _image;

        public PageThumb(SourceFile source, PdfPageInfo page)
        {
            Source = source;
            PageNumber = page.PageNumber;

            var rotated = ((page.Rotation % 360) + 360) % 360 is 90 or 270;
            Width = Math.Max(1.0, rotated ? page.Height : page.Width);
            Height = Math.Max(1.0, rotated ? page.Width : page.Height);

            var scale = (BoxSize - 2) / Math.Max(Width, Height);
            FrameWidth = Math.Max(8, Width * scale);
            FrameHeight = Math.Max(8, Height * scale);
            Dpi = (int)Math.Clamp(Math.Round(BoxSize * 1.5 * 72 / Math.Max(Width, Height)), 8, 150);
        }

        public SourceFile Source { get; }

        public int PageNumber { get; }

        public double BoxSize => 96;

        public double Width { get; }

        public double Height { get; }

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One page of the new PDF: a source page plus an extra rotation and its place in the order.</summary>
    public sealed class BasketItem : INotifyPropertyChanged
    {
        private int _rotation;
        private int _position;

        public BasketItem(PageThumb thumb)
        {
            Thumb = thumb;
            thumb.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageThumb.Image)) OnChanged(nameof(Image));
            };
        }

        public PageThumb Thumb { get; }

        public BitmapSource? Image => Thumb.Image;

        public double BoxSize => Thumb.BoxSize;

        public int Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
                OnChanged(nameof(Rotation));
                OnChanged(nameof(FrameWidth));
                OnChanged(nameof(FrameHeight));
            }
        }

        private bool Sideways => _rotation is 90 or 270;

        public double FrameWidth => Sideways ? Thumb.FrameHeight : Thumb.FrameWidth;

        public double FrameHeight => Sideways ? Thumb.FrameWidth : Thumb.FrameHeight;

        public int Position
        {
            get => _position;
            set
            {
                _position = value;
                OnChanged(nameof(Position));
            }
        }

        /// <summary>File name and original page number, shown under the thumbnail.</summary>
        public string SourceLabel => $"{Thumb.Source.Name} p.{Thumb.PageNumber}";

        public string Tooltip => $"{Thumb.Source.Path}\nOriginal page {Thumb.PageNumber}" + (_rotation != 0 ? $", turned {_rotation} degrees" : "");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
