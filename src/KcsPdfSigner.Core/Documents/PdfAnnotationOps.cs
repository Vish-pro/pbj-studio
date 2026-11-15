using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Utils;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Comment types. Other is reported by List for annotation types this app does not create.</summary>
public enum AnnotationKind
{
    Highlight,
    Underline,
    StrikeOut,
    Note,
    FreeText,
    Rectangle,
    Ellipse,
    Line,
    Ink,
    Stamp,
    Other
}

/// <summary>A point in unrotated PDF points.</summary>
public readonly record struct AnnotationPoint(float X, float Y);

/// <summary>
/// Describes one comment to add. Coordinates are unrotated PDF points. For Line, the line runs
/// from (Left, Bottom) to (Left + Width, Bottom + Height); width and height may be negative.
/// For Ink, InkPoints holds one list per stroke and the rectangle is ignored. For Note, a zero
/// size gives a 20 x 20 icon. Colour components are 0..1; when left null the colour is yellow
/// for Highlight and black for everything else.
/// </summary>
public sealed class AnnotationSpec
{
    public AnnotationKind Kind { get; set; }
    public int PageNumber { get; set; } = 1;
    public float Left { get; set; }
    public float Bottom { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public List<List<AnnotationPoint>> InkPoints { get; set; } = [];
    public float? ColorR { get; set; }
    public float? ColorG { get; set; }
    public float? ColorB { get; set; }
    public float FontSize { get; set; } = 12f;
    public float LineWidth { get; set; } = 1.5f;
}

/// <summary>An existing comment. IndexOnPage is its position in the page's /Annots array.</summary>
public sealed class AnnotationInfo
{
    public int PageNumber { get; init; }
    public int IndexOnPage { get; init; }
    public AnnotationKind Kind { get; init; }
    public string Subtype { get; init; } = "";
    public string Author { get; init; } = "";
    public string Text { get; init; } = "";
    public float Left { get; init; }
    public float Bottom { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

/// <summary>Adds, lists, removes and flattens comments. Every added comment gets its own appearance stream.</summary>
public static class PdfAnnotationOps
{
    private static readonly HashSet<PdfName> NotComments =
        [PdfName.Widget, PdfName.Link, PdfName.Popup];

    public static void Add(string input, string output, IEnumerable<AnnotationSpec> annotations, string? password = null)
    {
        var specs = annotations.ToList();
        if (specs.Count == 0) throw new ArgumentException("No comments to add.", nameof(annotations));

        PdfIo.Edit(input, output, password, document =>
        {
            foreach (var spec in specs)
            {
                PdfIo.RequirePage(document, spec.PageNumber);
                var page = document.GetPage(spec.PageNumber);
                var annotation = Build(document, spec);

                annotation.SetFlags(PdfAnnotation.PRINT);
                annotation.SetDate(new PdfDate().GetPdfObject());
                annotation.SetName(new PdfString(Guid.NewGuid().ToString("N")));
                if (!string.IsNullOrEmpty(spec.Text)) annotation.SetContents(spec.Text);
                if (annotation is PdfMarkupAnnotation markup && !string.IsNullOrEmpty(spec.Author))
                    markup.SetTitle(new PdfString(spec.Author));

                page.AddAnnotation(annotation);
            }
        });
    }

    public static List<AnnotationInfo> List(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var result = new List<AnnotationInfo>();

        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            var annotations = document.GetPage(pageNumber).GetAnnotations();
            for (var index = 0; index < annotations.Count; index++)
            {
                var annotation = annotations[index];
                var subtype = annotation.GetSubtype();
                if (subtype is null || NotComments.Contains(subtype)) continue;

                var rect = annotation.GetRectangle()?.ToRectangle() ?? new Rectangle(0, 0, 0, 0);
                result.Add(new AnnotationInfo
                {
                    PageNumber = pageNumber,
                    IndexOnPage = index,
                    Kind = KindOf(subtype),
                    Subtype = subtype.GetValue(),
                    Author = (annotation as PdfMarkupAnnotation)?.GetTitle()?.ToUnicodeString() ?? "",
                    Text = annotation.GetContents()?.ToUnicodeString() ?? "",
                    Left = rect.GetLeft(),
                    Bottom = rect.GetBottom(),
                    Width = rect.GetWidth(),
                    Height = rect.GetHeight()
                });
            }
        }

        return result;
    }

    public static void Remove(string input, string output, int pageNumber, int indexOnPage, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            PdfIo.RequirePage(document, pageNumber);
            var page = document.GetPage(pageNumber);
            var annotations = page.GetAnnotations();

            if (indexOnPage < 0 || indexOnPage >= annotations.Count)
                throw new ArgumentOutOfRangeException(nameof(indexOnPage), $"Page {pageNumber} has no annotation at index {indexOnPage}.");

            var annotation = annotations[indexOnPage];
            if (annotation.GetSubtype() is { } subtype && NotComments.Contains(subtype))
                throw new InvalidOperationException("That annotation is a form field, link or popup, not a comment.");

            page.RemoveAnnotation(annotation);
        });
    }

    /// <summary>Burns every comment into the page content. Form fields and links are left alone.</summary>
    public static void Flatten(string input, string output, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var targets = new List<PdfAnnotation>();
            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                targets.AddRange(document.GetPage(i).GetAnnotations()
                    .Where(a => a.GetSubtype() is { } s && !NotComments.Contains(s)));
            }

            if (targets.Count > 0) new PdfAnnotationFlattener().Flatten(targets);

            // Popups belong to the comments that were just flattened.
            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                foreach (var popup in page.GetAnnotations().Where(a => PdfName.Popup.Equals(a.GetSubtype())).ToList())
                    page.RemoveAnnotation(popup);
            }
        });
    }

    private static AnnotationKind KindOf(PdfName subtype)
    {
        if (PdfName.Highlight.Equals(subtype)) return AnnotationKind.Highlight;
        if (PdfName.Underline.Equals(subtype)) return AnnotationKind.Underline;
        if (PdfName.StrikeOut.Equals(subtype)) return AnnotationKind.StrikeOut;
        if (PdfName.Text.Equals(subtype)) return AnnotationKind.Note;
        if (PdfName.FreeText.Equals(subtype)) return AnnotationKind.FreeText;
        if (PdfName.Square.Equals(subtype)) return AnnotationKind.Rectangle;
        if (PdfName.Circle.Equals(subtype)) return AnnotationKind.Ellipse;
        if (PdfName.Line.Equals(subtype)) return AnnotationKind.Line;
        if (PdfName.Ink.Equals(subtype)) return AnnotationKind.Ink;
        if (PdfName.Stamp.Equals(subtype)) return AnnotationKind.Stamp;
        return AnnotationKind.Other;
    }

    private static DeviceRgb ColorOf(AnnotationSpec spec)
    {
        var highlight = spec.Kind == AnnotationKind.Highlight;
        return new DeviceRgb(
            Clamp(spec.ColorR ?? (highlight ? 1f : 0f)),
            Clamp(spec.ColorG ?? (highlight ? 1f : 0f)),
            Clamp(spec.ColorB ?? 0f));

        static float Clamp(float v) => Math.Clamp(v, 0f, 1f);
    }

    private static PdfAnnotation Build(PdfDocument document, AnnotationSpec spec)
    {
        var color = ColorOf(spec);
        var lineWidth = MathF.Max(spec.LineWidth, 0.25f);

        switch (spec.Kind)
        {
            case AnnotationKind.Highlight:
            case AnnotationKind.Underline:
            case AnnotationKind.StrikeOut:
            {
                var rect = RequireArea(spec);
                var quad = new float[]
                {
                    rect.GetLeft(), rect.GetTop(), rect.GetRight(), rect.GetTop(),
                    rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetBottom()
                };
                var annotation = spec.Kind switch
                {
                    AnnotationKind.Highlight => PdfTextMarkupAnnotation.CreateHighLight(rect, quad),
                    AnnotationKind.Underline => PdfTextMarkupAnnotation.CreateUnderline(rect, quad),
                    _ => PdfTextMarkupAnnotation.CreateStrikeout(rect, quad)
                };
                annotation.SetColor(color);
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    if (spec.Kind == AnnotationKind.Highlight)
                    {
                        canvas.SetExtGState(new PdfExtGState().SetBlendMode(PdfExtGState.BM_MULTIPLY));
                        canvas.SetFillColor(color).Rectangle(0, 0, w, h).Fill();
                    }
                    else
                    {
                        var thickness = MathF.Max(h / 14f, 0.75f);
                        var y = spec.Kind == AnnotationKind.Underline ? thickness : h / 2f;
                        canvas.SetStrokeColor(color).SetLineWidth(thickness).MoveTo(0, y).LineTo(w, y).Stroke();
                    }
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.Note:
            {
                var size = spec.Width > 0 && spec.Height > 0 ? 0f : 20f;
                var rect = size > 0
                    ? new Rectangle(spec.Left, spec.Bottom, size, size)
                    : PdfIo.Normalize(spec.Left, spec.Bottom, spec.Width, spec.Height);
                var annotation = new PdfTextAnnotation(rect);
                annotation.SetIconName(new PdfName("Comment"));
                annotation.SetOpen(false);
                annotation.SetColor(color);
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    canvas.SetFillColor(ColorConstants.WHITE).SetStrokeColor(color).SetLineWidth(1)
                        .Rectangle(0.5, h * 0.25, w - 1, h * 0.75 - 0.5).FillStroke();
                    canvas.MoveTo(w * 0.25, h * 0.25).LineTo(w * 0.2, 0.5).LineTo(w * 0.45, h * 0.25).Stroke();
                    for (var i = 1; i <= 3; i++)
                    {
                        var y = h * 0.25 + i * (h * 0.75) / 4.5;
                        canvas.MoveTo(w * 0.2, y).LineTo(w * 0.8, y).Stroke();
                    }
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.FreeText:
            {
                var rect = RequireArea(spec);
                var annotation = new PdfFreeTextAnnotation(rect, new PdfString(spec.Text));
                annotation.SetDefaultAppearance(new PdfString(
                    $"/Helv {spec.FontSize:0.##} Tf {color.GetColorValue()[0]:0.###} {color.GetColorValue()[1]:0.###} {color.GetColorValue()[2]:0.###} rg"));
                annotation.SetColor(color);
                annotation.Put(PdfName.BS, BorderStyle(0));
                var font = PdfIo.CreateFont(document);
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) => { }, xobject =>
                {
                    using var layout = new Canvas(xobject, document);
                    layout.Add(new Paragraph(spec.Text)
                        .SetFont(font).SetFontSize(spec.FontSize).SetFontColor(color)
                        .SetMultipliedLeading(1.1f)
                        .SetFixedPosition(2, 0, rect.GetWidth() - 4)
                        .SetHeight(rect.GetHeight() - 2)
                        .SetVerticalAlignment(VerticalAlignment.TOP));
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.Rectangle:
            case AnnotationKind.Ellipse:
            {
                var rect = RequireArea(spec);
                PdfMarkupAnnotation annotation = spec.Kind == AnnotationKind.Rectangle
                    ? new PdfSquareAnnotation(rect)
                    : new PdfCircleAnnotation(rect);
                annotation.SetColor(color);
                annotation.Put(PdfName.BS, BorderStyle(lineWidth));
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    var inset = lineWidth / 2f;
                    canvas.SetStrokeColor(color).SetLineWidth(lineWidth);
                    if (spec.Kind == AnnotationKind.Rectangle)
                        canvas.Rectangle(inset, inset, w - lineWidth, h - lineWidth);
                    else
                        canvas.Ellipse(inset, inset, w - inset, h - inset);
                    canvas.Stroke();
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.Line:
            {
                float x1 = spec.Left, y1 = spec.Bottom, x2 = spec.Left + spec.Width, y2 = spec.Bottom + spec.Height;
                if (MathF.Abs(spec.Width) < 0.01f && MathF.Abs(spec.Height) < 0.01f)
                    throw new ArgumentException("A line needs two different end points.");
                var pad = lineWidth * 2;
                var rect = PdfIo.Normalize(spec.Left, spec.Bottom, spec.Width, spec.Height);
                rect = new Rectangle(rect.GetLeft() - pad, rect.GetBottom() - pad, rect.GetWidth() + 2 * pad, rect.GetHeight() + 2 * pad);
                var annotation = new PdfLineAnnotation(rect, new[] { x1, y1, x2, y2 });
                annotation.SetColor(color);
                annotation.Put(PdfName.BS, BorderStyle(lineWidth));
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    canvas.SetStrokeColor(color).SetLineWidth(lineWidth).SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND)
                        .MoveTo(x1 - rect.GetLeft(), y1 - rect.GetBottom())
                        .LineTo(x2 - rect.GetLeft(), y2 - rect.GetBottom()).Stroke();
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.Ink:
            {
                var strokes = spec.InkPoints.Where(s => s.Count > 0).ToList();
                if (strokes.Count == 0) throw new ArgumentException("An ink comment needs at least one stroke.");
                var pad = lineWidth * 2;
                var all = strokes.SelectMany(s => s).ToList();
                var minX = all.Min(p => p.X) - pad;
                var minY = all.Min(p => p.Y) - pad;
                var rect = new Rectangle(minX, minY, all.Max(p => p.X) + pad - minX, all.Max(p => p.Y) + pad - minY);

                var inkList = new PdfArray();
                foreach (var stroke in strokes)
                    inkList.Add(new PdfArray(stroke.SelectMany(p => new[] { p.X, p.Y }).ToArray()));

                var annotation = new PdfInkAnnotation(rect, inkList);
                annotation.SetColor(color);
                annotation.Put(PdfName.BS, BorderStyle(lineWidth));
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    canvas.SetStrokeColor(color).SetLineWidth(lineWidth)
                        .SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND)
                        .SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
                    foreach (var stroke in strokes)
                    {
                        canvas.MoveTo(stroke[0].X - rect.GetLeft(), stroke[0].Y - rect.GetBottom());
                        if (stroke.Count == 1)
                            canvas.LineTo(stroke[0].X - rect.GetLeft() + 0.01, stroke[0].Y - rect.GetBottom());
                        foreach (var point in stroke.Skip(1))
                            canvas.LineTo(point.X - rect.GetLeft(), point.Y - rect.GetBottom());
                    }
                    canvas.Stroke();
                }).GetPdfObject());
                return annotation;
            }

            case AnnotationKind.Stamp:
            {
                var rect = RequireArea(spec);
                var label = string.IsNullOrWhiteSpace(spec.Text) ? "APPROVED" : spec.Text.Trim();
                var annotation = new PdfStampAnnotation(rect);
                annotation.SetStampName(new PdfName("Draft"));
                annotation.SetColor(color);
                var font = PdfIo.CreateFont(document, bold: true);
                annotation.SetNormalAppearance(Appearance(document, rect, (canvas, w, h) =>
                {
                    var border = MathF.Max(MathF.Min(w, h) / 20f, 1f);
                    canvas.SetStrokeColor(color).SetLineWidth(border)
                        .RoundRectangle(border, border, w - 2 * border, h - 2 * border, MathF.Min(w, h) / 8f).Stroke();

                    var size = MathF.Min(h * 0.5f, (w - 6 * border) / MathF.Max(font.GetWidth(label, 1f), 0.01f));
                    size = MathF.Max(size, 4f);
                    var textWidth = font.GetWidth(label, size);
                    canvas.BeginText().SetFontAndSize(font, size).SetFillColor(color)
                        .MoveText((w - textWidth) / 2f, (h - size * 0.7f) / 2f).ShowText(label).EndText();
                }).GetPdfObject());
                return annotation;
            }

            default:
                throw new ArgumentException($"Cannot add a comment of kind {spec.Kind}.");
        }

    }

    private static Rectangle RequireArea(AnnotationSpec spec)
    {
        var rect = PdfIo.Normalize(spec.Left, spec.Bottom, spec.Width, spec.Height);
        if (rect.GetWidth() < 0.5f || rect.GetHeight() < 0.5f)
            throw new ArgumentException($"A {spec.Kind} comment needs an area (width and height).");
        return rect;
    }

    private static PdfDictionary BorderStyle(float width)
    {
        var style = new PdfDictionary();
        style.Put(PdfName.W, new PdfNumber(width));
        style.Put(PdfName.S, PdfName.S);
        return style;
    }

    /// <summary>
    /// Builds a normal-appearance form XObject whose BBox is (0, 0, w, h); the annotation's /Rect
    /// maps onto it, so drawing is done in coordinates local to the annotation.
    /// </summary>
    private static PdfFormXObject Appearance(
        PdfDocument document, Rectangle rect, Action<PdfCanvas, float, float> draw,
        Action<PdfFormXObject>? layout = null)
    {
        var xobject = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
        var canvas = new PdfCanvas(xobject, document);
        canvas.SaveState();
        draw(canvas, rect.GetWidth(), rect.GetHeight());
        canvas.RestoreState();
        layout?.Invoke(xobject);
        return xobject;
    }
}
