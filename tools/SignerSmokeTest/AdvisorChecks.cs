using System.Diagnostics;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Filespec;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using KcsPdfSigner.Core.Documents;
using Path = System.IO.Path;
using PageSize = iText.Kernel.Geom.PageSize;
using Rectangle = iText.Kernel.Geom.Rectangle;

/// <summary>
/// Checks 46 and up: the document advisor behind the suggestion bars (each detector with a positive
/// and a negative case), the hidden-information removal, the clean rewrite of a damaged file, and speed.
/// </summary>
internal static class AdvisorChecks
{
    private static List<string> _failures = [];

    public static void Run(string workFolder, string unsignedPdf, string signedPdf, List<string> failures)
    {
        _failures = failures;
        var docs = Path.Combine(workFolder, "docs");
        var folder = Path.Combine(workFolder, "advisor");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);
        string D(string name) => Path.Combine(docs, name);

        bool Has(List<DocumentFinding> findings, string id) => findings.Any(f => f.Id == id);
        void Show(string name, List<DocumentFinding> findings) =>
            Console.WriteLine($"    {name}: " + (findings.Count == 0 ? "(none)" : string.Join(" | ", findings.Select(f => f.Id))));

        // Warm up iText (first use JIT-compiles for a few seconds), so later timings are fair.
        _ = DocumentAdvisor.Analyze(unsignedPdf, null);

        Step("[46] Password and owner restrictions: bar for a password-protected file, restrictions listed", () =>
        {
            var locked = D("locked.pdf"); // user u123, owner o456; print allowed, copy and modify not
            var withUser = DocumentAdvisor.Analyze(locked, "u123");
            Show("locked.pdf (user password)", withUser);
            Check(Has(withUser, SuggestionIds.PasswordProtected), "advisor: password-protected file not reported");
            var restriction = withUser.FirstOrDefault(f => f.Id == SuggestionIds.OwnerRestrictions);
            Console.WriteLine($"    restrictions: {restriction?.Message}");
            Check(restriction is not null && restriction.ActionId == SuggestionActions.None
                  && restriction.Message.Contains("Copying text") && restriction.Message.Contains("editing")
                  && !restriction.Message.Contains("Printing"), "advisor: restrictions message wrong");

            var report = PdfPermissions.Inspect(locked, "u123");
            Check(report.IsRestricted && report.AllowsPrinting && !report.AllowsCopying && !report.AllowsModifying
                  && !report.AllowsAnnotating && !report.AllowsFillIn && !report.AllowsAssembly,
                "permissions: copy/modify/annotate/fill/assemble flags wrong");

            var withOwner = DocumentAdvisor.Analyze(locked, "o456");
            Show("locked.pdf (owner password)", withOwner);
            Check(!Has(withOwner, SuggestionIds.OwnerRestrictions), "advisor: owner password should lift the restrictions bar");

            // A restricted file cannot be rewritten without the owner password: no bar whose button would fail.
            var rewriteFails = false;
            try { PdfSanitizeOps.RemoveHiddenInfo(locked, F("locked-clean.pdf"), "u123"); }
            catch { rewriteFails = true; }
            Console.WriteLine($"    rewrite with the user password fails: {rewriteFails}");
            Check(!Has(withUser, SuggestionIds.HiddenInfo) && Has(withOwner, SuggestionIds.HiddenInfo),
                "advisor: hidden-info should be offered only when the file can be changed (owner password)");

            var plain = DocumentAdvisor.Analyze(D("three-pages.pdf"), null);
            Check(!Has(plain, SuggestionIds.PasswordProtected) && !Has(plain, SuggestionIds.OwnerRestrictions),
                "advisor: unprotected file reported as protected");
            Check(PdfPermissions.Inspect(D("three-pages.pdf")).LockedDescriptions().Count == 0, "permissions: plain file lists locks");
        });

        Step("[47] Scanned pages: a full-page picture without text is a scan; a text page is not", () =>
        {
            var scan = F("scan.pdf");
            PdfPageOps.ImagesToPdf([D("picture.png"), D("picture.png")], scan);
            var report = DocumentAdvisor.DetectScanned(scan);
            Console.WriteLine($"    scan.pdf: {report.ScannedPages.Count} of {report.PageCount} scanned, share {report.Share:0.00}");
            Check(report.IsScanned && report.Share == 1.0, "scan: picture pages not recognised");
            var findings = DocumentAdvisor.Analyze(scan, null);
            Check(findings.Any(f => f.Id == SuggestionIds.Scanned && f.ActionId == SuggestionActions.Ocr), "scan: no OCR suggestion");

            var text = DocumentAdvisor.DetectScanned(D("three-pages.pdf"));
            Check(!text.IsScanned && text.ScannedPages.Count == 0, "scan: text pages reported as scanned");
            var small = DocumentAdvisor.DetectScanned(D("image.pdf")); // small picture on a text page
            Check(small.ScannedPages.Count == 0, "scan: a small picture made a page look scanned");
        });

        Step("[48] Big file: shown above the threshold, not below", () =>
        {
            var file = D("three-pages.pdf");
            var size = new FileInfo(file).Length;
            var low = DocumentAdvisor.Analyze(file, null, new AdvisorOptions { LargeFileBytes = size - 1 });
            var big = low.FirstOrDefault(f => f.Id == SuggestionIds.LargeFile);
            Console.WriteLine($"    {size:N0} bytes, threshold {size - 1:N0}: {big?.Message}");
            Check(big is not null && big.Message.Contains("MB") && big.ActionId == SuggestionActions.ReduceSize, "big file: not reported above the threshold");
            Check(!Has(DocumentAdvisor.Analyze(file, null), SuggestionIds.LargeFile), "big file: reported under the default 5 MB");
        });

        Step("[49] Signing spots: counted on an unsigned PDF; not on a signed one or a page without the phrases", () =>
        {
            var findings = DocumentAdvisor.Analyze(unsignedPdf, null);
            var spots = findings.FirstOrDefault(f => f.Id == SuggestionIds.SigningSpots);
            Console.WriteLine($"    unsigned.pdf: {spots?.Message}");
            Check(spots is not null && spots.Count == 4 && spots.Message.StartsWith("Found 4 places"), "spots: expected 4 on unsigned.pdf");
            Check(!Has(DocumentAdvisor.Analyze(signedPdf, null), SuggestionIds.SigningSpots), "spots: offered on an already signed file");
            Check(!Has(DocumentAdvisor.Analyze(D("three-pages.pdf"), null), SuggestionIds.SigningSpots), "spots: found in a file without signing phrases");
        });

        Step("[50] Hidden information: found, removed (details, XMP, comment authors), pages and fields unchanged", () =>
        {
            var source = F("hidden.pdf");
            BuildHiddenInfoPdf(source, D("picture.png"));
            var found = PdfSanitizeOps.FindHiddenInfo(source);
            Console.WriteLine($"    found: {found.Summary()}");
            Check(found.HasPersonalInfo && found.Author == "Ramesh Kamath" && found.HasXmpMetadata
                  && found.CommentAuthors.GetValueOrDefault("Priya") == 2 && found.Attachments.Count == 1
                  && found.InfoFields.ContainsKey("Title") && found.InfoFields.ContainsKey("Company"),
                "hidden: detection incomplete");
            var finding = DocumentAdvisor.Analyze(source, null).FirstOrDefault(f => f.Id == SuggestionIds.HiddenInfo);
            Check(finding is not null && finding.Message.Contains("Author 'Ramesh Kamath'") && finding.Message.Contains("2 comments by 'Priya'")
                  && finding.Message.Contains("1 attachment"), $"hidden: bar message wrong ({finding?.Message})");

            var cleaned = F("hidden-cleaned.pdf");
            PdfSanitizeOps.RemoveHiddenInfo(source, cleaned);
            var after = PdfSanitizeOps.FindHiddenInfo(cleaned);
            Console.WriteLine($"    after removal: '{after.Summary()}', attachments kept: {after.Attachments.Count}");
            Check(after.Author.Length == 0 && !after.InfoFields.ContainsKey("Title") && !after.InfoFields.ContainsKey("Company")
                  && !after.HasXmpMetadata && after.CommentAuthors.Count == 0, "hidden: details, XMP or comment authors left behind");
            Check(after.Attachments.Count == 1, "hidden: attachment removed without being asked");
            var info = PdfInfoReader.Read(cleaned);
            Check(info.Producer.Contains("iText") && info.Created is not null, "hidden: dates or producer handling unexpected");
            Check(PdfAnnotationOps.List(cleaned).Count == PdfAnnotationOps.List(source).Count, "hidden: comments were removed");
            Check(info.PageCount == 2 && PdfTextSearch.ExtractText(cleaned).Contains("Quarterly figures"), "hidden: page content changed");

            var noFiles = F("hidden-no-attachments.pdf");
            PdfSanitizeOps.RemoveHiddenInfo(source, noFiles, removeAttachments: true);
            Check(PdfSanitizeOps.FindHiddenInfo(noFiles).Attachments.Count == 0, "hidden: attachment not removed when asked");
            Check(!PdfSanitizeOps.FindHiddenInfo(noFiles).HasPersonalInfo, "hidden: cleaned file still reports personal info");

            var form = F("form-cleaned.pdf");
            PdfSanitizeOps.RemoveHiddenInfo(D("form.pdf"), form);
            var names = PdfFormOps.ReadFields(form).Select(f => f.Name).OrderBy(n => n).ToList();
            Check(names.SequenceEqual(PdfFormOps.ReadFields(D("form.pdf")).Select(f => f.Name).OrderBy(n => n)),
                "hidden: form field names changed");

            Check(!PdfSanitizeOps.FindHiddenInfo(unsignedPdf).HasPersonalInfo, "hidden: iText's producer line counted as personal info");
        });

        Step("[51] Sideways pages: found from text direction, straightened, slanted watermarks ignored", () =>
        {
            var mixed = F("sideways.pdf");
            BuildSidewaysPdf(mixed);
            var turns = DocumentAdvisor.DetectSidewaysPages(mixed);
            Console.WriteLine("    sideways.pdf: " + string.Join(", ", turns.Select(t => $"p{t.PageNumber} turn {t.Degrees}")));
            Check(turns.Count == 3 && turns.All(t => t.PageNumber != 1), "sideways: expected pages 2, 3 and 4 (page 1 upright)");
            Check(turns.FirstOrDefault(t => t.PageNumber == 3)?.Degrees == 180, "sideways: upside-down page should need 180");

            var straight = F("straightened.pdf");
            DocumentAdvisor.StraightenPages(mixed, straight, turns);
            var again = DocumentAdvisor.DetectSidewaysPages(straight);
            Check(again.Count == 0, $"sideways: still sideways after straightening ({again.Count})");
            Check(PdfInfoReader.Read(straight).PageCount == 4, "sideways: page count changed");

            var finding = DocumentAdvisor.Analyze(mixed, null).FirstOrDefault(f => f.Id == SuggestionIds.SidewaysPages);
            Check(finding is not null && finding.Message.StartsWith("3 pages look sideways"), "sideways: bar message wrong");

            Check(DocumentAdvisor.DetectSidewaysPages(D("watermark.pdf")).Count == 0, "sideways: 45-degree watermark flagged");
            Check(DocumentAdvisor.DetectSidewaysPages(unsignedPdf).Count == 0, "sideways: upright document flagged");
            // rot90.pdf: three-pages.pdf with page 1 turned by /Rotate 90, so its upright text now reads sideways.
            var turned = DocumentAdvisor.DetectSidewaysPages(D("rot90.pdf"));
            Check(turned.Count == 1 && turned[0].PageNumber == 1 && turned[0].Degrees == 270, "sideways: page turned by /Rotate 90 not found");
            var little = F("rot90-little-text.pdf");
            PdfPageOps.RotatePages(D("three-pages.pdf"), little, [2], 90); // page 2 holds only "PAGE-TWO"
            Check(DocumentAdvisor.DetectSidewaysPages(little).Count == 0, "sideways: page with too little text flagged");
        });

        Step("[52] Damaged file: reported as repaired, the clean copy is no longer damaged", () =>
        {
            var bytes = File.ReadAllBytes(D("three-pages.pdf"));
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            var at = text.LastIndexOf("startxref", StringComparison.Ordinal);
            var damaged = F("damaged.pdf");
            File.WriteAllBytes(damaged, System.Text.Encoding.Latin1.GetBytes(text[..(at + "startxref".Length)] + "\n12345\n%%EOF\n"));
            Check(DocumentAdvisor.IsDamaged(damaged), "damaged: not detected");
            Check(Has(DocumentAdvisor.Analyze(damaged, null), SuggestionIds.RepairedFile), "damaged: no repaired-file suggestion");

            var clean = F("damaged-clean.pdf");
            DocumentAdvisor.WriteCleanCopy(damaged, clean);
            Check(!DocumentAdvisor.IsDamaged(clean) && PdfInfoReader.Read(clean).PageCount == 3, "damaged: clean copy still damaged");
            Check(!Has(DocumentAdvisor.Analyze(D("three-pages.pdf"), null), SuggestionIds.RepairedFile), "damaged: healthy file reported");

            var junk = F("junk.pdf");
            File.WriteAllText(junk, "not a pdf");
            var none = DocumentAdvisor.Inspect(junk, null);
            Check(none.Error.Length > 0 && DocumentAdvisor.BuildFindings(none, new AdvisorOptions()).Count == 0, "damaged: junk should give no findings");
            Check(DocumentAdvisor.Analyze(F("missing.pdf"), null).Count == 0, "damaged: missing file should give no findings");
            Check(DocumentAdvisor.Analyze(D("locked.pdf"), null).Count == 0, "damaged: protected file without password should give no findings");
        });

        Step("[53] Fillable form: counted without signature fields; a signature-only form is not offered", () =>
        {
            var form = DocumentAdvisor.Analyze(D("form.pdf"), null).FirstOrDefault(f => f.Id == SuggestionIds.FillableForm);
            Console.WriteLine($"    form.pdf: {form?.Message}");
            Check(form is not null && form.Count == 3 && form.Message.Contains("(3 fields)"), "form: expected 3 fields");
            Check(!Has(DocumentAdvisor.Analyze(Path.Combine(workFolder, "with-blank-field.pdf"), null), SuggestionIds.FillableForm),
                "form: signature-only form offered for filling");
            Check(!Has(DocumentAdvisor.Analyze(D("three-pages.pdf"), null), SuggestionIds.FillableForm), "form: plain file offered");
        });

        Step("[54] After merging and after signing: only for documents produced by those tools", () =>
        {
            var file = D("three-pages.pdf");
            var merged = DocumentAdvisor.Analyze(file, null, new AdvisorOptions { Origin = DocumentOrigin.Merged })
                .FirstOrDefault(f => f.Id == SuggestionIds.MergedPageNumbers);
            Check(merged is not null && merged.Message == "Your new PDF has 3 pages. Add page numbers?", "merged: message wrong");
            Check(!Has(DocumentAdvisor.Analyze(file, null), SuggestionIds.MergedPageNumbers), "merged: shown for an ordinary file");

            var signed = DocumentAdvisor.Analyze(signedPdf, null, new AdvisorOptions { Origin = DocumentOrigin.SignedHere });
            Check(signed.FirstOrDefault()?.Id == SuggestionIds.SignedSendCheck, "signed: send check not first");
            Check(!Has(DocumentAdvisor.Analyze(signedPdf, null), SuggestionIds.SignedSendCheck), "signed: shown for a file not signed here");
            Check(!Has(DocumentAdvisor.Analyze(unsignedPdf, null, new AdvisorOptions { Origin = DocumentOrigin.SignedHere }), SuggestionIds.SignedSendCheck),
                "signed: shown for a file without signatures");

            var boxes = DocumentAdvisor.InspectSignatureBoxes(signedPdf);
            Console.WriteLine($"    signed.pdf: {boxes.Count} boxes, printable {boxes.Count(b => b.IsPrintable)}, drawn {boxes.Count(b => b.HasAppearance)}");
            Check(boxes.Count == 4 && boxes.All(b => b.IsVisible && b.IsPrintable && b.HasAppearance), "signed: boxes not all printable with an appearance");
            var facts = DocumentAdvisor.Inspect(signedPdf, null);
            Check(facts.SignatureCount == 4 && facts.Signers.Count == 1, "signed: signature count or signers wrong");
        });

        Step("[55] Speed: a 300-page file is analysed in under 3 seconds", () =>
        {
            var big = F("300-pages.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(big)))
            {
                var document = new Document(pdf, PageSize.A4, false);
                for (var page = 1; page <= 300; page++)
                {
                    if (page > 1) document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                    document.Add(new Paragraph($"Statement page {page}").SetFontSize(16));
                    for (var i = 0; i < 25; i++)
                        document.Add(new Paragraph($"{i + 1:00}. Entry with a description, a date 01-04-2026 and an amount 12,345.00").SetFontSize(9));
                    if (page % 50 == 0) document.Add(new Paragraph("Authorised Signatory"));
                }

                document.Close();
            }

            var timer = Stopwatch.StartNew();
            var facts = DocumentAdvisor.Inspect(big, null);
            timer.Stop();
            Console.WriteLine($"    300 pages, {facts.FileSizeBytes:N0} bytes: {timer.Elapsed.TotalSeconds:0.00}s, {facts.SigningSpots.Count} spots, {facts.SidewaysPages.Count} sideways");
            Check(facts.PageCount == 300 && facts.SigningSpots.Count == 6 && facts.SidewaysPages.Count == 0, "speed: wrong facts for the 300-page file");
            Check(timer.Elapsed.TotalSeconds < 3, $"speed: 300 pages took {timer.Elapsed.TotalSeconds:0.0}s");
        });
    }

    private static void BuildHiddenInfoPdf(string path, string attachment)
    {
        using (var pdf = new PdfDocument(new PdfWriter(path)))
        {
            pdf.GetDocumentInfo().SetAuthor("Ramesh Kamath").SetTitle("Q3 internal draft").SetCreator("Word");
            pdf.GetDocumentInfo().SetMoreInfo("Company", "KCS & Associates");
            var document = new Document(pdf);
            document.Add(new Paragraph("Quarterly figures"));
            document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            document.Add(new Paragraph("Second page"));
            pdf.AddFileAttachment("notes.txt", PdfFileSpec.CreateEmbeddedFileSpec(pdf, File.ReadAllBytes(attachment), "notes.txt", "notes.txt", null, null));
            for (var i = 0; i < 2; i++)
            {
                var note = new PdfTextAnnotation(new Rectangle(100 + i * 40, 700, 20, 20));
                note.SetContents("Check this").SetTitle(new PdfString("Priya"));
                pdf.GetPage(1).AddAnnotation(note);
            }

            document.Close();
        }

        // Add an XMP packet like Word and Acrobat write.
        var withXmp = path + ".tmp";
        using (var pdf = new PdfDocument(new PdfReader(path), new PdfWriter(withXmp), new StampingProperties()))
        {
            var xmp = new PdfStream(System.Text.Encoding.UTF8.GetBytes(
                "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator><rdf:Seq><rdf:li>Ramesh Kamath</rdf:li></rdf:Seq></dc:creator></rdf:Description>" +
                "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"));
            xmp.Put(PdfName.Type, PdfName.Metadata);
            xmp.Put(PdfName.Subtype, PdfName.XML);
            pdf.GetCatalog().Put(PdfName.Metadata, xmp.MakeIndirect(pdf));
        }

        File.Move(withXmp, path, true);
    }

    /// <summary>Page 1 upright, page 2 text running up (sideways), page 3 upside down, page 4 running down.</summary>
    private static void BuildSidewaysPdf(string path)
    {
        using var pdf = new PdfDocument(new PdfWriter(path));
        var document = new Document(pdf, PageSize.A4, false);
        var lines = Enumerable.Range(1, 8).Select(i => $"Line {i}: this sentence makes the page direction clear.").ToList();
        float[] angles = [0, 90, 180, 270];
        for (var page = 1; page <= 4; page++)
        {
            pdf.AddNewPage(PageSize.A4);
            var radians = (float)(angles[page - 1] * Math.PI / 180);
            for (var i = 0; i < lines.Count; i++)
            {
                var (x, y) = angles[page - 1] switch
                {
                    90 => (300f + i * 18, 150f),
                    180 => (500f, 700f - i * 18),
                    270 => (300f - i * 18, 700f),
                    _ => (60f, 700f - i * 18)
                };
                document.ShowTextAligned(new Paragraph(lines[i]).SetFontSize(11), x, y, page, TextAlignment.LEFT, VerticalAlignment.BOTTOM, radians);
            }
        }

        document.Close();
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
