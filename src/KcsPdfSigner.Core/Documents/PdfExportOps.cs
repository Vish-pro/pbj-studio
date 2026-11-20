using System.Text;
using iText.Kernel.Pdf;
using PDFtoImage;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Exports pages as images or text, and rewrites a PDF smaller.</summary>
public static class PdfExportOps
{
    /// <summary>
    /// Renders pages to "name_page001.png" (or .jpg) in outputFolder. pages null = all pages.
    /// Returns the files written, in page order.
    /// </summary>
    public static List<string> ExportImages(
        string input, string outputFolder, string format = "png", int dpi = 150, IEnumerable<int>? pages = null,
        string? password = null)
    {
        var kind = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (kind == "jpeg") kind = "jpg";
        if (kind is not ("png" or "jpg")) throw new ArgumentException("Format must be \"png\" or \"jpg\".", nameof(format));
        if (dpi < 10 || dpi > 1200) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be 10 to 1200.");

        int pageCount;
        using (var document = PdfIo.OpenRead(input, password)) pageCount = document.GetNumberOfPages();

        var targets = (pages ?? Enumerable.Range(1, pageCount)).Distinct().OrderBy(p => p).ToList();
        foreach (var page in targets)
            if (page < 1 || page > pageCount)
                throw new ArgumentOutOfRangeException(nameof(pages), $"Page {page} does not exist (document has {pageCount} page(s)).");

        Directory.CreateDirectory(outputFolder);
        var bytes = File.ReadAllBytes(input);
        var baseName = Path.GetFileNameWithoutExtension(input);
        // Annotations and form fields on: without them the exported pages lose every signature,
        // comment, stamp and filled-in form value (PDFtoImage leaves them out by default).
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true);
        var written = new List<string>();

        foreach (var page in targets)
        {
            var file = Path.Combine(outputFolder, $"{baseName}_page{page:000}.{kind}");
            using (var stream = File.Create(file))
            {
                if (kind == "png")
                    Conversion.SavePng(stream, bytes, page - 1, password, options);
                else
                    Conversion.SaveJpeg(stream, bytes, page - 1, password, options);
            }
            written.Add(file);
        }

        return written;
    }

    /// <summary>Writes the text of every page to a UTF-8 file, pages separated by a form feed.</summary>
    public static void ExportText(string input, string outputTxt, string? password = null)
    {
        PdfIo.RequireDistinct(input, outputTxt);
        var text = PdfTextSearch.ExtractText(input, null, password);
        PdfIo.EnsureFolder(outputTxt);
        File.WriteAllText(outputTxt, text, new UTF8Encoding(false));
    }

    /// <summary>Rewrites with full compression and object streams, dropping unused objects.</summary>
    public static void Optimize(string input, string output, string? password = null)
    {
        var properties = new WriterProperties()
            .SetFullCompressionMode(true)
            .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);

        PdfIo.Edit(input, output, password, document =>
        {
            document.SetFlushUnusedObjects(true);
            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                for (var c = 0; c < page.GetContentStreamCount(); c++)
                    page.GetContentStream(c).SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);
            }
        }, properties);
    }
}
