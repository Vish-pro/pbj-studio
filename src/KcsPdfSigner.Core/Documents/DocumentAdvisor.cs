using System.Diagnostics;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Signatures;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Stable ids of the suggestions (also stored in the settings as "don't show again").</summary>
public static class SuggestionIds
{
    public const string PasswordProtected = "password-protected";
    public const string Scanned = "scanned";
    public const string LargeFile = "large-file";
    public const string SigningSpots = "signing-spots";
    public const string HiddenInfo = "hidden-info";
    public const string OwnerRestrictions = "owner-restrictions";
    public const string SidewaysPages = "sideways-pages";
    public const string RepairedFile = "repaired-file";
    public const string FillableForm = "fillable-form";
    public const string MergedPageNumbers = "merged-page-numbers";
    public const string SignedSendCheck = "signed-send-check";
}

/// <summary>What a suggestion's button does. The App maps each to an existing feature.</summary>
public static class SuggestionActions
{
    /// <summary>Information only: no button.</summary>
    public const string None = "";
    public const string RemovePasswordCopy = "remove-password-copy";
    public const string Ocr = "ocr";
    public const string ReduceSize = "reduce-size";
    public const string SignAtSpots = "sign-at-spots";
    public const string RemoveHiddenInfo = "remove-hidden-info";
    public const string StraightenPages = "straighten-pages";
    public const string SaveCleanCopy = "save-clean-copy";
    public const string FillForm = "fill-form";
    public const string AddPageNumbers = "add-page-numbers";
    public const string SendCheck = "send-check";
}

/// <summary>How a document came to be open: decides the "after merging" and "after signing" suggestions.</summary>
public enum DocumentOrigin
{
    Opened,
    Merged,
    SignedHere
}

/// <summary>One suggestion for the open document.</summary>
public sealed class DocumentFinding
{
    public string Id { get; init; } = "";

    /// <summary>Higher shows first.</summary>
    public int Priority { get; init; }

    public string Message { get; init; } = "";

    /// <summary>One of <see cref="SuggestionActions"/>; empty for information only.</summary>
    public string ActionId { get; init; } = SuggestionActions.None;

    /// <summary>Button text, e.g. "Reduce size...".</summary>
    public string ActionLabel { get; init; } = "";

    /// <summary>Pages concerned (sideways pages, scanned pages), when relevant.</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];

    public int Count { get; init; }
}

/// <summary>A sideways or upside-down page and the clockwise turn that straightens it.</summary>
public sealed record PageTurn(int PageNumber, int Degrees);

/// <summary>A signature box: whether it shows on screen and on paper.</summary>
public sealed record SignatureBoxInfo(string FieldName, int PageNumber, bool IsVisible, bool IsPrintable, bool HasAppearance);

/// <summary>Pages that are pictures of paper with no text layer.</summary>
public sealed class ScanReport
{
    public int PageCount { get; init; }
    public List<int> ScannedPages { get; init; } = [];

    /// <summary>0 to 1: share of pages that look scanned.</summary>
    public double Share => PageCount == 0 ? 0 : ScannedPages.Count / (double)PageCount;

    /// <summary>Most pages look scanned.</summary>
    public bool IsScanned => PageCount > 0 && Share >= 0.5;
}

/// <summary>Facts about one PDF file, read once; the findings are built from them.</summary>
public sealed class DocumentAnalysis
{
    public long FileSizeBytes { get; set; }
    public int PageCount { get; set; }

    /// <summary>The file is encrypted and was opened with a password the user typed.</summary>
    public bool OpenedWithPassword { get; set; }

    public PermissionReport? Permissions { get; set; }

    /// <summary>iText had to rebuild or fix the cross-reference table: the file is damaged.</summary>
    public bool StructureRepaired { get; set; }

    public int SignatureCount { get; set; }
    public List<string> Signers { get; set; } = [];
    public List<SignatureBoxInfo> SignatureBoxes { get; set; } = [];
    public List<SignaturePlacement> SigningSpots { get; set; } = [];

    /// <summary>Form fields a person fills in (not signature fields, not push buttons).</summary>
    public int FillableFieldCount { get; set; }

    public HiddenInfoReport HiddenInfo { get; set; } = new();
    public ScanReport Scan { get; set; } = new();
    public List<PageTurn> SidewaysPages { get; set; } = [];

    /// <summary>Set when the file could not be read; then there are no findings.</summary>
    public string Error { get; set; } = "";

    public TimeSpan Elapsed { get; set; }
}

public sealed class AdvisorOptions
{
    /// <summary>Above this size the "reduce the file size" suggestion appears. Default 5 MB.</summary>
    public long LargeFileBytes { get; init; } = 5L * 1024 * 1024;

    public DocumentOrigin Origin { get; init; } = DocumentOrigin.Opened;
}

/// <summary>
/// Looks at a PDF and suggests what the user may want to do next (like Acrobat's yellow bars).
/// <see cref="Inspect"/> reads the file once (every page is parsed a single time for text, images and
/// text direction) and never throws; <see cref="BuildFindings"/> turns the facts into suggestions.
/// </summary>
/// <remarks>
/// What it can and cannot recognise:
/// - Signing spots: the same phrases as Multi-Sign's text detection ("Authorised Signatory", "Partner", ...);
///   a scanned page has no text, so its spots are not found.
/// - Scanned pages: a picture covering most of the page and (almost) no text. Recognised, but only offered
///   when an OCR tool exists.
/// - Sideways pages: only from text direction. A page with no text (a scan) is never flagged here; that
///   needs OCR's orientation detection.
/// - Hidden information: document details, XMP metadata, comment author names, attachments. Not: names
///   inside the page content, hidden layers, form values.
/// </remarks>
public static class DocumentAdvisor
{
    /// <summary>Minimum characters on a page before its text direction is trusted.</summary>
    private const int MinCharsForDirection = 40;

    /// <summary>Share of a page's characters that must run in one sideways direction.</summary>
    private const double DirectionMajority = 0.8;

    /// <summary>Share of the page an image must cover for the page to count as a scan.</summary>
    private const double ScanImageCoverage = 0.6;

    /// <summary>At most this many characters of text on a scanned page (a page number or a stamp).</summary>
    private const int ScanMaxChars = 25;

    /// <summary>Reads the file and builds its suggestions. Never throws.</summary>
    public static List<DocumentFinding> Analyze(string path, string? password, AdvisorOptions? options = null)
        => BuildFindings(Inspect(path, password), options ?? new AdvisorOptions());

    /// <summary>Reads every fact the suggestions need. Never throws: problems end up in <see cref="DocumentAnalysis.Error"/>.</summary>
    public static DocumentAnalysis Inspect(string path, string? password)
    {
        var timer = Stopwatch.StartNew();
        var analysis = new DocumentAnalysis();

        try
        {
            analysis.FileSizeBytes = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            analysis.Error = ex.Message;
            return analysis;
        }

        PdfDocument document;
        try
        {
            document = PdfIo.OpenRead(path, password);
        }
        catch (Exception ex)
        {
            analysis.Error = ex.Message;
            analysis.Elapsed = timer.Elapsed;
            return analysis;
        }

        using (document)
        {
            var reader = document.GetReader();
            Try(() =>
            {
                analysis.PageCount = document.GetNumberOfPages();
                analysis.StructureRepaired = reader.HasRebuiltXref() || reader.HasFixedXref();
                analysis.OpenedWithPassword = reader.IsEncrypted() && !string.IsNullOrEmpty(password);
            });
            Try(() => analysis.Permissions = PdfPermissions.FromReader(reader));
            Try(() => ReadSignatures(document, analysis));
            Try(() => analysis.FillableFieldCount = PdfFormOps.ReadFields(document)
                .Count(f => f.FieldType is not ("signature" or "button")));
            Try(() => analysis.HiddenInfo = PdfSanitizeOps.FindHiddenInfo(document));
            Try(() => ReadPages(document, analysis));
        }

        analysis.Elapsed = timer.Elapsed;
        return analysis;
    }

    /// <summary>The suggestions for a document, highest priority first.</summary>
    public static List<DocumentFinding> BuildFindings(DocumentAnalysis a, AdvisorOptions options)
    {
        var list = new List<DocumentFinding>();
        if (!string.IsNullOrEmpty(a.Error)) return list;

        // An owner-restricted file cannot be rewritten without its owner password, so suggestions whose
        // button changes the file are not offered; the restrictions bar explains why.
        var canChange = a.Permissions?.IsRestricted != true;

        if (a.StructureRepaired && canChange)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.RepairedFile, Priority = 95,
                Message = "This file was damaged and has been repaired in memory. Save a clean copy?",
                ActionId = SuggestionActions.SaveCleanCopy, ActionLabel = "Save clean copy..."
            });
        }

        if (options.Origin == DocumentOrigin.SignedHere && a.SignatureCount > 0)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.SignedSendCheck, Priority = 90, Count = a.SignatureCount,
                Message = "Signed. Run a check before you send it to a bank?",
                ActionId = SuggestionActions.SendCheck, ActionLabel = "Run send check"
            });
        }

        if (options.Origin == DocumentOrigin.Merged && a.PageCount >= 2)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.MergedPageNumbers, Priority = 88, Count = a.PageCount,
                Message = $"Your new PDF has {a.PageCount} pages. Add page numbers?",
                ActionId = SuggestionActions.AddPageNumbers, ActionLabel = "Add page numbers"
            });
        }

        if (a.SidewaysPages.Count > 0 && canChange)
        {
            var n = a.SidewaysPages.Count;
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.SidewaysPages, Priority = 80, Count = n,
                Pages = a.SidewaysPages.Select(p => p.PageNumber).ToList(),
                Message = n == 1
                    ? $"Page {a.SidewaysPages[0].PageNumber} looks sideways or upside down. Straighten it?"
                    : $"{n} pages look sideways or upside down ({PageList(a.SidewaysPages.Select(p => p.PageNumber))}). Straighten them?",
                ActionId = SuggestionActions.StraightenPages, ActionLabel = n == 1 ? "Straighten page" : "Straighten pages"
            });
        }

        var permissions = a.Permissions;
        var encrypted = permissions?.IsEncrypted == true || a.OpenedWithPassword;
        if (a.SignatureCount == 0 && !encrypted && a.SigningSpots.Count > 0)
        {
            var n = a.SigningSpots.Count;
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.SigningSpots, Priority = 75, Count = n,
                Pages = a.SigningSpots.Select(p => p.PageNumber).Distinct().ToList(),
                Message = n == 1
                    ? "Found 1 place that looks like a signature spot (for example 'Authorised Signatory' or 'Partner'). Sign there?"
                    : $"Found {n} places that look like signature spots (for example 'Authorised Signatory' or 'Partner'). Sign at them?",
                ActionId = SuggestionActions.SignAtSpots, ActionLabel = "Sign with DSC..."
            });
        }

        if (a.FillableFieldCount > 0 && a.SignatureCount == 0 && canChange)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.FillableForm, Priority = 70, Count = a.FillableFieldCount,
                Message = $"This is a fillable form ({a.FillableFieldCount} field{(a.FillableFieldCount == 1 ? "" : "s")}). Fill it in?",
                ActionId = SuggestionActions.FillForm, ActionLabel = "Fill in form"
            });
        }

        if (a.OpenedWithPassword)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.PasswordProtected, Priority = 65,
                Message = "This PDF is password protected. Save a copy without password?",
                ActionId = SuggestionActions.RemovePasswordCopy, ActionLabel = "Save a copy without password..."
            });
        }

        if (a.Scan.IsScanned)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.Scanned, Priority = 60, Count = a.Scan.ScannedPages.Count, Pages = a.Scan.ScannedPages,
                Message = a.Scan.ScannedPages.Count == a.PageCount
                    ? "This looks like a scan: the pages are pictures without text. Make it searchable (OCR)?"
                    : $"This looks like a scan ({a.Scan.ScannedPages.Count} of {a.PageCount} pages are pictures without text). Make it searchable (OCR)?",
                ActionId = SuggestionActions.Ocr, ActionLabel = "Make searchable (OCR)..."
            });
        }

        var locked = permissions?.LockedDescriptions() ?? [];
        if (locked.Count > 0)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.OwnerRestrictions, Priority = 55,
                Message = "The owner of this PDF has locked some actions. " + string.Join(" ", locked)
            });
        }

        if (options.LargeFileBytes > 0 && a.FileSizeBytes > options.LargeFileBytes)
        {
            var mb = a.FileSizeBytes / (1024.0 * 1024.0);
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.LargeFile, Priority = 50,
                Message = $"This file is {mb.ToString(mb < 10 ? "0.0#" : "0.0", System.Globalization.CultureInfo.InvariantCulture)} MB. " +
                          "Many bank and portal uploads have size limits. Reduce the file size?",
                ActionId = SuggestionActions.ReduceSize, ActionLabel = "Reduce size..."
            });
        }

        if (a.HiddenInfo.HasPersonalInfo && canChange)
        {
            list.Add(new DocumentFinding
            {
                Id = SuggestionIds.HiddenInfo, Priority = 40,
                Message = "This file carries hidden information: " + a.HiddenInfo.Summary() + ". Remove it?",
                ActionId = SuggestionActions.RemoveHiddenInfo, ActionLabel = "Remove hidden info..."
            });
        }

        return list.OrderByDescending(f => f.Priority).ToList();
    }

    // ---- Detection functions (public for tests and future tools) -----------

    /// <summary>Pages that are full-page pictures with no (or almost no) text.</summary>
    public static ScanReport DetectScanned(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var analysis = new DocumentAnalysis();
        ReadPages(document, analysis, findSpots: false);
        return analysis.Scan;
    }

    /// <summary>Pages whose text runs sideways or upside down, with the clockwise turn that straightens each.</summary>
    public static List<PageTurn> DetectSidewaysPages(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var analysis = new DocumentAnalysis();
        ReadPages(document, analysis, findSpots: false);
        return analysis.SidewaysPages;
    }

    /// <summary>Every applied signature's box: visible, printable (Print flag) and drawn (has an appearance).</summary>
    public static List<SignatureBoxInfo> InspectSignatureBoxes(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var analysis = new DocumentAnalysis();
        ReadSignatures(document, analysis);
        return analysis.SignatureBoxes;
    }

    /// <summary>True when iText had to rebuild the file's structure to read it.</summary>
    public static bool IsDamaged(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var reader = document.GetReader();
        return reader.HasRebuiltXref() || reader.HasFixedXref();
    }

    /// <summary>Writes a fresh copy with a new cross-reference table (repairs a damaged file).</summary>
    public static void WriteCleanCopy(string input, string output, string? password = null)
        => PdfIo.Edit(input, output, password, _ => { });

    /// <summary>
    /// Turns each page by its own amount (pages grouped by turn), writing one output. For undo, run it
    /// inside a single session change.
    /// </summary>
    public static void StraightenPages(string input, string output, IReadOnlyList<PageTurn> turns, string? password = null)
    {
        var groups = turns.Where(t => t.Degrees % 360 != 0).GroupBy(t => PdfIo.NormalizeRotation(t.Degrees)).ToList();
        if (groups.Count == 0)
        {
            WriteCleanCopy(input, output, password);
            return;
        }

        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KcsPdfStudio-straighten-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var current = input;
            for (var i = 0; i < groups.Count; i++)
            {
                var next = i == groups.Count - 1 ? output : System.IO.Path.Combine(folder, i + ".pdf");
                PdfPageOps.RotatePages(current, next, groups[i].Select(t => t.PageNumber), groups[i].Key, password);
                current = next;
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { /* temp only */ }
        }
    }

    // ---- Reading -------------------------------------------------------------

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // One check failing must not stop the others; that check simply finds nothing.
        }
    }

    private static void ReadSignatures(PdfDocument document, DocumentAnalysis analysis)
    {
        var util = new SignatureUtil(document);
        var names = util.GetSignatureNames();
        analysis.SignatureCount = names.Count;
        if (names.Count == 0) return;

        var form = PdfFormCreator.GetAcroForm(document, false);
        foreach (var name in names)
        {
            try
            {
                var pkcs7 = util.ReadSignatureData(name);
                var signer = pkcs7?.GetSignName();
                if (string.IsNullOrWhiteSpace(signer) && pkcs7 is not null)
                    signer = CertificateInfo.GetSubjectFields(pkcs7.GetSigningCertificate())?.GetField("CN");
                if (!string.IsNullOrWhiteSpace(signer) && !analysis.Signers.Contains(signer)) analysis.Signers.Add(signer);
            }
            catch
            {
                // A damaged signature still counts; its signer is unknown.
            }

            var widget = form?.GetField(name)?.GetWidgets().FirstOrDefault();
            if (widget is null)
            {
                analysis.SignatureBoxes.Add(new SignatureBoxInfo(name, 0, false, true, false));
                continue;
            }

            var rect = widget.GetRectangle()?.ToRectangle();
            var visible = rect is not null && rect.GetWidth() > 0 && rect.GetHeight() > 0
                          && (widget.GetFlags() & PdfAnnotation.HIDDEN) == 0;
            var printable = (widget.GetFlags() & PdfAnnotation.PRINT) != 0;
            var appearance = widget.GetNormalAppearanceObject() is not null;
            var page = widget.GetPage();
            var number = page is null ? 0 : document.GetPageNumber(page);
            analysis.SignatureBoxes.Add(new SignatureBoxInfo(name, number, visible, printable, appearance));
        }
    }

    private static void ReadPages(PdfDocument document, DocumentAnalysis analysis, bool findSpots = true)
    {
        var pageCount = document.GetNumberOfPages();
        var scanned = new List<int>();
        var lines = new Dictionary<int, List<PageTextLayout.TextLine>>();
        var settings = new DetectionSettings();
        var phrases = settings.AnchorPhrases.Select(Squeeze).Where(p => p.Length > 0).ToList();

        for (var number = 1; number <= pageCount; number++)
        {
            try
            {
                var page = document.GetPage(number);
                var scanner = new PageScanner();
                new PdfCanvasProcessor(scanner).ProcessPageContent(page);

                // Glyph boxes are slow to compute; only pages whose text holds a signing phrase need them.
                if (findSpots)
                {
                    var text = Squeeze(scanner.Text.ToString());
                    if (phrases.Any(p => text.Contains(p, StringComparison.Ordinal)))
                    {
                        var glyphs = new PageTextLayout.GlyphCollector();
                        new PdfCanvasProcessor(glyphs).ProcessPageContent(page);
                        lines[number] = PageTextLayout.BuildLines(glyphs.Glyphs);
                    }
                }

                var box = PageGeometry.VisibleBox(page);
                var pageArea = Math.Max(1.0, (double)box.GetWidth() * box.GetHeight());
                if (scanner.LargestImageArea / pageArea >= ScanImageCoverage && scanner.TextChars <= ScanMaxChars)
                    scanned.Add(number);

                var turn = SidewaysTurn(scanner, PdfIo.NormalizeRotation(page.GetRotation()));
                if (turn != 0) analysis.SidewaysPages.Add(new PageTurn(number, turn));
            }
            catch
            {
                // A page that cannot be read is skipped.
            }
        }

        analysis.Scan = new ScanReport { PageCount = pageCount, ScannedPages = scanned };

        if (findSpots)
        {
            try
            {
                analysis.SigningSpots = TextAnchorDetector.Detect(document, settings,
                    (_, number) => lines.TryGetValue(number, out var found) ? found : []);
            }
            catch
            {
                analysis.SigningSpots = [];
            }
        }
    }

    /// <summary>
    /// The clockwise turn (90, 180 or 270) that makes the page's text upright, or 0. Conservative: at
    /// least <see cref="MinCharsForDirection"/> characters, and a clear majority of all the page's
    /// characters (slanted watermarks included) running in the same sideways or upside-down direction.
    /// </summary>
    private static int SidewaysTurn(PageScanner scanner, int pageRotation)
    {
        if (scanner.TextChars < MinCharsForDirection) return 0;

        // Text drawn at angle a (counter-clockwise, user space) on a page turned clockwise by /Rotate r
        // is seen at a - r. Turning the page a further (a - r) clockwise brings it upright.
        var byDisplay = new Dictionary<int, int>();
        foreach (var (angle, chars) in scanner.CharsByAngle)
        {
            var display = PdfIo.NormalizeRotation(angle - pageRotation);
            byDisplay[display] = byDisplay.TryGetValue(display, out var c) ? c + chars : chars;
        }

        var best = byDisplay.Where(p => p.Key != 0).OrderByDescending(p => p.Value).FirstOrDefault();
        if (best.Value == 0) return 0;
        return best.Value >= scanner.TextChars * DirectionMajority ? best.Key : 0;
    }

    /// <summary>Lower case letters and digits only, so a phrase split over text pieces still matches.</summary>
    private static string Squeeze(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static string PageList(IEnumerable<int> pages)
    {
        var list = pages.ToList();
        return list.Count <= 6 ? "pages " + string.Join(", ", list) : "pages " + string.Join(", ", list.Take(5)) + " and more";
    }

    /// <summary>One pass over a page: its text (to see whether a signing phrase is there), text direction, and image coverage.</summary>
    private sealed class PageScanner : IEventListener
    {
        public System.Text.StringBuilder Text { get; } = new();

        /// <summary>Visible characters on the page (spaces not counted).</summary>
        public int TextChars { get; private set; }

        /// <summary>Characters per text angle (0, 90, 180, 270, counter-clockwise in user space); slanted text is not listed.</summary>
        public Dictionary<int, int> CharsByAngle { get; } = new();

        public double LargestImageArea { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type == EventType.RENDER_TEXT && data is TextRenderInfo text)
            {
                var chars = 0;
                foreach (var ch in text.GetText() ?? "")
                {
                    if (!char.IsWhiteSpace(ch)) chars++;
                }

                if (chars == 0) return;
                TextChars += chars;
                Text.Append(text.GetText()).Append(' ');

                var baseline = text.GetBaseline();
                var start = baseline.GetStartPoint();
                var end = baseline.GetEndPoint();
                var dx = end.Get(Vector.I1) - start.Get(Vector.I1);
                var dy = end.Get(Vector.I2) - start.Get(Vector.I2);
                if (Math.Abs(dx) + Math.Abs(dy) > 0.01)
                {
                    var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
                    var snapped = (int)Math.Round(degrees / 90.0) * 90;
                    if (Math.Abs(degrees - snapped) <= 3)
                    {
                        var key = PdfIo.NormalizeRotation(snapped);
                        CharsByAngle[key] = CharsByAngle.TryGetValue(key, out var c) ? c + chars : chars;
                    }
                }
            }
            else if (type == EventType.RENDER_IMAGE && data is ImageRenderInfo image)
            {
                var m = image.GetImageCtm();
                var area = Math.Abs((double)m.Get(Matrix.I11) * m.Get(Matrix.I22) - (double)m.Get(Matrix.I12) * m.Get(Matrix.I21));
                if (area > LargestImageArea) LargestImageArea = area;
            }
        }

        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT, EventType.RENDER_IMAGE];
    }
}
