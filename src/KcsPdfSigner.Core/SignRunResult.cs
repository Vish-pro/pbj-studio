namespace KcsPdfSigner.Core;

public enum SignStatus
{
    Pending,
    Signing,
    Signed,
    Failed
}

public sealed class PlacementResult
{
    public required SignaturePlacement Placement { get; init; }

    /// <summary>The signature field name that was actually signed.</summary>
    public string FieldName { get; set; } = string.Empty;

    public SignStatus Status { get; set; } = SignStatus.Pending;

    public string Message { get; set; } = string.Empty;
}

public sealed class SignRunResult
{
    public required IReadOnlyList<PlacementResult> Results { get; init; }

    public string? OutputPath { get; init; }

    public bool Cancelled { get; init; }

    public TimeSpan Elapsed { get; init; }

    public int Signed => Results.Count(r => r.Status == SignStatus.Signed);

    public int Failed => Results.Count(r => r.Status == SignStatus.Failed);

    /// <summary>
    /// True when nothing was written. Signing is all-or-nothing: a failure part way through
    /// discards the whole run rather than leaving a document signed in some places only.
    /// </summary>
    public bool Aborted => OutputPath is null;
}

public sealed record SignProgress(int Completed, int Total, PlacementResult Current);
