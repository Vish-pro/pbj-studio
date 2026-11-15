using System.Text;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using KcsPdfSigner.Core;

namespace KcsPdfSigner.App.Documents;

/// <summary>Thrown when a PDF needs a password, or the one given was wrong.</summary>
public sealed class PasswordRequiredException : Exception
{
    public PasswordRequiredException(string path, bool passwordWasGiven)
        : base(passwordWasGiven
            ? $"The password for {System.IO.Path.GetFileName(path)} is not correct."
            : $"{System.IO.Path.GetFileName(path)} is protected by a password.")
    {
        PasswordWasGiven = passwordWasGiven;
    }

    public bool PasswordWasGiven { get; }
}

/// <summary>
/// Reads page geometry, honouring a password. Mirrors <see cref="PlacementDetector.ReadPages"/>,
/// which cannot open encrypted files.
/// </summary>
public static class PdfPageReader
{
    public static List<PdfPageInfo> ReadPages(string pdfPath, string? password)
    {
        try
        {
            using var reader = OpenReader(pdfPath, password);
            using var document = new PdfDocument(reader);

            var pages = new List<PdfPageInfo>(document.GetNumberOfPages());
            for (var i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var page = document.GetPage(i);
                var size = page.GetPageSize();
                pages.Add(new PdfPageInfo(i, size.GetWidth(), size.GetHeight(), page.GetRotation()));
            }

            return pages;
        }
        catch (BadPasswordException)
        {
            throw new PasswordRequiredException(pdfPath, !string.IsNullOrEmpty(password));
        }
    }

    private static PdfReader OpenReader(string pdfPath, string? password)
    {
        var properties = new ReaderProperties();
        if (!string.IsNullOrEmpty(password)) properties.SetPassword(Encoding.UTF8.GetBytes(password));

        var reader = new PdfReader(pdfPath, properties);
        // Owner-password-only files (print/copy restrictions) still open for viewing.
        reader.SetUnethicalReading(true);
        return reader;
    }
}
