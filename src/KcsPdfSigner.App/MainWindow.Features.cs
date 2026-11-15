using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App;

/// <summary>
/// Preferences-driven features: select and snapshot tools, quick actions, links from URLs, form
/// field highlights, session restore, Send to WhatsApp, touch mode and screen scaling.
/// </summary>
public partial class MainWindow
{
    private enum PointerTool
    {
        Hand,
        Select,
        Snapshot
    }

    /// <summary>Per-document results of the background analysis (links and form fields).</summary>
    private sealed class DocumentExtras
    {
        public List<UrlHit> Links { get; set; } = [];
        public List<FormFieldInfo> Fields { get; set; } = [];
        public string? AnalysedFile { get; set; }
    }

    private static readonly object FieldTag = new();
    private static readonly object SelectionTag = new();

    private readonly ConditionalWeakTable<PdfViewer, DocumentExtras> _extras = new();
    private PointerTool _pointerTool = PointerTool.Hand;
    private bool _panelSuspended;
    private Popup? _quickPopup;
    private DocumentTab? _selectionTab;
    private int _selectionPage;
    private TextSelection? _selection;
    private Point _linkDown;

    private RelayCommand? _preferencesCommand;
    private RelayCommand? _whatsAppCommand;
    private RelayCommand? _selectToolCommand;
    private RelayCommand? _handToolCommand;
    private RelayCommand? _snapshotToolCommand;

    public RelayCommand PreferencesCommand => _preferencesCommand ??= new RelayCommand(OpenPreferences);
    public RelayCommand SendWhatsAppCommand => _whatsAppCommand ??= new RelayCommand(SendToWhatsApp, HasDocument);
    public RelayCommand SelectToolCommand => _selectToolCommand ??= new RelayCommand(() => SetPointerTool(PointerTool.Select), HasDocument);
    public RelayCommand HandToolCommand => _handToolCommand ??= new RelayCommand(() => SetPointerTool(PointerTool.Hand), HasDocument);
    public RelayCommand SnapshotToolCommand => _snapshotToolCommand ??= new RelayCommand(() => SetPointerTool(PointerTool.Snapshot), HasDocument);

    private void InitFeatures()
    {
        AppSettings.Changed += (_, _) => OnSettingsChanged();
        Loaded += (_, _) => ApplyDisplaySettings();
        DpiChanged += (_, _) => ApplyDisplaySettings();
        Deactivated += (_, _) => HideQuickActions();
    }

    // ======================================================================
    // Preferences, session restore, close prompt
    // ======================================================================

    private void OpenPreferences()
    {
        HideQuickActions();
        new PreferencesDialog { Owner = this }.ShowDialog();
    }

    private void OnSettingsChanged()
    {
        ApplyDisplaySettings();
        foreach (var tab in _tabs) AnalyseDocument(tab);
    }

    /// <summary>Reopens the documents that were open when the app was last closed.</summary>
    public void RestoreLastSession()
    {
        foreach (var path in AppSettings.Current.LastSession.ToList())
        {
            if (File.Exists(path)) OpenFile(path);
        }
    }

    private void RememberSession()
    {
        AppSettings.Current.LastSession = _tabs
            .Where(t => !t.Session.IsEncrypted && File.Exists(t.Session.OriginalPath))
            .Select(t => t.Session.OriginalPath)
            .ToList();
        AppSettings.Save();
    }

    /// <summary>Returns false when the user chose to keep the window open.</summary>
    private bool ConfirmCloseMany()
    {
        if (!AppSettings.Current.PromptBeforeClosingMultiple || _tabs.Count < 2) return true;
        return MessageDialog.Show(this, $"Close PB&J Studio and its {_tabs.Count} open documents?",
                   "Close all documents", MessageBoxButton.YesNo, MessageBoxImage.Question)
               == MessageBoxResult.Yes;
    }

    // ======================================================================
    // Touch mode and screen scaling
    // ======================================================================

    private static bool HasTouchScreen()
    {
        try
        {
            return Tablet.TabletDevices.Cast<TabletDevice>().Any(d => d.Type == TabletDeviceType.Touch);
        }
        catch
        {
            return false;
        }
    }

    private void ApplyDisplaySettings()
    {
        var settings = AppSettings.Current;
        var touch = settings.Touch == TouchMode.On || (settings.Touch == TouchMode.Auto && HasTouchScreen());
        var scale = touch ? 1.25 : 1.0;
        if (settings.Scaling == ScreenScaling.None) scale /= Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);

        if (Content is FrameworkElement root)
        {
            root.LayoutTransform = Math.Abs(scale - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        }
    }

    // ======================================================================
    // Tools: Hand, Select, Snapshot
    // ======================================================================

    private void SetPointerTool(PointerTool tool)
    {
        if (_active is null)
        {
            SetStatus("Open a PDF first.");
            return;
        }

        HideQuickActions();
        ClearSelection();

        if (tool == PointerTool.Hand)
        {
            ResetPointerTool();
            SetStatus("Hand tool: drag to move around the page.");
            return;
        }

        if (!_panelSuspended)
        {
            // The open tool panel also listens to rectangle drags; pause it while we use them.
            _openTool?.Panel.Detach();
            _panelSuspended = true;
        }

        _pointerTool = tool;
        _active.Viewer.InteractionMode = InteractionMode.RectangleDrag;
        SetStatus(tool == PointerTool.Select
            ? "Select tool: drag over text, then use the quick actions or Ctrl+C. Press H for the hand tool."
            : "Snapshot tool: drag an area to copy it as a picture. Press H for the hand tool.");
    }

    /// <summary>Back to the hand tool, giving the paused tool panel its viewer again.</summary>
    private void ResetPointerTool(ToolEntry? opening = null)
    {
        if (_pointerTool == PointerTool.Hand && !_panelSuspended) return;

        _pointerTool = PointerTool.Hand;
        HideQuickActions();
        ClearSelection();

        var wasSuspended = _panelSuspended;
        _panelSuspended = false;

        if (_active is not null) _active.Viewer.InteractionMode = InteractionMode.Browse;
        // When a different tool is being opened, OpenToolEntry attaches that one instead.
        if (wasSuspended && (opening is null || ReferenceEquals(opening, _openTool)))
        {
            _openTool?.Panel.Attach(_active?.Session, _active?.Viewer);
        }
    }

    private static bool IsTyping() => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
        or PasswordBox or ComboBox;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || IsTyping()) return;

        if (Keyboard.Modifiers == ModifierKeys.None && AppSettings.Current.SingleKeyAccelerators && _active is not null)
        {
            var tool = e.Key switch
            {
                Key.V => PointerTool.Select,
                Key.H => PointerTool.Hand,
                Key.G => PointerTool.Snapshot,
                _ => (PointerTool?)null
            };

            if (tool is { } picked)
            {
                SetPointerTool(picked);
                e.Handled = true;
                return;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && _selection is not null)
        {
            CopySelection();
            e.Handled = true;
        }
    }

    // ======================================================================
    // Per-document hooks
    // ======================================================================

    private void AttachFeatures(DocumentTab tab)
    {
        var viewer = tab.Viewer;
        viewer.PageMouseDown += OnFeatureMouseDown;
        viewer.PageMouseMove += OnFeatureMouseMove;
        viewer.PageMouseUp += OnFeatureMouseUp;
        viewer.PageRectangleSelected += OnFeatureRectangle;
        viewer.OverlayLayoutChanged += (_, _) =>
        {
            DrawFields(tab);
            DrawSelection();
        };
        tab.Session.DocumentChanged += (_, _) => AnalyseDocument(tab);
        AnalyseDocument(tab);
    }

    /// <summary>Finds links and form fields off the UI thread, then draws the field highlights.</summary>
    private async void AnalyseDocument(DocumentTab tab)
    {
        var settings = AppSettings.Current;
        var file = tab.Session.CurrentFile;
        var password = tab.Session.Password;
        var wantLinks = settings.CreateLinksFromUrls;
        var wantFields = settings.DetectFormFields;

        var extras = _extras.GetOrCreateValue(tab.Viewer);
        if (!wantLinks) extras.Links = [];
        if (!wantFields)
        {
            extras.Fields = [];
            DrawFields(tab);
        }

        if (!wantLinks && !wantFields) return;

        try
        {
            var (links, fields) = await Task.Run(() =>
            {
                var foundLinks = wantLinks ? PdfTextRegions.FindUrls(file, password) : [];
                var foundFields = wantFields
                    ? PdfFormOps.ReadFields(file, password).Where(f => f.PageNumber > 0 && f.Width > 0 && f.Height > 0).ToList()
                    : [];
                return (foundLinks, foundFields);
            });

            // The document may have changed or closed while this ran.
            if (!ReferenceEquals(tab.Viewer, _tabs.FirstOrDefault(t => ReferenceEquals(t.Viewer, tab.Viewer))?.Viewer)
                || file != tab.Session.CurrentFile) return;

            if (wantLinks) extras.Links = links;
            if (wantFields) extras.Fields = fields;
            extras.AnalysedFile = file;
            DrawFields(tab);

            if (wantFields && fields.Count > 0 && ReferenceEquals(tab, _active))
            {
                SetStatus($"{fields.Count} form field(s) highlighted.");
            }
        }
        catch (Exception ex)
        {
            App.LogError("Analysing document (links and fields)", ex);
        }
    }

    private void DrawFields(DocumentTab tab)
    {
        var viewer = tab.Viewer;
        viewer.ClearOverlay(FieldTag);
        if (!AppSettings.Current.DetectFormFields || !_extras.TryGetValue(viewer, out var extras)) return;

        foreach (var field in extras.Fields)
        {
            var overlay = viewer.GetOverlay(field.PageNumber);
            var rect = viewer.PdfRectToOverlay(field.PageNumber, field.Left, field.Bottom, field.Width, field.Height);
            if (overlay is null || rect.IsEmpty) continue;

            var box = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                StrokeDashArray = [3, 2],
                Fill = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
                IsHitTestVisible = false,
                Tag = FieldTag
            };
            Canvas.SetLeft(box, rect.Left);
            Canvas.SetTop(box, rect.Top);
            overlay.Children.Add(box);
        }
    }

    // ======================================================================
    // Links from URLs
    // ======================================================================

    private UrlHit? LinkAt(PdfViewer viewer, int page, double x, double y)
    {
        if (!AppSettings.Current.CreateLinksFromUrls || !_extras.TryGetValue(viewer, out var extras)) return null;
        return extras.Links.FirstOrDefault(l => l.PageNumber == page
            && x >= l.Left - 1 && x <= l.Left + l.Width + 1 && y >= l.Bottom - 1 && y <= l.Bottom + l.Height + 1);
    }

    private void OnFeatureMouseDown(object? sender, PageMouseEventArgs e)
    {
        _linkDown = e.OverlayPoint;
        if (_quickPopup is not null && _pointerTool != PointerTool.Select) HideQuickActions();
    }

    private void OnFeatureMouseMove(object? sender, PageMouseEventArgs e)
    {
        if (sender is not PdfViewer viewer || !ReferenceEquals(viewer, _active?.Viewer)) return;
        if (_pointerTool != PointerTool.Hand) return;

        var link = LinkAt(viewer, e.PageNumber, e.X, e.Y);
        if (link is not null) SetStatus("Link: " + link.Url + "  (click to open)");
    }

    private void OnFeatureMouseUp(object? sender, PageMouseEventArgs e)
    {
        if (sender is not PdfViewer viewer || e.Button != MouseButton.Left || _pointerTool != PointerTool.Hand) return;
        if ((e.OverlayPoint - _linkDown).Length > 4) return; // it was a drag, not a click

        var link = LinkAt(viewer, e.PageNumber, e.X, e.Y);
        if (link is null) return;

        var answer = MessageDialog.Show(this, "Open this web address in your browser?\n\n" + link.Url,
            "Open link", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not open the link.\n\n" + ex.Message, "Open link",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ======================================================================
    // Select tool and quick actions
    // ======================================================================

    private void OnFeatureRectangle(object? sender, PageRectEventArgs e)
    {
        if (_pointerTool == PointerTool.Hand || sender is not PdfViewer viewer) return;
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Viewer, viewer));
        if (tab is null || !ReferenceEquals(tab, _active)) return;

        if (_pointerTool == PointerTool.Select) SelectText(tab, e);
        else TakeSnapshot(tab, e);
    }

    private async void SelectText(DocumentTab tab, PageRectEventArgs e)
    {
        HideQuickActions();
        ClearSelection();

        var file = tab.Session.CurrentFile;
        var password = tab.Session.Password;
        TextSelection result;
        try
        {
            result = await Task.Run(() => PdfTextRegions.SelectText(
                file, e.PageNumber, (float)e.Left, (float)e.Bottom, (float)e.Width, (float)e.Height, password));
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the text: " + ex.Message);
            return;
        }

        if (result.Text.Length == 0)
        {
            SetStatus("No text in that area (a scanned page has no selectable text).");
            return;
        }

        _selectionTab = tab;
        _selectionPage = e.PageNumber;
        _selection = result;
        DrawSelection();
        SetStatus($"{result.Text.Length} character(s) selected. Ctrl+C copies them.");

        if (AppSettings.Current.QuickActionsOnSelection) ShowQuickActions(tab, e.PageNumber, e.OverlayRect);
    }

    private void DrawSelection()
    {
        if (_selectionTab is null || _selection is null) return;
        var viewer = _selectionTab.Viewer;
        viewer.ClearOverlay(SelectionTag);

        var overlay = viewer.GetOverlay(_selectionPage);
        if (overlay is null) return;

        foreach (var line in _selection.LineBoxes)
        {
            var rect = viewer.PdfRectToOverlay(_selectionPage, line.Left, line.Bottom, line.Width, line.Height);
            if (rect.IsEmpty) continue;

            var box = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                IsHitTestVisible = false,
                Tag = SelectionTag
            };
            Canvas.SetLeft(box, rect.Left);
            Canvas.SetTop(box, rect.Top);
            overlay.Children.Add(box);
        }
    }

    private void ClearSelection()
    {
        _selectionTab?.Viewer.ClearOverlay(SelectionTag);
        _selectionTab = null;
        _selection = null;
    }

    private void CopySelection()
    {
        if (_selection is null) return;
        if (TryCopyText(_selection.Text)) SetStatus($"Copied {_selection.Text.Length} character(s).");
        else SetStatus("The clipboard is busy. Try again.");
    }

    private static bool TryCopyText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void ShowQuickActions(DocumentTab tab, int page, Rect overlayRect)
    {
        var overlay = tab.Viewer.GetOverlay(page);
        if (overlay is null) return;

        Button Make(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(2), Focusable = false };
            button.Click += (_, _) => action();
            return button;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Make("Copy", () => { CopySelection(); HideQuickActions(); }));
        row.Children.Add(Make("Highlight", () => { HideQuickActions(); HighlightSelection(); }));
        row.Children.Add(Make("Search", () => { HideQuickActions(); SearchSelection(); }));

        var anchor = overlay.TranslatePoint(new Point(overlayRect.Left, Math.Max(0, overlayRect.Top - 40)), tab.Viewer);
        _quickPopup = new Popup
        {
            PlacementTarget = tab.Viewer,
            Placement = PlacementMode.Relative,
            HorizontalOffset = anchor.X,
            VerticalOffset = anchor.Y,
            StaysOpen = true,
            Child = new Border
            {
                Background = (Brush)FindResource("Brush.Bg"),
                BorderBrush = (Brush)FindResource("Brush.Line"),
                BorderThickness = new Thickness(1),
                Child = row
            },
            IsOpen = true
        };
    }

    private void HideQuickActions()
    {
        if (_quickPopup is null) return;
        _quickPopup.IsOpen = false;
        _quickPopup = null;
    }

    private async void HighlightSelection()
    {
        if (_selection is null || _selectionTab is null) return;
        var tab = _selectionTab;
        var page = _selectionPage;
        var specs = _selection.LineBoxes.Select(b => new AnnotationSpec
        {
            Kind = AnnotationKind.Highlight,
            PageNumber = page,
            Left = b.Left,
            Bottom = b.Bottom,
            Width = b.Width,
            Height = b.Height
        }).ToList();
        var password = tab.Session.Password;

        ClearSelection();
        try
        {
            await tab.Session.ApplyAsync((input, output) => PdfAnnotationOps.Add(input, output, specs, password),
                "Highlight text");
            SetStatus("Text highlighted. Undo with Ctrl+Z.");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not highlight the text.\n\n" + ex.Message, "Highlight",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SearchSelection()
    {
        if (_selection is null) return;
        var query = _selection.Text.Split('\n')[0].Trim();
        if (query.Length > 80) query = query[..80];
        if (query.Length == 0) return;

        FindBox.Text = query;
        if (FindRequested is null) OpenTool("Search");
        if (FindRequested is null)
        {
            SetStatus("Text search is not available.");
            return;
        }

        FindRequested.Invoke(this, query);
    }

    // ======================================================================
    // Snapshot tool
    // ======================================================================

    private async void TakeSnapshot(DocumentTab tab, PageRectEventArgs e)
    {
        var viewer = tab.Viewer;
        var bytes = viewer.DocumentBytes;
        var slotSize = viewer.GetOverlaySize(e.PageNumber);
        if (bytes is null || slotSize.IsEmpty || slotSize.Width < 1 || slotSize.Height < 1) return;

        var settings = AppSettings.Current;
        var info = viewer.Pages.FirstOrDefault(p => p.PageNumber == e.PageNumber);
        var wanted = settings.SnapshotFixedResolution ? settings.SnapshotPixelsPerInch : Math.Clamp(viewer.Zoom * 96, 96, 600);
        var dpi = info is null ? (int)wanted : PageRenderer.CapDpi(info.Width, info.Height, wanted);
        var password = tab.Session.Password;

        try
        {
            SetStatus("Taking snapshot...");
            var page = await Task.Run(() => PageRenderer.Render(bytes, e.PageNumber, password, dpi));

            var x = (int)Math.Clamp(Math.Round(e.OverlayRect.X / slotSize.Width * page.PixelWidth), 0, page.PixelWidth - 1);
            var y = (int)Math.Clamp(Math.Round(e.OverlayRect.Y / slotSize.Height * page.PixelHeight), 0, page.PixelHeight - 1);
            var w = (int)Math.Clamp(Math.Round(e.OverlayRect.Width / slotSize.Width * page.PixelWidth), 1, page.PixelWidth - x);
            var h = (int)Math.Clamp(Math.Round(e.OverlayRect.Height / slotSize.Height * page.PixelHeight), 1, page.PixelHeight - y);
            if (w < 4 || h < 4)
            {
                SetStatus("Drag a larger area for the snapshot.");
                return;
            }

            // Paint on white: rendered pages can carry transparency, and pasted pictures should not turn black.
            var cropped = new CroppedBitmap(page, new Int32Rect(x, y, w, h));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var size = new Rect(0, 0, w * 96.0 / dpi, h * 96.0 / dpi);
                dc.DrawRectangle(Brushes.White, null, size);
                dc.DrawImage(cropped, size);
            }

            var picture = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
            picture.Render(visual);
            picture.Freeze();

            if (!TrySetImage(picture))
            {
                SetStatus("The clipboard is busy. Try the snapshot again.");
                return;
            }

            SetStatus($"Snapshot copied: {w} x {h} pixels at {dpi} pixels per inch.");

            if (settings.SnapshotOpenForMarkup) OpenSnapshotForMarkup(picture);
        }
        catch (Exception ex)
        {
            App.LogError("Snapshot", ex);
            MessageDialog.Show(this, "Could not take the snapshot.\n\n" + ex.Message, "Snapshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool TrySetImage(BitmapSource picture)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(picture);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void OpenSnapshotForMarkup(BitmapSource picture)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PBJ-Snapshots");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var png = System.IO.Path.Combine(folder, $"Snapshot-{stamp}.png");
        var pdf = System.IO.Path.Combine(folder, $"Snapshot-{stamp}.pdf");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(png)) encoder.Save(stream);

        PdfPageOps.ImagesToPdf([png], pdf);
        ResetPointerTool();
        OpenFile(pdf);
    }

    // ======================================================================
    // Send to WhatsApp
    // ======================================================================

    private void SendToWhatsApp()
    {
        if (_active is null) return;

        string target;
        try
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PBJ-WhatsApp");
            Directory.CreateDirectory(folder);
            target = System.IO.Path.Combine(folder, _active.Session.FileName);
            _active.Session.SaveCopy(target);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not prepare the file.\n\n" + ex.Message, "Send to WhatsApp",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var copied = false;
        try
        {
            var files = new StringCollection { target };
            Clipboard.SetFileDropList(files);
            copied = true;
        }
        catch
        {
            // The Explorer window below still lets the user drag the file.
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch
        {
            // Not fatal.
        }

        var started = false;
        try
        {
            Process.Start(new ProcessStartInfo("whatsapp://") { UseShellExecute = true });
            started = true;
        }
        catch
        {
            // WhatsApp Desktop is not installed; use the web version.
        }

        if (!started)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://web.whatsapp.com/") { UseShellExecute = true });
            }
            catch
            {
                // Not fatal.
            }
        }

        MessageDialog.Show(this,
            "A copy of the PDF (with your current changes) is ready." +
            (copied ? "\n\nThe file is on the clipboard: open the chat in WhatsApp and press Ctrl+V." : "") +
            "\n\nOr drag the file from the Explorer window into the chat.",
            "Send to WhatsApp", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
