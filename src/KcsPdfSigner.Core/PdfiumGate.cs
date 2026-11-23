namespace KcsPdfSigner.Core;

/// <summary>
/// PDFium (used through PDFtoImage) is not thread-safe. Every page render in the program, from the
/// viewer, thumbnails, printing, image export and OCR alike, takes this one lock around the render
/// call itself (never around slow work that follows, such as OCR).
/// </summary>
public static class PdfiumGate
{
    public static readonly object Sync = new();
}
