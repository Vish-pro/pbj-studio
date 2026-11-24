using iText.IO.Image;
using iText.Kernel.Geom;
using Path = System.IO.Path;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.Core.Scanning;

/// <summary>How a cleaned page looks.</summary>
public enum ScanColorMode
{
    /// <summary>Colour kept, paper whitened.</summary>
    Colour,

    /// <summary>Shades of grey.</summary>
    Grey,

    /// <summary>Crisp black text on white (smallest file).</summary>
    BlackAndWhite,
}

/// <summary>Settings for cleaning pictures into pages.</summary>
public sealed class ScanCleanOptions
{
    public ScanColorMode Mode { get; init; } = ScanColorMode.Colour;

    /// <summary>Find the page edges and flatten the perspective (when a page is found).</summary>
    public bool FindPageEdges { get; init; } = true;

    /// <summary>Straighten tilted text.</summary>
    public bool Straighten { get; init; } = true;

    /// <summary>Whiten the paper and remove shadows and uneven light.</summary>
    public bool CleanBackground { get; init; } = true;

    /// <summary>Resolution of colour and grey pages; 0 means 200 (250 when the pages are also read by OCR).</summary>
    public int Dpi { get; init; }

    /// <summary>Resolution of black and white pages.</summary>
    public int BlackAndWhiteDpi { get; init; } = 300;

    /// <summary>JPEG quality of colour and grey pages.</summary>
    public int JpegQuality { get; init; } = 78;

    /// <summary>Add a text layer with the OCR engine so the PDF is searchable.</summary>
    public bool MakeSearchable { get; init; }

    /// <summary>Tesseract language codes for <see cref="MakeSearchable"/>.</summary>
    public IReadOnlyList<string> OcrLanguages { get; init; } = ["eng"];

    /// <summary>Which crop to use for a picture file (index into <see cref="ScanCleaner.CropVariants"/>); null or 0 means the best.</summary>
    public Func<string, int>? CropFor { get; init; }

    /// <summary>A4 margin in points around the picture.</summary>
    public double MarginPoints { get; init; } = 0;

    internal int EffectiveDpi => Mode == ScanColorMode.BlackAndWhite ? BlackAndWhiteDpi
        : Dpi > 0 ? Dpi : MakeSearchable ? 250 : 200;
}

/// <summary>One cleaned picture, ready to be put on a page.</summary>
public sealed class CleanedPage
{
    public required Raster Image { get; init; }

    public required ScanColorMode Mode { get; init; }

    /// <summary>True when a page was found in the picture and cut out.</summary>
    public bool EdgesFound { get; init; }

    /// <summary>Tilt that was removed, in degrees (0 when none).</summary>
    public double SkewDegrees { get; init; }

    /// <summary>Name of the crop used, e.g. "Page edges".</summary>
    public string CropName { get; init; } = "";

    /// <summary>How many crops were on offer for this picture.</summary>
    public int CropCount { get; init; } = 1;
}

/// <summary>One way to cut the page out of a picture.</summary>
public sealed record CropVariant(string Name, PageQuad Quad, bool PageFound);

/// <summary>What a run of <see cref="ScanCleaner.CreatePdf"/> did.</summary>
public sealed class ScanRunResult
{
    public int PagesWritten { get; set; }

    /// <summary>Pictures that could not be read: file and reason.</summary>
    public List<(string File, string Reason)> Failed { get; } = [];

    public int PagesWithEdges { get; set; }

    public long OutputBytes { get; set; }

    /// <summary>The OCR result, when the PDF was also made searchable.</summary>
    public OcrRunResult? Ocr { get; set; }
}

/// <summary>Progress of a run: picture <see cref="Done"/> of <see cref="Total"/> finished, now on <see cref="File"/>.</summary>
public readonly record struct ScanProgress(int Done, int Total, string File, string Stage);

/// <summary>
/// Turns photographs of pages into clean scanned pages: find the page edges and flatten the perspective,
/// straighten, whiten the background and remove shadows, reduce noise, sharpen, then colour, grey or
/// black and white; one A4 page per picture in a PDF, with the picture stored compactly.
/// </summary>
public static class ScanCleaner
{
    public const double A4Width = 595.276;
    public const double A4Height = 841.89;

    private const int MaxSourceSide = 6000;

    /// <summary>Cleans one picture file. <paramref name="previewDpi"/> above 0 renders a smaller result for a preview.</summary>
    public static CleanedPage CleanFile(string path, ScanCleanOptions options, int previewDpi = 0)
    {
        var dpi = previewDpi > 0 ? previewDpi : options.EffectiveDpi;
        // A page is at most 297 mm: 11.7 in. The source needs no more detail than about twice the result.
        var needed = (int)Math.Min(MaxSourceSide, Math.Max(1200, dpi * 11.7 * 2.2));
        using var picture = new DisposableRaster(ScanImageOps.Load(path, needed));
        return Clean(picture.Value, options, dpi, options.CropFor?.Invoke(path) ?? 0);
    }

    private sealed class DisposableRaster(Raster value) : IDisposable
    {
        public Raster Value { get; } = value;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The ways the page can be cut out of a picture, best first: the page found by its edges (or, when no
    /// edge is found, the whole picture without its rim), then other readings of the edges, tighter and looser
    /// cuts, and the whole picture. Cuts that come out the same are listed once.
    /// </summary>
    public static List<CropVariant> CropVariants(Raster source)
    {
        var small = ScanImageOps.DownscaleToFit(ScanImageOps.ToGrey(source), 640, out var factor);
        var whole = new PageQuad(new(0, 0), new(source.Width, 0), new(source.Width, source.Height), new(0, source.Height));
        var variants = new List<CropVariant>();

        void Add(string name, PageQuad quad, bool found)
        {
            var diagonal = Math.Sqrt((double)source.Width * source.Width + (double)source.Height * source.Height);
            foreach (var other in variants)
            {
                var same = other.Quad.Corners.Zip(quad.Corners, (a, b) => ScanImageOps.Distance(a, b)).Max();
                if (same < 0.008 * diagonal) return;
            }

            variants.Add(new CropVariant(name, quad, found));
        }

        var best = ScanImageOps.FindPageQuad(small)?.Scaled(factor);
        var byLines = ScanImageOps.FindPageByLines(small)?.Scaled(factor);
        var byCut = ScanImageOps.FindPageByCut(small)?.Scaled(factor);
        if (best is not null)
        {
            Add("Page edges", ScanImageOps.Shrink(best, 0.008), true);
            Add("Page edges, tighter", ScanImageOps.Shrink(best, 0.06), true);
            Add("Page edges, looser", ScanImageOps.Shrink(best, -0.05), true);
        }

        if (byLines is not null) Add("Page edges (by lines)", ScanImageOps.Shrink(byLines, 0.008), true);
        if (byCut is not null) Add("Page edges (by brightness)", ScanImageOps.Shrink(byCut, 0.008), true);
        Add("Whole picture, rim trimmed", ScanImageOps.Shrink(whole, 0.03), false);
        Add("Whole picture, no crop", whole, false);
        return variants;
    }

    /// <summary>Cleans a picture already in memory at the given page resolution.</summary>
    public static CleanedPage Clean(Raster source, ScanCleanOptions options, int dpi, int cropIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        var mode = options.Mode;

        // 1. Where the page is: the chosen crop (the best one is first), as corners in source pixels.
        var whole = new PageQuad(new(0, 0), new(source.Width, 0), new(source.Width, source.Height), new(0, source.Height));
        PageQuad quad = whole;
        var edgesFound = false;
        var cropName = "Whole picture";
        var cropCount = 1;
        if (options.FindPageEdges)
        {
            var variants = CropVariants(source);
            var chosen = variants[Math.Clamp(cropIndex, 0, variants.Count - 1)];
            quad = chosen.Quad;
            edgesFound = chosen.PageFound;
            cropName = chosen.Name;
            cropCount = variants.Count;
        }

        // 2. Output size: the page fitted on A4 at the chosen resolution, never more detail than the picture has.
        double qw = (ScanImageOps.Distance(quad.TopLeft, quad.TopRight) + ScanImageOps.Distance(quad.BottomLeft, quad.BottomRight)) / 2;
        double qh = (ScanImageOps.Distance(quad.TopLeft, quad.BottomLeft) + ScanImageOps.Distance(quad.TopRight, quad.BottomRight)) / 2;
        var (_, _, fitW, fitH) = FitOnA4(qw, qh, options.MarginPoints);
        var width = (int)Math.Round(fitW * dpi / 72);
        var height = (int)Math.Round(fitH * dpi / 72);
        var limit = Math.Min(1.0, Math.Min(qw / Math.Max(1, width), qh / Math.Max(1, height)));
        width = Math.Max(16, (int)Math.Round(width * limit));
        height = Math.Max(16, (int)Math.Round(height * limit));

        // 3. A source far larger than the result is reduced first, so the sampling does not alias.
        var ratio = Math.Min(qw / width, qh / height);
        var reduce = ratio >= 2 ? (int)Math.Floor(ratio) : 1;
        var work = source;
        var workQuad = quad;
        if (reduce > 1)
        {
            work = ScanImageOps.DownscaleBy(source, reduce);
            workQuad = quad.Scaled(1.0 / reduce);
        }

        if (mode != ScanColorMode.Colour) work = ScanImageOps.ToGrey(work);

        // 4. Tilt, measured on a small flattened copy of the luminance.
        var skew = 0.0;
        if (options.Straighten)
        {
            var grey = ScanImageOps.ToGrey(work);
            var smallScale = Math.Min(1.0, 1000.0 / Math.Max(width, height));
            var sw = Math.Max(16, (int)(width * smallScale));
            var sh = Math.Max(16, (int)(height * smallScale));
            var flat = ScanImageOps.Warp(grey, workQuad, sw, sh, 0);
            var measured = ScanImageOps.EstimateSkew(flat, out var confidence);
            if (confidence >= 3 && Math.Abs(measured) >= 0.15 && Math.Abs(measured) < 14.9) skew = measured;
        }

        // 5. Cut out, flatten and straighten in one pass.
        var page = ScanImageOps.Warp(work, workQuad, width, height, skew);

        // 6. Clean-up.
        if (options.CleanBackground) page = ScanImageOps.NormalizeBackground(page);
        page = ScanImageOps.Denoise(page);
        page = ScanImageOps.Sharpen(page, mode == ScanColorMode.BlackAndWhite ? 0.8 : 0.9, Math.Max(1, dpi / 150));
        if (options.CleanBackground || mode == ScanColorMode.BlackAndWhite) page = ScanImageOps.Levels(page);
        if (mode == ScanColorMode.BlackAndWhite) page = ScanImageOps.ToBlackAndWhite(page);

        return new CleanedPage { Image = page, Mode = mode, EdgesFound = edgesFound, SkewDegrees = skew, CropName = cropName, CropCount = cropCount };
    }

    /// <summary>
    /// Where a picture of the given shape goes on A4 (portrait, or landscape when the picture is clearly wider than tall):
    /// returns the page size in points, and the largest rectangle of the same shape inside the margins.
    /// </summary>
    public static (double PageWidth, double PageHeight, double Width, double Height) FitOnA4(double pictureWidth, double pictureHeight, double margin)
    {
        var landscape = pictureWidth > pictureHeight * 1.05;
        var pw = landscape ? A4Height : A4Width;
        var ph = landscape ? A4Width : A4Height;
        // A picture within 8% of A4's shape is an A4 page: it fills the sheet (the shape is corrected by that little).
        var shape = Math.Min(pictureWidth, pictureHeight) / Math.Max(pictureWidth, pictureHeight);
        const double A4Shape = A4Width / A4Height;
        if (Math.Abs(shape / A4Shape - 1) < 0.08) return (pw, ph, pw - 2 * margin, ph - 2 * margin);
        var scale = Math.Min((pw - 2 * margin) / pictureWidth, (ph - 2 * margin) / pictureHeight);
        return (pw, ph, pictureWidth * scale, pictureHeight * scale);
    }

    /// <summary>
    /// Cleans every picture in order and writes one A4 page per picture to <paramref name="output"/>. A picture
    /// that cannot be read is skipped and listed in the result. Runs on the calling thread; cancelling deletes
    /// the half-written file and throws <see cref="OperationCanceledException"/>. One picture is in memory at a time.
    /// </summary>
    public static ScanRunResult CreatePdf(
        IReadOnlyList<string> pictures, string output, ScanCleanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        ArgumentNullException.ThrowIfNull(options);
        if (pictures.Count == 0) throw new ArgumentException("Add at least one picture.", nameof(pictures));

        var result = new ScanRunResult();
        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var draft = Path.Combine(directory ?? "", Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
        var searchable = draft + ".ocr";

        try
        {
            using (var writer = new PdfWriter(draft))
            using (var document = new PdfDocument(writer))
            {
                document.GetDocumentInfo().SetCreator("PB&J Studio Scan Cleaner");
                for (var i = 0; i < pictures.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = pictures[i];
                    progress?.Report(new ScanProgress(i, pictures.Count, file, "cleaning"));
                    try
                    {
                        var page = CleanFile(file, options);
                        AddPage(document, page, options);
                        if (page.EdgesFound) result.PagesWithEdges++;
                        result.PagesWritten++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                                   or InvalidOperationException or OutOfMemoryException or ArgumentException)
                    {
                        result.Failed.Add((file, ex is OutOfMemoryException ? "The picture is too large to open." : ex.Message));
                    }

                    progress?.Report(new ScanProgress(i + 1, pictures.Count, file, "cleaning"));
                }

                if (result.PagesWritten == 0) throw new InvalidDataException("None of the pictures could be read.");
            }

            var final = draft;
            if (options.MakeSearchable)
            {
                var engine = PdfOcrOps.CheckEngine();
                if (engine.Length > 0) throw new InvalidOperationException(engine);
                var ocrOptions = new OcrOptions { Languages = options.OcrLanguages, SkipPagesWithText = false, DetectOrientation = true, Dpi = 300 };
                var ocrProgress = progress is null ? null : new Progress<OcrProgress>(p =>
                    progress.Report(new ScanProgress(p.Done, p.Total, "page " + p.PageNumber, "reading")));
                result.Ocr = PdfOcrOps.MakeSearchable(draft, searchable, ocrOptions, ocrProgress, cancellationToken);
                final = searchable;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(final, output, overwrite: true);
            result.OutputBytes = new FileInfo(output).Length;
            return result;
        }
        finally
        {
            TryDelete(draft);
            TryDelete(searchable);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddPage(PdfDocument document, CleanedPage page, ScanCleanOptions options)
    {
        var image = page.Image;
        var (pw, ph, fw, fh) = FitOnA4(image.Width, image.Height, options.MarginPoints);
        var pdfPage = document.AddNewPage(new PageSize((float)pw, (float)ph));

        ImageData data = page.Mode == ScanColorMode.BlackAndWhite
            ? ImageDataFactory.Create(image.Width, image.Height, 1, 1, ScanImageOps.PackBits(image), null)
            : ImageDataFactory.Create(ScanImageOps.ToJpeg(image, options.JpegQuality));

        var xobject = new PdfImageXObject(data);
        var canvas = new PdfCanvas(pdfPage);
        canvas.AddXObjectFittedIntoRectangle(xobject, new Rectangle((float)((pw - fw) / 2), (float)((ph - fh) / 2), (float)fw, (float)fh));
        canvas.Release();
        // Write this page out now: the picture does not stay in memory until the end.
        pdfPage.Flush(true);
    }
}
