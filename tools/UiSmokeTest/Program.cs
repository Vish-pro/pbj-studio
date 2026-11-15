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
using Path = System.IO.Path;

var failures = new List<string>();
void Check(bool ok, string message)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + message);
    if (!ok) failures.Add(message);
}

var docs = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest", "docs");
var linksPdf = Path.Combine(docs, "links.pdf");
var formPdf = Path.Combine(docs, "form.pdf");
if (!File.Exists(linksPdf) || !File.Exists(formPdf))
{
    Console.WriteLine("Run tools\\SignerSmokeTest first (it creates links.pdf and form.pdf).");
    return 2;
}

var backup = File.Exists(AppSettings.StorePath) ? File.ReadAllText(AppSettings.StorePath) : null;
var exitCode = 1;

var thread = new Thread(() =>
{
    var dispatcher = Dispatcher.CurrentDispatcher;
    dispatcher.BeginInvoke(async () =>
    {
        try
        {
            exitCode = await RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("UI smoke test crashed: " + ex);
            exitCode = 1;
        }
        finally
        {
            dispatcher.InvokeShutdown();
        }
    });
    Dispatcher.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (backup is null) { if (File.Exists(AppSettings.StorePath)) File.Delete(AppSettings.StorePath); }
else File.WriteAllText(AppSettings.StorePath, backup);

var passed = failures.Count == 0 && exitCode == 0;
Console.WriteLine(passed ? "\nPASS - all UI checks succeeded." : $"\nFAIL - {failures.Count} check(s) failed.");
return passed ? 0 : 1;

async Task Pump(int milliseconds)
{
    await Task.Delay(milliseconds);
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

async Task<int> RunAsync()
{
    var app = new App();
    app.InitializeComponent();

    Console.WriteLine("[1] Preferences dialog");
    AppSettings.Apply(new AppSettings());
    var prefs = new PreferencesDialog();
    prefs.Show();
    await Pump(300);

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
    Check(boxes.Count == 10, $"dialog shows 10 checkboxes (found {boxes.Count})");
    boxes.First(b => b.Name == "WheelZoom").IsChecked = true;
    boxes.First(b => b.Name == "SingleKey").IsChecked = true;
    boxes.First(b => b.Name == "HideWarnings").IsChecked = true;
    boxes.First(b => b.Name == "FixedResolution").IsChecked = true;
    ((TextBox)LogicalTreeHelper.FindLogicalNode(prefs, "Ppi")).Text = "200";
    FindButton(prefs, "OK")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await Pump(200);

    var now = AppSettings.Current;
    Check(now.MouseWheelZoom && now.SingleKeyAccelerators && now.HideEditWarnings
          && now.SnapshotFixedResolution && now.SnapshotPixelsPerInch == 200,
        "OK stores the chosen values");
    var saved = File.ReadAllText(AppSettings.StorePath);
    Check(saved.Contains("\"MouseWheelZoom\": true") && saved.Contains("\"SnapshotPixelsPerInch\": 200"),
        "settings.json on disk holds the values");

    Console.WriteLine("[2] Main window: form fields, links, select tool, highlight, snapshot");
    AppSettings.Apply(new AppSettings { QuickActionsOnSelection = false, SnapshotFixedResolution = true, SnapshotPixelsPerInch = 144 });
    var window = new MainWindow();
    window.Show();
    window.OpenFile(linksPdf);
    window.OpenFile(formPdf);
    await Pump(6000);

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
    await Pump(500);

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
    await Pump(1500);
    var selection = Get<TextSelection?>(window, "_selection");
    Check(selection is not null && selection.Text.Contains("example.com"), "select tool reads the dragged text");
    var shaded = linkViewer.GetOverlay(1)?.Children.OfType<Rectangle>().Count() ?? 0;
    Check(shaded >= 1, "selected text is shaded on the page");

    var before = PdfAnnotationOps.List(linkSession.CurrentFile).Count;
    Method("HighlightSelection").Invoke(window, null);
    await Pump(2000);
    var after = PdfAnnotationOps.List(linkSession.CurrentFile).Count;
    Check(after == before + 1, $"highlight adds one annotation ({before} -> {after})");

    Method("SetPointerTool").Invoke(window, [snapshot]);
    Clipboard.Clear();
    Method("TakeSnapshot").Invoke(window, [linkTab, drag]);
    await Pump(3500);
    var image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
    Check(image is not null && image.PixelWidth > 20 && image.PixelHeight > 5,
        image is null ? "snapshot reached the clipboard" : $"snapshot on the clipboard: {image.PixelWidth} x {image.PixelHeight} px");

    Method("SetPointerTool").Invoke(window, [hand]);
    Check(linkViewer.InteractionMode == InteractionMode.Browse, "hand tool restores browse mode");

    Console.WriteLine("[3] Session list and display settings");
    Method("RememberSession").Invoke(window, null);
    Check(AppSettings.Current.LastSession.Count == 2, "open documents are remembered for the next start");

    AppSettings.Apply(new AppSettings { Touch = TouchMode.On });
    await Pump(300);
    Check(window.Content is FrameworkElement { LayoutTransform: ScaleTransform { ScaleX: > 1.2 } }, "touch mode enlarges the interface");
    AppSettings.Apply(new AppSettings { Touch = TouchMode.Off });
    await Pump(300);
    Check(window.Content is FrameworkElement fe && !(fe.LayoutTransform is ScaleTransform { ScaleX: > 1.2 }),
        "touch mode off restores it");

    var log = App.ErrorLogPath;
    Check(!File.Exists(log) || new FileInfo(log).LastWriteTime < DateTime.Now.AddMinutes(-3), "no new entries in error.log");

    window.Close();
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
