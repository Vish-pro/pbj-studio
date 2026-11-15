using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using Path = System.IO.Path;

/// <summary>Checks 7 to 16: every class in KcsPdfSigner.Core.Documents, end to end on temp files.</summary>
internal static class DocumentOpsChecks
{
    private static List<string> _failures = [];

    public static void Run(
        string workFolder, string sourcePdf, string signedPdf, int expectedSignatures, bool signedOk,
        List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "docs");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        var threePages = F("three-pages.pdf");
        BuildThreePagePdf(threePages);
        var formPdf = F("form.pdf");
        BuildFormPdf(formPdf);
        var pngFile = F("picture.png");
        File.WriteAllBytes(pngFile, PdfPageRasterizer.RenderPng(File.ReadAllBytes(sourcePdf), 1, 30));

        Step("[7] PdfInfoReader", () =>
        {
            var info = PdfInfoReader.Read(threePages);
            Console.WriteLine($"    pages={info.PageCount} title='{info.Title}' author='{info.Author}' " +
                              $"version={info.PdfVersion} created={info.Created:yyyy-MM-dd} size={info.FileSizeBytes}");
            Check(info.PageCount == 3, "info: expected 3 pages");
            Check(info.Title == "Smoke Title" && info.Author == "Smoke Author", "info: title/author not read");
            Check(info.Created is not null, "info: creation date not parsed");
            Check(!info.IsEncrypted && !PdfInfoReader.IsEncrypted(threePages), "info: plain file reported encrypted");

            var outline = PdfInfoReader.ReadOutline(threePages);
            Console.WriteLine("    outline: " + string.Join(", ",
                outline.Select(o => $"{o.Title}->p{o.PageNumber}({o.Children.Count})")));
            Check(outline.Count == 2 && outline[0].PageNumber == 1 && outline[1].PageNumber == 3,
                "outline: expected Chapter 1 -> p1 and Chapter 3 -> p3");
            Check(outline.Count == 2 && outline[0].Children.Count == 1 && outline[0].Children[0].PageNumber == 2,
                "outline: expected one child pointing at p2");

            var edited = F("meta.pdf");
            PdfInfoReader.SetMetadata(threePages, edited, "New Title", null, "New Subject", "a, b");
            var after = PdfInfoReader.Read(edited);
            Check(after.Title == "New Title" && after.Author == "Smoke Author" &&
                  after.Subject == "New Subject" && after.Keywords == "a, b",
                "SetMetadata: values not written or null not left unchanged");
        });

        Step("[8] PdfTextSearch", () =>
        {
            var loose = PdfTextSearch.Search(sourcePdf, "partner");
            var whole = PdfTextSearch.Search(sourcePdf, "Partner", caseSensitive: true, wholeWord: true);
            Console.WriteLine($"    'partner' any={loose.Count}, 'Partner' whole+case={whole.Count}; " +
                              $"first: p{whole.FirstOrDefault()?.PageNumber} '{whole.FirstOrDefault()?.Snippet}'");
            Check(loose.Count == 3, $"search: expected 3 loose hits for 'partner', got {loose.Count}");
            Check(whole.Count == 1 && whole[0].Width > 0 && whole[0].Height > 0,
                $"search: expected 1 whole-word hit, got {whole.Count}");

            var page1 = PdfTextSearch.ExtractText(sourcePdf, 1);
            var all = PdfTextSearch.ExtractText(sourcePdf);
            Check(page1.Contains("Independent Auditor's Report") && !page1.Contains("Annexure"),
                "extract: page 1 text wrong");
            Check(all.Contains("Annexure") && all.Contains("Membership No. 123456"),
                "extract: whole-document text wrong");
        });

        Step("[9] PdfPageOps", () =>
        {
            var rotated = F("rotated.pdf");
            PdfPageOps.RotatePages(threePages, rotated, [2], 90);
            Check(Rotations(rotated).SequenceEqual([0, 90, 0]), "rotate: expected rotations 0,90,0");
            var rotatedBack = F("rotated-back.pdf");
            PdfPageOps.RotatePages(rotated, rotatedBack, [2], -90);
            Check(Rotations(rotatedBack).SequenceEqual([0, 0, 0]), "rotate: -90 did not undo +90");

            var deleted = F("deleted.pdf");
            PdfPageOps.DeletePages(threePages, deleted, [2]);
            Check(PageMarkers(deleted).SequenceEqual(["PAGE-ONE", "PAGE-THREE"]), "delete: wrong pages left");
            Check(Throws(() => PdfPageOps.DeletePages(threePages, F("none.pdf"), [1, 2, 3])),
                "delete: deleting all pages was allowed");

            var extracted = F("extracted.pdf");
            PdfPageOps.ExtractPages(threePages, extracted, [3, 1]);
            Check(PageMarkers(extracted).SequenceEqual(["PAGE-THREE", "PAGE-ONE"]), "extract: wrong pages or order");

            var reordered = F("reordered.pdf");
            PdfPageOps.ReorderPages(threePages, reordered, [3, 1, 2]);
            Check(PageMarkers(reordered).SequenceEqual(["PAGE-THREE", "PAGE-ONE", "PAGE-TWO"]),
                "reorder: wrong order (3,1,2)");
            var reordered2 = F("reordered2.pdf");
            PdfPageOps.ReorderPages(threePages, reordered2, [2, 3, 1]);
            Check(PageMarkers(reordered2).SequenceEqual(["PAGE-TWO", "PAGE-THREE", "PAGE-ONE"]),
                "reorder: wrong order (2,3,1)");
            Check(Throws(() => PdfPageOps.ReorderPages(threePages, F("bad.pdf"), [1, 1, 2])),
                "reorder: non-permutation accepted");

            var inserted = F("inserted.pdf");
            PdfPageOps.InsertPages(threePages, inserted, sourcePdf, 1);
            var markers = PageMarkers(inserted);
            Check(markers.Count == 5 && markers[0] == "PAGE-ONE" && markers[1] == "" && markers[3] == "PAGE-TWO" &&
                  PdfTextSearch.ExtractText(inserted, 2).Contains("Independent"),
                "insert pages: wrong result " + string.Join("|", markers));
            var insertedEnd = F("inserted-end.pdf");
            PdfPageOps.InsertPages(threePages, insertedEnd, sourcePdf, 3);
            Check(PageCount(insertedEnd) == 5 && PageMarkers(insertedEnd)[2] == "PAGE-THREE",
                "insert pages at end failed");

            var blank = F("blank.pdf");
            PdfPageOps.InsertBlankPage(threePages, blank, 0);
            Check(PageMarkers(blank).SequenceEqual(["", "PAGE-ONE", "PAGE-TWO", "PAGE-THREE"]),
                "insert blank at start failed");
            var blankEnd = F("blank-end.pdf");
            PdfPageOps.InsertBlankPage(threePages, blankEnd, 3);
            Check(PageCount(blankEnd) == 4 && PdfTextSearch.ExtractText(blankEnd, 4).Trim() == "",
                "insert blank at end failed");

            var merged = F("merged.pdf");
            PdfPageOps.Merge([threePages, sourcePdf], merged);
            Check(PageCount(merged) == 5, "merge: expected 5 pages");
            Check(PdfInfoReader.ReadOutline(merged).Count == 2, "merge: bookmarks not carried over");

            var parts = PdfPageOps.Split(merged, F("split"), 2);
            Console.WriteLine($"    split -> {string.Join(", ", parts.Select(Path.GetFileName))}");
            Check(parts.Count == 3 && parts.Select(PageCount).SequenceEqual([2, 2, 1]), "split: expected 2+2+1 pages");

            var images = F("images.pdf");
            PdfPageOps.ImagesToPdf([pngFile, pngFile], images);
            Check(PageCount(images) == 2, "images to pdf: expected 2 pages");
            Check(ImageCount(images, 1) == 1, "images to pdf: page has no image");
        });

        Step("[10] PdfAnnotationOps", () =>
        {
            var specs = new List<AnnotationSpec>();
            foreach (var kind in Enum.GetValues<AnnotationKind>().Where(k => k != AnnotationKind.Other))
            {
                specs.Add(new AnnotationSpec
                {
                    Kind = kind, PageNumber = 1, Left = 72, Bottom = 400, Width = 150, Height = 40,
                    Text = kind + " text", Author = "Tester",
                    InkPoints = kind == AnnotationKind.Ink
                        ? [[new(80, 300), new(120, 340), new(160, 300)], [new(90, 290), new(150, 290)]]
                        : []
                });
            }

            var annotated = F("annotated.pdf");
            PdfAnnotationOps.Add(threePages, annotated, specs);
            var list = PdfAnnotationOps.List(annotated);
            Console.WriteLine($"    added {list.Count}: {string.Join(", ", list.Select(a => a.Kind))}");
            Check(list.Count == 10, $"annotations: expected 10, got {list.Count}");
            Check(list.Select(a => a.Kind).Distinct().Count() == 10, "annotations: kinds not all distinct");
            Check(list.All(a => a.Author == "Tester"), "annotations: author missing");
            Check(list.FirstOrDefault(a => a.Kind == AnnotationKind.Note)?.Text == "Note text",
                "annotations: note text missing");
            Check(HasAppearances(annotated), "annotations: an annotation has no appearance stream");

            var removed = F("annotation-removed.pdf");
            var victim = list.First(a => a.Kind == AnnotationKind.Ellipse);
            PdfAnnotationOps.Remove(annotated, removed, victim.PageNumber, victim.IndexOnPage);
            var afterRemove = PdfAnnotationOps.List(removed);
            Check(afterRemove.Count == 9 && afterRemove.All(a => a.Kind != AnnotationKind.Ellipse),
                "annotations: remove failed");

            var flattened = F("annotation-flat.pdf");
            PdfAnnotationOps.Flatten(removed, flattened);
            Check(PdfAnnotationOps.List(flattened).Count == 0, "annotations: flatten left comments behind");
            Check(PdfTextSearch.ExtractText(flattened, 1).Contains("FreeText text"),
                "annotations: flattened free text is not in the page content");
            Check(IsPng(PdfPageRasterizer.RenderPng(File.ReadAllBytes(flattened), 1, 40)),
                "annotations: flattened page does not render");
        });

        Step("[11] PdfFormOps", () =>
        {
            var fields = PdfFormOps.ReadFields(formPdf);
            Console.WriteLine("    fields: " + string.Join(", ",
                fields.Select(f => $"{f.Name}:{f.FieldType}:p{f.PageNumber}[{string.Join("/", f.Options)}]")));
            Check(fields.Count == 3, $"forms: expected 3 fields, got {fields.Count}");
            Check(fields.Any(f => f.Name == "FullName" && f.FieldType == "text" && f.PageNumber == 1 && f.Width > 0),
                "forms: text field wrong");
            Check(fields.Any(f => f.Name == "Agree" && f.FieldType == "checkbox" && f.Options.Count == 1),
                "forms: checkbox wrong");
            Check(fields.Any(f => f.Name == "City" && f.FieldType == "choice" && f.Options.Count == 3),
                "forms: choice wrong");

            var filled = F("form-filled.pdf");
            PdfFormOps.Fill(formPdf, filled, new Dictionary<string, string>
            {
                ["FullName"] = "Smoke Filler", ["Agree"] = "true", ["City"] = "Udupi"
            });
            var after = PdfFormOps.ReadFields(filled).ToDictionary(f => f.Name, f => f.Value);
            Check(after["FullName"] == "Smoke Filler", "forms: text value not filled");
            Check(after["Agree"] != "Off" && after["Agree"] != "", "forms: checkbox not ticked");
            Check(after["City"] == "Udupi", "forms: choice not set");
            Check(Throws(() => PdfFormOps.Fill(formPdf, F("x.pdf"), new Dictionary<string, string> { ["Nope"] = "1" })),
                "forms: unknown field accepted");

            var flat = F("form-flat.pdf");
            PdfFormOps.Flatten(filled, flat);
            Check(PdfFormOps.ReadFields(flat).Count == 0, "forms: flatten left fields");
            Check(PdfTextSearch.ExtractText(flat).Contains("Smoke Filler"), "forms: flattened value not in page content");
        });

        Step("[12] PdfContentOps", () =>
        {
            var withText = F("text.pdf");
            PdfContentOps.AddText(threePages, withText, 2, 100, 500, "ADDED-TEXT\nSECOND-LINE", 14);
            var hit = PdfTextSearch.Search(withText, "ADDED-TEXT").SingleOrDefault();
            Check(hit is not null && hit.PageNumber == 2 && Math.Abs(hit.Left - 100) < 2,
                "add text: not found where placed");
            var second = PdfTextSearch.Search(withText, "SECOND-LINE").SingleOrDefault();
            Check(hit is not null && second is not null && second.Bottom < hit.Bottom,
                "add text: second line not below first");

            // On a /Rotate 90 page the text must be drawn along +y in user space so it reads
            // upright; line reconstruction groups by y, so it then no longer appears as one word.
            var rotated = F("rot90.pdf");
            PdfPageOps.RotatePages(threePages, rotated, [1], 90);
            var rotatedText = F("rot90-text.pdf");
            PdfContentOps.AddText(rotated, rotatedText, 1, 300, 300, "UPRIGHT", 14);
            Check(!PdfTextSearch.ExtractText(rotatedText, 1).Contains("UPRIGHT"),
                "add text on rotated page: text was drawn unrotated");

            var withImage = F("image.pdf");
            PdfContentOps.AddImage(threePages, withImage, 1, pngFile, 300, 600, 120, 90);
            Check(ImageCount(withImage, 1) == 1, "add image: no image on page 1");

            var watermarked = F("watermark.pdf");
            PdfContentOps.AddWatermark(threePages, watermarked, "CONFIDENTIAL", 0.25f, 45f, 60f);
            Check(PdfTextSearch.ExtractText(watermarked).Contains("PAGE-TWO"), "watermark: original text lost");
            Check(Enumerable.Range(1, 3).All(p => ContentHas(watermarked, p, " gs")),
                "watermark: transparency missing");

            var headed = F("header.pdf");
            PdfContentOps.AddHeaderFooter(threePages, headed, "KCS HEADER", "Footer {page}/{pages}", true);
            var headedText = PdfTextSearch.ExtractText(headed);
            Check(headedText.Contains("KCS HEADER") && headedText.Contains("Footer 2/3") &&
                  headedText.Contains("Page 3 of 3"), "header/footer: text missing");
            var header = PdfTextSearch.Search(headed, "KCS HEADER").FirstOrDefault();
            Check(header is not null && header.Bottom > 780, "header: not at the top of the page");

            var marker = PdfTextSearch.Search(threePages, "PAGE-ONE").Single();
            var redacted = F("redacted.pdf");
            PdfContentOps.Redact(threePages, redacted,
                [new RedactArea(1, marker.Left - 2, marker.Bottom - 2, marker.Width + 4, marker.Height + 4)]);
            var redactedText = PdfTextSearch.ExtractText(redacted, 1);
            Console.WriteLine($"    redacted page 1 text: '{redactedText.Replace("\n", " | ")}'");
            Check(!redactedText.Contains("PAGE-ONE"), "redact: text under the area is still in the file");
            Check(!RawBytesContain(redacted, "PAGE-ONE"), "redact: marker still present in the raw file");
            Check(redactedText.Contains("Chapter 1 body"), "redact: text outside the area was removed");
        });

        Step("[13] PdfSecurityOps", () =>
        {
            var locked = F("locked.pdf");
            PdfSecurityOps.Encrypt(threePages, locked, "u123", "o456", allowPrint: true, allowCopy: false,
                allowModify: false);
            Check(PdfInfoReader.IsEncrypted(locked) && PdfInfoReader.NeedsPassword(locked), "encrypt: file not protected");
            Check(Throws(() => PdfInfoReader.Read(locked)), "encrypt: opened without password");
            Check(PdfInfoReader.Read(locked, "u123").PageCount == 3, "encrypt: user password does not open");
            Check(PdfTextSearch.ExtractText(locked, 1, "u123").Contains("PAGE-ONE"),
                "encrypt: text not readable with password");
            Check(IsPng(PdfPageRasterizer.RenderPng(File.ReadAllBytes(locked), 1, 40, "u123")),
                "encrypt: rasterizer with password failed");

            using (var reader = new PdfReader(locked, new ReaderProperties().SetPassword("u123"u8.ToArray())))
            using (new PdfDocument(reader))
            {
                Check(reader.GetCryptoMode() == EncryptionConstants.ENCRYPTION_AES_256, "encrypt: not AES-256");
                var permissions = reader.GetPermissions();
                Check((permissions & EncryptionConstants.ALLOW_PRINTING) != 0 &&
                      (permissions & EncryptionConstants.ALLOW_COPY) == 0, "encrypt: permissions wrong");
            }

            Check(Throws(() => PdfSecurityOps.Decrypt(locked, F("nope.pdf"), "wrong")),
                "decrypt: wrong password accepted");
            Check(Throws(() => PdfSecurityOps.Decrypt(locked, F("nope2.pdf"), "u123")),
                "decrypt: user password removed restrictions");

            var edited = F("locked-watermark.pdf");
            PdfContentOps.AddWatermark(locked, edited, "X", password: "o456");
            Check(PdfInfoReader.NeedsPassword(edited), "edit of encrypted file dropped its encryption");

            var unlocked = F("unlocked.pdf");
            PdfSecurityOps.Decrypt(locked, unlocked, "o456");
            Check(!PdfInfoReader.IsEncrypted(unlocked) && PdfInfoReader.Read(unlocked).PageCount == 3,
                "decrypt: still encrypted");
        });

        Step("[14] PdfExportOps", () =>
        {
            var pngs = PdfExportOps.ExportImages(threePages, F("png"), "png", 50, [1, 3]);
            Check(pngs.Count == 2 && pngs.All(p => IsPng(File.ReadAllBytes(p))), "export: PNG pages wrong");
            var jpgs = PdfExportOps.ExportImages(threePages, F("jpg"), "jpg", 50);
            Check(jpgs.Count == 3 && jpgs.All(p =>
            {
                var b = File.ReadAllBytes(p);
                return b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;
            }), "export: JPEG pages wrong");

            var txt = F("text.txt");
            PdfExportOps.ExportText(threePages, txt);
            var text = File.ReadAllText(txt);
            Check(text.Contains("PAGE-ONE") && text.Contains("PAGE-THREE"), "export text: content missing");

            var optimized = F("optimized.pdf");
            PdfExportOps.Optimize(F("merged.pdf"), optimized);
            Console.WriteLine($"    optimize: {new FileInfo(F("merged.pdf")).Length:N0} -> " +
                              $"{new FileInfo(optimized).Length:N0} bytes");
            Check(PageCount(optimized) == 5 && PdfTextSearch.ExtractText(optimized).Contains("PAGE-TWO"),
                "optimize: output broken");
        });

        Step("[15] SignatureInspector", () =>
        {
            if (!signedOk)
            {
                Check(false, "inspector: signing failed earlier, nothing to inspect");
                return;
            }

            var signatures = SignatureInspector.Read(signedPdf);
            foreach (var s in signatures)
                Console.WriteLine($"    [{s.FieldName}] {s.SignerName} {s.SignDate:yyyy-MM-dd HH:mm} " +
                                  $"p{s.PageNumber} ok={s.IntegrityOk} whole={s.CoversWholeDocument}");
            Check(signatures.Count == expectedSignatures,
                $"inspector: expected {expectedSignatures} signatures, got {signatures.Count}");
            Check(signatures.All(s => s.IntegrityOk && s.SignerName.Contains("KCS Smoke Test Signer")),
                "inspector: integrity or signer wrong");
            Check(signatures.Count(s => s.CoversWholeDocument) == 1,
                "inspector: expected exactly one covering the whole file");
            Check(signatures.All(s => s.PageNumber > 0 && s.Width > 0 && s.SignDate is not null && s.Reason == "Smoke test"),
                "inspector: page, rectangle, date or reason missing");
            Check(SignatureInspector.Read(threePages).Count == 0, "inspector: unsigned file reported signatures");
        });

                Step("[17] PdfTextRegions (links and selection)", () =>
        {
            var linkPdf = F("links.pdf");
            using (var writer = new PdfWriter(linkPdf))
            using (var pdf = new PdfDocument(writer))
            {
                var doc = new Document(pdf, PageSize.A4);
                doc.Add(new Paragraph("Visit https://example.com/path/page, then www.example.org."));
                doc.Add(new Paragraph("Second line without a link."));
                doc.Close();
            }

            var urls = PdfTextRegions.FindUrls(linkPdf);
            Console.WriteLine($"    urls={string.Join(" | ", urls.Select(u => u.Url))}");
            Check(urls.Count == 2, $"links: expected 2 urls, got {urls.Count}");
            Check(urls.Count == 2 && urls[0].Url == "https://example.com/path/page" && urls[0].Width > 0,
                "links: first url wrong or has no box");
            Check(urls.Count == 2 && urls[1].Url == "https://www.example.org", "links: www url not normalised");

            var first = urls[0];
            var chosen = PdfTextRegions.SelectText(linkPdf, 1, first.Left - 2, first.Bottom - 2, first.Width + 4, first.Height + 4);
            Console.WriteLine($"    selected='{chosen.Text}'");
            Check(chosen.Text.Contains("example.com") && !chosen.Text.Contains("Second"), "selection: wrong text");
            Check(chosen.LineBoxes.Count == 1, "selection: expected one line box");
            Check(PdfTextRegions.SelectText(linkPdf, 1, 0, 0, 5, 5).Text.Length == 0, "selection: empty area returned text");
        });

        Step("[16] Bad input", () =>
        {
            Check(Throws(() => PdfInfoReader.Read(F("missing.pdf"))), "missing file did not throw");
            Check(Throws(() => PdfPageOps.RotatePages(threePages, threePages, [1], 90)), "in-place write was allowed");
            Check(Throws(() => PdfPageOps.RotatePages(threePages, F("r.pdf"), [9], 90)), "page out of range accepted");
            Check(!File.Exists(F("r.pdf")), "failed operation left a partial output file");
        });
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
        }

        Console.WriteLine(_failures.Count == before ? "    ok" : $"    {_failures.Count - before} problem(s)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) _failures.Add(message);
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static int PageCount(string path) => PdfInfoReader.Read(path).PageCount;

    private static List<int> Rotations(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        return Enumerable.Range(1, pdf.GetNumberOfPages()).Select(i => pdf.GetPage(i).GetRotation()).ToList();
    }

    /// <summary>The PAGE-xxx marker on each page, or "" for a page without one.</summary>
    private static List<string> PageMarkers(string path)
    {
        return Enumerable.Range(1, PageCount(path)).Select(i =>
        {
            var text = PdfTextSearch.ExtractText(path, i);
            return new[] { "PAGE-ONE", "PAGE-TWO", "PAGE-THREE" }.FirstOrDefault(text.Contains) ?? "";
        }).ToList();
    }

    private static int ImageCount(string path, int page)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var xobjects = pdf.GetPage(page).GetResources().GetResource(PdfName.XObject);
        if (xobjects is null) return 0;
        return xobjects.Values().Count(o => o is PdfStream s && PdfName.Image.Equals(s.GetAsName(PdfName.Subtype)));
    }

    private static bool ContentHas(string path, int page, string token)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        return System.Text.Encoding.Latin1.GetString(pdf.GetPage(page).GetContentBytes()).Contains(token);
    }

    /// <summary>Searches every decoded content stream in the file for a token.</summary>
    private static bool RawBytesContain(string path, string token)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        for (var i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            if (System.Text.Encoding.Latin1.GetString(pdf.GetPage(i).GetContentBytes()).Contains(token))
                return true;
        }

        return false;
    }

    private static bool HasAppearances(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        return pdf.GetPage(1).GetAnnotations()
            .Where(a => !PdfName.Popup.Equals(a.GetSubtype()))
            .All(a => a.GetNormalAppearanceObject() is not null);
    }

    private static void BuildThreePagePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.GetDocumentInfo().SetTitle("Smoke Title").SetAuthor("Smoke Author");

        // immediateFlush off so the pages can still be referenced by the bookmarks below.
        var document = new Document(pdf, PageSize.A4, false);
        document.Add(new Paragraph("PAGE-ONE"));
        document.Add(new Paragraph("Chapter 1 body text stays after redaction."));
        document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
        document.Add(new Paragraph("PAGE-TWO"));
        document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
        document.Add(new Paragraph("PAGE-THREE"));

        var root = pdf.GetOutlines(false);
        var chapter1 = root.AddOutline("Chapter 1");
        chapter1.AddDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(1)));
        var section = chapter1.AddOutline("Section 1.1");
        section.AddDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(2)));
        var chapter3 = root.AddOutline("Chapter 3");
        chapter3.AddDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(3)));

        document.Close();
    }

    private static void BuildFormPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);

        // Not disposed: closing the Document would close the PdfDocument before the fields exist.
        var document = new Document(pdf);
        document.Add(new Paragraph("Form test"));
        document.Flush();

        var form = PdfFormCreator.GetAcroForm(pdf, true);
        var name = new TextFormFieldBuilder(pdf, "FullName")
            .SetWidgetRectangle(new Rectangle(72, 600, 200, 24)).CreateText();
        name.SetValue("");
        form.AddField(name, pdf.GetPage(1));

        var agree = new CheckBoxFormFieldBuilder(pdf, "Agree")
            .SetWidgetRectangle(new Rectangle(72, 560, 16, 16)).CreateCheckBox();
        agree.SetValue("Off");
        form.AddField(agree, pdf.GetPage(1));

        var city = new ChoiceFormFieldBuilder(pdf, "City")
            .SetWidgetRectangle(new Rectangle(72, 520, 150, 20))
            .SetOptions(["Mangaluru", "Udupi", "Bantwal"]).CreateComboBox();
        form.AddField(city, pdf.GetPage(1));
    }
}
