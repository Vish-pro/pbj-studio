namespace KcsPdfSigner.Core;

/// <summary>A placement rectangle expressed in preview-image pixels, origin top-left.</summary>
public readonly record struct PreviewRect(double Left, double Top, double Width, double Height);

/// <summary>
/// Maps between PDF user space (origin bottom-left, unrotated media box) and the rendered
/// preview image (origin top-left, page rotation already applied by the rasteriser).
/// </summary>
/// <remarks>
/// Signature widget rectangles are stored against the unrotated media box, but a rasteriser
/// hands back an image with /Rotate already applied. Without this transform, placements on a
/// rotated page would be drawn in the wrong place, and clicks would land somewhere else again.
/// </remarks>
public static class PreviewGeometry
{
    /// <summary>Size of the rendered page in PDF points, after rotation.</summary>
    public static (double Width, double Height) RotatedSize(PdfPageInfo page)
        => Normalise(page.Rotation) is 90 or 270
            ? (page.Height, page.Width)
            : (page.Width, page.Height);

    public static PreviewRect ToPreview(
        SignaturePlacement placement, PdfPageInfo page, double renderedWidth, double renderedHeight)
    {
        var (rotatedWidth, rotatedHeight) = RotatedSize(page);
        if (rotatedWidth <= 0 || rotatedHeight <= 0) return default;

        var scaleX = renderedWidth / rotatedWidth;
        var scaleY = renderedHeight / rotatedHeight;

        var (x0, y0) = ToRotatedPoint(placement.Left - page.Left, placement.Bottom - page.Bottom, page);
        var (x1, y1) = ToRotatedPoint(
            placement.Left + placement.Width - page.Left, placement.Bottom + placement.Height - page.Bottom, page);

        return new PreviewRect(
            Math.Min(x0, x1) * scaleX,
            Math.Min(y0, y1) * scaleY,
            Math.Abs(x1 - x0) * scaleX,
            Math.Abs(y1 - y0) * scaleY);
    }

    /// <summary>Turns a click on the preview back into a point in unrotated PDF user space.</summary>
    public static (float X, float Y) FromPreview(
        double previewX, double previewY, PdfPageInfo page,
        double renderedWidth, double renderedHeight)
    {
        if (renderedWidth <= 0 || renderedHeight <= 0) return (0, 0);

        var (rotatedWidth, rotatedHeight) = RotatedSize(page);

        // Into rotated PDF points, still measured from the top-left.
        var rx = previewX / renderedWidth * rotatedWidth;
        var ry = previewY / renderedHeight * rotatedHeight;

        // Each case inverts the matching branch of ToRotatedPoint. For 270 the forward map is
        // (x, y) -> (Height - y, Width - x), so x comes back from ry and y comes back from rx.
        var (x, y) = Normalise(page.Rotation) switch
        {
            90 => (ry, rx),
            180 => (page.Width - rx, ry),
            270 => (page.Width - ry, page.Height - rx),
            _ => (rx, page.Height - ry)
        };

        // Back from the visible area's corner to user space (crop boxes need not start at 0, 0).
        return ((float)(x + page.Left), (float)(y + page.Bottom));
    }

    /// <summary>
    /// Applies the page rotation to a point measured from the visible area's lower-left corner,
    /// yielding display coordinates in points from the top-left of the rendered image.
    /// </summary>
    private static (double X, double Y) ToRotatedPoint(double x, double y, PdfPageInfo page)
        => Normalise(page.Rotation) switch
        {
            90 => (y, x),
            180 => (page.Width - x, y),
            270 => (page.Height - y, page.Width - x),
            _ => (x, page.Height - y)
        };

    private static int Normalise(int rotation)
    {
        var value = rotation % 360;
        return value < 0 ? value + 360 : value;
    }
}
