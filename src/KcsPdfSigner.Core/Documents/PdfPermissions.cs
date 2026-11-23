using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// What a PDF's owner allows. <see cref="IsRestricted"/> is true when the file is encrypted and was
/// opened without its owner password, so the owner's permission bits apply.
/// </summary>
public sealed record PermissionReport(
    bool IsEncrypted, bool NeedsPassword, bool HasFullAccess, bool AllowsPrinting, bool AllowsSigning,
    bool AllowsCopying = true, bool AllowsModifying = true, bool AllowsAnnotating = true,
    bool AllowsFillIn = true, bool AllowsAssembly = true)
{
    public bool IsRestricted => IsEncrypted && !HasFullAccess;

    /// <summary>
    /// Plain sentences naming what the owner has locked, e.g. "Printing is not allowed." Empty when
    /// nothing is locked (or the file is not restricted).
    /// </summary>
    public List<string> LockedDescriptions()
    {
        var lines = new List<string>();
        if (!IsRestricted || NeedsPassword) return lines;

        if (!AllowsPrinting) lines.Add("Printing is not allowed.");

        var actions = new List<string>();
        if (!AllowsCopying) actions.Add("copying text");
        if (!AllowsModifying) actions.Add("editing");
        if (!AllowsAnnotating) actions.Add("adding comments");
        if (!AllowsFillIn) actions.Add("filling in forms");
        if (!AllowsAssembly) actions.Add("changing pages");
        if (actions.Count > 0)
        {
            var joined = actions.Count == 1
                ? actions[0]
                : string.Join(", ", actions.Take(actions.Count - 1)) + " and " + actions[^1];
            lines.Add(char.ToUpperInvariant(joined[0]) + joined[1..] + (actions.Count == 1 ? " is" : " are") + " not allowed.");
        }

        return lines;
    }
}

/// <summary>Reads a PDF's permission bits (printing, modifying, filling in and signing) the way Acrobat does.</summary>
public static class PdfPermissions
{
    public static PermissionReport Inspect(string path, string? password = null)
    {
        try
        {
            using var reader = new PdfReader(path, PdfIo.ReaderProperties(password));
            using var document = new PdfDocument(reader);
            return FromReader(reader);
        }
        catch (BadPasswordException)
        {
            // A password is needed just to open it; permissions cannot be read without it.
            return new PermissionReport(true, true, false, false, false, false, false, false, false, false);
        }
    }

    /// <summary>Permissions of a reader whose document is already open.</summary>
    internal static PermissionReport FromReader(PdfReader reader)
    {
        if (!reader.IsEncrypted()) return new PermissionReport(false, false, true, true, true);

        var full = reader.IsOpenedWithFullPermission();
        var bits = reader.GetPermissions();
        bool Has(int flag) => full || (bits & flag) != 0;

        var printing = Has(EncryptionConstants.ALLOW_PRINTING | EncryptionConstants.ALLOW_DEGRADED_PRINTING);

        // Acrobat lets a document be signed when the owner allows modifying it or filling in forms
        // (which includes signing).
        var signing = Has(EncryptionConstants.ALLOW_MODIFY_CONTENTS | EncryptionConstants.ALLOW_FILL_IN);

        return new PermissionReport(true, false, full, printing, signing,
            AllowsCopying: Has(EncryptionConstants.ALLOW_COPY),
            AllowsModifying: Has(EncryptionConstants.ALLOW_MODIFY_CONTENTS),
            AllowsAnnotating: Has(EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS),
            AllowsFillIn: Has(EncryptionConstants.ALLOW_FILL_IN | EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS),
            AllowsAssembly: Has(EncryptionConstants.ALLOW_ASSEMBLY | EncryptionConstants.ALLOW_MODIFY_CONTENTS));
    }
}
