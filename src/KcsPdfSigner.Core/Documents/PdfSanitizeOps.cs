using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// Information a PDF carries that the reader does not see on the page: document properties
/// (author, title, ...), XMP metadata, the author names on comments, and attached files.
/// </summary>
public sealed class HiddenInfoReport
{
    /// <summary>Document-property fields with a value, e.g. "Author" -> "R. Kamath". iText's own producer line is left out.</summary>
    public Dictionary<string, string> InfoFields { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the file has an XMP metadata stream (a second copy of the properties, often with more detail).</summary>
    public bool HasXmpMetadata { get; set; }

    /// <summary>Comment author name -> number of comments (markup annotations, not form fields).</summary>
    public Dictionary<string, int> CommentAuthors { get; } = new(StringComparer.Ordinal);

    /// <summary>Names of files attached to the document (document-level attachments).</summary>
    public List<string> Attachments { get; } = [];

    public string Author => InfoFields.TryGetValue("Author", out var author) ? author : "";

    /// <summary>
    /// Information that names a person or carries a file: the author, comment authors, attachments.
    /// Titles and the creating program alone do not count (almost every PDF has them).
    /// </summary>
    public bool HasPersonalInfo => Author.Length > 0 || CommentAuthors.Count > 0 || Attachments.Count > 0;

    public bool HasAnything => InfoFields.Count > 0 || HasXmpMetadata || CommentAuthors.Count > 0 || Attachments.Count > 0;

    /// <summary>Short list for the suggestion bar, e.g. "Author 'X', 2 comments by 'Y', 1 attachment".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Author.Length > 0) parts.Add($"Author '{Shorten(Author)}'");
        foreach (var (name, count) in CommentAuthors.OrderByDescending(p => p.Value).Take(2))
            parts.Add($"{count} comment{(count == 1 ? "" : "s")} by '{Shorten(name)}'");
        if (CommentAuthors.Count > 2) parts.Add($"comments by {CommentAuthors.Count - 2} more people");
        if (Attachments.Count > 0) parts.Add($"{Attachments.Count} attachment{(Attachments.Count == 1 ? "" : "s")}");

        var others = InfoFields.Keys.Count(k => k != "Author") + (HasXmpMetadata ? 1 : 0);
        if (others > 0) parts.Add($"{others} other detail{(others == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    /// <summary>One line per item that <see cref="PdfSanitizeOps.RemoveHiddenInfo"/> removes (attachments listed separately).</summary>
    public List<string> RemovableItems()
    {
        var lines = new List<string>();
        foreach (var (key, value) in InfoFields) lines.Add($"{key}: {Shorten(value)}");
        if (HasXmpMetadata) lines.Add("XMP metadata (a second copy of the document details)");
        foreach (var (name, count) in CommentAuthors)
            lines.Add($"Author name '{Shorten(name)}' on {count} comment{(count == 1 ? "" : "s")} (the comments stay)");
        return lines;
    }

    private static string Shorten(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 40 ? text : text[..37] + "...";
    }
}

/// <summary>Finds and removes hidden information (document details, XMP, comment authors, attachments).</summary>
public static class PdfSanitizeOps
{
    /// <summary>Info keys that are kept: dates and the trapping flag say nothing about a person, and iText rewrites Producer anyway.</summary>
    private static readonly HashSet<string> KeptInfoKeys = new(StringComparer.Ordinal) { "CreationDate", "ModDate", "Trapped", "Producer" };

    public static HiddenInfoReport FindHiddenInfo(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        return FindHiddenInfo(document);
    }

    internal static HiddenInfoReport FindHiddenInfo(PdfDocument document)
    {
        var report = new HiddenInfoReport();

        var info = document.GetTrailer().GetAsDictionary(PdfName.Info);
        if (info is not null)
        {
            foreach (var key in info.KeySet())
            {
                var name = key.GetValue();
                if (name is "CreationDate" or "ModDate" or "Trapped") continue;
                var value = (info.Get(key) as PdfString)?.ToUnicodeString()?.Trim() ?? "";
                if (value.Length == 0) continue;
                if (name == "Producer" && value.Contains("iText", StringComparison.OrdinalIgnoreCase)) continue;
                report.InfoFields[name] = value;
            }
        }

        report.HasXmpMetadata = document.GetCatalog().GetPdfObject().ContainsKey(PdfName.Metadata);

        for (var number = 1; number <= document.GetNumberOfPages(); number++)
        {
            foreach (var annotation in document.GetPage(number).GetAnnotations())
            {
                var dictionary = annotation.GetPdfObject();
                if (PdfName.Widget.Equals(annotation.GetSubtype())) continue; // /T is a form field name there
                var author = dictionary.GetAsString(PdfName.T)?.ToUnicodeString()?.Trim() ?? "";
                if (author.Length == 0) continue;
                report.CommentAuthors[author] = report.CommentAuthors.TryGetValue(author, out var count) ? count + 1 : 1;
            }
        }

        var names = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
        var files = names?.GetAsDictionary(PdfName.EmbeddedFiles);
        if (files is not null) CollectNames(files, report.Attachments, 0);

        return report;
    }

    /// <summary>
    /// Writes a copy without hidden information: the document details (title, author, subject, keywords,
    /// creator and any custom entries; the dates are kept), the XMP metadata, and the author names on
    /// comments (the comments themselves stay). Attached files are removed only when
    /// <paramref name="removeAttachments"/> is true. Page content, form fields and their values are not touched.
    /// Note: the program that writes the file (iText) always records itself as Producer.
    /// </summary>
    public static void RemoveHiddenInfo(string input, string output, string? password = null, bool removeAttachments = false)
    {
        // In stamping mode the trailer's Info is not reliably the dictionary iText writes, so the keys
        // (custom ones such as "Company" included) are read first from a read-only open.
        List<string> existingKeys;
        using (var read = PdfIo.OpenRead(input, password))
        {
            existingKeys = read.GetTrailer().GetAsDictionary(PdfName.Info)?.KeySet().Select(k => k.GetValue()).ToList() ?? [];
        }

        PdfIo.Edit(input, output, password, document =>
        {
            // iText writes the dictionary behind GetDocumentInfo() (SetMoreInfo with null removes a key);
            // the trailer's dictionary is cleared as well in case it is a separate copy.
            var documentInfo = document.GetDocumentInfo();
            var trailerInfo = document.GetTrailer().GetAsDictionary(PdfName.Info);
            var keys = new HashSet<string>(StringComparer.Ordinal) { "Title", "Author", "Subject", "Keywords", "Creator" };
            keys.UnionWith(existingKeys);
            if (trailerInfo is not null)
            {
                foreach (var key in trailerInfo.KeySet()) keys.Add(key.GetValue());
            }

            foreach (var key in keys.Where(k => !KeptInfoKeys.Contains(k)))
            {
                documentInfo.SetMoreInfo(key, null);
                trailerInfo?.Remove(new PdfName(key));
            }

            document.GetCatalog().GetPdfObject().Remove(PdfName.Metadata);
            ForgetXmp(document);

            for (var number = 1; number <= document.GetNumberOfPages(); number++)
            {
                foreach (var annotation in document.GetPage(number).GetAnnotations())
                {
                    if (PdfName.Widget.Equals(annotation.GetSubtype())) continue;
                    if (annotation.GetPdfObject().ContainsKey(PdfName.T))
                    {
                        annotation.GetPdfObject().Remove(PdfName.T);
                        annotation.SetModified();
                    }
                }
            }

            if (removeAttachments)
            {
                var names = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
                if (names is not null && names.ContainsKey(PdfName.EmbeddedFiles))
                {
                    names.Remove(PdfName.EmbeddedFiles);
                    names.SetModified();
                }
            }
        });
    }

    /// <summary>
    /// iText keeps the XMP it read and writes it back (updated) on close; it has no public way to drop it.
    /// Clearing its two private copies makes the close write none. If a future iText renames them, the
    /// catalog entry is still removed and only iText's regenerated packet (producer and dates) remains.
    /// </summary>
    private static void ForgetXmp(PdfDocument document)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var name in new[] { "xmpMetadata", "xmpMetadataBytes" })
        {
            try
            {
                typeof(PdfDocument).GetField(name, flags)?.SetValue(document, null);
            }
            catch
            {
                // See the summary: best effort.
            }
        }
    }

    private static void CollectNames(PdfDictionary node, List<string> into, int depth)
    {
        if (depth > 20) return;
        var pairs = node.GetAsArray(PdfName.Names);
        if (pairs is not null)
        {
            for (var i = 0; i + 1 < pairs.Size(); i += 2)
                into.Add(pairs.GetAsString(i)?.ToUnicodeString() ?? "(attachment)");
        }

        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null) return;
        for (var i = 0; i < kids.Size(); i++)
        {
            if (kids.GetAsDictionary(i) is { } kid) CollectNames(kid, into, depth + 1);
        }
    }
}
