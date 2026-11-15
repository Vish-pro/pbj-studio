using iText.Kernel.Geom;

namespace KcsPdfSigner.Core;

/// <summary>How a placement was arrived at. Shown in the UI so a detected spot can be judged.</summary>
public enum PlacementSource
{
    /// <summary>Matched a keyword in the page text.</summary>
    TextAnchor,

    /// <summary>An unsigned signature field that already existed in the PDF.</summary>
    ExistingField,

    /// <summary>Fixed position applied to every page.</summary>
    FixedPosition,

    /// <summary>Positioned by hand on the page preview.</summary>
    Manual
}

/// <summary>
/// One spot in the PDF that will receive its own signature field and its own signature.
/// Coordinates are PDF user-space points with the origin at the bottom-left of the page.
/// </summary>
public sealed class SignaturePlacement
{
    public required int PageNumber { get; init; }

    public required float Left { get; set; }

    public required float Bottom { get; set; }

    public required float Width { get; set; }

    public required float Height { get; set; }

    public required PlacementSource Source { get; init; }

    /// <summary>Human-readable note about the origin, e.g. the anchor text that matched.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Set when the placement is an existing unsigned field in the PDF. That field is signed
    /// directly rather than a new one being created at these coordinates.
    /// </summary>
    public string? ExistingFieldName { get; init; }

    /// <summary>Unchecking this in the UI leaves the spot unsigned.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Page dimensions of the unrotated media box, kept so the UI can map these points onto a
    /// rendered preview. All placement coordinates live in this unrotated space, which is what
    /// iText uses for signature widget rectangles.
    /// </summary>
    public required float PageWidth { get; init; }

    public required float PageHeight { get; init; }

    /// <summary>The page's /Rotate value. The preview overlay applies this; signing does not.</summary>
    public int Rotation { get; init; }

    public Rectangle ToRectangle() => new(Left, Bottom, Width, Height);

    public string Describe() =>
        $"Page {PageNumber} · {Source} · {Left:0}, {Bottom:0} ({Width:0}×{Height:0})" +
        (string.IsNullOrWhiteSpace(Label) ? string.Empty : $" · {Label}");

    /// <summary>Nudges the placement back inside the page if an offset pushed it over an edge.</summary>
    public void ClampToPage(float margin = 2f)
    {
        Left = Math.Clamp(Left, margin, Math.Max(margin, PageWidth - Width - margin));
        Bottom = Math.Clamp(Bottom, margin, Math.Max(margin, PageHeight - Height - margin));
    }
}
