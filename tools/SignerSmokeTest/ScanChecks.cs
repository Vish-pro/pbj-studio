using iText.Kernel.Pdf;
using KcsPdfSigner.Core.Documents;
using KcsPdfSigner.Core.Scanning;
using SkiaSharp;
using Path = System.IO.Path;

/// <summary>
/// Checks 66 and up: the Scan Cleaner (pictures to clean A4 pages). Test pictures are drawn here: a text
/// page photographed at an angle on a dark desk, with a shadow across it and sensor noise.
/// </summary>
internal static class ScanChecks
{
    private static List<string> _failures = [];

    private static readonly string[] Words =
    [
        "Invoice", "number", "4711", "dated", "March", "Received", "from", "Kamath", "Steels", "the", "sum", "of",
        "rupees", "fifty", "thousand", "only", "towards", "steel", "supply", "Please", "keep", "this", "receipt",
    ];

    // Corners of the paper in the 1800 x 2400 photo, clockwise from top-left.
    private static readonly PageQuad TruePaper = new(new(260, 300), new(1480, 380), new(1560, 2150), new(180, 2050));

    public static void Run(string workFolder, List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "scan");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        var photo = F("photo.jpg");
        MakePhoto(photo, TruePaper, shadow: true, noise: 14, textGrey: 20);
        var photoRaster = ScanImageOps.Load(photo);

        Step("[66] Scan cleaner, page edges: the paper's four corners are found on a dark desk", () =>
        {
            var small = ScanImageOps.DownscaleToFit(ScanImageOps.ToGrey(photoRaster), 640, out var factor);
            var quad = ScanImageOps.FindPageQuad(small);
            Check(quad is not null, "no page found in the photo");
            if (quad is null) return;
            var found = quad.Scaled(factor);
            var diagonal = Math.Sqrt(1800.0 * 1800 + 2400.0 * 2400);
            var worst = found.Corners.Zip(TruePaper.Corners, (a, b) => ScanImageOps.Distance(a, b)).Max();
            Console.WriteLine($"    worst corner error {worst:0} px ({worst / diagonal:P1} of the diagonal)");
            Check(worst < 0.025 * diagonal, $"a corner is {worst:0} px off");

            // A light desk, close to the paper in brightness: the page is found correctly or not at all, never a wrong cut.
            var pale = F("pale-desk.jpg");
            MakePhoto(pale, TruePaper, shadow: false, noise: 10, textGrey: 20, desk: 170);
            var paleSmall = ScanImageOps.DownscaleToFit(ScanImageOps.ToGrey(ScanImageOps.Load(pale)), 640, out var paleFactor);
            var paleQuad = ScanImageOps.FindPageQuad(paleSmall)?.Scaled(paleFactor);
            var paleWorst = paleQuad is null ? 0 : paleQuad.Corners.Zip(TruePaper.Corners, (a, b) => ScanImageOps.Distance(a, b)).Max();
            Console.WriteLine(paleQuad is null ? "    pale desk: no page found (fine)" : $"    pale desk: worst corner error {paleWorst:0} px");
            Check(paleQuad is null || paleWorst < 0.04 * diagonal, $"on a pale desk a wrong page was cut out (corner {paleWorst:0} px off)");

            // A flat picture that is all paper has no edge to find.
            using var flat = FlatPage(600, 800, 0, 20);
            var flatGrey = ScanImageOps.DownscaleToFit(ScanImageOps.ToGrey(ScanImageOps.FromBitmap(flat)), 640, out _);
            Check(ScanImageOps.FindPageQuad(flatGrey) is null, "an all-paper picture was given a page edge");
        });

        Step("[67] Scan cleaner, perspective: the tilted photo comes out as a flat, upright page without the desk", () =>
        {
            var cleaned = ScanCleaner.Clean(photoRaster, new ScanCleanOptions { Mode = ScanColorMode.Colour }, 100);
            var image = cleaned.Image;
            Console.WriteLine($"    {image.Width} x {image.Height} px, edges found {cleaned.EdgesFound}, skew {cleaned.SkewDegrees:0.0} deg");
            File.WriteAllBytes(F("cleaned-colour.png"), ScanImageOps.ToPng(image));
            Check(cleaned.EdgesFound, "page edges were not found");
            var aspect = image.Width / (double)image.Height;
            Check(aspect is > 0.62 and < 0.82, $"output is not a portrait page (aspect {aspect:0.00})");
            var grey = ScanImageOps.ToGrey(image).Planes[0];
            var border = BorderMean(grey, image.Width, image.Height, 6);
            Console.WriteLine($"    border brightness {border:0}");
            Check(border > 215, $"the desk still shows at the edges (border brightness {border:0})");
        });

        Step("[68] Scan cleaner, straightening: skewed text is measured and removed", () =>
        {
            foreach (var tilt in new[] { 6.5, -4.0 })
            {
                using var tilted = FlatPage(1240, 1754, tilt, 20);
                var raster = ScanImageOps.FromBitmap(tilted);
                var grey = ScanImageOps.DownscaleToFit(ScanImageOps.ToGrey(raster), 1000, out _);
                var measured = ScanImageOps.EstimateSkew(grey, out var confidence);
                Console.WriteLine($"    page tilted {tilt:0.0}: measured {measured:0.00} (confidence {confidence:0.0})");
                Check(Math.Abs(measured - tilt) < 0.4, $"tilt {tilt} measured as {measured:0.00}");

                var cleaned = ScanCleaner.Clean(raster, new ScanCleanOptions { FindPageEdges = false }, 100);
                var again = ScanImageOps.EstimateSkew(ScanImageOps.ToGrey(cleaned.Image), out _);
                Console.WriteLine($"    after cleaning: {again:0.00}");
                Check(Math.Abs(again) < 0.4, $"after cleaning the tilt is still {again:0.00}");
            }

            using var straight = FlatPage(1240, 1754, 0, 20);
            var none = ScanCleaner.Clean(ScanImageOps.FromBitmap(straight), new ScanCleanOptions { FindPageEdges = false }, 100);
            Check(none.SkewDegrees == 0, $"a straight page was turned by {none.SkewDegrees:0.00}");
        });

        Step("[69] Scan cleaner, background: shadow and uneven light go, text stays dark", () =>
        {
            var cleaned = ScanCleaner.Clean(photoRaster, new ScanCleanOptions { Mode = ScanColorMode.Grey }, 100);
            var grey = cleaned.Image.Planes[0];
            int w = cleaned.Image.Width, h = cleaned.Image.Height;
            // The page's right margin and the left margin are in different light (the shadow runs across).
            var left = RegionMean(grey, w, (int)(w * 0.015), (int)(h * 0.30), (int)(w * 0.06), (int)(h * 0.40));
            var right = RegionMean(grey, w, (int)(w * 0.935), (int)(h * 0.30), (int)(w * 0.06), (int)(h * 0.40));
            Console.WriteLine($"    margins: left {left:0}, right {right:0}");
            Check(left > 235 && right > 235, $"the margins are not white ({left:0} and {right:0})");
            var darkShare = grey.Count(v => v < 110) / (double)grey.Length;
            Console.WriteLine($"    dark pixels {darkShare:P1}");
            Check(darkShare is > 0.01 and < 0.30, $"text pixels are {darkShare:P1} of the page");

            // Without cleaning the same two margins differ strongly.
            var raw = ScanCleaner.Clean(photoRaster, new ScanCleanOptions { Mode = ScanColorMode.Grey, CleanBackground = false }, 100);
            var rawGrey = raw.Image.Planes[0];
            var rl = RegionMean(rawGrey, w, (int)(w * 0.015), (int)(h * 0.30), (int)(w * 0.06), (int)(h * 0.40));
            var rr = RegionMean(rawGrey, w, (int)(w * 0.935), (int)(h * 0.30), (int)(w * 0.06), (int)(h * 0.40));
            Console.WriteLine($"    margins without background cleaning: left {rl:0}, right {rr:0}");
            Check(Math.Abs(rl - rr) > 25, "the test photo has no uneven light, so the check proves nothing");
        });

        Step("[70] Scan cleaner, black and white: faint text becomes crisp and the paper pure white", () =>
        {
            using var faint = FlatPage(1240, 1754, 0, textGrey: 135, paperGrey: 205, noise: 10);
            var raster = ScanImageOps.FromBitmap(faint);
            var cleaned = ScanCleaner.Clean(raster, new ScanCleanOptions { Mode = ScanColorMode.BlackAndWhite, FindPageEdges = false }, 150);
            var plane = cleaned.Image.Planes[0];
            Check(plane.All(v => v is 0 or 255), "black-and-white output has grey values");
            var black = plane.Count(v => v == 0) / (double)plane.Length;
            Console.WriteLine($"    black pixels {black:P2}");
            Check(black is > 0.008 and < 0.25, $"black share {black:P2} is not a text page");
            // The margin must be clean white (no speckle).
            int w = cleaned.Image.Width, h = cleaned.Image.Height;
            var marginBlack = 0;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < (int)(w * 0.05); x++)
                    if (plane[y * w + x] == 0) marginBlack++;
            Check(marginBlack < 20, $"{marginBlack} black specks in the margin");
        });

        Step("[71] Scan cleaner, noise: speckle is reduced without losing the text", () =>
        {
            using var noisy = FlatPage(800, 1100, 0, 20, noise: 28);
            var raster = ScanImageOps.FromBitmap(noisy);
            var before = Spread(raster.Planes[0], raster.Width, 20, 1045, 700, 40);
            var after = ScanImageOps.Denoise(raster);
            var spread = Spread(after.Planes[0], raster.Width, 20, 1045, 700, 40);
            Console.WriteLine($"    blank-area spread {before:0.0} -> {spread:0.0}");
            Check(spread < before * 0.8, "noise was not reduced");
            var sharp = ScanImageOps.Sharpen(raster, 1.0, 2);
            Check(sharp.Planes[0].Length == raster.Planes[0].Length, "sharpen changed the size");
        });

        Step("[72] Scan cleaner, PDF: one A4 page per picture, compact images in each mode", () =>
        {
            var pictures = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var p = F($"p{i}.jpg");
                MakePhoto(p, TruePaper, shadow: i != 1, noise: 12, textGrey: 20);
                pictures.Add(p);
            }

            // The third is a wide picture: it goes on a landscape page.
            var wide = F("wide.png");
            using (var bmp = FlatPage(1754, 1240, 0, 20)) Save(bmp, wide, SKEncodedImageFormat.Png);
            pictures.Add(wide);

            foreach (var mode in new[] { ScanColorMode.Colour, ScanColorMode.Grey, ScanColorMode.BlackAndWhite })
            {
                var pdf = F($"out-{mode}.pdf");
                var result = ScanCleaner.CreatePdf(pictures, pdf, new ScanCleanOptions { Mode = mode });
                var size = new FileInfo(pdf).Length;
                Console.WriteLine($"    {mode}: {result.PagesWritten} pages, {size / 1024.0:0} KB");
                Check(result.PagesWritten == 4 && result.Failed.Count == 0, $"{mode}: {result.PagesWritten} pages, {result.Failed.Count} failed");
                using var doc = new PdfDocument(new PdfReader(pdf));
                Check(doc.GetNumberOfPages() == 4, $"{mode}: {doc.GetNumberOfPages()} pages in the PDF");
                var first = doc.GetPage(1).GetPageSize();
                Check(Math.Abs(first.GetWidth() - 595.3) < 1 && Math.Abs(first.GetHeight() - 841.9) < 1, $"{mode}: page 1 is not A4 portrait");
                var last = doc.GetPage(4).GetPageSize();
                Check(last.GetWidth() > last.GetHeight(), $"{mode}: the wide picture is not on a landscape page");

                var res = doc.GetPage(1).GetResources().GetResource(PdfName.XObject) as PdfDictionary;
                var image = res?.Values().OfType<PdfStream>().FirstOrDefault();
                var filter = image?.Get(PdfName.Filter)?.ToString() ?? "";
                var bits = image?.GetAsNumber(PdfName.BitsPerComponent)?.IntValue() ?? 0;
                var space = image?.Get(PdfName.ColorSpace)?.ToString() ?? "";
                Console.WriteLine($"    page 1 image: filter {filter}, {bits} bit, {space}");
                if (mode == ScanColorMode.BlackAndWhite)
                    Check(bits == 1 && (filter.Contains("FlateDecode") || filter.Contains("CCITT")), $"black-and-white image is {bits} bit, {filter}");
                else
                    Check(filter.Contains("DCTDecode"), $"{mode} image is not JPEG ({filter})");
                if (mode == ScanColorMode.Grey) Check(space.Contains("DeviceGray"), $"grey image is stored as {space}");
                Check(size < 2_500_000, $"{mode}: four pages take {size / 1024} KB");
            }

            // Twenty photos stay small.
            var twenty = Enumerable.Range(0, 20).Select(i => pictures[i % 3]).ToList();
            foreach (var mode in new[] { ScanColorMode.Colour, ScanColorMode.BlackAndWhite })
            {
                var pdf = F($"twenty-{mode}.pdf");
                var started = DateTime.UtcNow;
                var result = ScanCleaner.CreatePdf(twenty, pdf, new ScanCleanOptions { Mode = mode });
                var mb = new FileInfo(pdf).Length / 1048576.0;
                Console.WriteLine($"    20 photos, {mode}: {mb:0.00} MB in {(DateTime.UtcNow - started).TotalSeconds:0} s");
                Check(result.PagesWritten == 20, $"{mode}: {result.PagesWritten} of 20 pages");
                Check(mb < (mode == ScanColorMode.Colour ? 14 : 4), $"{mode}: 20 pages are {mb:0.0} MB");
            }
        });

        Step("[73] Scan cleaner, bad input and cancel: a broken picture is skipped, cancel leaves no file", () =>
        {
            var broken = F("broken.jpg");
            File.WriteAllBytes(broken, [1, 2, 3, 4, 5, 6, 7, 8]);
            var pdf = F("mixed.pdf");
            var result = ScanCleaner.CreatePdf([F("p0.jpg"), broken, F("missing.png")], pdf, new ScanCleanOptions());
            Check(result.PagesWritten == 1 && result.Failed.Count == 2, $"{result.PagesWritten} written, {result.Failed.Count} failed");
            Check(File.Exists(pdf), "no PDF was written");

            var thrown = false;
            try
            {
                ScanCleaner.CreatePdf([broken], F("none.pdf"), new ScanCleanOptions());
            }
            catch (InvalidDataException)
            {
                thrown = true;
            }

            Check(thrown && !File.Exists(F("none.pdf")), "all pictures broken should fail without a file");

            using var cts = new CancellationTokenSource();
            var cancelled = false;
            var progress = new Progress<ScanProgress>(_ => { });
            var inline = new InlineProgress(p => { if (p.Done >= 1) cts.Cancel(); });
            try
            {
                ScanCleaner.CreatePdf([F("p0.jpg"), F("p1.jpg"), F("p2.jpg")], F("cancel.pdf"), new ScanCleanOptions(), inline, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            _ = progress;
            Check(cancelled, "cancelling did not stop the run");
            Check(!File.Exists(F("cancel.pdf")), "a cancelled run left a PDF");
            Check(Directory.GetFiles(folder, "*.part*").Length == 0, "a cancelled run left a temporary file");
        });

        Step("[75] Scan cleaner, crops: several crops are offered, the best first, and the chosen one is used", () =>
        {
            var variants = ScanCleaner.CropVariants(photoRaster);
            Console.WriteLine("    crops: " + string.Join(" | ", variants.Select(v => v.Name)));
            Check(variants.Count >= 4, $"only {variants.Count} crops offered");
            Check(variants[0].Name == "Page edges" && variants[0].PageFound, "the first crop is not the page found by its edges");
            Check(variants[^1].Name == "Whole picture, no crop", "the last crop is not the whole picture");
            var first = ScanCleaner.Clean(photoRaster, new ScanCleanOptions(), 60, 0);
            var whole = ScanCleaner.Clean(photoRaster, new ScanCleanOptions(), 60, variants.Count - 1);
            Check(first.CropName == "Page edges" && first.CropCount == variants.Count, "Clean did not report the crop and the count");
            var firstBorder = BorderMean(ScanImageOps.ToGrey(first.Image).Planes[0], first.Image.Width, first.Image.Height, 4);
            var wholeBorder = BorderMean(ScanImageOps.ToGrey(whole.Image).Planes[0], whole.Image.Width, whole.Image.Height, 4);
            Console.WriteLine($"    border brightness: best crop {firstBorder:0}, whole picture {wholeBorder:0}");
            Check(firstBorder > 215 && wholeBorder < firstBorder - 20, "the whole-picture crop should show the desk and the best crop should not");
            var pdf = F("crop-choice.pdf");
            ScanCleaner.CreatePdf([F("p0.jpg")], pdf, new ScanCleanOptions { Mode = ScanColorMode.Grey, CropFor = _ => variants.Count - 1 });
            ScanCleaner.CreatePdf([F("p0.jpg")], F("crop-best.pdf"), new ScanCleanOptions { Mode = ScanColorMode.Grey });
            Check(new FileInfo(pdf).Length != new FileInfo(F("crop-best.pdf")).Length, "the chosen crop was not used for the PDF");
        });

        Step("[74] Scan cleaner, searchable: the cleaned PDF can be read by OCR and searched", () =>
        {
            var engine = PdfOcrOps.CheckEngine();
            if (engine.Length > 0 || !OcrLanguages.FindInstalled().ContainsKey("eng"))
            {
                Console.WriteLine("    skipped: OCR engine or English data is not available here");
                return;
            }

            using var page = FlatPage(1240, 1754, 0, 20, fontSize: 40);
            var pic = F("text.png");
            Save(page, pic, SKEncodedImageFormat.Png);
            var pdf = F("searchable.pdf");
            var result = ScanCleaner.CreatePdf([pic], pdf, new ScanCleanOptions { Mode = ScanColorMode.BlackAndWhite, MakeSearchable = true });
            Check(result.Ocr is not null && result.Ocr.PagesRead.Count == 1, "OCR did not read the page");
            var text = PdfTextSearch.ExtractText(pdf);
            Console.WriteLine($"    {text.Length} characters found");
            Check(text.Contains("Kamath", StringComparison.OrdinalIgnoreCase) || text.Contains("Invoice", StringComparison.OrdinalIgnoreCase),
                "the words on the page were not found by text extraction");
        });
    }

    // ---- Test pictures --------------------------------------------------------------

    /// <summary>A white page of text lines; the whole page (text and paper) is turned by <paramref name="tilt"/> degrees.</summary>
    private static SKBitmap FlatPage(int width, int height, double tilt, int textGrey, int paperGrey = 250, int noise = 0, int fontSize = 30)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor((byte)paperGrey, (byte)paperGrey, (byte)paperGrey));
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees((float)tilt);
        canvas.Translate(-width / 2f, -height / 2f);
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, fontSize);
        using var paint = new SKPaint { Color = new SKColor((byte)textGrey, (byte)textGrey, (byte)textGrey), IsAntialias = true };
        var random = new Random(7);
        var margin = width * 0.12f;
        var step = fontSize * 1.45f;
        for (var y = height * 0.10f; y < height * 0.92f; y += step)
        {
            var x = margin;
            var line = new List<string>();
            var textWidth = 0f;
            while (true)
            {
                var word = Words[random.Next(Words.Length)];
                var w = font.MeasureText(word + " ");
                if (x + textWidth + w > width - margin) break;
                line.Add(word);
                textWidth += w;
            }

            canvas.DrawText(string.Join(" ", line), x, y, font, paint);
        }

        canvas.Flush();
        if (noise > 0) AddNoise(bitmap, noise, 3);
        return bitmap;
    }

    private static void MakePhoto(string path, PageQuad paper, bool shadow, int noise, int textGrey, byte desk = 40)
    {
        using var page = FlatPage(1240, 1754, 0, textGrey);
        using var photo = new SKBitmap(new SKImageInfo(1800, 2400, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(photo))
        {
            canvas.Clear(new SKColor((byte)(desk + 6), desk, (byte)(desk - 4)));
            var h = ScanImageOps.SolveHomography(page.Width, page.Height, paper);
            var matrix = new SKMatrix((float)h[0], (float)h[1], (float)h[2], (float)h[3], (float)h[4], (float)h[5], (float)h[6], (float)h[7], 1f);
            canvas.SetMatrix(matrix);
            using var paint = new SKPaint { IsAntialias = true };
            using var image = SKImage.FromBitmap(page);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.ResetMatrix();
            if (shadow)
            {
                using var shade = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1800, 600),
                        [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 120)], [0f, 1f], SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(0, 0, 1800, 2400, shade);
            }

            canvas.Flush();
        }

        AddNoise(photo, noise, 11);
        Save(photo, path, path.EndsWith(".png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg);
    }

    private static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void AddNoise(SKBitmap bitmap, int amplitude, int seed)
    {
        var random = new Random(seed);
        var count = bitmap.Width * bitmap.Height * 4;
        var bytes = new byte[count];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, count);
        for (var i = 0; i < count; i += 4)
        {
            var n = random.Next(-amplitude, amplitude + 1);
            for (var c = 0; c < 3; c++) bytes[i + c] = (byte)Math.Clamp(bytes[i + c] + n + random.Next(-2, 3), 0, 255);
        }

        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), count);
    }

    // ---- Measures --------------------------------------------------------------------

    private static double BorderMean(byte[] plane, int w, int h, int thickness)
    {
        double sum = 0;
        long count = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x >= thickness && x < w - thickness && y >= thickness && y < h - thickness) continue;
                sum += plane[y * w + x];
                count++;
            }
        }

        return sum / count;
    }

    private static double RegionMean(byte[] plane, int w, int x0, int y0, int rw, int rh)
    {
        double sum = 0;
        for (var y = y0; y < y0 + rh; y++)
            for (var x = x0; x < x0 + rw; x++)
                sum += plane[y * w + x];
        return sum / (rw * rh);
    }

    private static double Spread(byte[] plane, int w, int x0, int y0, int rw, int rh)
    {
        var mean = RegionMean(plane, w, x0, y0, rw, rh);
        double sum = 0;
        for (var y = y0; y < y0 + rh; y++)
            for (var x = x0; x < x0 + rw; x++)
                sum += (plane[y * w + x] - mean) * (plane[y * w + x] - mean);
        return Math.Sqrt(sum / (rw * rh));
    }

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private static void Step(string title, Action body)
    {
        Console.WriteLine($"\n{title}");
        var before = _failures.Count;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _failures.Add($"{title} threw {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("    " + ex);
        }

        Console.WriteLine(_failures.Count == before ? "    ok" : $"    {_failures.Count - before} problem(s)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failures.Add(message);
            Console.WriteLine("    FAIL: " + message);
        }
    }
}
