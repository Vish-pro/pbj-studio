using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Signatures;

namespace KcsPdfSigner.Core;

/// <summary>Page geometry the UI needs to render previews and place stamps by hand.</summary>
public sealed record PdfPageInfo(int PageNumber, float Width, float Height, int Rotation);

/// <summary>Dispatches to whichever detection mode was chosen for this run.</summary>
public static class PlacementDetector
{
    public static List<SignaturePlacement> Detect(string pdfPath, DetectionSettings settings)
    {
        using var reader = new PdfReader(pdfPath);
        using var document = new PdfDocument(reader);

        return settings.Mode switch
        {
            DetectionMode.TextAnchors => TextAnchorDetector.Detect(document, settings),
            DetectionMode.ExistingFields => DetectExistingFields(document),
            DetectionMode.FixedPosition => DetectFixedPositions(document, settings),
            DetectionMode.Manual => [],
            _ => []
        };
    }

    public static List<PdfPageInfo> ReadPages(string pdfPath)
    {
        using var reader = new PdfReader(pdfPath);
        using var document = new PdfDocument(reader);

        var pages = new List<PdfPageInfo>();
        for (var i = 1; i <= document.GetNumberOfPages(); i++)
        {
            var size = document.GetPage(i).GetPageSize();
            pages.Add(new PdfPageInfo(i, size.GetWidth(), size.GetHeight(),
                document.GetPage(i).GetRotation()));
        }

        return pages;
    }

    /// <summary>
    /// Uses signature fields the PDF already carries but which nobody has signed yet.
    /// These are the most trustworthy placements available, because the document author
    /// declared exactly where a signature belongs.
    /// </summary>
    private static List<SignaturePlacement> DetectExistingFields(PdfDocument document)
    {
        var placements = new List<SignaturePlacement>();

        var form = PdfFormCreator.GetAcroForm(document, false);
        if (form is null) return placements;

        var blankFieldNames = new SignatureUtil(document).GetBlankSignatureNames();
        if (blankFieldNames.Count == 0) return placements;

        var widgetPages = MapWidgetsToPages(document);

        foreach (var fieldName in blankFieldNames)
        {
            var field = form.GetField(fieldName);
            if (field is null) continue;

            foreach (var widget in field.GetWidgets())
            {
                var reference = widget.GetPdfObject().GetIndirectReference();
                if (reference is null || !widgetPages.TryGetValue(reference, out var pageNumber))
                    continue;

                var rectangle = widget.GetRectangle()?.ToRectangle();
                if (rectangle is null || rectangle.GetWidth() <= 0 || rectangle.GetHeight() <= 0)
                    continue;

                var page = document.GetPage(pageNumber);
                var mediaBox = page.GetPageSize();

                placements.Add(new SignaturePlacement
                {
                    PageNumber = pageNumber,
                    Left = rectangle.GetLeft(),
                    Bottom = rectangle.GetBottom(),
                    Width = rectangle.GetWidth(),
                    Height = rectangle.GetHeight(),
                    Source = PlacementSource.ExistingField,
                    Label = fieldName,
                    ExistingFieldName = fieldName,
                    PageWidth = mediaBox.GetWidth(),
                    PageHeight = mediaBox.GetHeight(),
                    Rotation = page.GetRotation()
                });
            }
        }

        return placements.OrderBy(p => p.PageNumber).ThenByDescending(p => p.Bottom).ToList();
    }

    private static Dictionary<PdfIndirectReference, int> MapWidgetsToPages(PdfDocument document)
    {
        var map = new Dictionary<PdfIndirectReference, int>();

        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            foreach (var annotation in document.GetPage(pageNumber).GetAnnotations())
            {
                if (annotation is not PdfWidgetAnnotation) continue;

                var reference = annotation.GetPdfObject().GetIndirectReference();
                if (reference is not null) map[reference] = pageNumber;
            }
        }

        return map;
    }

    private static List<SignaturePlacement> DetectFixedPositions(
        PdfDocument document, DetectionSettings settings)
    {
        var pageCount = document.GetNumberOfPages();

        var targetPages = settings.FixedScope switch
        {
            PageScope.FirstPageOnly => [1],
            PageScope.LastPageOnly => new[] { pageCount },
            _ => Enumerable.Range(1, pageCount).ToArray()
        };

        var placements = new List<SignaturePlacement>();

        foreach (var pageNumber in targetPages)
        {
            var page = document.GetPage(pageNumber);
            var mediaBox = page.GetPageSize();

            placements.Add(BuildFixedPlacement(mediaBox, page.GetRotation(), pageNumber, settings));
        }

        return placements;
    }

    private static SignaturePlacement BuildFixedPlacement(
        Rectangle mediaBox, int rotation, int pageNumber, DetectionSettings settings)
    {
        var width = settings.StampWidth;
        var height = settings.StampHeight;
        var margin = settings.StampMargin;

        var left = settings.FixedCorner is StampCorner.BottomLeft or StampCorner.TopLeft
            ? mediaBox.GetLeft() + margin
            : mediaBox.GetRight() - margin - width;

        var bottom = settings.FixedCorner is StampCorner.BottomLeft or StampCorner.BottomRight
            ? mediaBox.GetBottom() + margin
            : mediaBox.GetTop() - margin - height;

        var placement = new SignaturePlacement
        {
            PageNumber = pageNumber,
            Left = left,
            Bottom = bottom,
            Width = width,
            Height = height,
            Source = PlacementSource.FixedPosition,
            Label = settings.FixedCorner.ToString(),
            PageWidth = mediaBox.GetWidth(),
            PageHeight = mediaBox.GetHeight(),
            Rotation = rotation
        };

        placement.ClampToPage();
        return placement;
    }

    /// <summary>
    /// Builds a placement centred on a point the user clicked in the page preview.
    /// </summary>
    public static SignaturePlacement CreateManual(
        PdfPageInfo page, float centreX, float centreY, DetectionSettings settings)
    {
        var placement = new SignaturePlacement
        {
            PageNumber = page.PageNumber,
            Left = centreX - settings.StampWidth / 2f,
            Bottom = centreY - settings.StampHeight / 2f,
            Width = settings.StampWidth,
            Height = settings.StampHeight,
            Source = PlacementSource.Manual,
            Label = "Placed by hand",
            PageWidth = page.Width,
            PageHeight = page.Height,
            Rotation = page.Rotation
        };

        placement.ClampToPage();
        return placement;
    }
}
