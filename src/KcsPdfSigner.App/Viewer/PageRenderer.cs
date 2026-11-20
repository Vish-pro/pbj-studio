using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace KcsPdfSigner.App.Viewer;

/// <summary>
/// Turns one PDF page into a frozen WPF bitmap. Safe to call from any thread: PDFium is not
/// thread-safe, so every call goes through one lock (viewer pages, thumbnails and printing alike).
/// </summary>
public static class PageRenderer
{
    private static readonly object Gate = new();

    /// <summary>Upper bound on rendered pixels per page (about 64 MB of BGRA).</summary>
    public const double MaxPixels = 16_000_000;

    /// <summary>Renders page <paramref name="pageNumber"/> (1-based) at <paramref name="dpi"/>.</summary>
    public static BitmapSource Render(byte[] pdf, int pageNumber, string? password, int dpi, bool withAnnotations = true)
    {
        dpi = Math.Clamp(dpi, 8, 1200);

        lock (Gate)
        {
            using var bitmap = Conversion.ToImage(
                pdf, pageNumber - 1, password,
                new PDFtoImage.RenderOptions(Dpi: dpi, WithAnnotations: withAnnotations, WithFormFill: withAnnotations));

            return ToBitmapSource(bitmap);
        }
    }

    /// <summary>
    /// Renders a page at an exact pixel size, so the bitmap maps one-to-one onto screen pixels
    /// (no resampling, which is what keeps text crisp). <paramref name="screenScale"/> is the
    /// Windows display scale (1.0 at 100%); it only sets the bitmap's declared resolution.
    /// </summary>
    public static BitmapSource RenderPixels(
        byte[] pdf, int pageNumber, string? password, int pixelWidth, int pixelHeight, double screenScale)
    {
        pixelWidth = Math.Clamp(pixelWidth, 8, 16000);
        pixelHeight = Math.Clamp(pixelHeight, 8, 16000);

        lock (Gate)
        {
            using var bitmap = Conversion.ToImage(
                pdf, pageNumber - 1, password,
                new PDFtoImage.RenderOptions(
                    Dpi: 96, Width: pixelWidth, Height: pixelHeight, WithAnnotations: true, WithFormFill: true,
                    WithAspectRatio: false));

            return ToBitmapSource(bitmap, 96.0 * screenScale);
        }
    }

    /// <summary>Highest DPI that keeps a page of this size (in points) under <see cref="MaxPixels"/>.</summary>
    public static int CapDpi(double widthPoints, double heightPoints, double wantedDpi)
    {
        if (widthPoints <= 0 || heightPoints <= 0) return (int)Math.Round(wantedDpi);
        var inches = widthPoints / 72.0 * (heightPoints / 72.0);
        var cap = Math.Sqrt(MaxPixels / inches);
        return (int)Math.Max(8, Math.Round(Math.Min(wantedDpi, cap)));
    }

    private static BitmapSource ToBitmapSource(SKBitmap bitmap, double dpi = 96)
    {
        SKBitmap source = bitmap;
        SKBitmap? converted = null;

        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            converted = bitmap.Copy(SKColorType.Bgra8888);
            source = converted ?? throw new InvalidOperationException("Could not convert the rendered page.");
        }

        try
        {
            var format = source.AlphaType == SKAlphaType.Unpremul ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
            var image = BitmapSource.Create(
                source.Width, source.Height, dpi, dpi, format, null,
                source.GetPixels(), source.ByteCount, source.RowBytes);
            image.Freeze();
            return image;
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
