using System.Text.RegularExpressions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core;

/// <summary>
/// Finds signing spots by reading the page text with its coordinates and matching keywords.
/// </summary>
/// <remarks>Line reconstruction lives in <see cref="PageTextLayout"/>.</remarks>
public static class TextAnchorDetector
{
    public static List<SignaturePlacement> Detect(PdfDocument document, DetectionSettings settings)
    {
        var matchers = BuildMatchers(settings);
        if (matchers.Count == 0) return [];

        var placements = new List<SignaturePlacement>();

        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            if (placements.Count >= settings.MaxPlacements) break;

            var page = document.GetPage(pageNumber);
            var mediaBox = page.GetPageSize();

            foreach (var line in PageTextLayout.ReadLines(page))
            {
                foreach (var matcher in matchers)
                {
                    foreach (Match match in matcher.Matches(line.Text))
                    {
                        if (match.Length == 0) continue;
                        if (placements.Count >= settings.MaxPlacements) break;

                        var anchorBox = PageTextLayout.UnionOf(line.BoxPerChar, match.Index, match.Length);
                        if (anchorBox is null) continue;

                        var placement = BuildPlacement(
                            anchorBox, mediaBox, page.GetRotation(), pageNumber, settings,
                            match.Value.Trim());

                        if (!IsDuplicate(placements, placement, settings.DeduplicateWithin))
                            placements.Add(placement);
                    }
                }
            }
        }

        return placements
            .OrderBy(p => p.PageNumber)
            .ThenByDescending(p => p.Bottom)
            .ThenBy(p => p.Left)
            .ToList();
    }

    private static List<Regex> BuildMatchers(DetectionSettings settings)
    {
        var options = RegexOptions.CultureInvariant;
        if (!settings.CaseSensitive) options |= RegexOptions.IgnoreCase;

        var matchers = new List<Regex>();

        foreach (var phrase in settings.AnchorPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;

            try
            {
                if (settings.UseRegex)
                {
                    matchers.Add(new Regex(phrase, options, TimeSpan.FromSeconds(2)));
                }
                else
                {
                    // Word boundaries keep "Partner" from matching inside "Partnership", which
                    // otherwise produces a stamp on every balance sheet line.
                    var escaped = Regex.Escape(phrase.Trim());
                    var pattern = char.IsLetterOrDigit(phrase.Trim()[0])
                        ? $@"\b{escaped}\b"
                        : escaped;
                    matchers.Add(new Regex(pattern, options, TimeSpan.FromSeconds(2)));
                }
            }
            catch (ArgumentException)
            {
                // An invalid user-supplied regex is skipped rather than aborting detection.
            }
        }

        return matchers;
    }

    private static SignaturePlacement BuildPlacement(
        Rectangle anchor,
        Rectangle mediaBox,
        int rotation,
        int pageNumber,
        DetectionSettings settings,
        string label)
    {
        var width = settings.StampWidth;
        var height = settings.StampHeight;

        var (left, bottom) = settings.Align switch
        {
            AnchorAlign.Above => (anchor.GetLeft(), anchor.GetTop() + settings.OffsetY),
            AnchorAlign.Below => (anchor.GetLeft(), anchor.GetBottom() - height - settings.OffsetY),
            AnchorAlign.RightOf => (anchor.GetRight() + settings.OffsetX,
                anchor.GetBottom() - (height - anchor.GetHeight()) / 2f),
            AnchorAlign.LeftOf => (anchor.GetLeft() - width - settings.OffsetX,
                anchor.GetBottom() - (height - anchor.GetHeight()) / 2f),
            _ => (anchor.GetLeft(), anchor.GetBottom())
        };

        if (settings.Align is AnchorAlign.Above or AnchorAlign.Below)
            left += settings.OffsetX;

        var placement = new SignaturePlacement
        {
            PageNumber = pageNumber,
            Left = left,
            Bottom = bottom,
            Width = width,
            Height = height,
            Source = PlacementSource.TextAnchor,
            Label = label,
            PageWidth = mediaBox.GetWidth(),
            PageHeight = mediaBox.GetHeight(),
            Rotation = rotation
        };

        placement.ClampToPage();
        return placement;
    }

    private static bool IsDuplicate(
        IEnumerable<SignaturePlacement> existing, SignaturePlacement candidate, float tolerance)
    {
        return existing.Any(p =>
            p.PageNumber == candidate.PageNumber &&
            MathF.Abs(p.Left - candidate.Left) < tolerance &&
            MathF.Abs(p.Bottom - candidate.Bottom) < tolerance);
    }
}
