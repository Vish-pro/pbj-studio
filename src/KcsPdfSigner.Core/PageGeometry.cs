using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core;

/// <summary>
/// The part of a page that readers actually show, and conversions between what the reader sees
/// (display space) and PDF user space.
/// </summary>
/// <remarks>
/// Every viewer and printer shows only the crop box (clipped to the media box), and turns the page
/// by its /Rotate value. Placing a signature against the media box, or ignoring the rotation, puts
/// it in the trimmed-off margin (invisible everywhere) or draws it sideways.
/// </remarks>
public static class PageGeometry
{
    /// <summary>The visible page area in user space: the crop box clipped to the media box.</summary>
    public static Rectangle VisibleBox(PdfPage page)
    {
        var media = page.GetMediaBox();
        var crop = page.GetCropBox();
        var visible = crop?.GetIntersection(media);
        return visible is null || visible.GetWidth() <= 0 || visible.GetHeight() <= 0 ? media : visible;
    }

    /// <summary>Geometry of one page as the UI and the placement code need it.</summary>
    public static PdfPageInfo Describe(PdfPage page, int pageNumber)
    {
        var box = VisibleBox(page);
        return new PdfPageInfo(pageNumber, box.GetWidth(), box.GetHeight(), Normalise(page.GetRotation()),
            box.GetLeft(), box.GetBottom());
    }

    /// <summary>Width and height of the page as displayed (swapped at 90 and 270).</summary>
    public static (float Width, float Height) DisplaySize(PdfPageInfo page)
        => Normalise(page.Rotation) is 90 or 270 ? (page.Height, page.Width) : (page.Width, page.Height);

    /// <summary>
    /// Converts a rectangle measured on the page as displayed (origin bottom-left of what the reader
    /// shows, after rotation) into a user-space rectangle.
    /// </summary>
    public static Rectangle DisplayToUser(PdfPageInfo page, float left, float bottom, float width, float height)
    {
        var (x0, y0) = DisplayPointToUser(page, left, bottom);
        var (x1, y1) = DisplayPointToUser(page, left + width, bottom + height);
        return new Rectangle(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
    }

    /// <summary>The inverse of <see cref="DisplayToUser"/>.</summary>
    public static Rectangle UserToDisplay(PdfPageInfo page, Rectangle user)
    {
        var (x0, y0) = UserPointToDisplay(page, user.GetLeft(), user.GetBottom());
        var (x1, y1) = UserPointToDisplay(page, user.GetRight(), user.GetTop());
        return new Rectangle(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
    }

    private static (float X, float Y) DisplayPointToUser(PdfPageInfo page, float dx, float dy)
    {
        float w = page.Width, h = page.Height;
        var (x, y) = Normalise(page.Rotation) switch
        {
            90 => (w - dy, dx),
            180 => (w - dx, h - dy),
            270 => (dy, h - dx),
            _ => (dx, dy)
        };
        return (x + page.Left, y + page.Bottom);
    }

    private static (float X, float Y) UserPointToDisplay(PdfPageInfo page, float ux, float uy)
    {
        float w = page.Width, h = page.Height;
        float x = ux - page.Left, y = uy - page.Bottom;
        return Normalise(page.Rotation) switch
        {
            90 => (y, w - x),
            180 => (w - x, h - y),
            270 => (h - y, x),
            _ => (x, y)
        };
    }

    internal static int Normalise(int rotation)
    {
        var value = rotation % 360;
        return value < 0 ? value + 360 : value;
    }
}
