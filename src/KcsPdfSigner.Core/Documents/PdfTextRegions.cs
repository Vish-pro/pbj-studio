using System.Text;
using System.Text.RegularExpressions;

namespace KcsPdfSigner.Core.Documents;

/// <summary>A web address found in the page text. The box is in unrotated PDF points (origin bottom-left).</summary>
public sealed record UrlHit(int PageNumber, float Left, float Bottom, float Width, float Height, string Url);

/// <summary>One line's worth of selected text, in unrotated PDF points (origin bottom-left).</summary>
public sealed record TextRegionBox(float Left, float Bottom, float Width, float Height);

/// <summary>Text inside a dragged rectangle: the text, and one box per line for highlighting.</summary>
public sealed record TextSelection(string Text, List<TextRegionBox> LineBoxes);

/// <summary>Finds web addresses in page text and reads the text inside a rectangle.</summary>
public static class PdfTextRegions
{
    private static readonly Regex UrlPattern = new(
        @"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string TrailingPunctuation = ".,;:!?)]}>'\"";

    public static List<UrlHit> FindUrls(string path, string? password = null)
    {
        var hits = new List<UrlHit>();

        using var document = PdfIo.OpenRead(path, password);
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            foreach (var line in PageTextLayout.ReadLines(document.GetPage(pageNumber)))
            {
                foreach (Match match in UrlPattern.Matches(line.Text))
                {
                    var text = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
                    if (text.Length < 8) continue;

                    var box = PageTextLayout.UnionOf(line.BoxPerChar, match.Index, text.Length);
                    if (box is null) continue;

                    var url = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text;
                    hits.Add(new UrlHit(pageNumber, box.GetLeft(), box.GetBottom(), box.GetWidth(), box.GetHeight(), url));
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Text whose characters lie inside the rectangle (a character counts when its centre does).
    /// Lines are returned top to bottom as laid out by <see cref="PageTextLayout"/>.
    /// </summary>
    public static TextSelection SelectText(
        string path, int pageNumber, float left, float bottom, float width, float height, string? password = null)
    {
        var right = left + width;
        var top = bottom + height;
        var text = new StringBuilder();
        var boxes = new List<TextRegionBox>();

        using var document = PdfIo.OpenRead(path, password);
        PdfIo.RequirePage(document, pageNumber);

        foreach (var line in PageTextLayout.ReadLines(document.GetPage(pageNumber)))
        {
            int first = -1, last = -1;
            for (var i = 0; i < line.Text.Length && i < line.BoxPerChar.Count; i++)
            {
                var box = line.BoxPerChar[i];
                if (box is null) continue;

                var cx = box.GetLeft() + box.GetWidth() / 2;
                var cy = box.GetBottom() + box.GetHeight() / 2;
                if (cx < left || cx > right || cy < bottom || cy > top) continue;

                if (first < 0) first = i;
                last = i;
            }

            if (first < 0) continue;

            var piece = line.Text.Substring(first, last - first + 1).TrimEnd();
            if (piece.Length == 0) continue;

            if (text.Length > 0) text.Append('\n');
            text.Append(piece);

            var union = PageTextLayout.UnionOf(line.BoxPerChar, first, piece.Length);
            if (union is not null)
            {
                boxes.Add(new TextRegionBox(union.GetLeft(), union.GetBottom(), union.GetWidth(), union.GetHeight()));
            }
        }

        return new TextSelection(text.ToString(), boxes);
    }
}
