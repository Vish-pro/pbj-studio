using System.Diagnostics;
using System.Net;
using System.Net.Http;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using Path = System.IO.Path;
using PageSize = iText.Kernel.Geom.PageSize;
using Rectangle = iText.Kernel.Geom.Rectangle;
using Vector = iText.Kernel.Geom.Vector;

/// <summary>
/// Checks 56 and up: OCR (making scanned PDFs searchable). Scans are made here: a typed page is rendered
/// to a picture and that picture is put alone on an A4 page, exactly what a scanner produces. English and
/// orientation data come from packaging\ocr-languages (run packaging\fetch-ocr-languages.ps1 once); the
/// Hindi check runs only when hin.traineddata is there. Nothing here uses the network.
/// </summary>
internal static class OcrChecks
{
    private static List<string> _failures = [];

    private static readonly string[] Lines =
    [
        "Invoice number 4711 dated 12 March 2026",
        "The quick brown fox jumps over the lazy dog",
        "Received from Kamath Steels the sum of rupees",
        "fifty thousand only towards the steel supply",
        "Please keep this receipt for your records",
        "Authorised Signatory",
    ];

    private static readonly string[] KnownWords = ["Invoice", "4711", "quick", "Kamath", "receipt", "Authorised"];

    public static void Run(string workFolder, List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "ocr");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        var packs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packaging", "ocr-languages"));
        var installed = OcrLanguages.FindInstalled();
        if (!installed.ContainsKey("eng") || !installed.ContainsKey("osd"))
        {
            Console.WriteLine("\n[56] OCR: English or orientation data missing next to the test. Run packaging\\fetch-ocr-languages.ps1 first.");
            _failures.Add("OCR checks need packaging\\ocr-languages\\eng.traineddata and osd.traineddata (run packaging\\fetch-ocr-languages.ps1)");
            return;
        }

        var engine = PdfOcrOps.CheckEngine();
        if (engine.Length > 0)
        {
            _failures.Add("OCR engine not available: " + engine);
            return;
        }

        // ---- Fixtures ----------------------------------------------------------
        var typed = F("typed.pdf");
        BuildTypedPdf(typed, 0);
        var scan = F("scan-en.pdf");
        BuildScan(typed, scan);

        Step("[56] OCR, English: the scan gets searchable text; the page looks the same (size, rotation, picture)", () =>
        {
            var before = PageFacts(scan);
            var output = F("scan-en-ocr.pdf");
            var timer = Stopwatch.StartNew();
            var result = PdfOcrOps.MakeSearchable(scan, output, new OcrOptions());
            Console.WriteLine($"    read pages [{string.Join(",", result.PagesRead)}], {result.Words} words, confidence {result.MeanConfidence:0}%, {timer.Elapsed.TotalSeconds:0.0}s");
            var text = Text(output);
            Console.WriteLine("    text: " + text.Replace("\n", " | ").Trim());
            foreach (var word in KnownWords) Check(text.Contains(word), $"ocr: '{word}' not found in the text layer");
            Check(Text(scan).Trim().Length == 0, "ocr: the input scan should have no text");

            var after = PageFacts(output);
            Check(after == before, $"ocr: page changed ({before} -> {after})");
            Check(DocumentAdvisor.DetectScanned(output).ScannedPages.Count == 0, "ocr: page still counted as a scan without text");

            // The words lie where they are printed (not just somewhere in the file).
            var reference = WordPositions(typed);
            var found = WordPositions(output);
            Check(SamePlaces(reference, found, 0.01, out var report, absolute: true), "ocr: words are not over their printed place: " + report);
            Console.WriteLine("    positions vs typed page: " + report);

            // A second run with "skip pages that already have text" changes nothing.
            var again = PdfOcrOps.MakeSearchable(output, F("scan-en-ocr2.pdf"), new OcrOptions());
            Check(again.PagesRead.Count == 0 && again.PagesSkipped.SequenceEqual([1]), "ocr: a page with text was read again");
            Check(PdfOcrOps.PagesToRead(output, null, null, true).Count == 0 && PdfOcrOps.PagesToRead(scan, null, null, true).SequenceEqual([1]),
                "ocr: PagesToRead wrong");
        });

        Step("[57] OCR, orientation: pictures turned 90, 180 and 270 degrees are detected, read upright, words placed right", () =>
        {
            var reference = Normalised(WordPositions(typed), typed);
            foreach (var rotation in new[] { 90, 180, 270 })
            {
                var turnedTyped = F($"typed-{rotation}.pdf");
                BuildTypedPdf(turnedTyped, rotation); // /Rotate makes the rendered picture turned clockwise
                var turnedScan = F($"scan-{rotation}.pdf");
                BuildScan(turnedTyped, turnedScan);

                var osd = PdfOcrOps.DetectOrientation(turnedScan, null, [1]).Single();
                var expectedTurn = (360 - rotation) % 360;
                Console.WriteLine($"    picture turned {rotation}: detected turn {osd.TurnClockwise} (confidence {osd.Confidence:0.0}), expected {expectedTurn}");
                Check(osd.TurnClockwise == expectedTurn && osd.Confidence >= PdfOcrOps.SidewaysSuggestionConfidence,
                    $"orientation: picture turned {rotation} detected as {osd.TurnClockwise} ({osd.Confidence:0.0})");

                var output = F($"scan-{rotation}-ocr.pdf");
                var result = PdfOcrOps.MakeSearchable(turnedScan, output, new OcrOptions());
                var text = Text(output);
                Check(result.TurnedForReading.Count == 1 && result.TurnedForReading[0].TurnClockwise == expectedTurn,
                    $"orientation: page {rotation} not turned for reading");
                foreach (var word in KnownWords) Check(text.Contains(word), $"orientation {rotation}: '{word}' not read");
                Check(PageFacts(output) == PageFacts(turnedScan), $"orientation {rotation}: the page itself was changed");

                // Expected place of each word in the turned picture (normalised display coordinates).
                var expected = reference.ToDictionary(p => p.Key, p => TurnPoint(p.Value, rotation));
                Check(SamePlaces(expected, Normalised(WordPositions(output), output), 0.03, out var report),
                    $"orientation {rotation}: words misplaced: {report}");
                Console.WriteLine($"    turned {rotation}: {result.Words} words; positions {report}");
            }

            // A photo-like picture (no text) is not reported as turned.
            var picture = F("picture-scan.pdf");
            BuildImagePdf(Path.Combine(workFolder, "docs", "picture.png"), picture);
            var noText = PdfOcrOps.DetectOrientation(picture, null, [1]).Single();
            Console.WriteLine($"    picture without text: turn {noText.TurnClockwise}, confidence {noText.Confidence:0.0}");
            Check(noText.TurnClockwise == 0 || noText.Confidence < PdfOcrOps.SidewaysSuggestionConfidence,
                "orientation: a picture without text was reported as sideways");
            var upright = PdfOcrOps.DetectOrientation(scan, null, [1]).Single();
            Console.WriteLine($"    upright scan: turn {upright.TurnClockwise}, confidence {upright.Confidence:0.0}");
            Check(upright.TurnClockwise == 0, "orientation: an upright scan was reported as turned");
        });

        Step("[58] OCR, page geometry: /Rotate 90 page and a crop box away from (0,0) keep the words in place", () =>
        {
            // A scan stored sideways and shown upright by /Rotate 90 (common from scanners).
            var sideways = F("typed-270.pdf"); // its picture is turned 270 clockwise
            var stored = F("scan-stored-sideways.pdf");
            BuildScan(sideways, stored, pageRotation: 90);
            var shown = F("scan-stored-sideways-ocr.pdf");
            var result = PdfOcrOps.MakeSearchable(stored, shown, new OcrOptions());
            Check(result.TurnedForReading.Count == 0, "geometry: a page shown upright by /Rotate was turned for reading");
            var reference = Normalised(WordPositions(typed), typed);
            Check(SamePlaces(reference, Normalised(WordPositions(shown), shown), 0.03, out var report1), "geometry: /Rotate 90 words misplaced: " + report1);
            Check(PageFacts(shown) == PageFacts(stored), "geometry: /Rotate page changed");
            Console.WriteLine($"    /Rotate 90: {result.Words} words, positions {report1}");

            // Crop box from (40, 50): the visible part is read and placed in user space as printed.
            var cropped = F("scan-cropped.pdf");
            using (var pdf = new PdfDocument(new PdfReader(scan), new PdfWriter(cropped)))
            {
                var page = pdf.GetPage(1);
                var media = page.GetMediaBox();
                page.SetCropBox(new Rectangle(40, 50, media.GetWidth() - 70, media.GetHeight() - 90));
            }

            var croppedOcr = F("scan-cropped-ocr.pdf");
            PdfOcrOps.MakeSearchable(cropped, croppedOcr, new OcrOptions());
            Check(SamePlaces(WordPositions(typed), WordPositions(croppedOcr), 0.02, out var report2, absolute: true),
                "geometry: crop box words misplaced: " + report2);
            Check(PageFacts(croppedOcr) == PageFacts(cropped), "geometry: cropped page changed");
            Console.WriteLine("    crop box at (40, 50): positions " + report2);
        });

        Step("[59] OCR, mixed document: typed pages are skipped, scanned pages read; 'skip' off reads all", () =>
        {
            var mixed = F("mixed.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(mixed)))
            {
                using var typedDoc = new PdfDocument(new PdfReader(typed));
                using var scanDoc = new PdfDocument(new PdfReader(scan));
                typedDoc.CopyPagesTo(1, 1, pdf);
                scanDoc.CopyPagesTo(1, 1, pdf);
                scanDoc.CopyPagesTo(1, 1, pdf);
            }

            var typedText = PdfTextExtractor.GetTextFromPage(new PdfDocument(new PdfReader(mixed)).GetPage(1));
            var output = F("mixed-ocr.pdf");
            var result = PdfOcrOps.MakeSearchable(mixed, output, new OcrOptions());
            Console.WriteLine($"    read [{string.Join(",", result.PagesRead)}], skipped [{string.Join(",", result.PagesSkipped)}]");
            Check(result.PagesRead.SequenceEqual([2, 3]) && result.PagesSkipped.SequenceEqual([1]), "mixed: wrong pages read or skipped");
            using (var doc = new PdfDocument(new PdfReader(output)))
            {
                Check(PdfTextExtractor.GetTextFromPage(doc.GetPage(1)) == typedText, "mixed: the typed page's text changed");
                Check(PdfTextExtractor.GetTextFromPage(doc.GetPage(3)).Contains("Kamath"), "mixed: page 3 not read");
            }

            var only = PdfOcrOps.MakeSearchable(mixed, F("mixed-page2.pdf"), new OcrOptions { Pages = [2] });
            Check(only.PagesRead.SequenceEqual([2]), "mixed: a page range was not respected");
            var all = PdfOcrOps.MakeSearchable(mixed, F("mixed-all.pdf"), new OcrOptions { SkipPagesWithText = false, Pages = [1] });
            Check(all.PagesRead.SequenceEqual([1]) && all.PagesSkipped.Count == 0, "mixed: 'skip' off did not read the typed page");
        });

        Step("[60] OCR, cancel: stops between pages, leaves no output and no temp files", () =>
        {
            var three = F("three-scans.pdf");
            BuildRepeatedScan(scan, three, 3);
            var output = F("three-scans-ocr.pdf");
            var tempBefore = OcrTempFolders();
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress<OcrProgress>(p => { if (p.Done == 1) cts.Cancel(); });
            var cancelled = false;
            try { PdfOcrOps.MakeSearchable(three, output, new OcrOptions(), progress, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancel: no OperationCanceledException");
            Check(!File.Exists(output), "cancel: a partial output file was left");
            Check(OcrTempFolders() == tempBefore, "cancel: a temp folder was left behind");
        });

        Step("[61] OCR, password-protected scan: read with the password, still protected afterwards", () =>
        {
            // Like every change in the program, rewriting an encrypted file needs the owner password
            // (or a file whose open password is also its owner password).
            var locked = F("scan-locked.pdf");
            PdfSecurityOps.Encrypt(scan, locked, "scan123", "scan123", true, true, true);
            var output = F("scan-locked-ocr.pdf");
            var result = PdfOcrOps.MakeSearchable(locked, output, new OcrOptions(), password: "scan123");
            Check(result.PagesRead.SequenceEqual([1]), "password: page not read");
            var opensWithout = true;
            try { using var _ = new PdfDocument(new PdfReader(output)); }
            catch { opensWithout = false; }
            Check(!opensWithout, "password: the output is no longer protected");
            using var doc = new PdfDocument(new PdfReader(output, new ReaderProperties().SetPassword("scan123"u8.ToArray())));
            Check(PdfTextExtractor.GetTextFromPage(doc.GetPage(1)).Contains("Kamath"), "password: no text in the protected output");
            var wrong = false;
            try { PdfOcrOps.MakeSearchable(locked, F("wrong.pdf"), new OcrOptions(), password: "nope"); }
            catch (InvalidOperationException) { wrong = true; }
            Check(wrong && !File.Exists(F("wrong.pdf")), "password: a wrong password did not fail cleanly");

            // Open password only (owner password unknown): refused with the program's usual message, nothing written.
            var userOnly = F("scan-user-only.pdf");
            PdfSecurityOps.Encrypt(scan, userOnly, "open1", "owner-unknown", true, true, true);
            var message = "";
            try { PdfOcrOps.MakeSearchable(userOnly, F("user-only-ocr.pdf"), new OcrOptions(), password: "open1"); }
            catch (InvalidOperationException ex) { message = ex.Message; }
            Console.WriteLine("    open password only: " + message);
            Check(message.Contains("owner") && !File.Exists(F("user-only-ocr.pdf")), "password: open-password-only file not refused clearly");
        });

        Step("[62] OCR languages: found in either folder, damaged files ignored, catalogue complete", () =>
        {
            var programDir = F("lang-program");
            var userDir = F("lang-user");
            Directory.CreateDirectory(programDir);
            Directory.CreateDirectory(userDir);
            FakePack(Path.Combine(programDir, "aaa.traineddata"), 2000);
            File.WriteAllBytes(Path.Combine(programDir, "bad.traineddata"), "not a language file at all"u8.ToArray());
            FakePack(Path.Combine(userDir, "aaa.traineddata"), 3000);
            FakePack(Path.Combine(userDir, "bbb.traineddata"), 2500);
            var found = OcrLanguages.FindInstalled([programDir, userDir]);
            Console.WriteLine("    found: " + string.Join(", ", found.Select(f => $"{f.Key} in {Path.GetFileName(Path.GetDirectoryName(f.Value))}")));
            Check(found.Count == 2 && found["aaa"].StartsWith(programDir) && found["bbb"].StartsWith(userDir) && !found.ContainsKey("bad"),
                "languages: discovery across the two folders is wrong");
            Check(OcrLanguages.FindInstalled([F("missing-folder")]).Count == 0, "languages: a missing folder should give nothing");

            var reading = OcrLanguages.ReadingLanguages.Select(l => l.Code).ToList();
            Check(reading.Count == 11 && reading.SequenceEqual(["eng", "hin", "kan", "mar", "tam", "tel", "mal", "guj", "ben", "pan", "urd"]),
                "languages: the catalogue should list English and the ten Indian languages");
            Check(OcrLanguages.Catalog.All(l => l.Url.StartsWith("https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/") && l.Sha256.Length == 64),
                "languages: every pack needs an official URL and a pinned SHA-256");

            // Every pack fetched by the script matches the pinned size and checksum.
            foreach (var language in OcrLanguages.Catalog)
            {
                var file = Path.Combine(packs, language.FileName);
                if (!File.Exists(file)) { Console.WriteLine($"    {language.Code}: not fetched (skipped)"); continue; }
                Check(OcrLanguages.Verify(file, language) == "", $"languages: {language.FileName} does not match its pinned checksum");
            }
        });

        Step("[63] OCR language files: the verifier and installer refuse wrong files (local fakes, no network)", () =>
        {
            var fake = F("fake.traineddata");
            FakePack(fake, 4096);
            var size = new FileInfo(fake).Length;
            var sha = OcrLanguages.Sha256Of(fake);
            Check(OcrLanguages.Verify(fake, size, sha) == "", "verify: a good file was refused");
            Check(OcrLanguages.Verify(fake, size + 1, sha).Contains("wrong size"), "verify: wrong size not reported");
            Check(OcrLanguages.Verify(fake, size, new string('0', 64)).Contains("checksum"), "verify: wrong checksum not reported");
            var junk = F("junk.traineddata");
            File.WriteAllBytes(junk, Enumerable.Repeat((byte)0x41, 500).ToArray());
            Check(OcrLanguages.Verify(junk).Length > 0, "verify: a file that is not a language pack was accepted");

            var target = F("installed");
            var fakeHindi = F("hin.traineddata");
            File.Copy(fake, fakeHindi);
            var refused = false;
            try { OcrLanguages.InstallFromFile(fakeHindi, target); }
            catch (InvalidOperationException ex) { refused = ex.Message.Length > 0; }
            Check(refused && !File.Exists(Path.Combine(target, "hin.traineddata")), "install: a fake hin.traineddata was accepted");
            var english = Path.Combine(packs, "eng.traineddata");
            Check(OcrLanguages.InstallFromFile(english, target) == "eng" && File.Exists(Path.Combine(target, "eng.traineddata")),
                "install: the official English file was refused");
            OcrLanguages.RemoveUserLanguage("eng", target);
            Check(!File.Exists(Path.Combine(target, "eng.traineddata")), "remove: file still there");

            // Download path, served from memory by a fake handler.
            var englishPack = OcrLanguages.Find("eng")!;
            var bytes = File.ReadAllBytes(english);
            long reported = 0;
            using (var good = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                var path = OcrLanguages.DownloadAsync(englishPack, target, new SyncProgress<long>(b => reported = b), default, good).GetAwaiter().GetResult();
                Check(File.Exists(path) && reported == bytes.Length && OcrLanguages.Verify(path, englishPack) == "", "download: a good download was not installed");
                File.Delete(path);
            }

            using (var truncated = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..100000]) })))
            {
                var message = "";
                try { OcrLanguages.DownloadAsync(englishPack, target, null, default, truncated).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { message = ex.Message; }
                Check(message.Contains("wrong size") && Directory.GetFiles(target).Length == 0, "download: a cut-off download was accepted or left a file");
            }

            using (var offline = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("No such host is known."))))
            {
                var message = "";
                try { OcrLanguages.DownloadAsync(englishPack, target, null, default, offline).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { message = ex.Message; }
                Console.WriteLine("    offline: " + message.Split('\n')[0]);
                Check(message.StartsWith("Could not reach github.com") && message.Contains("Add from a file"), "download: offline message unclear");
            }
        });

        Step("[64] OCR, Hindi + English (Devanagari copy and search)", () =>
        {
            var hindiFile = Path.Combine(packs, "hin.traineddata");
            if (!File.Exists(hindiFile))
            {
                Console.WriteLine("    SKIP: hin.traineddata is not in packaging\\ocr-languages (run the fetch script to include this check).");
                return;
            }

            var typedHindi = F("typed-hindi.pdf");
            BuildHindiPdf(typedHindi);
            var hindiScan = F("scan-hindi.pdf");
            BuildScan(typedHindi, hindiScan);
            var output = F("scan-hindi-ocr.pdf");
            var result = PdfOcrOps.MakeSearchable(hindiScan, output,
                new OcrOptions { Languages = ["hin", "eng"], LanguageFolders = [OcrLanguages.ProgramFolder, packs] });
            var text = Text(output);
            Console.WriteLine($"    {result.Words} words, confidence {result.MeanConfidence:0}%: " + text.Replace("\n", " | ").Trim());
            foreach (var word in new[] { "\u092D\u093E\u0930\u0924", "\u0938\u0930\u0915\u093E\u0930", "\u0928\u0917\u0930", "Invoice" })
                Check(text.Contains(word), $"hindi: '{word}' not found in the text layer");
            // Search and copy use the program's own line reading (glyph by glyph), not iText's extractor.
            var lines = PdfTextSearch.ExtractText(output);
            Console.WriteLine("    as search sees it: " + lines.Replace("\n", " | "));
            var hits = PdfTextSearch.Search(output, "\u0938\u0930\u0915\u093E\u0930");
            Check(hits.Count >= 1, "hindi: searching for a Devanagari word found nothing");
        });

        Step("[65] OCR speed: a 20-page scan (performance note)", () =>
        {
            var twenty = F("twenty-scans.pdf");
            BuildRepeatedScan(scan, twenty, 20);
            var steps = 0;
            var timer = Stopwatch.StartNew();
            var result = PdfOcrOps.MakeSearchable(twenty, F("twenty-scans-ocr.pdf"), new OcrOptions(), new SyncProgress<OcrProgress>(_ => steps++));
            timer.Stop();
            var perPage = timer.Elapsed.TotalSeconds / 20;
            Console.WriteLine($"    20 A4 pages: {timer.Elapsed.TotalSeconds:0.0}s ({perPage:0.00}s per page), {result.Words} words, {steps} progress reports");
            Check(result.PagesRead.Count == 20 && steps >= 20, "speed: not every page read or reported");
            Check(perPage < 10, $"speed: {perPage:0.0}s per page is far too slow");
        });
    }

    // ---- Fixtures --------------------------------------------------------------

    /// <summary>One typed A4 page with the test lines; /Rotate set to <paramref name="rotation"/>.</summary>
    private static void BuildTypedPdf(string path, int rotation)
    {
        using var pdf = new PdfDocument(new PdfWriter(path));
        var document = new Document(pdf, PageSize.A4);
        document.SetMargins(70, 60, 60, 60);
        foreach (var line in Lines) document.Add(new Paragraph(line).SetFontSize(15).SetMarginBottom(10));
        document.Flush();
        if (rotation != 0) pdf.GetPage(1).SetRotation(rotation);
        document.Close();
    }

    private static void BuildHindiPdf(string path)
    {
        using var pdf = new PdfDocument(new PdfWriter(path));
        var document = new Document(pdf, PageSize.A4);
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var font = PdfFontFactory.CreateTtcFont(Path.Combine(fonts, "Nirmala.ttc"), 0, "Identity-H",
            PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED, false);
        // Words without conjuncts or the i-sign, so they print correctly without a text shaper.
        document.Add(new Paragraph("\u092D\u093E\u0930\u0924 \u0938\u0930\u0915\u093E\u0930").SetFont(font).SetFontSize(28));
        document.Add(new Paragraph("\u0928\u0917\u0930 \u0915\u092E\u0932 \u091C\u0928\u0924\u093E \u092A\u093E\u0928\u0940").SetFont(font).SetFontSize(28));
        document.Add(new Paragraph("\u0938\u092E\u093E\u091A\u093E\u0930 \u0915\u093E\u092E \u0918\u0930").SetFont(font).SetFontSize(28));
        document.Add(new Paragraph("Invoice number 4711").SetFontSize(20));
        document.Close();
    }

    /// <summary>Renders the first page of <paramref name="typedPdf"/> at 200 dpi and puts the picture alone on an A4 page.</summary>
    private static void BuildScan(string typedPdf, string scanPdf, int pageRotation = 0)
    {
        var png = PdfPageRasterizer.RenderPng(File.ReadAllBytes(typedPdf), 1, 200);
        var picture = ImageDataFactory.Create(png);
        using var pdf = new PdfDocument(new PdfWriter(scanPdf));
        var size = picture.GetWidth() > picture.GetHeight() ? PageSize.A4.Rotate() : PageSize.A4;
        var page = pdf.AddNewPage(new PageSize(size));
        new PdfCanvas(page).AddImageFittedIntoRectangle(picture, new Rectangle(0, 0, size.GetWidth(), size.GetHeight()), false);
        if (pageRotation != 0) page.SetRotation(pageRotation);
    }

    private static void BuildImagePdf(string image, string output)
    {
        var picture = ImageDataFactory.Create(image);
        using var pdf = new PdfDocument(new PdfWriter(output));
        var page = pdf.AddNewPage(PageSize.A4);
        new PdfCanvas(page).AddImageFittedIntoRectangle(picture, new Rectangle(0, 0, PageSize.A4.GetWidth(), PageSize.A4.GetHeight()), false);
    }

    private static void BuildRepeatedScan(string scan, string output, int pages)
    {
        using var pdf = new PdfDocument(new PdfWriter(output));
        using var source = new PdfDocument(new PdfReader(scan));
        for (var i = 0; i < pages; i++) source.CopyPagesTo(1, 1, pdf);
    }

    /// <summary>A file with a valid traineddata header (one entry) followed by filler bytes.</summary>
    private static void FakePack(string path, int size)
    {
        var bytes = new byte[size];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        BitConverter.GetBytes(12L).CopyTo(bytes, 4);
        for (var i = 12; i < size; i++) bytes[i] = (byte)(i * 7);
        File.WriteAllBytes(path, bytes);
    }

    // ---- Reading ---------------------------------------------------------------

    private static string Text(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        return string.Join("\n", Enumerable.Range(1, pdf.GetNumberOfPages()).Select(p => PdfTextExtractor.GetTextFromPage(pdf.GetPage(p))));
    }

    /// <summary>Size, rotation, crop box and image count of page 1: what must not change.</summary>
    private static string PageFacts(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var page = pdf.GetPage(1);
        var crop = page.GetCropBox();
        var images = page.GetResources().GetResourceNames(PdfName.XObject)
            .Count(n => page.GetResources().GetResourceObject(PdfName.XObject, n) is PdfStream s && PdfName.Image.Equals(s.GetAsName(PdfName.Subtype)));
        return $"{crop.GetX():0},{crop.GetY():0},{crop.GetWidth():0}x{crop.GetHeight():0} rot {page.GetRotation()} images {images}";
    }

    /// <summary>Start of the first text chunk containing each known word, in user space.</summary>
    private static Dictionary<string, (double X, double Y)> WordPositions(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var collector = new ChunkCollector();
        new PdfCanvasProcessor(collector).ProcessPageContent(pdf.GetPage(1));
        var result = new Dictionary<string, (double, double)>();
        foreach (var word in KnownWords)
        {
            var chunk = collector.Chunks.FirstOrDefault(c => c.Text.Contains(word));
            if (chunk.Text is null) continue;
            result[word] = (chunk.X, chunk.Y);
        }

        return result;
    }

    /// <summary>User-space positions to 0..1 display coordinates (y up), with the page's crop box and /Rotate.</summary>
    private static Dictionary<string, (double X, double Y)> Normalised(Dictionary<string, (double X, double Y)> points, string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var page = pdf.GetPage(1);
        var crop = page.GetCropBox();
        var rotation = ((page.GetRotation() % 360) + 360) % 360;
        double w = crop.GetWidth(), h = crop.GetHeight();
        return points.ToDictionary(p => p.Key, p =>
        {
            var px = p.Value.X - crop.GetX();
            var py = p.Value.Y - crop.GetY();
            var (u, v, wd, hd) = rotation switch
            {
                90 => (py, w - px, h, w),
                180 => (w - px, h - py, w, h),
                270 => (h - py, px, h, w),
                _ => (px, py, w, h)
            };
            return (u / wd, v / hd);
        });
    }

    /// <summary>Where a normalised point of an upright page lands when the picture is turned clockwise.</summary>
    private static (double X, double Y) TurnPoint((double X, double Y) p, int clockwise) => clockwise switch
    {
        90 => (p.Y, 1 - p.X),
        180 => (1 - p.X, 1 - p.Y),
        270 => (1 - p.Y, p.X),
        _ => p
    };

    private static bool SamePlaces(
        Dictionary<string, (double X, double Y)> expected, Dictionary<string, (double X, double Y)> actual, double tolerance,
        out string report, bool absolute = false)
    {
        var worst = 0.0;
        var missing = new List<string>();
        foreach (var (word, point) in expected)
        {
            if (!actual.TryGetValue(word, out var found)) { missing.Add(word); continue; }
            var distance = Math.Max(Math.Abs(found.X - point.X), Math.Abs(found.Y - point.Y));
            if (absolute) distance /= PageSize.A4.GetHeight();
            worst = Math.Max(worst, distance);
        }

        report = $"{expected.Count - missing.Count}/{expected.Count} words, worst offset {worst:0.000} of the page" +
                 (missing.Count > 0 ? ", missing " + string.Join(" ", missing) : "");
        return missing.Count == 0 && worst <= tolerance;
    }

    private static int OcrTempFolders()
    {
        try { return Directory.GetDirectories(Path.GetTempPath(), "KcsPdfStudio-ocr-*").Length; }
        catch { return -1; }
    }

    private sealed class ChunkCollector : IEventListener
    {
        public List<(string Text, double X, double Y)> Chunks { get; } = [];

        /// <summary>Each known word with the start of its own first character (a typed line is one chunk).</summary>
        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo info) return;
            var text = info.GetText() ?? "";
            var characters = info.GetCharacterRenderInfos();
            foreach (var word in KnownWords)
            {
                var index = text.IndexOf(word, StringComparison.Ordinal);
                if (index < 0 || index >= characters.Count) continue;
                var start = characters[index].GetBaseline().GetStartPoint();
                Chunks.Add((word, start.Get(Vector.I1), start.Get(Vector.I2)));
            }
        }

        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT];
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
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
