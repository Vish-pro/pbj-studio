using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Signatures;

namespace KcsPdfSigner.Core;

/// <summary>
/// Page geometry the UI needs to render previews and place stamps by hand: the visible page area
/// (crop box) in unrotated user space, whose lower-left corner is (Left, Bottom), and the page's /Rotate.
/// </summary>
public sealed record PdfPageInfo(int PageNumber, float Width, float Height, int Rotation, float Left = 0f, float Bottom = 0f);

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
            pages.Add(PageGeometry.Describe(document.GetPage(i), i));
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

                var info = PageGeometry.Describe(document.GetPage(pageNumber), pageNumber);

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
                    PageWidth = info.Width,
                    PageHeight = info.Height,
                    PageLeft = info.Left,
                    PageBottom = info.Bottom,
                    Rotation = info.Rotation
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
            var page = PageGeometry.Describe(document.GetPage(pageNumber), pageNumber);
            placements.Add(BuildFixedPlacement(page, settings));
        }

        return placements;
    }

    /// <summary>
    /// The chosen corner of the page as the reader sees it (visible area, after rotation), with the
    /// stamp upright and the configured width across the page.
    /// </summary>
    private static SignaturePlacement BuildFixedPlacement(PdfPageInfo page, DetectionSettings settings)
    {
        var width = settings.StampWidth;
        var height = settings.StampHeight;
        var margin = settings.StampMargin;
        var (displayWidth, displayHeight) = PageGeometry.DisplaySize(page);

        var left = settings.FixedCorner is StampCorner.BottomLeft or StampCorner.TopLeft
            ? margin
            : displayWidth - margin - width;

        var bottom = settings.FixedCorner is StampCorner.BottomLeft or StampCorner.BottomRight
            ? margin
            : displayHeight - margin - height;

        var placement = FromDisplay(page, left, bottom, width, height, PlacementSource.FixedPosition,
            settings.FixedCorner.ToString());

        placement.ClampToPage();
        return placement;
    }

    /// <summary>Builds a placement from a rectangle measured on the page as displayed.</summary>
    internal static SignaturePlacement FromDisplay(
        PdfPageInfo page, float left, float bottom, float width, float height, PlacementSource source, string label)
    {
        var user = PageGeometry.DisplayToUser(page, left, bottom, width, height);
        return new SignaturePlacement
        {
            PageNumber = page.PageNumber,
            Left = user.GetLeft(),
            Bottom = user.GetBottom(),
            Width = user.GetWidth(),
            Height = user.GetHeight(),
            Source = source,
            Label = label,
            PageWidth = page.Width,
            PageHeight = page.Height,
            PageLeft = page.Left,
            PageBottom = page.Bottom,
            Rotation = page.Rotation
        };
    }

    /// <summary>
    /// Builds a placement centred on a point the user clicked in the page preview.
    /// </summary>
    public static SignaturePlacement CreateManual(
        PdfPageInfo page, float centreX, float centreY, DetectionSettings settings)
    {
        // The stamp is upright for the reader, so on a page turned 90 or 270 degrees its width runs
        // along the page's user-space y axis.
        var sideways = PageGeometry.Normalise(page.Rotation) is 90 or 270;
        var width = sideways ? settings.StampHeight : settings.StampWidth;
        var height = sideways ? settings.StampWidth : settings.StampHeight;

        var placement = new SignaturePlacement
        {
            PageNumber = page.PageNumber,
            Left = centreX - width / 2f,
            Bottom = centreY - height / 2f,
            Width = width,
            Height = height,
            Source = PlacementSource.Manual,
            Label = "Placed by hand",
            PageWidth = page.Width,
            PageHeight = page.Height,
            PageLeft = page.Left,
            PageBottom = page.Bottom,
            Rotation = page.Rotation
        };

        placement.ClampToPage();
        return placement;
    }
}
