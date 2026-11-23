// UI smoke test: builds the real windows in-process (no screenshots, no mouse) and checks the
// Preferences-driven features. Needs the smoke-test PDFs: run tools\SignerSmokeTest first.
// Settings are backed up and restored, so your own preferences are not changed.
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KcsPdfSigner.App;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using PageSize = iText.Kernel.Geom.PageSize;
using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.IO.Path;

var failures = new List<string>();
void Check(bool ok, string message)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + message);
    if (!ok) failures.Add(message);
}

// Run beside an installed copy of the app: this test must not hand its work to it.
Environment.SetEnvironmentVariable("PBJ_ALLOW_MULTIPLE", "1");

// Screenshot mode: "UiSmokeTest --shots <folder>" renders the real windows to PNG files (used for the website).
var shotsDir = args.Length >= 2 && args[0] == "--shots" ? Path.GetFullPath(args[1]) : null;

var docs = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "docs");
var linksPdf = Path.Combine(docs, "links.pdf");
var formPdf = Path.Combine(docs, "form.pdf");
if (shotsDir is null && (!File.Exists(linksPdf) || !File.Exists(formPdf)))
{
    Console.WriteLine("Run tools\\SignerSmokeTest first (it creates links.pdf and form.pdf).");
    return 2;
}

var backup = File.Exists(AppSettings.StorePath) ? File.ReadAllText(AppSettings.StorePath) : null;
// The trusted-certificates list is changed by check [7]; keep the user's own list.
var trustBackup = File.Exists(TrustedCertificates.StorePath) ? File.ReadAllBytes(TrustedCertificates.StorePath) : null;
var exitCode = 1;

var thread = new Thread(() =>
{
    try
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        exitCode = shotsDir is not null ? RunShots(shotsDir) : RunAll();
    }
    catch (Exception ex)
    {
        Console.WriteLine("UI smoke test crashed: " + ex);
        exitCode = 1;
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (backup is null) { if (File.Exists(AppSettings.StorePath)) File.Delete(AppSettings.StorePath); }
else File.WriteAllText(AppSettings.StorePath, backup);
if (trustBackup is null) { if (File.Exists(TrustedCertificates.StorePath)) File.Delete(TrustedCertificates.StorePath); }
else File.WriteAllBytes(TrustedCertificates.StorePath, trustBackup);

var passed = failures.Count == 0 && exitCode == 0;
Console.WriteLine(passed ? "\nPASS - all UI checks succeeded." : $"\nFAIL - {failures.Count} check(s) failed.");
return passed ? 0 : 1;

// Runs the WPF message loop for a while (this thread owns the windows), so layout, rendering and
// background continuations get their turn without any async plumbing.
void PumpNow(int milliseconds)
{
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
    timer.Start();
    Dispatcher.PushFrame(frame);
}

// The Windows clipboard is shared with every other program; it can be busy for a moment.
T? TryClipboard<T>(Func<T> action)
{
    for (var attempt = 0; attempt < 8; attempt++)
    {
        try { return action(); }
        catch (System.Runtime.InteropServices.COMException) { PumpNow(150); }
    }

    return default;
}

void Wait(Task task)
{
    while (!task.IsCompleted) PumpNow(40);
    task.GetAwaiter().GetResult();
}

T Get<T>(object target, string name)
{
    var type = target.GetType();
    const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    var field = type.GetField(name, all);
    if (field is not null) return (T)field.GetValue(target)!;
    return (T)type.GetProperty(name, all)!.GetValue(target)!;
}

MethodInfo Method(string name) =>
    typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;


// ---- Screenshot mode ------------------------------------------------------------------------
string MakeSample(string folder, string name, string title, int pages)
{
    var path = Path.Combine(folder, name);
    using var writer = new PdfWriter(path);
    using var pdf = new PdfDocument(writer);
    var doc = new Document(pdf, PageSize.A4);
    for (var page = 1; page <= pages; page++)
    {
        if (page > 1) doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
        doc.Add(new Paragraph(title).SetFontSize(20).SetFont(iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD)));
        doc.Add(new Paragraph($"Page {page} of {pages}").SetFontSize(10).SetMarginBottom(18));
        for (var i = 0; i < 9; i++)
            doc.Add(new Paragraph("This is a sample paragraph used to show how PB&J Studio displays a document. " +
                                  "Replace it with your own text, pages and signatures.").SetFontSize(11).SetMarginBottom(8));
        doc.Add(new Paragraph("Sample Company").SetFontSize(11).SetMarginTop(30));
        doc.Add(new Paragraph("Authorised Signatory").SetFontSize(11));
    }

    doc.Close();
    return path;
}

void SaveWindow(Window window, string file)
{
    window.UpdateLayout();
    var width = (int)Math.Ceiling(window.ActualWidth);
    var height = (int)Math.Ceiling(window.ActualHeight);
    var rect = new Rect(0, 0, width, height);

    // Draw on an opaque black sheet so semi-transparent text keeps its true brightness.
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, rect);
        dc.DrawRectangle(new System.Windows.Media.VisualBrush((Visual)window.Content), null, rect);
    }

    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using var stream = File.Create(file);
    encoder.Save(stream);
    Console.WriteLine($"  wrote {Path.GetFileName(file)} ({width}x{height})");
}

void SaveElement(FrameworkElement element, string file, double scale)
{
    var width = (int)Math.Ceiling(element.ActualWidth);
    var height = (int)Math.Ceiling(element.ActualHeight);
    var rect = new Rect(0, 0, width, height);
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, rect);
        dc.DrawRectangle(new System.Windows.Media.VisualBrush(element), null, rect);
    }

    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using var stream = File.Create(file);
    encoder.Save(stream);
    Console.WriteLine($"  wrote {Path.GetFileName(file)} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
}

int RunShots(string outDir)
{
    Directory.CreateDirectory(outDir);
    var work = Path.Combine(Path.GetTempPath(), "PBJ-Shots");
    if (Directory.Exists(work)) Directory.Delete(work, true);
    Directory.CreateDirectory(work);

    var report = MakeSample(work, "Project Report.pdf", "Project Report", 4);
    var statement = MakeSample(work, "Invoice.pdf", "Invoice", 3);
    var letter = MakeSample(work, "Cover Letter.pdf", "Cover Letter", 1);

    var app = new App();
    app.InitializeComponent();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    AppSettings.Apply(new AppSettings());

    var window = new MainWindow { Width = 1480, Height = 1120, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
    window.Show();
    window.OpenFile(report);
    PumpNow(5000);
    // Show Organize Pages (never Multi-Sign: it lists the PC's real certificates, which must not appear on the website).
    window.OpenTool("Organize Pages");
    PumpNow(2500);
    SaveWindow(window, Path.Combine(outDir, "main-window.png"));

    // Multiple PDFs: add three files, put pages from each in the new PDF, then show it.
    window.OpenTool("Multiple PDFs");
    PumpNow(800);
    var toolHost = (ContentControl)LogicalTreeHelper.FindLogicalNode(window, "ToolHost");
    var panel = (FrameworkElement)toolHost.Content;
    var panelType = panel.GetType();
    Task AddSource(string path) => (Task)panelType.GetMethod("AddSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [path, null])!;
    Wait(AddSource(report));
    Wait(AddSource(statement));
    Wait(AddSource(letter));
    var sources = Get<System.Collections.IList>(panel, "_sources");
    var addToBasket = panelType.GetMethod("AddToBasket", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var thumbType = Get<System.Collections.IList>(sources[0]!, "Thumbs")[0]!.GetType();
    void Pick(int source, params int[] pages)
    {
        var thumbs = Get<System.Collections.IList>(sources[source]!, "Thumbs");
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(thumbType))!;
        foreach (var page in pages) list.Add(thumbs[page - 1]);
        addToBasket.Invoke(panel, [list]);
    }

    Pick(2, 1);        // covering letter first
    Pick(0, 2, 3, 4);  // report pages 2-4
    Pick(1, 3);        // statement page 3
    var sourceList = (ListBox)Get<object>(panel, "SourceList");
    sourceList.SelectedIndex = 0;
    PumpNow(1500);
    if (panel is UserControl { Content: ScrollViewer panelScroll }) panelScroll.ScrollToTop();
    PumpNow(3500);
    if (panel is UserControl { Content: ScrollViewer fullScroll })
    {
        // Mark two pages as picked so the thumbnails look "in use", then render the whole panel, not just what fits.
        PumpNow(2500);
        SaveElement((FrameworkElement)fullScroll.Content, Path.Combine(outDir, "multiple-pdfs.png"), 2);
    }
    return 0;
}

int RunAll()
{
    var logSizeAtStart = File.Exists(App.ErrorLogPath) ? new FileInfo(App.ErrorLogPath).Length : 0;
    var app = new App();
    app.InitializeComponent();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    // A tiny window that stays open, so closing the dialogs below never ends the application.
    var anchor = new Window { Width = 1, Height = 1, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -3000, Top = -3000 };
    anchor.Show();
    app.MainWindow = anchor;

    Console.WriteLine("[2] Main window: form fields, links, select tool, highlight, snapshot");
    AppSettings.Apply(new AppSettings { QuickActionsOnSelection = false, SnapshotFixedResolution = true, SnapshotPixelsPerInch = 144 });
    var window = new MainWindow();
    window.Show();
    window.OpenFile(linksPdf);
    window.OpenFile(formPdf);
    PumpNow(9000);

    // Warm up iText (first use JIT-compiles for seconds) so the background analysis finishes inside the wait above.
    _ = PdfTextRegions.FindUrls(linksPdf).Count + PdfFormOps.ReadFields(formPdf).Count;
    var tabs = Get<System.Collections.IList>(window, "_tabs");
    Check(tabs.Count == 2, "two documents opened");

    object TabNamed(string file) => tabs.Cast<object>().First(t =>
        Path.GetFileName(Get<DocumentSession>(t, "Session").OriginalPath) == file);

    var formViewer = Get<PdfViewer>(TabNamed("form.pdf"), "Viewer");
    var fieldBoxes = formViewer.GetOverlay(1)?.Children.OfType<Rectangle>().Count() ?? 0;
    Check(fieldBoxes == 3, $"form.pdf: 3 form fields highlighted on open (found {fieldBoxes})");

    var linkTab = TabNamed("links.pdf");
    var linkViewer = Get<PdfViewer>(linkTab, "Viewer");
    var linkSession = Get<DocumentSession>(linkTab, "Session");
    var extras = Get<object>(window, "_extras");
    var probe = new object?[] { linkViewer, null };
    var found = (bool)extras.GetType().GetMethod("TryGetValue")!.Invoke(extras, probe)!;
    var links = found ? Get<List<UrlHit>>(probe[1]!, "Links") : [];
    Console.WriteLine($"    extras found={found}, analysed file={(found ? Get<string?>(probe[1]!, "AnalysedFile") : "-")}");
    Check(links.Count == 2, $"links.pdf: 2 web addresses found (found {links.Count})");
    if (links.Count == 0) return 1;

    var toolType = typeof(MainWindow).GetNestedType("PointerTool", BindingFlags.NonPublic)!;
    var select = Enum.Parse(toolType, "Select");
    var snapshot = Enum.Parse(toolType, "Snapshot");
    var hand = Enum.Parse(toolType, "Hand");

    var tabControl = (TabControl)LogicalTreeHelper.FindLogicalNode(window, "DocumentTabs");
    tabControl.SelectedIndex = tabs.IndexOf(linkTab);
    PumpNow(500);

    // Page sharpness: the page bitmap must have exactly as many pixels as the screen area it covers.
    var slots = Get<System.Collections.IList>(linkViewer, "_slots");
    var firstSlot = slots[0]!;
    var surface = Get<FrameworkElement>(firstSlot, "Surface");
    var pageImage = Get<System.Windows.Controls.Image>(firstSlot, "Image");
    var dpiScale = VisualTreeHelper.GetDpi(linkViewer).DpiScaleX;
    var bitmapSource = pageImage.Source as System.Windows.Media.Imaging.BitmapSource;
    var wantPx = (int)Math.Round(surface.Width * dpiScale);
    Check(bitmapSource is not null && bitmapSource.PixelWidth == wantPx && Math.Abs(surface.Width * dpiScale - wantPx) < 0.01,
        $"page bitmap is pixel-exact ({bitmapSource?.PixelWidth} px for {surface.Width * dpiScale:0.##} screen px)");

    Method("SetPointerTool").Invoke(window, [select]);
    Check(linkViewer.InteractionMode == InteractionMode.RectangleDrag, "select tool puts the viewer in drag mode");

    var first = links[0];
    var rect = linkViewer.PdfRectToOverlay(1, first.Left - 2, first.Bottom - 2, first.Width + 4, first.Height + 4);
    var drag = new PageRectEventArgs
    {
        PageNumber = 1, Left = first.Left - 2, Bottom = first.Bottom - 2, Width = first.Width + 4,
        Height = first.Height + 4, OverlayRect = rect
    };

    Method("SelectText").Invoke(window, [linkTab, drag]);
    PumpNow(1500);
    var selection = Get<TextSelection?>(window, "_selection");
    Check(selection is not null && selection.Text.Contains("example.com"), "select tool reads the dragged text");
    var shaded = linkViewer.GetOverlay(1)?.Children.OfType<Rectangle>().Count() ?? 0;
    Check(shaded >= 1, "selected text is shaded on the page");

    var before = PdfAnnotationOps.List(linkSession.CurrentFile).Count;
    Method("HighlightSelection").Invoke(window, null);
    PumpNow(100);
    PumpNow(1900);
    var after = PdfAnnotationOps.List(linkSession.CurrentFile).Count;
    Check(after == before + 1, $"highlight adds one annotation ({before} -> {after})");

    Method("SetPointerTool").Invoke(window, [snapshot]);
    TryClipboard(() => { Clipboard.Clear(); return true; });
    Method("TakeSnapshot").Invoke(window, [linkTab, drag]);
    PumpNow(3500);
    var image = TryClipboard(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);
    var statusLine = Get<TextBlock>(window, "StatusText").Text;
    if (image is not null)
        Check(image.PixelWidth > 20 && image.PixelHeight > 5, $"snapshot on the clipboard: {image.PixelWidth} x {image.PixelHeight} px");
    else if (statusLine.Contains("clipboard is busy"))
        Console.WriteLine("  SKIP snapshot clipboard check: another program is holding the Windows clipboard");
    else
        Check(statusLine.StartsWith("Snapshot copied"), $"snapshot reached the clipboard (status: '{statusLine}')");

    Method("SetPointerTool").Invoke(window, [hand]);
    Check(linkViewer.InteractionMode == InteractionMode.Browse, "hand tool restores browse mode");

    Console.WriteLine("[3] Session list and display settings");
    Check(window.RemovePasswordCopyCommand is not null && !window.RemovePasswordCopyCommand.CanExecute(null),
        "'Save a copy without password' is disabled for a document that has no password");
    var builtBy = Get<TextBlock>(window, "BuiltByStatus");
    var builtByLink = builtBy.Inlines.OfType<System.Windows.Documents.Hyperlink>().FirstOrDefault();
    Check(builtByLink is not null && builtByLink.NavigateUri?.Host.EndsWith("vistaar.space") == true,
        "footer 'vistaar.space' is a hyperlink to vistaar.space");
    Method("RememberSession").Invoke(window, null);
    Check(AppSettings.Current.LastSession.Count == 2, "open documents are remembered for the next start");

    AppSettings.Apply(new AppSettings { Touch = TouchMode.On });
    PumpNow(300);
    Check(window.Content is FrameworkElement { LayoutTransform: ScaleTransform { ScaleX: > 1.2 } }, "touch mode enlarges the interface");
    AppSettings.Apply(new AppSettings { Touch = TouchMode.Off });
    PumpNow(300);
    Check(window.Content is FrameworkElement fe && !(fe.LayoutTransform is ScaleTransform { ScaleX: > 1.2 }),
        "touch mode off restores it");

    Console.WriteLine("[4] Multiple PDFs panel");
    var threePages = Path.Combine(docs, "three-pages.pdf");
    var panelType = typeof(MainWindow).Assembly.GetType("KcsPdfSigner.App.Tools.PageBasketPanel")!;
    var panel = (FrameworkElement)Activator.CreateInstance(panelType)!;
    panelType.GetMethod("Initialize")!.Invoke(panel, [window]);
    Task AddSource(string path) => (Task)panelType.GetMethod("AddSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [path, null])!;
    Wait(AddSource(threePages));
    Wait(AddSource(linksPdf));
    PumpNow(500);
    var sourceList = Get<System.Collections.IList>(panel, "_sources");
    Check(sourceList.Count == 2, "two PDFs added as sources");
    var threeSource = sourceList[0]!;
    var linksSource = sourceList[1]!;
    var threeThumbs = Get<System.Collections.IList>(threeSource, "Thumbs");
    Check(threeThumbs.Count == 3, "source shows 3 page thumbnails");

    var parse = panelType.GetMethod("ParseRangeInOrder", BindingFlags.Static | BindingFlags.NonPublic)!;
    object?[] parseArgs = ["3-1, 2", 3, null];
    var parsed = (List<int>?)parse.Invoke(null, parseArgs);
    Check(parsed is not null && parsed.SequenceEqual([3, 2, 1, 2]), "range '3-1, 2' keeps the order typed");
    object?[] badArgs = ["4", 3, null];
    Check(parse.Invoke(null, badArgs) is null && badArgs[2] is string, "range outside the file is rejected with a message");

    var addToBasket = panelType.GetMethod("AddToBasket", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var thumbClass = threeThumbs[0]!.GetType();
    var typedList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(thumbClass))!;
    typedList.Add(threeThumbs[2]);                                   // three-pages p.3
    typedList.Add(Get<System.Collections.IList>(linksSource, "Thumbs")[0]);  // links p.1
    typedList.Add(threeThumbs[0]);                                   // three-pages p.1
    addToBasket.Invoke(panel, [typedList]);
    var basket = Get<System.Collections.IList>(panel, "_basket");
    string Labels() => string.Join(" | ", basket.Cast<object>().Select(b => Get<string>(b, "SourceLabel")));
    Console.WriteLine("    basket: " + Labels());
    Check(basket.Count == 3 && Labels() == "three-pages.pdf p.3 | links.pdf p.1 | three-pages.pdf p.1", "pages are added in the order picked, with file and page labels");

    var basketList = (ListBox)Get<object>(panel, "BasketList");
    basketList.SelectedItems.Clear();
    basketList.SelectedItems.Add(basket[2]);
    panelType.GetMethod("OnToTop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [null, new RoutedEventArgs()]);
    Check(Labels().StartsWith("three-pages.pdf p.1"), "To top moves the selected page first");
    panelType.GetMethod("OnToBottom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [null, new RoutedEventArgs()]);
    Check(Labels().EndsWith("three-pages.pdf p.1"), "To bottom moves it back to the end");
    panelType.GetMethod("OnRotateRight", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [null, new RoutedEventArgs()]);
    Check(Get<int>(basket[2]!, "Rotation") == 90, "rotate right turns the selected page 90 degrees");
    var positions = basket.Cast<object>().Select(b => Get<int>(b, "Position")).ToList();
    Check(positions.SequenceEqual([1, 2, 3]), "positions are numbered 1..3 after moving");
    panelType.GetMethod("OnRemovePages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [null, new RoutedEventArgs()]);
    Check(basket.Count == 2, "remove takes the selected page out of the new PDF");
    var summary = Get<TextBlock>(panel, "SummaryText").Text;
    Check(summary.Contains("2 page(s) from 2 file(s)"), $"live summary is right ('{summary}')");

    Console.WriteLine("[5] Changing a signed document asks first");
    Check(DocumentSession.ConfirmChangeToSignedDocument is not null, "main window installs the signed-document warning");
    var signedPath = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "signed.pdf");
    var asked = 0;
    var answer = false;
    var installed = DocumentSession.ConfirmChangeToSignedDocument;
    DocumentSession.ConfirmChangeToSignedDocument = _ => { asked++; return answer; };
    using (var signedSession = DocumentSession.Open(signedPath))
    {
        Check(signedSession.HasSignatures, "signed.pdf is recognised as signed");
        void Rotate(string input, string output) => PdfPageOps.RotatePages(input, output, [1], 90);

        var declined = false;
        try { signedSession.Apply(Rotate, "Rotate"); }
        catch (SignedChangeDeclinedException) { declined = true; }
        Check(declined && asked == 1 && !signedSession.CanUndo, "declining leaves the signed document unchanged");

        var toolUi = typeof(MainWindow).Assembly.GetType("KcsPdfSigner.App.Tools.ToolUi")!;
        var viaTool = (Task<bool>)toolUi.GetMethod("ApplyAsync")!.Invoke(null, [null, new Grid(), signedSession, (Action<string, string>)Rotate, "Rotate"])!;
        Wait(viaTool);
        Check(!viaTool.Result && asked == 2 && !signedSession.CanUndo, "a tool whose change is declined reports no error and changes nothing");

        answer = true;
        signedSession.Apply(Rotate, "Rotate");
        signedSession.Apply(Rotate, "Rotate");
        Check(asked == 3 && signedSession.CanUndo, "after one OK, further changes go ahead without asking again");
    }

    using (var unsignedSession = DocumentSession.Open(threePages))
    {
        unsignedSession.Apply((input, output) => PdfPageOps.RotatePages(input, output, [1], 90), "Rotate");
        Check(asked == 3 && unsignedSession.CanUndo, "unsigned documents are changed without the warning");
    }

    DocumentSession.ConfirmChangeToSignedDocument = installed;

    Console.WriteLine("[6] Printing renders each page for the printer, large pages included");
    var huge = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "huge-page.pdf");
    using (var hugeDoc = new PdfDocument(new PdfWriter(huge)))
    {
        // About A0 x 2 (1189 x 1682 mm): 300 dpi would need over 500 MB for one page.
        var big = new Document(hugeDoc, new PageSize(3370, 4768));
        big.Add(new Paragraph("LARGE DRAWING").SetFontSize(120));
        big.Close();
    }

    var paginatorType = typeof(PdfPrinter).GetNestedType("RenderedPdfPaginator", BindingFlags.NonPublic)!;
    foreach (var file in new[] { signedPath, huge })
    {
        var bytes = PdfPrintPrep.ForPrinting(File.ReadAllBytes(file), null);
        var info = PdfPageReader.ReadPages(file, null);
        var paginator = (System.Windows.Documents.DocumentPaginator)Activator.CreateInstance(paginatorType,
            [bytes, null, (IReadOnlyList<int>)[1], (IReadOnlyList<KcsPdfSigner.Core.PdfPageInfo>)info, new Size(793, 1122), new Rect(20, 20, 753, 1082)])!;
        var memoryBefore = GC.GetTotalMemory(true);
        var printedPage = paginator.GetPage(0);
        var drawing = VisualTreeHelper.GetDrawing(printedPage.Visual)?.Bounds ?? Rect.Empty;
        Console.WriteLine($"    {Path.GetFileName(file)}: drawn {drawing.Width:0}x{drawing.Height:0}, " +
                          $"managed memory +{(GC.GetTotalMemory(false) - memoryBefore) / 1048576.0:0} MB");
        Check(!drawing.IsEmpty && drawing.Width <= 754 && drawing.Height <= 1083, $"{Path.GetFileName(file)}: printed page fits the paper");
    }

    Console.WriteLine("[7] Signature check: banner, Signatures pane, trusted certificates");
    {
        // Start from an empty trust list (the user's list is restored at the end).
        if (File.Exists(TrustedCertificates.StorePath)) File.Delete(TrustedCertificates.StorePath);
        TrustedCertificates.Reload();

        window.OpenFile(signedPath);
        var signedTab = TabNamed("signed.pdf");
        tabControl.SelectedIndex = tabs.IndexOf(signedTab);
        string BannerText() => Get<TextBlock>(window, "BannerText").Text;
        var banner = Get<Border>(window, "SignatureBanner");
        for (var i = 0; i < 100 && BannerText().Length == 0; i++) PumpNow(100);
        Console.WriteLine($"    banner: '{BannerText()}' / '{Get<TextBlock>(window, "BannerDetail").Text}'");
        Check(banner.Visibility == Visibility.Visible && BannerText() == "Signature validity is unknown.",
            "signed.pdf (self-signed, not trusted): banner says validity is unknown");

        var nav = Get<KcsPdfSigner.App.Navigation.NavigationPane>(window, "NavPane");
        var list = Get<ListBox>(nav, "SignatureList");
        for (var i = 0; i < 100 && (list.ItemsSource as System.Collections.IList)?.Count is null or 0; i++) PumpNow(100);
        var rows = ((System.Collections.IEnumerable?)list.ItemsSource)?.Cast<KcsPdfSigner.App.Navigation.NavItem>().ToList() ?? [];
        foreach (var row in rows) Console.WriteLine($"    pane: {row.Title} | {row.Detail.Split('\n')[0]} | glyph={(row.Glyph is null ? "none" : "yes")}");
        Check(rows.Count == 4 && rows.All(r => r.Detail.StartsWith("Validity unknown") && r.Glyph is not null),
            "Signatures pane lists 4 signatures with status 'Validity unknown' and a glyph");

        var viewer = Get<PdfViewer>(signedTab, "Viewer");
        PumpNow(500);
        var badges = Enumerable.Range(1, viewer.PageCount).Sum(p => viewer.GetOverlay(p)?.Children.OfType<Border>().Count() ?? 0);
        Check(badges >= 1, $"status badges drawn on the signature boxes ({badges})");

        var firstSig = (SignatureVerification)rows[0].Data!;
        TrustedCertificates.Add(firstSig.Certificate!.RawData);
        for (var i = 0; i < 100 && BannerText() != "Signed and all signatures are valid."; i++) PumpNow(100);
        Console.WriteLine($"    after trusting the signer: '{BannerText()}'");
        Check(BannerText() == "Signed and all signatures are valid.", "trusting the signer's certificate makes the banner say all valid");
        var stored = File.Exists(TrustedCertificates.StorePath) ? File.ReadAllText(TrustedCertificates.StorePath) : "";
        Check(stored.Contains(firstSig.Certificate.Thumbprint), "trusted-certs.json holds the trusted certificate");

        var dialog = new SignaturePropertiesDialog(Get<DocumentSession>(signedTab, "Session"), nav.Verification!, firstSig.FieldName);
        dialog.Show();
        PumpNow(300);
        var title = Get<TextBlock>(dialog, "StatusTitle").Text;
        var checks = Get<StackPanel>(dialog, "ChecksPanel").Children.Count;
        Console.WriteLine($"    properties: '{title}', {checks} check line(s)");
        Check(title.StartsWith("Signature is valid") && checks >= 4, "Signature Properties shows the status and the checks");
        dialog.Close();

        TrustedCertificates.Remove(firstSig.Certificate.Thumbprint);
        for (var i = 0; i < 100 && BannerText() != "Signature validity is unknown."; i++) PumpNow(100);
        Check(BannerText() == "Signature validity is unknown.", "removing the trusted certificate returns the banner to unknown");
    }

    Console.WriteLine("[8] Suggestion bars");
    {
        AppSettings.Apply(new AppSettings());
        var host = Get<StackPanel>(window, "SuggestionHost");
        List<string> Bars() => window.VisibleSuggestions.Select(s => s.Id).ToList();
        void WaitFor(Func<bool> condition) { for (var i = 0; i < 150 && !condition(); i++) PumpNow(100); }
        KcsPdfSigner.App.Suggestions.SuggestionBar Bar(string id) =>
            host.Children.OfType<KcsPdfSigner.App.Suggestions.SuggestionBar>().First(b => b.Suggestion.Id == id);
        void Click(KcsPdfSigner.App.Suggestions.SuggestionBar bar, string button) =>
            Get<Button>(bar, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Select(string file) { tabControl.SelectedIndex = tabs.IndexOf(TabNamed(file)); PumpNow(200); }

        var lockedPath = Path.Combine(docs, "locked.pdf");
        window.OpenFile(lockedPath, "u123");
        WaitFor(() => Bars().Contains(SuggestionIds.PasswordProtected));
        Console.WriteLine("    locked.pdf: " + string.Join(", ", Bars()));
        Check(Bars().Contains(SuggestionIds.PasswordProtected), "password-protected file shows 'Save a copy without password?'");
        var passwordBar = Bar(SuggestionIds.PasswordProtected);
        Check(Get<Button>(passwordBar, "ActionButton").Visibility == Visibility.Visible
              && window.RemovePasswordCopyCommand.CanExecute(null), "its action button is shown and the command is available");
        Check(host.Children.Count <= 2, $"at most two bars at once ({host.Children.Count})");

        var unsignedPath = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "unsigned.pdf");
        window.OpenFile(unsignedPath);
        WaitFor(() => Bars().Contains(SuggestionIds.SigningSpots));
        Console.WriteLine("    unsigned.pdf: " + string.Join(" | ", window.VisibleSuggestions.Select(s => s.Message)));
        Check(window.VisibleSuggestions.Any(s => s.Id == SuggestionIds.SigningSpots && s.Message.StartsWith("Found 4 places")),
            "unsigned.pdf shows 'Found 4 places that look like signature spots'");
        Check(!Bars().Contains(SuggestionIds.PasswordProtected), "the password bar of the other tab is not shown here");

        // Big file: lower the threshold below the 300-page file's size (about 180 KB).
        var bigPath = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "advisor", "300-pages.pdf");
        var lowered = AppSettings.Current.Clone();
        lowered.LargeFileMegabytes = 0.1;
        AppSettings.Apply(lowered);
        window.OpenFile(bigPath);
        WaitFor(() => Bars().Contains(SuggestionIds.LargeFile));
        Console.WriteLine("    300-pages.pdf: " + string.Join(" | ", window.VisibleSuggestions.Select(s => s.Message)));
        Check(window.VisibleSuggestions.Any(s => s.Id == SuggestionIds.LargeFile && s.Message.StartsWith("This file is 0.17 MB")),
            "big file (threshold lowered to 0.1 MB) shows 'Reduce the file size?'");
        Check(host.Children.Count == 2, $"two bars when two suggestions apply ({host.Children.Count})");

        // Dismiss: hidden for this document only.
        Select("unsigned.pdf");
        WaitFor(() => Bars().Contains(SuggestionIds.SigningSpots));
        Click(Bar(SuggestionIds.SigningSpots), "DismissButton");
        WaitFor(() => !Bars().Contains(SuggestionIds.SigningSpots));
        Check(!Bars().Contains(SuggestionIds.SigningSpots), "Dismiss hides the bar for this document");
        Select("300-pages.pdf");
        WaitFor(() => Bars().Contains(SuggestionIds.SigningSpots));
        Check(Bars().Contains(SuggestionIds.SigningSpots), "the dismissed suggestion still shows for another document");
        Select("unsigned.pdf");
        PumpNow(500);
        Check(!Bars().Contains(SuggestionIds.SigningSpots), "dismissed stays hidden when coming back to the document");

        // Don't show this again: stored in settings.json for every document.
        Select("300-pages.pdf");
        WaitFor(() => Bars().Contains(SuggestionIds.LargeFile));
        Click(Bar(SuggestionIds.LargeFile), "NeverButton");
        WaitFor(() => !Bars().Contains(SuggestionIds.LargeFile));
        Check(!Bars().Contains(SuggestionIds.LargeFile), "'Don't show this again' hides the bar");
        var stored = File.ReadAllText(AppSettings.StorePath);
        Check(AppSettings.Current.DismissedSuggestions.Contains(SuggestionIds.LargeFile) && stored.Contains("\"large-file\""),
            "'Don't show this again' is saved in settings.json");

        // After merging: only for a document marked as a fresh merge.
        var threePath = Path.Combine(docs, "three-pages.pdf");
        var merged = window.OpenFile(threePath)!;
        WaitFor(() => Bars().Count > 0);
        Check(!Bars().Contains(SuggestionIds.MergedPageNumbers), "ordinary file: no 'Add page numbers?'");
        merged.MarkOrigin(DocumentOrigin.Merged);
        WaitFor(() => Bars().Contains(SuggestionIds.MergedPageNumbers));
        Check(window.VisibleSuggestions.FirstOrDefault()?.Message == "Your new PDF has 3 pages. Add page numbers?",
            "freshly merged file: 'Your new PDF has 3 pages. Add page numbers?'");

        // OCR is a hook only: a scanned file shows no OCR bar while no OCR tool exists.
        var scanPath = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "advisor", "scan.pdf");
        window.OpenFile(scanPath);
        PumpNow(1500);
        var scanFacts = DocumentAdvisor.Inspect(scanPath, null);
        Check(scanFacts.Scan.IsScanned && !Bars().Contains(SuggestionIds.Scanned), "scanned file: detected, but no OCR bar without an OCR tool");

        // Preferences switch hides every bar; switching it back shows them again.
        Select("locked.pdf");
        WaitFor(() => Bars().Count > 0);
        var prefsOff = new PreferencesDialog();
        prefsOff.Show();
        PumpNow(200);
        ((CheckBox)LogicalTreeHelper.FindLogicalNode(prefsOff, "ShowSuggestionsBox")).IsChecked = false;
        FindButton(prefsOff, "OK")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpNow(500);
        Check(!AppSettings.Current.ShowSuggestions && host.Children.Count == 0 && host.Visibility != Visibility.Visible,
            "Preferences 'Show suggestions' off hides all bars");
        var on = AppSettings.Current.Clone();
        on.ShowSuggestions = true;
        AppSettings.Apply(on);
        WaitFor(() => Bars().Count > 0);
        Check(Bars().Contains(SuggestionIds.PasswordProtected), "switching it back on shows the bars again");
    }

    void PreferencesTest()
    {
        Console.WriteLine("[1] Preferences dialog");
        AppSettings.Apply(new AppSettings());
        var prefs = new PreferencesDialog();
        prefs.Show();
        PumpNow(300);

        var boxes = new List<CheckBox>();
        void Walk(DependencyObject d)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(d))
            {
                if (child is CheckBox cb) boxes.Add(cb);
                if (child is DependencyObject next) Walk(next);
            }
        }

        Walk(prefs);
        Check(boxes.Count == 11, $"dialog shows 11 checkboxes (found {boxes.Count})");
        boxes.First(b => b.Name == "WheelZoom").IsChecked = true;
        boxes.First(b => b.Name == "SingleKey").IsChecked = true;
        boxes.First(b => b.Name == "HideWarnings").IsChecked = true;
        boxes.First(b => b.Name == "FixedResolution").IsChecked = true;
        ((TextBox)LogicalTreeHelper.FindLogicalNode(prefs, "Ppi")).Text = "200";
        FindButton(prefs, "OK")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpNow(200);

        var now = AppSettings.Current;
        Check(now.MouseWheelZoom && now.SingleKeyAccelerators && now.HideEditWarnings
              && now.SnapshotFixedResolution && now.SnapshotPixelsPerInch == 200,
            "OK stores the chosen values");
        var saved = File.ReadAllText(AppSettings.StorePath);
        Check(saved.Contains("\"MouseWheelZoom\": true") && saved.Contains("\"SnapshotPixelsPerInch\": 200"),
            "settings.json on disk holds the values");
    }

    PreferencesTest();

    var log = App.ErrorLogPath;
    Check((File.Exists(log) ? new FileInfo(log).Length : 0) == logSizeAtStart, "no new entries in error.log");

    // The windows are left open: closing would ask about the highlight this test made. The process just ends.
    return 0;
}

static Button? FindButton(DependencyObject root, string text)
{
    foreach (var child in LogicalTreeHelper.GetChildren(root))
    {
        if (child is Button b && Equals(b.Content, text)) return b;
        if (child is DependencyObject next && FindButton(next, text) is { } hit) return hit;
    }

    return null;
}
