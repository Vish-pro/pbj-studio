using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using PDFtoImage;
using SkiaSharp;
using Tesseract;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>What to OCR and how.</summary>
public sealed class OcrOptions
{
    /// <summary>1-based pages; null or empty means every page.</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>Tesseract codes, e.g. ["hin", "eng"]. Several are read together.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [OcrLanguages.EnglishCode];

    /// <summary>Folders searched for the language files; null means <see cref="OcrLanguages.DefaultFolders"/>.</summary>
    public IReadOnlyList<string>? LanguageFolders { get; init; }

    /// <summary>Leave pages that already carry text (a typed page, or OCR done before) as they are.</summary>
    public bool SkipPagesWithText { get; init; } = true;

    /// <summary>Turn sideways or upside-down picture pages upright before reading them (needs osd).</summary>
    public bool DetectOrientation { get; init; } = true;

    /// <summary>Render resolution; lowered for very large pages (see <see cref="PdfOcrOps.MaxPixels"/>).</summary>
    public int Dpi { get; init; } = 300;
}

/// <summary>Progress of a run: <see cref="Done"/> of <see cref="Total"/> pages, now on <see cref="PageNumber"/>.</summary>
public readonly record struct OcrProgress(int Done, int Total, int PageNumber);

/// <summary>A picture page's orientation as read by Tesseract's orientation detection.</summary>
/// <param name="TurnClockwise">Clockwise turn that makes it upright: 0, 90, 180 or 270.</param>
/// <param name="Confidence">Tesseract's orientation confidence (higher is surer; about 5 to 30 on real pages).</param>
public sealed record PageOrientation(int PageNumber, int TurnClockwise, double Confidence);

/// <summary>What an OCR run did.</summary>
public sealed class OcrRunResult
{
    /// <summary>Pages that got a text layer.</summary>
    public List<int> PagesRead { get; } = [];

    /// <summary>Pages left alone because they already had text.</summary>
    public List<int> PagesSkipped { get; } = [];

    /// <summary>Pages that were read but where no text was found (blank, a photo, handwriting).</summary>
    public List<int> PagesWithoutText { get; } = [];

    /// <summary>Picture pages that were sideways or upside down and were turned upright for reading (the page itself is not turned).</summary>
    public List<PageOrientation> TurnedForReading { get; } = [];

    public int Words { get; set; }

    /// <summary>Tesseract's average word confidence over the pages read, 0 to 100.</summary>
    public double MeanConfidence { get; set; }

    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// OCR: makes scanned pages searchable by putting an invisible text layer over them. The page picture is
/// not changed: each page is rendered (PDFium, about 300 dpi), read by Tesseract, and Tesseract's own
/// text-only PDF output (a glyphless font with a ToUnicode map, which works for every script) is drawn
/// over the original page as a form XObject, scaled to the visible page and turned to match its /Rotate.
/// </summary>
/// <remarks>
/// Accuracy depends on the scan: clean printed text at 300 dpi reads well; handwriting, faint or skewed
/// photos, stamps and mixed scripts read poorly or not at all. Nothing is sent anywhere: all work is local.
/// </remarks>
public static class PdfOcrOps
{
    /// <summary>Pages with more visible characters than this already have text.</summary>
    public const int TextCharsThreshold = 25;

    /// <summary>Upper bound on rendered pixels per page (an A3 page at 300 dpi is about 17 million).</summary>
    public const double MaxPixels = 24_000_000;

    /// <summary>
    /// Orientation confidence above which a picture page is turned before it is read. Clean pages score
    /// about 7 to 25; pages without enough text score below 2 and are read as they are.
    /// </summary>
    public const double TurnForReadingConfidence = 3.0;

    /// <summary>
    /// Orientation confidence above which a picture page is reported as sideways or upside down (the
    /// suggestion bar). Higher than <see cref="TurnForReadingConfidence"/>: a false alarm is worse than a miss.
    /// </summary>
    public const double SidewaysSuggestionConfidence = 6.0;

    /// <summary>
    /// A page turned for reading that reads with a mean confidence below this is also read as it is;
    /// the better reading is kept (a safety net against a wrong orientation).
    /// </summary>
    public const float SecondOpinionConfidence = 0.6f;

    private static readonly object EngineSetupLock = new();
    private static bool _searchPathSet;

    // ---- Availability --------------------------------------------------------

    /// <summary>
    /// "" when the OCR engine can run on this PC, otherwise a sentence for the user (missing engine files,
    /// or the Microsoft Visual C++ runtime that Tesseract needs).
    /// </summary>
    public static string CheckEngine()
    {
        var folder = NativeFolder();
        if (folder is null)
            return "The OCR engine files (x64\\tesseract50.dll and x64\\leptonica-1.82.0.dll) are missing from the program folder. Reinstall PB&J Studio.";

        foreach (var runtime in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
        {
            if (NativeLibrary.TryLoad(runtime, out var handle))
            {
                NativeLibrary.Free(handle);
                continue;
            }

            return "OCR needs the Microsoft Visual C++ 2015-2022 Redistributable (x64), which is not installed on this PC. " +
                   "It is free from Microsoft: search for \"Visual C++ Redistributable latest supported downloads\", " +
                   "install vc_redist.x64.exe, then try again.";
        }

        return "";
    }

    /// <summary>Folder that holds x64\tesseract50.dll: the program folder, or null when the files are missing.</summary>
    private static string? NativeFolder()
    {
        var baseDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "x64", "tesseract50.dll")) && File.Exists(Path.Combine(baseDir, "x64", "leptonica-1.82.0.dll"))
            ? baseDir
            : null;
    }

    private static void PrepareEngine()
    {
        var problem = CheckEngine();
        if (problem.Length > 0) throw new InvalidOperationException(problem);

        lock (EngineSetupLock)
        {
            if (_searchPathSet) return;
            // The wrapper looks for x64\*.dll under this folder. In a single-file build the assembly has no
            // location of its own, so the folder is set explicitly.
            TesseractEnviornment.CustomSearchPath = NativeFolder()!.TrimEnd('\\', '/');
            _searchPathSet = true;
        }
    }

    // ---- Facts ---------------------------------------------------------------

    /// <summary>Visible characters of text on each page (index 0 = page 1).</summary>
    public static int[] CountTextChars(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        return CountTextChars(document);
    }

    private static int[] CountTextChars(PdfDocument document)
    {
        var count = document.GetNumberOfPages();
        var result = new int[count];
        for (var number = 1; number <= count; number++)
        {
            try
            {
                var counter = new CharCounter();
                new PdfCanvasProcessor(counter).ProcessPageContent(document.GetPage(number));
                result[number - 1] = counter.Chars;
            }
            catch
            {
                result[number - 1] = 0; // unreadable content: treat as a picture page
            }
        }

        return result;
    }

    /// <summary>The pages a run with these options would read (the others already have text, or are out of range).</summary>
    public static List<int> PagesToRead(string path, string? password, IReadOnlyList<int>? pages, bool skipPagesWithText)
    {
        var chars = CountTextChars(path, password);
        return SelectPages(chars, pages, skipPagesWithText, out _);
    }

    private static List<int> SelectPages(int[] chars, IReadOnlyList<int>? pages, bool skip, out List<int> skipped)
    {
        var wanted = (pages is { Count: > 0 } ? pages : Enumerable.Range(1, chars.Length))
            .Distinct().OrderBy(p => p).ToList();
        foreach (var page in wanted)
            if (page < 1 || page > chars.Length)
                throw new ArgumentOutOfRangeException(nameof(pages), $"Page {page} does not exist (the document has {chars.Length} page(s)).");

        skipped = skip ? wanted.Where(p => chars[p - 1] > TextCharsThreshold).ToList() : [];
        var skippedSet = skipped.ToHashSet();
        return wanted.Where(p => !skippedSet.Contains(p)).ToList();
    }

    // ---- Make searchable -----------------------------------------------------

    /// <summary>
    /// Writes <paramref name="output"/>: <paramref name="input"/> with an invisible, searchable text layer on
    /// the chosen pages. Page pictures, sizes, rotation, signatures' appearance and encryption stay as they
    /// are. Runs on the calling thread (call it off the UI thread); checks <paramref name="cancellationToken"/>
    /// between pages, and on cancellation deletes the half-written output and throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static OcrRunResult MakeSearchable(
        string input, string output, OcrOptions options, IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        PdfIo.RequireFile(input);
        PdfIo.RequireDistinct(input, output);
        var timer = Stopwatch.StartNew();
        var result = new OcrRunResult();

        var languages = options.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l != OcrLanguages.OsdCode).Distinct().ToList();
        if (languages.Count == 0) throw new ArgumentException("Choose at least one language.", nameof(options));

        var installed = OcrLanguages.FindInstalled(options.LanguageFolders);
        var missing = languages.Where(l => !installed.ContainsKey(l)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("These languages are not installed: " +
                string.Join(", ", missing.Select(m => OcrLanguages.Find(m)?.Name ?? m)) + ". Add them with Languages... in the OCR tool.");

        PrepareEngine();
        var work = Path.Combine(Path.GetTempPath(), "KcsPdfStudio-ocr-" + Guid.NewGuid().ToString("N"));
        var tessdata = Path.Combine(work, "tessdata");
        Directory.CreateDirectory(tessdata);
        var priority = Thread.CurrentThread.Priority;

        try
        {
            // Tesseract reads every language from one folder: gather the chosen ones (they may come from
            // the program folder and the user folder), orientation detection, and the glyphless font.
            foreach (var code in languages) File.Copy(installed[code], Path.Combine(tessdata, code + ".traineddata"));
            var canTurn = false;
            if (options.DetectOrientation && installed.TryGetValue(OcrLanguages.OsdCode, out var osdFile))
            {
                File.Copy(osdFile, Path.Combine(tessdata, "osd.traineddata"));
                canTurn = true;
            }
            WriteGlyphlessFont(tessdata);

            var bytes = File.ReadAllBytes(input);
            PdfIo.Edit(input, output, password, document =>
            {
                var chars = CountTextChars(document);
                var pages = SelectPages(chars, options.Pages, options.SkipPagesWithText, out var skipped);
                result.PagesSkipped.AddRange(skipped);

                // OCR is heavy: keep the rest of the program (and the PC) responsive while it runs.
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* not allowed: ignore */ }

                using var engine = new TesseractEngine(tessdata, string.Join("+", languages), EngineMode.LstmOnly);
                using var osd = canTurn ? new TesseractEngine(tessdata, OcrLanguages.OsdCode, EngineMode.TesseractOnly) : null;
                double confidenceSum = 0;

                for (var i = 0; i < pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var number = pages[i];
                    progress?.Report(new OcrProgress(i, pages.Count, number));

                    var page = document.GetPage(number);
                    var display = DisplaySize(page);
                    var dpi = CapDpi(display.Width, display.Height, options.Dpi);
                    using var pix = RenderPix(bytes, number, password, dpi);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Picture pages only: a page with real text shows its direction in that text.
                    PageOrientation? orientation = null;
                    if (osd is not null && chars[number - 1] <= TextCharsThreshold)
                    {
                        var found = DetectOrientation(osd, pix, number);
                        if (found.TurnClockwise != 0 && found.Confidence >= TurnForReadingConfidence) orientation = found;
                    }

                    var turn = orientation?.TurnClockwise ?? 0;
                    var read = ReadPage(engine, pix, dpi, turn, tessdata, Path.Combine(work, $"layer-{number}-{turn}"));
                    if (turn != 0 && read.Confidence < SecondOpinionConfidence)
                    {
                        // A doubtful turn: read the page as it is too, and keep whichever reads better.
                        cancellationToken.ThrowIfCancellationRequested();
                        var asIs = ReadPage(engine, pix, dpi, 0, tessdata, Path.Combine(work, $"layer-{number}-0"));
                        if (asIs.Words > 0 && asIs.Confidence > read.Confidence)
                        {
                            TryDelete(read.File);
                            read = asIs;
                            turn = 0;
                        }
                        else
                        {
                            TryDelete(asIs.File);
                        }
                    }

                    if (turn != 0) result.TurnedForReading.Add(orientation!);
                    if (read.Words == 0)
                    {
                        result.PagesWithoutText.Add(number);
                    }
                    else
                    {
                        DrawLayer(document, page, read.File, turn);
                        result.PagesRead.Add(number);
                        result.Words += read.Words;
                        confidenceSum += read.Confidence * 100;
                    }

                    TryDelete(read.File);
                    progress?.Report(new OcrProgress(i + 1, pages.Count, number));
                }

                result.MeanConfidence = result.PagesRead.Count == 0 ? 0 : confidenceSum / result.PagesRead.Count;
            });
        }
        catch
        {
            PdfIo.TryDelete(output);
            throw;
        }
        finally
        {
            try { Thread.CurrentThread.Priority = priority; } catch { /* ignore */ }
            try { Directory.Delete(work, true); } catch { /* temp only */ }
        }

        result.Elapsed = timer.Elapsed;
        return result;
    }

    /// <summary>
    /// Reads one page picture (turned <paramref name="turn"/> degrees clockwise first) and writes Tesseract's
    /// one-page text-only PDF to <paramref name="layerBase"/>.pdf. Returns the word count and Tesseract's
    /// mean word confidence (0 to 1).
    /// </summary>
    private static (int Words, float Confidence, string File) ReadPage(
        TesseractEngine engine, Pix pix, int dpi, int turn, string tessdata, string layerBase)
    {
        using var picture = turn == 0 ? pix.Clone() : Turn(pix, turn);
        picture.XRes = dpi;
        picture.YRes = dpi;

        int words;
        float confidence;
        using (var renderer = ResultRenderer.CreatePdfRenderer(layerBase, tessdata, true))
        {
            using (renderer.BeginDocument("OCR"))
            using (var ocr = engine.Process(picture))
            {
                var text = ocr.GetText() ?? "";
                words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                confidence = ocr.GetMeanConfidence();
                renderer.AddPage(ocr);
            }
        }

        return (words, confidence, layerBase + ".pdf");
    }

    // ---- Orientation ---------------------------------------------------------

    /// <summary>
    /// Reads the orientation of picture pages (pages without text) with Tesseract's orientation detection,
    /// at <paramref name="dpi"/> (150 is enough and fast). Pages where it cannot tell (too little text)
    /// come back with turn 0 and confidence 0. Needs osd.traineddata.
    /// </summary>
    public static List<PageOrientation> DetectOrientation(
        string path, string? password, IReadOnlyList<int> pages, IReadOnlyList<string>? languageFolders = null,
        int dpi = 150, CancellationToken cancellationToken = default)
    {
        var installed = OcrLanguages.FindInstalled(languageFolders);
        if (!installed.TryGetValue(OcrLanguages.OsdCode, out var osdFile))
            throw new InvalidOperationException("Orientation detection (osd.traineddata) is not installed.");

        PrepareEngine();
        var work = Path.Combine(Path.GetTempPath(), "KcsPdfStudio-osd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var list = new List<PageOrientation>();
        try
        {
            File.Copy(osdFile, Path.Combine(work, "osd.traineddata"));
            var bytes = File.ReadAllBytes(path);
            using var osd = new TesseractEngine(work, OcrLanguages.OsdCode, EngineMode.TesseractOnly);
            using var document = PdfIo.OpenRead(path, password);
            foreach (var number in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (number < 1 || number > document.GetNumberOfPages()) continue;
                var display = DisplaySize(document.GetPage(number));
                using var pix = RenderPix(bytes, number, password, CapDpi(display.Width, display.Height, dpi));
                list.Add(DetectOrientation(osd, pix, number));
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp only */ }
        }

        return list;
    }

    private static PageOrientation DetectOrientation(TesseractEngine osd, Pix pix, int number)
    {
        try
        {
            using var page = osd.Process(pix, PageSegMode.OsdOnly);
            page.DetectBestOrientationAndScript(out int degrees, out float confidence, out _, out _);
            // Tesseract reports how far the page is turned anticlockwise; turning it that far clockwise
            // makes it upright (an image turned 90 degrees anticlockwise reports 270... see the smoke test).
            var turn = PdfIo.NormalizeRotation(360 - degrees);
            if (float.IsNaN(confidence) || float.IsInfinity(confidence)) return new PageOrientation(number, 0, 0);
            return new PageOrientation(number, turn, confidence);
        }
        catch
        {
            // "Too few characters": nothing to go on.
            return new PageOrientation(number, 0, 0);
        }
    }

    /// <summary>A copy of the picture turned clockwise by 90, 180 or 270 degrees.</summary>
    private static Pix Turn(Pix pix, int clockwise)
    {
        switch (clockwise)
        {
            case 90:
                return pix.Rotate90(1);
            case 270:
                return pix.Rotate90(-1);
            case 180:
                using (var half = pix.Rotate90(1)) return half.Rotate90(1);
            default:
                return pix.Clone();
        }
    }

    // ---- Rendering -----------------------------------------------------------

    /// <summary>The visible page (crop box) as it is displayed, i.e. with /Rotate applied, in points.</summary>
    private static (float Width, float Height) DisplaySize(PdfPage page)
    {
        var crop = page.GetCropBox();
        var rotation = PdfIo.NormalizeRotation(page.GetRotation());
        return rotation is 90 or 270 ? (crop.GetHeight(), crop.GetWidth()) : (crop.GetWidth(), crop.GetHeight());
    }

    private static int CapDpi(double widthPoints, double heightPoints, int wanted)
    {
        var inches = Math.Max(0.01, widthPoints / 72.0) * Math.Max(0.01, heightPoints / 72.0);
        var cap = Math.Sqrt(MaxPixels / inches);
        return (int)Math.Clamp(Math.Floor(Math.Min(wanted, cap)), 50, 600);
    }

    /// <summary>Renders the page content (no comments or form fields) as an 8-bit grey picture for Tesseract.</summary>
    private static Pix RenderPix(byte[] pdf, int pageNumber, string? password, int dpi)
    {
        SKBitmap bitmap;
        lock (PdfiumGate.Sync)
        {
            bitmap = Conversion.ToImage(pdf, pageNumber - 1, password,
                new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false));
        }

        using (bitmap)
        {
            var pix = ToGreyPix(bitmap);
            pix.XRes = dpi;
            pix.YRes = dpi;
            return pix;
        }
    }

    /// <summary>
    /// Copies a rendered page into a new 8-bit grey Leptonica picture, row by row (no file or
    /// compression step). Leptonica keeps 8-bit pixels in 32-bit words, most significant byte first,
    /// so on this little-endian machine pixel x sits at byte (x XOR 3) of its row.
    /// </summary>
    private static Pix ToGreyPix(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var source = bitmap;
        SKBitmap? converted = null;
        if (bitmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
        {
            converted = bitmap.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("Could not convert the rendered page.");
            source = converted;
        }

        var pix = Pix.Create(width, height, 8);
        try
        {
            var data = pix.GetData();
            var rowBytes = data.WordsPerLine * 4;
            var line = new byte[rowBytes];
            Array.Fill(line, (byte)255); // padding at the end of each row stays white
            var pixels = source.GetPixelSpan();
            var stride = source.RowBytes;
            var bgra = source.ColorType == SKColorType.Bgra8888;
            var premultiplied = source.AlphaType == SKAlphaType.Premul;

            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * stride, width * 4);
                for (var x = 0; x < width; x++)
                {
                    var p = x * 4;
                    int r = bgra ? row[p + 2] : row[p], g = row[p + 1], b = bgra ? row[p] : row[p + 2], a = row[p + 3];
                    var luminance = (r * 299 + g * 587 + b * 114) / 1000;
                    // Composite on white (PDFium renders on white already; this only matters for transparency).
                    var value = premultiplied ? luminance + (255 - a) : (luminance * a / 255) + (255 - a);
                    line[x ^ 3] = (byte)Math.Clamp(value, 0, 255);
                }

                Marshal.Copy(line, 0, data.Data + y * rowBytes, rowBytes);
            }

            return pix;
        }
        catch
        {
            pix.Dispose();
            throw;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static void WriteGlyphlessFont(string folder)
    {
        using var resource = typeof(PdfOcrOps).Assembly.GetManifestResourceStream("GlyphLessFont.ttf")
                             ?? throw new InvalidOperationException("The OCR text font is missing from the program.");
        using var file = File.Create(Path.Combine(folder, "pdf.ttf"));
        resource.CopyTo(file);
    }

    // ---- Text layer ----------------------------------------------------------

    /// <summary>
    /// Draws Tesseract's one-page text-only PDF over <paramref name="page"/>: the original content is wrapped
    /// in q/Q so its graphics state cannot leak, then the layer is placed so the picture Tesseract read (the
    /// displayed page, turned <paramref name="turn"/> degrees clockwise) lines up with the page.
    /// </summary>
    private static void DrawLayer(PdfDocument document, PdfPage page, string layerFile, int turn)
    {
        using var layerDocument = new PdfDocument(new PdfReader(layerFile));
        var layerPage = layerDocument.GetFirstPage();
        var layerBox = layerPage.GetMediaBox();
        var form = layerPage.CopyAsFormXObject(document);
        RemoveBlankWords(form);

        var transform = LayerTransform(page, layerBox.GetWidth(), layerBox.GetHeight(), turn);
        // Raw q ... Q around the existing content (PdfCanvas checks save/restore balance per canvas).
        page.NewContentStreamBefore().GetOutputStream().WriteBytes(Encoding.ASCII.GetBytes("q\n"));
        var after = page.NewContentStreamAfter();
        after.GetOutputStream().WriteBytes(Encoding.ASCII.GetBytes("\nQ\n"));
        var canvas = new PdfCanvas(after, page.GetResources(), document);
        canvas.SaveState();
        canvas.AddXObjectWithTransformationMatrix(form, transform[0], transform[1], transform[2], transform[3], transform[4], transform[5]);
        canvas.RestoreState();
    }

    /// <summary>
    /// Drops the text lines Tesseract writes for marks it found but could not read (a line of spaces,
    /// often stretched over a whole row of Devanagari headline). They hold no text, but they sit on the same
    /// baseline as real words and would put stray spaces inside them for search and copy.
    /// </summary>
    private static void RemoveBlankWords(PdfFormXObject form)
    {
        var stream = form.GetPdfObject();
        var content = Encoding.Latin1.GetString(stream.GetBytes());
        var cleaned = BlankLine.Replace(content, match =>
        {
            var strings = HexString.Matches(match.Value);
            return strings.Count > 0 && strings.All(s => BlankHex.IsMatch(s.Groups[1].Value)) ? "" : match.Value;
        });

        if (cleaned.Length != content.Length) stream.SetData(Encoding.Latin1.GetBytes(cleaned));
    }

    private static readonly System.Text.RegularExpressions.Regex BlankLine =
        new(@"BT\s.*?\sET\s?", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex HexString =
        new(@"<([0-9A-Fa-f\s]*)>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex BlankHex =
        new(@"^(\s*0020\s*)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The matrix [a b c d e f] that maps the layer (Tesseract's page, <paramref name="layerWidth"/> by
    /// <paramref name="layerHeight"/> points, showing the displayed page turned <paramref name="turn"/>
    /// degrees clockwise) onto the page's user space (crop box, /Rotate).
    /// </summary>
    internal static float[] LayerTransform(PdfPage page, float layerWidth, float layerHeight, int turn)
    {
        var crop = page.GetCropBox();
        var rotation = PdfIo.NormalizeRotation(page.GetRotation());
        return LayerTransform(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight(), rotation, layerWidth, layerHeight, turn);
    }

    internal static float[] LayerTransform(
        double x0, double y0, double w, double h, int rotation, double layerWidth, double layerHeight, int turn)
    {
        // Displayed page size.
        var (wd, hd) = rotation is 90 or 270 ? (h, w) : (w, h);
        // Size of the picture Tesseract read (the displayed page turned by `turn`).
        var (wr, hr) = turn is 90 or 270 ? (hd, wd) : (wd, hd);

        (double X, double Y) Map(double tx, double ty)
        {
            // Layer points to the turned picture.
            var xr = tx * wr / Math.Max(layerWidth, 0.001);
            var yr = ty * hr / Math.Max(layerHeight, 0.001);

            // Undo the turn: back to the displayed page (y up).
            var (u, v) = turn switch
            {
                90 => (wd - yr, xr),
                180 => (wd - xr, hd - yr),
                270 => (yr, hd - xr),
                _ => (xr, yr)
            };

            // Displayed page to user space: the viewer turns the page `rotation` degrees clockwise.
            var (px, py) = rotation switch
            {
                90 => (w - v, u),
                180 => (w - u, h - v),
                270 => (v, h - u),
                _ => (u, v)
            };

            return (x0 + px, y0 + py);
        }

        var origin = Map(0, 0);
        var unitX = Map(1, 0);
        var unitY = Map(0, 1);
        return
        [
            (float)(unitX.X - origin.X), (float)(unitX.Y - origin.Y),
            (float)(unitY.X - origin.X), (float)(unitY.Y - origin.Y),
            (float)origin.X, (float)origin.Y
        ];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp only */ }
    }

    /// <summary>Counts visible characters (any render mode: an invisible OCR layer counts as text).</summary>
    private sealed class CharCounter : IEventListener
    {
        public int Chars { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo text) return;
            foreach (var ch in text.GetText() ?? "")
                if (!char.IsWhiteSpace(ch)) Chars++;
        }

        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT];
    }
}
