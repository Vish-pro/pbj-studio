using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Layout.Element;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using SkiaSharp;
using Path = System.IO.Path;
using Rectangle = iText.Kernel.Geom.Rectangle;

/// <summary>
/// Checks 30 and up: printing puts on paper what Acrobat puts on paper, and page tools keep the
/// parts of a document Acrobat keeps.
/// </summary>
internal static class PrintAndKeepChecks
{
    private static List<string> _failures = [];

    public static void Run(string workFolder, List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "print-keep");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        Step("[30] Printing follows each annotation's Print / Hidden / NoView flags", () =>
        {
            var blank = F("blank.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(blank)))
            {
                var document = new Document(pdf);
                document.Add(new Paragraph(" "));
                document.Close();
            }

            // Four boxes with appearances: screen only, print only, hidden, normal.
            var boxes = new (string Name, float Left, float Bottom, int Flags, bool OnPaper)[]
            {
                ("screen-only", 72, 600, 0, false),
                ("print-only", 72, 450, PdfAnnotation.PRINT | PdfAnnotation.NO_VIEW, true),
                ("hidden", 72, 300, PdfAnnotation.PRINT | PdfAnnotation.HIDDEN, false),
                ("normal", 300, 600, PdfAnnotation.PRINT, true)
            };

            var drawn = F("boxes.pdf");
            PdfAnnotationOps.Add(blank, drawn, boxes.Select(b => new AnnotationSpec
            {
                Kind = AnnotationKind.Rectangle, PageNumber = 1, Left = b.Left, Bottom = b.Bottom, Width = 120, Height = 80,
                LineWidth = 6, Text = b.Name
            }));

            var flagged = F("flagged.pdf");
            using (var pdf = new PdfDocument(new PdfReader(drawn), new PdfWriter(flagged)))
            {
                var annotations = pdf.GetPage(1).GetAnnotations().Where(a => PdfName.Square.Equals(a.GetSubtype())).ToList();
                for (var i = 0; i < boxes.Length; i++) annotations[i].SetFlags(boxes[i].Flags);
            }

            var printable = PdfPrintPrep.ForPrinting(File.ReadAllBytes(flagged), null);
            var png = PdfPageRasterizer.RenderPng(printable, 1, 72);
            using var bitmap = SKBitmap.Decode(png);

            foreach (var box in boxes)
            {
                var ink = Ink(bitmap, box.Left, box.Bottom, 120, 80, 842);
                Console.WriteLine($"    {box.Name,-12} /F {box.Flags,-2} -> {(ink > 0 ? "on paper" : "not printed")} ({ink} dark px)");
                Check((ink > 0) == box.OnPaper, $"print: '{box.Name}' should {(box.OnPaper ? "" : "not ")}be printed");
            }

            // A signature box still prints, and a password-protected file can be prepared.
            var signed = Path.Combine(workFolder, "signed.pdf");
            var signedPrint = PdfPageRasterizer.RenderPng(PdfPrintPrep.ForPrinting(File.ReadAllBytes(signed), null), 1, 50);
            var unsignedPrint = PdfPageRasterizer.RenderPng(File.ReadAllBytes(Path.Combine(workFolder, "unsigned.pdf")), 1, 50);
            Check(!signedPrint.SequenceEqual(unsignedPrint), "print: signatures missing from the printed page");

            var locked = F("locked.pdf");
            PdfSecurityOps.Encrypt(flagged, locked, "open", "owner", true, false, false);
            Check(PdfPrintPrep.ForPrinting(File.ReadAllBytes(locked), "open").Length > 0, "print: password-protected file could not be prepared");
        });

        Step("[31] Multiple PDFs and Extract keep bookmarks and links between the pages taken", () =>
        {
            var source = F("linked.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(source)))
            {
                var document = new Document(pdf, iText.Kernel.Geom.PageSize.A4, false);
                document.Add(new Paragraph("CONTENTS"));
                document.Add(new AreaBreak(iText.Layout.Properties.AreaBreakType.NEXT_PAGE));
                document.Add(new Paragraph("MIDDLE"));
                document.Add(new AreaBreak(iText.Layout.Properties.AreaBreakType.NEXT_PAGE));
                document.Add(new Paragraph("ANNEXURE"));
                var outline = pdf.GetOutlines(false);
                outline.AddOutline("Contents").AddDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(1)));
                outline.AddOutline("Annexure").AddDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(3)));
                pdf.GetPage(1).AddAnnotation(new PdfLinkAnnotation(new Rectangle(72, 700, 120, 20))
                    .SetDestination(PdfExplicitDestination.CreateFit(pdf.GetPage(3))));
                document.Close();
            }

            var built = F("built.pdf");
            PdfPageOps.BuildFromSelection([new(source, 3), new(source, 1), new(source, 2, 90), new(source, 1)], built);
            var (links, linkTargets, bookmarks) = Navigation(built);
            Console.WriteLine($"    built: links on page 2 -> pages [{string.Join(",", linkTargets)}], bookmarks {bookmarks}");
            Check(PdfTextSearch.ExtractText(built, 1).Contains("ANNEXURE") && PdfTextSearch.ExtractText(built, 2).Contains("CONTENTS"),
                "build: pages not in the order picked");
            Check(links >= 1 && linkTargets.Contains(1), "build: the contents link to the annexure was lost or points elsewhere");
            Check(bookmarks == 2, $"build: expected 2 bookmarks, found {bookmarks}");
            Check(PdfInfoReader.ReadOutline(built).Any(o => o.Title == "Annexure" && o.PageNumber == 1),
                "build: 'Annexure' bookmark does not lead to its page");

            var extracted = F("extracted.pdf");
            PdfPageOps.ExtractPages(source, extracted, [1, 3]);
            var outline2 = PdfInfoReader.ReadOutline(extracted);
            Check(outline2.Count == 2 && outline2[1].PageNumber == 2, "extract: bookmarks not kept");
        });
    }

    private static (int Links, List<int> Targets, int Bookmarks) Navigation(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var targets = new List<int>();
        var links = 0;
        foreach (var link in pdf.GetPage(2).GetAnnotations().OfType<PdfLinkAnnotation>())
        {
            links++;
            if (link.GetDestinationObject() is PdfArray array && array.Get(0) is PdfDictionary page)
                targets.Add(pdf.GetPageNumber(page));
        }

        var bookmarks = pdf.GetOutlines(false)?.GetAllChildren().Count ?? 0;
        return (links, targets, bookmarks);
    }

    /// <summary>Dark pixels inside a PDF rectangle of a page rendered at 72 dpi.</summary>
    private static int Ink(SKBitmap bitmap, float left, float bottom, float width, float height, float pageHeight)
    {
        var count = 0;
        var top = (int)(pageHeight - bottom - height);
        for (var y = Math.Max(0, top); y < Math.Min(bitmap.Height, top + (int)height); y++)
        for (var x = Math.Max(0, (int)left); x < Math.Min(bitmap.Width, (int)(left + width)); x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue < 300) count++;
        }

        return count;
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
}
