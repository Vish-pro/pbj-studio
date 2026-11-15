using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Document properties as stored in the PDF.</summary>
public sealed class DocumentInfo
{
    public int PageCount { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Producer { get; init; } = "";
    public DateTime? Created { get; init; }
    public DateTime? Modified { get; init; }
    public string PdfVersion { get; init; } = "";
    public bool IsEncrypted { get; init; }
    public long FileSizeBytes { get; init; }
}

/// <summary>One bookmark. PageNumber is 0 when the bookmark does not point at a page.</summary>
public sealed class OutlineItem
{
    public string Title { get; init; } = "";
    public int PageNumber { get; init; }
    public List<OutlineItem> Children { get; init; } = [];
}

/// <summary>Reads and edits document properties and bookmarks.</summary>
public static class PdfInfoReader
{
    public static DocumentInfo Read(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var info = document.GetDocumentInfo();

        return new DocumentInfo
        {
            PageCount = document.GetNumberOfPages(),
            Title = info.GetTitle() ?? "",
            Author = info.GetAuthor() ?? "",
            Subject = info.GetSubject() ?? "",
            Keywords = info.GetKeywords() ?? "",
            Creator = info.GetCreator() ?? "",
            Producer = info.GetProducer() ?? "",
            Created = ParseDate(info.GetMoreInfo("CreationDate")),
            Modified = ParseDate(info.GetMoreInfo("ModDate")),
            PdfVersion = document.GetPdfVersion().ToString(),
            IsEncrypted = document.GetReader().IsEncrypted(),
            FileSizeBytes = new FileInfo(path).Length
        };
    }

    public static List<OutlineItem> ReadOutline(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);

        var root = document.GetOutlines(false);
        if (root is null) return [];

        var names = document.GetCatalog().GetNameTree(PdfName.Dests);
        return root.GetAllChildren().Select(o => ToItem(document, names, o, 0)).ToList();
    }

    /// <summary>Sets the four editable properties. A null argument leaves that property unchanged.</summary>
    public static void SetMetadata(
        string input, string output, string? title, string? author, string? subject,
        string? keywords, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var info = document.GetDocumentInfo();
            if (title is not null) info.SetTitle(title);
            if (author is not null) info.SetAuthor(author);
            if (subject is not null) info.SetSubject(subject);
            if (keywords is not null) info.SetKeywords(keywords);
        });
    }

    /// <summary>True if the file carries any encryption (with or without an open password).</summary>
    public static bool IsEncrypted(string path)
    {
        PdfIo.RequireFile(path);
        try
        {
            using var reader = new PdfReader(path);
            using var document = new PdfDocument(reader);
            return reader.IsEncrypted();
        }
        catch (BadPasswordException)
        {
            return true;
        }
    }

    /// <summary>True if the file cannot be opened without a password.</summary>
    public static bool NeedsPassword(string path)
    {
        PdfIo.RequireFile(path);
        try
        {
            using var reader = new PdfReader(path);
            using var document = new PdfDocument(reader);
            return false;
        }
        catch (BadPasswordException)
        {
            return true;
        }
    }

    private static OutlineItem ToItem(PdfDocument document, PdfNameTree names, PdfOutline outline, int depth)
    {
        return new OutlineItem
        {
            Title = outline.GetTitle() ?? "",
            PageNumber = ResolvePage(document, names, outline),
            Children = depth > 64
                ? []
                : outline.GetAllChildren().Select(c => ToItem(document, names, c, depth + 1)).ToList()
        };
    }

    private static int ResolvePage(PdfDocument document, PdfNameTree names, PdfOutline outline)
    {
        try
        {
            var destination = outline.GetDestination();
            if (destination is null)
            {
                // Many producers use a GoTo action instead of a direct /Dest entry.
                var action = outline.GetContent().GetAsDictionary(PdfName.A);
                var target = action?.Get(PdfName.D);
                if (target is not null && PdfName.GoTo.Equals(action!.GetAsName(PdfName.S)))
                    destination = PdfDestination.MakeDestination(target);
            }

            if (destination?.GetDestinationPage(names) is PdfDictionary pageDictionary)
                return document.GetPageNumber(pageDictionary);
        }
        catch (Exception)
        {
            // A broken destination is shown as a bookmark that goes nowhere.
        }

        return 0;
    }

    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return PdfDate.Decode(value);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
