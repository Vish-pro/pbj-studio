using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// Makes the printed page match what Adobe Acrobat prints, for renderers that only know how to
/// draw a page as it looks on screen.
/// </summary>
/// <remarks>
/// Every annotation (comment, stamp, form field, signature) says whether it belongs on paper: the
/// Print flag puts it there, Hidden keeps it off everywhere, and NoView keeps a print-only mark
/// (a "COPY" or "printed on" stamp) off the screen but on paper. A screen render ignores Print and
/// honours NoView, so printing the screen image would print screen-only items and drop print-only
/// ones. This builds an in-memory copy in which the screen render is exactly the print result.
/// </remarks>
public static class PdfPrintPrep
{
    /// <summary>
    /// Returns an unencrypted in-memory copy for printing: annotations that do not print are
    /// removed and print-only annotations are made visible. The copy is never written to disk.
    /// </summary>
    public static byte[] ForPrinting(byte[] pdf, string? password)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();

        var reader = new PdfReader(input, PdfIo.ReaderProperties(password));
        // Owner-password-only files open for viewing, so they print the same way.
        reader.SetUnethicalReading(true);

        using (var document = new PdfDocument(reader, new PdfWriter(output)))
        {
            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                foreach (var annotation in page.GetAnnotations().ToList())
                {
                    // Popups are never drawn by the renderer; leave them alone.
                    if (PdfName.Popup.Equals(annotation.GetSubtype())) continue;

                    var flags = annotation.GetFlags();
                    var prints = (flags & PdfAnnotation.PRINT) != 0 && (flags & PdfAnnotation.HIDDEN) == 0;

                    if (!prints)
                        page.RemoveAnnotation(annotation);
                    else if ((flags & PdfAnnotation.NO_VIEW) != 0)
                        annotation.SetFlags(flags & ~PdfAnnotation.NO_VIEW);
                }
            }
        }

        return output.ToArray();
    }
}
