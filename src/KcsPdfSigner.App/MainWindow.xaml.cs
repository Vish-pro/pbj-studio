using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Tools;
using KcsPdfSigner.App.Viewer;
using Microsoft.Win32;

namespace KcsPdfSigner.App;

/// <summary>
/// PB&J Studio main window: menu, toolbar, navigation pane, document tabs, tools pane and
/// status bar. Each tab owns one <see cref="DocumentSession"/> and one <see cref="PdfViewer"/>.
/// </summary>
public partial class MainWindow : Window, IStudioHost
{
    /// <summary>One open document.</summary>
    private sealed class DocumentTab
    {
        public required DocumentSession Session { get; init; }
        public required PdfViewer Viewer { get; init; }
        public required TabItem Item { get; init; }
    }

    /// <summary>Row in the tools list.</summary>
    public sealed class ToolEntry
    {
        public required IToolPanel Panel { get; init; }
        public string Title => Panel.Title;
        public string Description => Panel.Description;
        public Geometry? Icon { get; init; }
        public bool IsPrimary { get; init; }
    }

    private readonly List<DocumentTab> _tabs = [];
    private readonly List<ToolEntry> _tools = [];
    private DocumentTab? _active;
    private ToolEntry? _openTool;

    private bool _updatingZoomBox;
    private bool _isFullScreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private GridLength _navWidth = new(230);
    private GridLength _toolsWidth = new(400);

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);

        OpenCommand = new RelayCommand(OpenWithDialog);
        SaveCommand = new RelayCommand(Save, () => _active is not null);
        SaveAsCommand = new RelayCommand(SaveAs, () => _active is not null);
        PrintCommand = new RelayCommand(Print, () => _active?.Viewer.DocumentBytes is not null);
        CloseTabCommand = new RelayCommand(() => { if (_active is not null) CloseTab(_active); }, () => _active is not null);
        ExitCommand = new RelayCommand(Close);
        UndoCommand = new RelayCommand(Undo, () => _active?.Session.CanUndo == true);
        RedoCommand = new RelayCommand(Redo, () => _active?.Session.CanRedo == true);
        FindCommand = new RelayCommand(FocusFindBox);
        GoToPageCommand = new RelayCommand(GoToPagePrompt, HasDocument);
        FitPageCommand = new RelayCommand(() => _active?.Viewer.FitPage(), HasDocument);
        FitWidthCommand = new RelayCommand(() => _active?.Viewer.FitWidth(), HasDocument);
        ActualSizeCommand = new RelayCommand(() => _active?.Viewer.ActualSize(), HasDocument);
        ZoomInCommand = new RelayCommand(() => _active?.Viewer.ZoomIn(), HasDocument);
        ZoomOutCommand = new RelayCommand(() => _active?.Viewer.ZoomOut(), HasDocument);
        RotateClockwiseCommand = new RelayCommand(() => _active?.Viewer.RotateClockwise(), HasDocument);
        RotateCounterClockwiseCommand = new RelayCommand(() => _active?.Viewer.RotateCounterClockwise(), HasDocument);
        PreviousPageCommand = new RelayCommand(() => _active?.Viewer.PreviousPage(),
            () => _active is not null && _active.Viewer.CurrentPage > 1);
        NextPageCommand = new RelayCommand(() => _active?.Viewer.NextPage(),
            () => _active is not null && _active.Viewer.CurrentPage < _active.Viewer.PageCount);
        FirstPageCommand = new RelayCommand(() => _active?.Viewer.FirstPage(), HasDocument);
        LastPageCommand = new RelayCommand(() => _active?.Viewer.LastPage(), HasDocument);
        FullScreenCommand = new RelayCommand(ToggleFullScreen);
        EscapeCommand = new RelayCommand(Escape);
        DocumentPropertiesCommand = new RelayCommand(ShowProperties, HasDocument);
        ShowShortcutsCommand = new RelayCommand(ShowShortcuts);
        AboutCommand = new RelayCommand(ShowAbout);

        DataContext = this;
        InitFeatures();

        NavPane.PageRequested += (_, page) => _active?.Viewer.GoToPage(page);
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += OnWindowClosing;
        FindBox.TextChanged += (_, _) =>
            FindHint.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        DocumentSession.CleanStaleFolders(TimeSpan.FromDays(2));

        RegisterTools();
        BuildRecentMenu();
        UpdateChrome();

        // The signing tool is the star feature: it is open from the start.
        if (_tools.Count > 0) OpenToolEntry(_tools[0]);
    }

    // ======================================================================
    // Commands
    // ======================================================================

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand FindCommand { get; }
    public RelayCommand GoToPageCommand { get; }
    public RelayCommand FitPageCommand { get; }
    public RelayCommand FitWidthCommand { get; }
    public RelayCommand ActualSizeCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand RotateClockwiseCommand { get; }
    public RelayCommand RotateCounterClockwiseCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand FullScreenCommand { get; }
    public RelayCommand EscapeCommand { get; }
    public RelayCommand DocumentPropertiesCommand { get; }
    public RelayCommand ShowShortcutsCommand { get; }
    public RelayCommand AboutCommand { get; }

    private bool HasDocument() => _active is not null && _active.Viewer.PageCount > 0;

    // ======================================================================
    // IStudioHost
    // ======================================================================

    Window IStudioHost.Window => this;

    public DocumentSession? ActiveSession => _active?.Session;

    public PdfViewer? ActiveViewer => _active?.Viewer;

    public event EventHandler<string>? FindRequested;

    public event EventHandler<DocumentSession?>? ActiveDocumentChanged;

    public event EventHandler<DocumentSession>? DocumentClosed;

    public DocumentSession? OpenDocument(string path) => OpenFile(path);

    public void SetStatus(string message) => StatusText.Text = message;

    public bool OpenTool(string title)
    {
        var entry = _tools.FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        OpenToolEntry(entry);
        return true;
    }

    // ======================================================================
    // Opening and closing documents
    // ======================================================================

    private void OpenWithDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open PDF",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) OpenFile(file);
    }

    /// <summary>
    /// Opens a PDF in a new tab, asking for a password if needed. If it is already open, its tab
    /// is activated instead. Returns null when the user cancelled or the file could not be opened.
    /// </summary>
    public DocumentSession? OpenFile(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Could not open", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        var existing = _tabs.FirstOrDefault(t =>
            string.Equals(t.Session.OriginalPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            DocumentTabs.SelectedItem = existing.Item;
            return existing.Session;
        }

        if (!File.Exists(fullPath))
        {
            MessageDialog.Show(this, $"The file was not found:\n{fullPath}", "Could not open",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RecentFiles.Remove(fullPath);
            BuildRecentMenu();
            return null;
        }

        DocumentSession? session = null;
        string? password = null;

        while (session is null)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                session = DocumentSession.Open(fullPath, password);
            }
            catch (PasswordRequiredException ex)
            {
                Mouse.OverrideCursor = null;
                password = MessageDialog.Prompt(this, ex.Message + "\n\nEnter the password to open it.",
                    "Password required", isPassword: true);
                if (password is null) return null;
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageDialog.Show(this, $"Could not open {Path.GetFileName(fullPath)}.\n\n{ex.Message}",
                    "Could not open", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        var viewer = new PdfViewer();
        var item = new TabItem { Content = viewer, Header = BuildTabHeader(session), ToolTip = fullPath };
        var tab = new DocumentTab { Session = session, Viewer = viewer, Item = item };
        item.Tag = tab;

        session.PropertyChanged += OnSessionPropertyChanged;
        viewer.CurrentPageChanged += OnViewerPageChanged;
        viewer.ZoomChanged += OnViewerZoomChanged;

        _tabs.Add(tab);
        DocumentTabs.Items.Add(item);
        viewer.ShowSession(session);
        DocumentTabs.SelectedItem = item;

        RecentFiles.Add(fullPath);
        BuildRecentMenu();
        SetStatus($"Opened {session.FileName} ({session.PageCount} page(s)).");
        AttachFeatures(tab);
        return session;
    }

    private object BuildTabHeader(DocumentSession session)
    {
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(DocumentSession.DisplayName)) { Source = session });

        var close = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Padding = new Thickness(2),
            Margin = new Thickness(8, 0, -4, 0),
            ToolTip = "Close (Ctrl+W)",
            Focusable = false,
            Content = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("IconPath"),
                Data = (Geometry)FindResource("Icon.Close"),
                Width = 12,
                Height = 12,
                RenderTransform = new ScaleTransform(0.75, 0.75),
                RenderTransformOrigin = new Point(0, 0)
            }
        };
        close.Click += (_, _) =>
        {
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session, session));
            if (tab is not null) CloseTab(tab);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(name);
        panel.Children.Add(close);
        panel.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session, session));
            if (tab is not null) CloseTab(tab);
        };
        return panel;
    }

    /// <summary>Closes a tab, offering to save changes first. Returns false if the user cancelled.</summary>
    private bool CloseTab(DocumentTab tab)
    {
        if (tab.Session.IsDirty)
        {
            DocumentTabs.SelectedItem = tab.Item;
            var answer = MessageDialog.Show(this, $"Save changes to {tab.Session.FileName}?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !TrySave(tab)) return false;
        }

        if (ReferenceEquals(tab, _active))
        {
            _openTool?.Panel.Detach();
        }

        tab.Session.PropertyChanged -= OnSessionPropertyChanged;
        tab.Viewer.CurrentPageChanged -= OnViewerPageChanged;
        tab.Viewer.ZoomChanged -= OnViewerZoomChanged;

        _tabs.Remove(tab);
        DocumentTabs.Items.Remove(tab.Item);
        tab.Viewer.Clear();
        tab.Session.Dispose();
        DocumentClosed?.Invoke(this, tab.Session);

        if (_tabs.Count == 0) SetActive(null);
        return true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmCloseMany())
        {
            e.Cancel = true;
            return;
        }

        foreach (var tab in _tabs.Where(t => t.Session.IsDirty).ToList())
        {
            DocumentTabs.SelectedItem = tab.Item;
            var answer = MessageDialog.Show(this, $"Save changes to {tab.Session.FileName}?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !TrySave(tab)))
            {
                e.Cancel = true;
                return;
            }
        }

        RememberSession();
        _openTool?.Panel.Detach();
        foreach (var tab in _tabs) tab.Session.Dispose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPdfs(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var files = DroppedPdfs(e);
        e.Handled = true;
        foreach (var file in files) OpenFile(file);
        if (files.Count > 0) Activate();
    }

    private static List<string> DroppedPdfs(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
        return ((string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [])
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .ToList();
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var recent = RecentFiles.Load();

        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        var number = 1;
        foreach (var path in recent)
        {
            var label = number <= 9 ? $"_{number} {Path.GetFileName(path)}" : Path.GetFileName(path);
            var item = new MenuItem { Header = label, ToolTip = path };
            var target = path;
            item.Click += (_, _) => OpenFile(target);
            RecentMenu.Items.Add(item);
            number++;
        }

        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear list" };
        clear.Click += (_, _) =>
        {
            RecentFiles.Clear();
            BuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
    }

    // ======================================================================
    // Active document
    // ======================================================================

    private void OnDocumentTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from lists inside the tabs; only react to the tab strip.
        if (!ReferenceEquals(e.OriginalSource, DocumentTabs)) return;
        SetActive((DocumentTabs.SelectedItem as TabItem)?.Tag as DocumentTab);
    }

    private void SetActive(DocumentTab? tab)
    {
        if (ReferenceEquals(tab, _active) && tab is not null) return;

        ResetPointerTool();
        _openTool?.Panel.Detach();
        _active = tab;

        NavPane.Attach(tab?.Session, tab?.Viewer);
        _openTool?.Panel.Attach(tab?.Session, tab?.Viewer);

        UpdateChrome();
        ActiveDocumentChanged?.Invoke(this, tab?.Session);
        CommandManager.InvalidateRequerySuggested();

        tab?.Viewer.Focus();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _active?.Session)) return;
        if (e.PropertyName is nameof(DocumentSession.IsDirty) or nameof(DocumentSession.DisplayName)
            or nameof(DocumentSession.PageCount))
        {
            UpdateChrome();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnViewerPageChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _active?.Viewer)) UpdatePageDisplay();
    }

    private void OnViewerZoomChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _active?.Viewer)) UpdateZoomDisplay();
    }

    /// <summary>Refreshes title, page box, zoom box and status bar for the active document.</summary>
    private void UpdateChrome()
    {
        WelcomePanel.Visibility = _active is null ? Visibility.Visible : Visibility.Collapsed;
        DocumentTabs.Visibility = _active is null ? Visibility.Hidden : Visibility.Visible;

        Title = _active is null ? App.ProductName : $"{_active.Session.DisplayName} - {App.ProductName}";
        DirtyStatus.Text = _active?.Session.IsDirty == true ? "Unsaved changes" : string.Empty;

        UpdatePageDisplay();
        UpdateZoomDisplay();
    }

    private void UpdatePageDisplay()
    {
        var viewer = _active?.Viewer;
        if (viewer is null || viewer.PageCount == 0)
        {
            PageBox.Text = string.Empty;
            PageCountText.Text = "/ 0";
            PageStatus.Text = string.Empty;
            return;
        }

        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = viewer.CurrentPage.ToString(CultureInfo.InvariantCulture);
        PageCountText.Text = $"/ {viewer.PageCount}";
        PageStatus.Text = $"Page {viewer.CurrentPage} of {viewer.PageCount}";
    }

    private void UpdateZoomDisplay()
    {
        var viewer = _active?.Viewer;
        _updatingZoomBox = true;
        try
        {
            if (viewer is null)
            {
                ZoomBox.Text = string.Empty;
                ZoomStatus.Text = string.Empty;
                return;
            }

            var percent = $"{viewer.Zoom * 100:0}%";
            if (!ZoomBox.IsKeyboardFocusWithin) ZoomBox.Text = percent;
            ZoomStatus.Text = percent;
        }
        finally
        {
            _updatingZoomBox = false;
        }
    }

    // ======================================================================
    // Toolbar inputs
    // ======================================================================

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _active is not null)
        {
            if (int.TryParse(PageBox.Text.Trim(), out var page)) _active.Viewer.GoToPage(page);
            _active.Viewer.Focus();
            UpdatePageDisplay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _active?.Viewer.Focus();
            UpdatePageDisplay();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdatePageDisplay();

    private void OnZoomBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingZoomBox || ZoomBox.SelectedItem is not ComboBoxItem { Content: string text }) return;
        ApplyZoomText(text);
    }

    private void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyZoomText(ZoomBox.Text);
        _active?.Viewer.Focus();
        e.Handled = true;
    }

    private void ApplyZoomText(string text)
    {
        var viewer = _active?.Viewer;
        if (viewer is null) return;

        text = text.Trim();
        if (text.Equals("Fit width", StringComparison.OrdinalIgnoreCase)) viewer.FitWidth();
        else if (text.Equals("Fit page", StringComparison.OrdinalIgnoreCase)) viewer.FitPage();
        else if (double.TryParse(text.TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.CurrentCulture, out var percent)
                 || double.TryParse(text.TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
        {
            viewer.Zoom = percent / 100.0;
        }

        Dispatcher.BeginInvoke(UpdateZoomDisplay, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _active?.Viewer.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var query = FindBox.Text.Trim();
        if (query.Length == 0) return;

        if (FindRequested is null)
        {
            // A Search tool subscribes when registered; open it so it can pick the query up.
            OpenTool("Search");
        }

        if (FindRequested is null)
        {
            SetStatus("Text search is not available in this build yet.");
            return;
        }

        FindRequested.Invoke(this, query);
    }

    private void FocusFindBox()
    {
        if (_isFullScreen) ToggleFullScreen();
        FindBox.Focus();
        FindBox.SelectAll();
    }

    // ======================================================================
    // File commands
    // ======================================================================

    private void Save()
    {
        if (_active is not null) TrySave(_active);
    }

    private bool TrySave(DocumentTab tab)
    {
        try
        {
            tab.Session.Save();
            SetStatus($"Saved {tab.Session.FileName}.");
            return true;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Could not save {tab.Session.FileName}.\n\n{ex.Message}\n\nTry Save As.",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveAs()
    {
        if (_active is null) return;
        var session = _active.Session;

        var dialog = new SaveFileDialog
        {
            Title = "Save PDF as",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = session.FileName,
            InitialDirectory = Path.GetDirectoryName(session.OriginalPath)
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            session.SaveAs(dialog.FileName);
            _active.Item.ToolTip = session.OriginalPath;
            RecentFiles.Add(session.OriginalPath);
            BuildRecentMenu();
            SetStatus($"Saved as {session.OriginalPath}.");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Could not save.\n\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Print()
    {
        var viewer = _active?.Viewer;
        if (viewer?.DocumentBytes is null || _active is null) return;

        try
        {
            SetStatus("Preparing to print...");
            var printed = PdfPrinter.Print(viewer.DocumentBytes, viewer.DocumentPassword, viewer.PageCount,
                _active.Session.FileName, viewer.CurrentPage);
            SetStatus(printed ? $"Sent {_active.Session.FileName} to the printer." : "Printing cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Printing failed.");
            MessageDialog.Show(this, $"Could not print.\n\n{ex.Message}", "Print failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ======================================================================
    // Edit commands
    // ======================================================================

    private void Undo()
    {
        if (_active is null || !_active.Session.CanUndo) return;
        var what = _active.Session.UndoDescription;
        try
        {
            _active.Session.Undo();
            SetStatus($"Undone: {what}.");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Undo failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Redo()
    {
        if (_active is null || !_active.Session.CanRedo) return;
        var what = _active.Session.RedoDescription;
        try
        {
            _active.Session.Redo();
            SetStatus($"Redone: {what}.");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Redo failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GoToPagePrompt()
    {
        var viewer = _active?.Viewer;
        if (viewer is null) return;

        var answer = MessageDialog.Prompt(this, $"Go to page (1 to {viewer.PageCount}):", "Go to page",
            viewer.CurrentPage.ToString(CultureInfo.InvariantCulture));
        if (answer is not null && int.TryParse(answer.Trim(), out var page)) viewer.GoToPage(page);
        viewer.Focus();
    }

    // ======================================================================
    // View commands
    // ======================================================================

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;

            MainMenu.Visibility = Visibility.Collapsed;
            MainToolBar.Visibility = Visibility.Collapsed;
            MainStatusBar.Visibility = Visibility.Collapsed;
            SetNavPaneVisible(false, remember: false);
            SetToolsPaneVisible(false, remember: false);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
            SetStatus("Full screen. Press Esc or F11 to leave.");
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;

            MainMenu.Visibility = Visibility.Visible;
            MainToolBar.Visibility = Visibility.Visible;
            MainStatusBar.Visibility = Visibility.Visible;
            SetNavPaneVisible(NavPaneMenuItem.IsChecked, remember: false);
            SetToolsPaneVisible(ToolsPaneMenuItem.IsChecked, remember: false);
            _isFullScreen = false;
        }

        _active?.Viewer.Focus();
    }

    private void Escape()
    {
        if (_isFullScreen)
        {
            ToggleFullScreen();
            return;
        }

        _active?.Viewer.CancelGesture();
        _active?.Viewer.Focus();
    }

    private void OnToggleNavPane(object sender, RoutedEventArgs e)
    {
        var show = sender is ToggleButton toggle ? toggle.IsChecked == true : NavPaneMenuItem.IsChecked;
        SetNavPaneVisible(show, remember: true);
    }

    private void OnToggleToolsPane(object sender, RoutedEventArgs e)
    {
        var show = sender is ToggleButton toggle ? toggle.IsChecked == true : ToolsPaneMenuItem.IsChecked;
        SetToolsPaneVisible(show, remember: true);
    }

    private void SetNavPaneVisible(bool show, bool remember)
    {
        if (remember)
        {
            NavPaneMenuItem.IsChecked = show;
            NavPaneToggle.IsChecked = show;
        }

        if (!show && NavColumn.Width.Value > 0) _navWidth = NavColumn.Width;
        NavColumn.Width = show ? _navWidth : new GridLength(0);
        NavSplitterColumn.Width = new GridLength(show ? 4 : 0);
        NavPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NavSplitter.Visibility = NavPane.Visibility;
    }

    private void SetToolsPaneVisible(bool show, bool remember)
    {
        if (remember)
        {
            ToolsPaneMenuItem.IsChecked = show;
            ToolsPaneToggle.IsChecked = show;
        }

        if (!show && ToolsColumn.Width.Value > 0) _toolsWidth = ToolsColumn.Width;
        ToolsColumn.Width = show ? _toolsWidth : new GridLength(0);
        ToolsSplitterColumn.Width = new GridLength(show ? 4 : 0);
        ToolsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToolsSplitter.Visibility = ToolsPane.Visibility;
    }

    // ======================================================================
    // Tools pane
    // ======================================================================

    private void RegisterTools()
    {
        var panels = ToolRegistry.CreateAll();
        for (var i = 0; i < panels.Count; i++)
        {
            var panel = panels[i];
            panel.Initialize(this);
            _tools.Add(new ToolEntry
            {
                Panel = panel,
                Icon = TryFindResource(panel.IconKey) as Geometry,
                IsPrimary = i == 0
            });
        }

        ToolList.ItemsSource = _tools;

        ToolsMenu.Items.Clear();
        foreach (var entry in _tools)
        {
            var item = new MenuItem { Header = entry.Title };
            if (entry.Icon is not null)
            {
                item.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("IconPath"), Data = entry.Icon };
            }

            var target = entry;
            item.Click += (_, _) => OpenToolEntry(target);
            ToolsMenu.Items.Add(item);
        }

        ToolsMenu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all tools" };
        all.Click += (_, _) => ShowAllTools();
        ToolsMenu.Items.Add(all);
    }

    private void OpenToolEntry(ToolEntry entry)
    {
        ResetPointerTool(entry);
        if (!ToolsPane.IsVisible && !_isFullScreen) SetToolsPaneVisible(true, remember: true);

        if (!ReferenceEquals(entry, _openTool))
        {
            _openTool?.Panel.Detach();
            _openTool = entry;
            ToolHost.Content = entry.Panel.View;
            entry.Panel.Attach(_active?.Session, _active?.Viewer);
        }

        ToolList.Visibility = Visibility.Collapsed;
        ToolHost.Visibility = Visibility.Visible;
        AllToolsButton.Visibility = Visibility.Visible;
        ToolHeaderTitle.Text = entry.Title;
        ToolHeaderIcon.Data = entry.Icon ?? (Geometry)FindResource("Icon.Tools");
    }

    private void ShowAllTools()
    {
        if (!ToolsPane.IsVisible) SetToolsPaneVisible(true, remember: true);

        _openTool?.Panel.Detach();
        _openTool = null;
        ToolHost.Content = null;

        ToolHost.Visibility = Visibility.Collapsed;
        ToolList.Visibility = Visibility.Visible;
        AllToolsButton.Visibility = Visibility.Collapsed;
        ToolHeaderTitle.Text = "Tools";
        ToolHeaderIcon.Data = (Geometry)FindResource("Icon.Tools");
        ToolList.SelectedItem = null;
    }

    private void OnShowAllTools(object sender, RoutedEventArgs e) => ShowAllTools();

    private void OnToolListClicked(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem { DataContext: ToolEntry entry }) OpenToolEntry(entry);
    }

    private void OnToolListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ToolList.SelectedItem is ToolEntry entry)
        {
            OpenToolEntry(entry);
            e.Handled = true;
        }
    }

    private void OnOpenMultiSign(object sender, RoutedEventArgs e)
    {
        if (_tools.FirstOrDefault(t => t.Panel is MultiSignPanel) is { } entry) OpenToolEntry(entry);
    }

    // ======================================================================
    // Document and help
    // ======================================================================

    private void ShowProperties()
    {
        if (OpenTool("Properties")) return;
        if (_active is null) return;

        var session = _active.Session;
        var size = File.Exists(session.OriginalPath) ? new FileInfo(session.OriginalPath).Length : 0;
        var page = session.Pages.Count > 0 ? session.Pages[Math.Max(0, _active.Viewer.CurrentPage - 1)] : null;

        var text =
            $"File: {session.OriginalPath}\n" +
            $"Size on disk: {size / 1024.0:0.#} KB\n" +
            $"Pages: {session.PageCount}\n" +
            $"Password protected: {(session.IsEncrypted ? "Yes" : "No")}\n" +
            $"Unsaved changes: {(session.IsDirty ? "Yes" : "No")}\n" +
            (page is null ? string.Empty :
                $"Current page size: {page.Width:0} x {page.Height:0} pt " +
                $"({page.Width / 72 * 25.4:0} x {page.Height / 72 * 25.4:0} mm), rotation {page.Rotation} deg");

        MessageDialog.Show(this, text, "Document properties", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowShortcuts()
    {
        const string text =
            "Ctrl+O  Open\n" +
            "Ctrl+S  Save\n" +
            "Ctrl+Shift+S  Save as\n" +
            "Ctrl+P  Print\n" +
            "Ctrl+W  Close document\n" +
            "Ctrl+Z / Ctrl+Y  Undo / Redo\n" +
            "Ctrl+F  Find\n" +
            "Ctrl+G  Go to page\n" +
            "Ctrl+0 / Ctrl+1 / Ctrl+2  Fit page / Actual size / Fit width\n" +
            "Ctrl+Plus / Ctrl+Minus  Zoom in / out (or Ctrl+mouse wheel, or the wheel alone if turned on)\n" +
            "Ctrl+K  Preferences\n" +
            "V / H / G  Select / Hand / Snapshot tool (when turned on in Preferences)\n" +
            "PgUp / PgDn  Previous / next page\n" +
            "Home / End  First / last page\n" +
            "F11  Full screen\n" +
            "Esc  Leave full screen, cancel a drag";

        MessageDialog.Show(this, text, "Keyboard shortcuts");
    }

    private void ShowAbout()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        MessageDialog.Show(this,
            $"{App.ProductName} {version}\n\n" +
            "View, organise and sign PDF documents. Multi-Sign (DSC) signs one document at many places " +
            "with a USB DSC token in a single run.\n\nKCS",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
