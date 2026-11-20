using iText.Forms;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// One page picked for a new document: which file, which page (1-based), and how many degrees
/// (a multiple of 90, may be negative) to turn it on top of its own rotation.
/// </summary>
public sealed record PageSelection(string File, int PageNumber, int Rotation = 0, string? Password = null);

/// <summary>Page organisation: rotate, delete, extract, reorder, insert, merge, split, images to PDF.</summary>
/// <remarks>Page numbers are 1-based throughout.</remarks>
public static class PdfPageOps
{
    /// <summary>Adds degrees (a multiple of 90, may be negative) to the /Rotate of each listed page.</summary>
    public static void RotatePages(
        string input, string output, IEnumerable<int> pages, int degrees, string? password = null)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees));
        var targets = pages.Distinct().ToList();

        PdfIo.Edit(input, output, password, document =>
        {
            foreach (var number in targets)
            {
                PdfIo.RequirePage(document, number);
                var page = document.GetPage(number);
                page.SetRotation(PdfIo.NormalizeRotation(page.GetRotation() + degrees));
            }
        });
    }

    public static void DeletePages(string input, string output, IEnumerable<int> pages, string? password = null)
    {
        var targets = pages.Distinct().OrderByDescending(p => p).ToList();

        PdfIo.Edit(input, output, password, document =>
        {
            foreach (var number in targets) PdfIo.RequirePage(document, number);
            if (targets.Count >= document.GetNumberOfPages())
                throw new InvalidOperationException("Cannot delete every page of a document.");

            foreach (var number in targets) document.RemovePage(number);
        });
    }

    /// <summary>Copies the listed pages, in the order given, into a new document.</summary>
    public static void ExtractPages(string input, string output, IEnumerable<int> pages, string? password = null)
    {
        var targets = pages.ToList();
        if (targets.Count == 0) throw new ArgumentException("No pages chosen to extract.", nameof(pages));
        PdfIo.RequireDistinct(input, output);

        using var source = PdfIo.OpenRead(input, password);
        foreach (var number in targets) PdfIo.RequirePage(source, number);

        WriteNew(output, target => source.CopyPagesTo(targets, target, new PdfPageFormCopier()));
    }

    /// <summary>
    /// Builds one new PDF from pages of several files, in exactly the order listed (the same page
    /// may appear more than once). Each selection can carry an extra rotation and the password of
    /// its source file. Every source is opened once, however many of its pages are used.
    /// </summary>
    public static void BuildFromSelection(IReadOnlyList<PageSelection> items, string output)
    {
        if (items.Count == 0) throw new ArgumentException("No pages chosen.", nameof(items));
        foreach (var item in items)
        {
            if (item.Rotation % 90 != 0)
                throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(items));
            PdfIo.RequireDistinct(item.File, output);
        }

        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var group in items.GroupBy(i => Path.GetFullPath(i.File), StringComparer.OrdinalIgnoreCase))
            {
                var password = group.Select(i => i.Password).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                var source = PdfIo.OpenRead(group.First().File, password);
                sources[group.Key] = source;
                foreach (var item in group) PdfIo.RequirePage(source, item.PageNumber);
            }

            WriteNew(output, target =>
            {
                var copier = new PdfPageFormCopier();
                foreach (var item in items)
                {
                    var source = sources[Path.GetFullPath(item.File)];
                    source.CopyPagesTo(item.PageNumber, item.PageNumber, target, copier);

                    if (item.Rotation != 0)
                    {
                        var added = target.GetPage(target.GetNumberOfPages());
                        added.SetRotation(PdfIo.NormalizeRotation(added.GetRotation() + item.Rotation));
                    }
                }
            });
        }
        catch (iText.Kernel.Exceptions.BadPasswordException ex)
        {
            throw new InvalidOperationException(
                "A protected file does not allow pages to be taken from it with the password given. " +
                "Open it with its owner password (the password that also lets you change the file).", ex);
        }
        finally
        {
            foreach (var source in sources.Values)
            {
                try { source.Close(); } catch { /* read-only: nothing to lose */ }
            }
        }
    }

    /// <summary>newOrder lists every page exactly once, e.g. [3, 1, 2] puts page 3 first.</summary>
    public static void ReorderPages(string input, string output, IList<int> newOrder, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var count = document.GetNumberOfPages();
            if (newOrder.Count != count || newOrder.Distinct().Count() != count ||
                newOrder.Any(p => p < 1 || p > count))
                throw new ArgumentException($"The new order must list each of the {count} page(s) exactly once.", nameof(newOrder));

            var originals = Enumerable.Range(1, count).Select(document.GetPage).ToList();
            for (var position = 1; position <= count; position++)
            {
                var current = document.GetPageNumber(originals[newOrder[position - 1] - 1]);
                if (current != position) document.MovePage(current, position);
            }
        });
    }

    /// <summary>Inserts every page of otherPdf after afterPage (0 = at the start).</summary>
    public static void InsertPages(
        string input, string output, string otherPdf, int afterPage, string? password = null,
        string? otherPassword = null)
    {
        using var other = PdfIo.OpenRead(otherPdf, otherPassword);

        PdfIo.Edit(input, output, password, document =>
        {
            var count = document.GetNumberOfPages();
            if (afterPage < 0 || afterPage > count)
                throw new ArgumentOutOfRangeException(nameof(afterPage), $"Insert position must be 0 to {count}.");

            var copier = new PdfPageFormCopier();
            if (afterPage == count)
                other.CopyPagesTo(1, other.GetNumberOfPages(), document, copier);
            else
                other.CopyPagesTo(1, other.GetNumberOfPages(), document, afterPage + 1, copier);
        });
    }

    /// <summary>Inserts one blank page, sized like its neighbour, after afterPage (0 = at the start).</summary>
    public static void InsertBlankPage(string input, string output, int afterPage, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var count = document.GetNumberOfPages();
            if (afterPage < 0 || afterPage > count)
                throw new ArgumentOutOfRangeException(nameof(afterPage), $"Insert position must be 0 to {count}.");

            var neighbour = document.GetPage(Math.Max(afterPage, 1));
            var size = new PageSize(neighbour.GetPageSize());

            var page = afterPage == count ? document.AddNewPage(size) : document.AddNewPage(afterPage + 1, size);
            page.SetRotation(neighbour.GetRotation());
        });
    }

    /// <summary>Joins whole documents in the order given. Bookmarks and form fields are carried over.</summary>
    public static void Merge(IList<string> inputs, string output)
    {
        if (inputs.Count == 0) throw new ArgumentException("No files to merge.", nameof(inputs));
        foreach (var input in inputs) PdfIo.RequireDistinct(input, output);

        WriteNew(output, target =>
        {
            target.InitializeOutlines();
            var copier = new PdfPageFormCopier();
            foreach (var input in inputs)
            {
                using var source = PdfIo.OpenRead(input, null);
                source.CopyPagesTo(1, source.GetNumberOfPages(), target, copier);
            }
        });
    }

    /// <summary>
    /// Splits into files of pagesPerFile pages each, named "name_p1-3.pdf" and so on.
    /// Returns the files written, in page order.
    /// </summary>
    public static List<string> Split(string input, string outputFolder, int pagesPerFile, string? password = null)
    {
        if (pagesPerFile < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerFile), "Pages per file must be at least 1.");
        Directory.CreateDirectory(outputFolder);

        var baseName = Path.GetFileNameWithoutExtension(input);
        var written = new List<string>();

        using var source = PdfIo.OpenRead(input, password);
        var count = source.GetNumberOfPages();

        for (var start = 1; start <= count; start += pagesPerFile)
        {
            var end = Math.Min(start + pagesPerFile - 1, count);
            var name = start == end ? $"{baseName}_p{start}.pdf" : $"{baseName}_p{start}-{end}.pdf";
            var file = Path.Combine(outputFolder, name);

            WriteNew(file, target => source.CopyPagesTo(start, end, target, new PdfPageFormCopier()));
            written.Add(file);
        }

        return written;
    }

    /// <summary>
    /// One page per image, each page the image's own size (at its stored resolution, 96 dpi if
    /// none). Supports PNG, JPEG, BMP, GIF and TIFF (first frame).
    /// </summary>
    public static void ImagesToPdf(IList<string> images, string output)
    {
        if (images.Count == 0) throw new ArgumentException("No images given.", nameof(images));
        foreach (var image in images) PdfIo.RequireFile(image);

        WriteNew(output, target =>
        {
            foreach (var file in images)
            {
                ImageData data;
                try
                {
                    data = ImageDataFactory.Create(file);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"'{Path.GetFileName(file)}' is not a supported image: {ex.Message}", ex);
                }

                var dpiX = data.GetDpiX() > 0 ? data.GetDpiX() : 96;
                var dpiY = data.GetDpiY() > 0 ? data.GetDpiY() : 96;
                var width = MathF.Min(data.GetWidth() * 72f / dpiX, 14400f);
                var height = MathF.Min(data.GetHeight() * 72f / dpiY, 14400f);

                var page = target.AddNewPage(new PageSize(width, height));
                new PdfCanvas(page).AddImageFittedIntoRectangle(data, new Rectangle(0, 0, width, height), false);
            }
        });
    }

    private static void WriteNew(string output, Action<PdfDocument> fill)
    {
        var document = PdfIo.CreateNew(output);
        try
        {
            fill(document);
            document.Close();
        }
        catch
        {
            try { document.Close(); } catch { /* already failing */ }
            PdfIo.TryDelete(output);
            throw;
        }
    }
}
