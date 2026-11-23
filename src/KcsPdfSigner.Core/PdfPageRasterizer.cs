using PDFtoImage;

namespace KcsPdfSigner.Core;

/// <summary>Renders a PDF page to a PNG so placements can be checked, and placed, visually.</summary>
public static class PdfPageRasterizer
{
    public const int PreviewDpi = 110;

    /// <summary>Renders one page (1-based), with its signatures, comments and form fields, and returns PNG bytes.</summary>
    public static byte[] RenderPng(
        byte[] pdfBytes, int pageNumber, int dpi = PreviewDpi, string? password = null)
    {
        using var buffer = new MemoryStream();
        lock (PdfiumGate.Sync)
        {
            Conversion.SavePng(buffer, pdfBytes, pageNumber - 1, password,
                options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));
        }
        return buffer.ToArray();
    }
}
