using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.PdfCleanup;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>An area to redact, in unrotated PDF points.</summary>
public sealed record RedactArea(int PageNumber, float Left, float Bottom, float Width, float Height);

/// <summary>
/// Adds new content on top of pages (text, images, watermark, header and footer) and redacts.
/// Positions are unrotated PDF points; text and images are drawn upright as the page is viewed,
/// so they read correctly on rotated pages too.
/// </summary>
public static class PdfContentOps
{
    /// <summary>Writes text with its first baseline starting at (x, y). "\n" starts a new line.</summary>
    public static void AddText(
        string input, string output, int page, float x, float y, string text, float fontSize = 12f,
        string? password = null)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("No text given.", nameof(text));
        if (fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));

        PdfIo.Edit(input, output, password, document =>
        {
            PdfIo.RequirePage(document, page);
            var pdfPage = document.GetPage(page);
            var font = PdfIo.CreateFont(document);
            var canvas = new PdfCanvas(pdfPage, true);
            var angle = PdfIo.NormalizeRotation(pdfPage.GetRotation()) * MathF.PI / 180f;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            canvas.SaveState().SetFillColor(ColorConstants.BLACK);
            for (var i = 0; i < lines.Length; i++)
            {
                // Each following line moves "down" as the page is viewed.
                var down = i * fontSize * 1.2f;
                var lx = x + down * MathF.Sin(angle);
                var ly = y - down * MathF.Cos(angle);
                DrawText(canvas, font, fontSize, lines[i], angle, lx, ly);
            }
            canvas.RestoreState();
        });
    }

    /// <summary>Draws an image stretched to fill the rectangle (upright as the page is viewed).</summary>
    public static void AddImage(
        string input, string output, int page, string imagePath, float left, float bottom, float width,
        float height, string? password = null)
    {
        PdfIo.RequireFile(imagePath);
        if (width <= 0 || height <= 0) throw new ArgumentException("Image width and height must be positive.");

        ImageData data;
        try
        {
            data = ImageDataFactory.Create(imagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"'{Path.GetFileName(imagePath)}' is not a supported image: {ex.Message}", ex);
        }

        PdfIo.Edit(input, output, password, document =>
        {
            PdfIo.RequirePage(document, page);
            var pdfPage = document.GetPage(page);
            var canvas = new PdfCanvas(pdfPage, true);

            // Image space unit square -> user space: origin + u * widthVector + v * heightVector.
            var (a, b, c, d, e, f) = PdfIo.NormalizeRotation(pdfPage.GetRotation()) switch
            {
                90 => (0f, height, -width, 0f, left + width, bottom),
                180 => (-width, 0f, 0f, -height, left + width, bottom + height),
                270 => (0f, -height, width, 0f, left, bottom + height),
                _ => (width, 0f, 0f, height, left, bottom)
            };

            canvas.SaveState();
            canvas.AddImageWithTransformationMatrix(data, a, b, c, d, e, f);
            canvas.RestoreState();
        });
    }

    /// <summary>
    /// Centred text on every page, over the content. Opacity 0..1; rotation is counter-clockwise
    /// as the page is viewed (45 gives the usual diagonal).
    /// </summary>
    public static void AddWatermark(
        string input, string output, string text, float opacity = 0.3f, float rotationDegrees = 45f,
        float fontSize = 60f, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("No watermark text given.", nameof(text));
        if (fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        opacity = Math.Clamp(opacity, 0f, 1f);

        PdfIo.Edit(input, output, password, document =>
        {
            var font = PdfIo.CreateFont(document, bold: true);
            var state = new PdfExtGState().SetFillOpacity(opacity).SetStrokeOpacity(opacity);
            var textWidth = font.GetWidth(text, fontSize);

            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                var (w, h) = PdfIo.DisplaySize(page);
                var (cx, cy) = PdfIo.DisplayToUser(page, w / 2f, h / 2f);
                var angle = (PdfIo.NormalizeRotation(page.GetRotation()) + rotationDegrees) * MathF.PI / 180f;

                // Start point = centre minus half the text run along the baseline direction,
                // and minus a third of the font size across it to centre the glyphs vertically.
                var cos = MathF.Cos(angle);
                var sin = MathF.Sin(angle);
                var ox = -textWidth / 2f;
                var oy = -fontSize * 0.35f;
                var sx = cx + ox * cos - oy * sin;
                var sy = cy + ox * sin + oy * cos;

                var canvas = new PdfCanvas(page, true);
                canvas.SaveState().SetExtGState(state).SetFillColor(new DeviceGray(0.5f));
                DrawText(canvas, font, fontSize, text, angle, sx, sy);
                canvas.RestoreState();
            }
        });
    }

    /// <summary>
    /// Header centred at the top, footer centred at the bottom of every page. Both may contain
    /// {page} and {pages}. pageNumbers adds "Page n of N" at the bottom right.
    /// </summary>
    public static void AddHeaderFooter(
        string input, string output, string? headerText, string? footerText, bool pageNumbers,
        string? password = null, float fontSize = 10f, float margin = 24f)
    {
        if (string.IsNullOrEmpty(headerText) && string.IsNullOrEmpty(footerText) && !pageNumbers)
            throw new ArgumentException("Nothing to add: give a header, a footer or page numbers.");

        PdfIo.Edit(input, output, password, document =>
        {
            var font = PdfIo.CreateFont(document);
            var total = document.GetNumberOfPages();

            for (var i = 1; i <= total; i++)
            {
                var page = document.GetPage(i);
                var (w, h) = PdfIo.DisplaySize(page);
                var angle = PdfIo.NormalizeRotation(page.GetRotation()) * MathF.PI / 180f;
                var canvas = new PdfCanvas(page, true);
                canvas.SaveState().SetFillColor(ColorConstants.BLACK);

                void Place(string raw, float displayX, float displayY, float alignFraction)
                {
                    var line = raw.Replace("{page}", i.ToString()).Replace("{pages}", total.ToString());
                    var startX = displayX - font.GetWidth(line, fontSize) * alignFraction;
                    var (ux, uy) = PdfIo.DisplayToUser(page, startX, displayY);
                    DrawText(canvas, font, fontSize, line, angle, ux, uy);
                }

                if (!string.IsNullOrEmpty(headerText)) Place(headerText, w / 2f, h - margin, 0.5f);
                if (!string.IsNullOrEmpty(footerText)) Place(footerText, w / 2f, margin - fontSize * 0.3f, 0.5f);
                if (pageNumbers) Place($"Page {i} of {total}", w - margin, margin - fontSize * 0.3f, 1f);

                canvas.RestoreState();
            }
        });
    }

    /// <summary>
    /// Permanently removes all text, image and vector content inside the areas (iText pdfSweep),
    /// then paints them black. Annotations overlapping the areas are removed too.
    /// </summary>
    public static void Redact(string input, string output, IEnumerable<RedactArea> areas, string? password = null)
    {
        var list = areas.ToList();
        if (list.Count == 0) throw new ArgumentException("No areas to redact.", nameof(areas));

        PdfIo.Edit(input, output, password, document =>
        {
            var locations = new List<PdfCleanUpLocation>();
            foreach (var area in list)
            {
                PdfIo.RequirePage(document, area.PageNumber);
                var rect = PdfIo.Normalize(area.Left, area.Bottom, area.Width, area.Height);
                if (rect.GetWidth() <= 0 || rect.GetHeight() <= 0) continue;
                locations.Add(new PdfCleanUpLocation(area.PageNumber, rect, ColorConstants.BLACK));
            }

            if (locations.Count == 0) throw new ArgumentException("Every redaction area is empty.");
            PdfCleaner.CleanUp(document, locations);
        });
    }

    /// <summary>Draws one line of text whose baseline starts at (x, y) and runs at angle radians.</summary>
    private static void DrawText(PdfCanvas canvas, PdfFont font, float size, string text, float angle, float x, float y)
    {
        if (text.Length == 0) return;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        canvas.BeginText()
            .SetFontAndSize(font, size)
            .SetTextMatrix(cos, sin, -sin, cos, x, y)
            .ShowText(text)
            .EndText();
    }
}
