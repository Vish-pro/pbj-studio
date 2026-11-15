using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace KcsPdfSigner.Core;

/// <summary>
/// Shared glyph and line reconstruction used by text-anchor detection, search and extraction.
/// </summary>
/// <remarks>
/// iText hands text back as arbitrary chunks in drawing order, not as lines, so this walks
/// individual glyphs, regroups them into visual lines by baseline, and keeps a glyph-to-index
/// map. That map is what lets a phrase match be turned back into a rectangle on the page.
/// </remarks>
internal static class PageTextLayout
{
    internal sealed record Glyph(string Text, Rectangle Box, float Baseline);

    internal sealed record TextLine(string Text, IReadOnlyList<Rectangle> BoxPerChar);

    /// <summary>Reads one page and returns its visual lines, top to bottom.</summary>
    internal static List<TextLine> ReadLines(PdfPage page)
    {
        var collector = new GlyphCollector();
        new PdfCanvasProcessor(collector).ProcessPageContent(page);
        return BuildLines(collector.Glyphs);
    }

    /// <summary>
    /// Regroups glyphs into visual lines. Glyphs sharing a baseline (within a tolerance scaled to
    /// their own height) belong to one line; a horizontal gap wider than a quarter of the glyph
    /// height is treated as a word space, since PDFs frequently omit space glyphs entirely.
    /// </summary>
    internal static List<TextLine> BuildLines(List<Glyph> glyphs)
    {
        var lines = new List<TextLine>();
        if (glyphs.Count == 0) return lines;

        foreach (var group in glyphs
                     .GroupBy(g => MathF.Round(g.Baseline, 1))
                     .OrderByDescending(g => g.Key))
        {
            var ordered = group.OrderBy(g => g.Box.GetLeft()).ToList();
            var builder = new StringBuilder();
            var boxes = new List<Rectangle>();
            Rectangle? previous = null;

            foreach (var glyph in ordered)
            {
                if (previous is not null)
                {
                    var gap = glyph.Box.GetLeft() - previous.GetRight();
                    var threshold = MathF.Max(glyph.Box.GetHeight() * 0.25f, 0.8f);

                    if (gap > threshold && !glyph.Text.StartsWith(' ') && builder.Length > 0 &&
                        builder[^1] != ' ')
                    {
                        builder.Append(' ');
                        boxes.Add(glyph.Box);
                    }
                }

                foreach (var _ in glyph.Text)
                {
                    boxes.Add(glyph.Box);
                }

                builder.Append(glyph.Text);
                previous = glyph.Box;
            }

            if (builder.Length > 0)
                lines.Add(new TextLine(builder.ToString(), boxes));
        }

        return lines;
    }

    internal static Rectangle? UnionOf(IReadOnlyList<Rectangle> boxes, int start, int length)
    {
        if (start >= boxes.Count) return null;

        var end = Math.Min(start + length, boxes.Count);
        float left = float.MaxValue, bottom = float.MaxValue;
        float right = float.MinValue, top = float.MinValue;

        for (var i = start; i < end; i++)
        {
            left = MathF.Min(left, boxes[i].GetLeft());
            bottom = MathF.Min(bottom, boxes[i].GetBottom());
            right = MathF.Max(right, boxes[i].GetRight());
            top = MathF.Max(top, boxes[i].GetTop());
        }

        return right <= left ? null : new Rectangle(left, bottom, right - left, top - bottom);
    }

    /// <summary>Collects every glyph drawn on a page together with its bounding box.</summary>
    internal sealed class GlyphCollector : IEventListener
    {
        public List<Glyph> Glyphs { get; } = [];

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo render) return;

            foreach (var character in render.GetCharacterRenderInfos())
            {
                var text = character.GetText();
                if (string.IsNullOrEmpty(text)) continue;

                var descentStart = character.GetDescentLine().GetStartPoint();
                var ascentEnd = character.GetAscentLine().GetEndPoint();

                var x0 = MathF.Min(descentStart.Get(Vector.I1), ascentEnd.Get(Vector.I1));
                var x1 = MathF.Max(descentStart.Get(Vector.I1), ascentEnd.Get(Vector.I1));
                var y0 = MathF.Min(descentStart.Get(Vector.I2), ascentEnd.Get(Vector.I2));
                var y1 = MathF.Max(descentStart.Get(Vector.I2), ascentEnd.Get(Vector.I2));

                if (x1 - x0 <= 0 && text.Trim().Length > 0) continue;

                var baseline = character.GetBaseline().GetStartPoint().Get(Vector.I2);
                Glyphs.Add(new Glyph(text, new Rectangle(x0, y0, MathF.Max(x1 - x0, 0.01f),
                    MathF.Max(y1 - y0, 0.01f)), baseline));
            }
        }

        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT];
    }
}
