using System.Text;
using iText.IO.Font.Constants;
using iText.Kernel.Exceptions;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using Path = System.IO.Path;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Opening, saving and coordinate helpers shared by every document operation.</summary>
internal static class PdfIo
{
    /// <summary>Opens a PDF for reading only. Caller disposes the returned document.</summary>
    internal static PdfDocument OpenRead(string path, string? password)
    {
        RequireFile(path);
        var reader = new PdfReader(path, ReaderProperties(password));
        try
        {
            return new PdfDocument(reader);
        }
        catch (BadPasswordException ex)
        {
            reader.Close();
            throw new InvalidOperationException(PasswordMessage(path, password, ex), ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            reader.Close();
            throw new InvalidOperationException($"Could not open '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Opens input for modification, writing a full rewrite to output. If the input is encrypted
    /// its encryption is kept, which needs the owner (permissions) password.
    /// </summary>
    internal static PdfDocument OpenEdit(
        string input, string output, string? password, WriterProperties? writerProperties = null,
        bool preserveEncryption = true)
    {
        RequireFile(input);
        RequireDistinct(input, output);
        EnsureFolder(output);

        var reader = new PdfReader(input, ReaderProperties(password));
        PdfWriter? writer = null;
        try
        {
            writer = new PdfWriter(output, writerProperties ?? new WriterProperties());
            var stamping = new StampingProperties();
            if (preserveEncryption) stamping.PreserveEncryption();
            return new PdfDocument(reader, writer, stamping);
        }
        catch (Exception ex)
        {
            reader.Close();
            writer?.Close();
            TryDelete(output);
            if (ex is BadPasswordException)
                throw new InvalidOperationException(PasswordMessage(input, password, ex), ex);
            throw new InvalidOperationException($"Could not open '{Path.GetFileName(input)}' for editing: {ex.Message}", ex);
        }
    }

    /// <summary>Creates a new empty document at output.</summary>
    internal static PdfDocument CreateNew(string output, WriterProperties? writerProperties = null)
    {
        EnsureFolder(output);
        return new PdfDocument(new PdfWriter(output, writerProperties ?? new WriterProperties()));
    }

    internal static ReaderProperties ReaderProperties(string? password)
    {
        var properties = new ReaderProperties();
        if (!string.IsNullOrEmpty(password)) properties.SetPassword(Encoding.UTF8.GetBytes(password));
        return properties;
    }

    internal static string PasswordMessage(string path, string? password, Exception ex)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(password))
            return $"'{name}' is password protected. Supply its password.";
        if (ex.Message.Contains("owner", StringComparison.OrdinalIgnoreCase))
            return $"'{name}' restricts changes. The permissions (owner) password is needed for this operation.";
        return $"The password for '{name}' is not correct.";
    }

    internal static void RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file path given.");
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}", path);
    }

    internal static void RequireDistinct(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("No output path given.");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output file must be different from the input file.");
    }

    internal static void EnsureFolder(string output)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void RequirePage(PdfDocument document, int pageNumber)
    {
        var count = document.GetNumberOfPages();
        if (pageNumber < 1 || pageNumber > count)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} does not exist (document has {count} page(s)).");
    }

    /// <summary>
    /// Runs an edit and removes the half-written output if it fails, so a failed operation
    /// never leaves a corrupt file behind.
    /// </summary>
    internal static void Edit(string input, string output, string? password, Action<PdfDocument> edit,
        WriterProperties? writerProperties = null, bool preserveEncryption = true)
    {
        var document = OpenEdit(input, output, password, writerProperties, preserveEncryption);
        try
        {
            edit(document);
            document.Close();
        }
        catch
        {
            try { document.Close(); } catch { /* already failing */ }
            TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// A Unicode-capable font: Arial from Windows when available (embedded as a subset),
    /// otherwise the standard Helvetica.
    /// </summary>
    internal static PdfFont CreateFont(PdfDocument document, bool bold = false)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var file = Path.Combine(fonts, bold ? "arialbd.ttf" : "arial.ttf");
        if (File.Exists(file))
        {
            try
            {
                return PdfFontFactory.CreateFont(file, iText.IO.Font.PdfEncodings.IDENTITY_H,
                    PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED, document);
            }
            catch (Exception)
            {
                // Fall through to the built-in font.
            }
        }

        return PdfFontFactory.CreateFont(bold ? StandardFonts.HELVETICA_BOLD : StandardFonts.HELVETICA);
    }

    /// <summary>Size of the page as the reader sees it (width and height swap at 90 and 270).</summary>
    internal static (float Width, float Height) DisplaySize(PdfPage page)
    {
        var box = PageGeometry.VisibleBox(page);
        var rotation = NormalizeRotation(page.GetRotation());
        return rotation is 90 or 270 ? (box.GetHeight(), box.GetWidth()) : (box.GetWidth(), box.GetHeight());
    }

    internal static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;

    /// <summary>
    /// Converts a point measured on the page as displayed (origin bottom-left of what the reader
    /// sees) into unrotated PDF user space.
    /// </summary>
    internal static (float X, float Y) DisplayToUser(PdfPage page, float dx, float dy)
    {
        var box = PageGeometry.VisibleBox(page);
        float x0 = box.GetLeft(), y0 = box.GetBottom(), x1 = box.GetRight(), y1 = box.GetTop();

        return NormalizeRotation(page.GetRotation()) switch
        {
            90 => (x1 - dy, y0 + dx),
            180 => (x1 - dx, y1 - dy),
            270 => (x0 + dy, y1 - dx),
            _ => (x0 + dx, y0 + dy)
        };
    }

    internal static Rectangle Normalize(float left, float bottom, float width, float height)
    {
        var l = MathF.Min(left, left + width);
        var b = MathF.Min(bottom, bottom + height);
        return new Rectangle(l, b, MathF.Abs(width), MathF.Abs(height));
    }
}
