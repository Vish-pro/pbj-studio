using System.Text;
using System.Text.RegularExpressions;

namespace KcsPdfSigner.Core.Documents;

/// <summary>One match. The rectangle is in unrotated PDF points (origin bottom-left of the page box).</summary>
public sealed record SearchHit(int PageNumber, float Left, float Bottom, float Width, float Height, string Snippet);

/// <summary>Text search and extraction using the same line reconstruction as anchor detection.</summary>
public static class PdfTextSearch
{
    private const int SnippetContext = 40;

    public static List<SearchHit> Search(
        string path, string query, bool caseSensitive = false, bool wholeWord = false,
        string? password = null)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        var matcher = BuildMatcher(query.Trim(), caseSensitive, wholeWord);

        using var document = PdfIo.OpenRead(path, password);
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            foreach (var line in PageTextLayout.ReadLines(document.GetPage(pageNumber)))
            {
                foreach (Match match in matcher.Matches(line.Text))
                {
                    if (match.Length == 0) continue;

                    var box = PageTextLayout.UnionOf(line.BoxPerChar, match.Index, match.Length);
                    if (box is null) continue;

                    hits.Add(new SearchHit(pageNumber, box.GetLeft(), box.GetBottom(), box.GetWidth(),
                        box.GetHeight(), Snippet(line.Text, match.Index, match.Length)));
                }
            }
        }

        return hits;
    }

    /// <summary>Text of one page (1-based), or of the whole document when pageNumber is null.</summary>
    public static string ExtractText(string path, int? pageNumber = null, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);

        if (pageNumber is int single)
        {
            PdfIo.RequirePage(document, single);
            return PageText(document.GetPage(single));
        }

        var builder = new StringBuilder();
        for (var i = 1; i <= document.GetNumberOfPages(); i++)
        {
            if (i > 1) builder.Append("\n\f\n");
            builder.Append(PageText(document.GetPage(i)));
        }

        return builder.ToString();
    }

    private static string PageText(iText.Kernel.Pdf.PdfPage page) =>
        string.Join("\n", PageTextLayout.ReadLines(page).Select(l => l.Text.TrimEnd()));

    private static Regex BuildMatcher(string query, bool caseSensitive, bool wholeWord)
    {
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;

        // Any run of whitespace in the query matches any run of whitespace on the page, since
        // reconstructed lines may carry one space where the source had several.
        var pattern = string.Join(@"\s+",
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

        if (wholeWord)
        {
            if (char.IsLetterOrDigit(query[0])) pattern = @"\b" + pattern;
            if (char.IsLetterOrDigit(query[^1])) pattern += @"\b";
        }

        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    private static string Snippet(string text, int index, int length)
    {
        var start = Math.Max(0, index - SnippetContext);
        var end = Math.Min(text.Length, index + length + SnippetContext);
        var snippet = text[start..end].Trim();
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }
}
