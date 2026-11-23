namespace KcsPdfSigner.Core;

/// <summary>How signature spots are located. Chosen fresh before every signing run.</summary>
public enum DetectionMode
{
    /// <summary>Match keywords in the page text and place a stamp relative to each match.</summary>
    TextAnchors,

    /// <summary>Use unsigned signature fields the PDF already contains.</summary>
    ExistingFields,

    /// <summary>Fixed position on every page (or just the first/last), no detection.</summary>
    FixedPosition,

    /// <summary>Positions placed by hand on the page preview.</summary>
    Manual
}

/// <summary>Where the stamp sits relative to the anchor text that matched.</summary>
public enum AnchorAlign
{
    Above,
    Below,
    RightOf,
    LeftOf,
    OverText
}

public enum PageScope
{
    AllPages,
    FirstPageOnly,
    LastPageOnly
}

public enum StampCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft
}

/// <summary>Everything that governs where placements are found.</summary>
public sealed class DetectionSettings
{
    public DetectionMode Mode { get; set; } = DetectionMode.TextAnchors;

    /// <summary>
    /// Phrases that mark a signing spot. Matched case-insensitively as substrings of a
    /// reconstructed text line. Tuned for Indian professional documents by default.
    /// </summary>
    public List<string> AnchorPhrases { get; set; } =
    [
        "Signature",
        "Authorised Signatory",
        "Authorized Signatory",
        "Partner",
        "Proprietor",
        "Membership No",
        "UDIN"
    ];

    public bool CaseSensitive { get; set; }

    /// <summary>Treat each phrase as a regular expression instead of literal text.</summary>
    public bool UseRegex { get; set; }

    public AnchorAlign Align { get; set; } = AnchorAlign.Above;

    /// <summary>Extra nudge applied after alignment, in PDF points.</summary>
    public float OffsetX { get; set; }

    public float OffsetY { get; set; } = 4f;

    /// <summary>Collapse matches that land within this many points of each other on a page.</summary>
    public float DeduplicateWithin { get; set; } = 24f;

    /// <summary>Guard against a runaway keyword matching hundreds of times.</summary>
    public int MaxPlacements { get; set; } = 100;

    public PageScope FixedScope { get; set; } = PageScope.AllPages;

    public StampCorner FixedCorner { get; set; } = StampCorner.BottomRight;

    public float StampWidth { get; set; } = 170f;

    public float StampHeight { get; set; } = 52f;

    public float StampMargin { get; set; } = 28f;
}

/// <summary>Everything that governs the signatures themselves.</summary>
public sealed class SigningOptions
{
    public string InputPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string ContactInfo { get; set; } = string.Empty;

    /// <summary>
    /// Sign a PDF that its owner protected with a permissions password but that allows signing
    /// (the same permission Acrobat checks). The file stays encrypted and the owner's other
    /// restrictions stay in place. The caller must have warned the user.
    /// </summary>
    public bool AllowRestrictedSigning { get; set; }

    /// <summary>Draw the signer's name and date inside each signature widget.</summary>
    public bool VisibleAppearance { get; set; } = true;

    /// <summary>Optional RFC 3161 timestamp authority. Empty means the local clock is used.</summary>
    public string TimestampUrl { get; set; } = string.Empty;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
            return "Pick a PDF to sign.";

        if (string.IsNullOrWhiteSpace(OutputPath))
            return "Pick where the signed PDF should be written.";

        if (Path.GetFullPath(InputPath).Equals(Path.GetFullPath(OutputPath), StringComparison.OrdinalIgnoreCase))
            return "The output file must differ from the input file.";

        if (!string.IsNullOrWhiteSpace(TimestampUrl) &&
            !Uri.TryCreate(TimestampUrl.Trim(), UriKind.Absolute, out _))
            return "The timestamp server address is not a valid URL.";

        return null;
    }
}
