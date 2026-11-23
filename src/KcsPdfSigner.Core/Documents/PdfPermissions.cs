using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// What a PDF's owner allows. <see cref="IsRestricted"/> is true when the file is encrypted and was
/// opened without its owner password, so the owner's permission bits apply.
/// </summary>
public sealed record PermissionReport(
    bool IsEncrypted, bool NeedsPassword, bool HasFullAccess, bool AllowsPrinting, bool AllowsSigning)
{
    public bool IsRestricted => IsEncrypted && !HasFullAccess;
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

            if (!reader.IsEncrypted()) return new PermissionReport(false, false, true, true, true);

            var full = reader.IsOpenedWithFullPermission();
            var bits = reader.GetPermissions();
            var printing = full || (bits & (EncryptionConstants.ALLOW_PRINTING | EncryptionConstants.ALLOW_DEGRADED_PRINTING)) != 0;

            // Acrobat lets a document be signed when the owner allows modifying it or filling in forms
            // (which includes signing).
            var signing = full || (bits & (EncryptionConstants.ALLOW_MODIFY_CONTENTS | EncryptionConstants.ALLOW_FILL_IN)) != 0;

            return new PermissionReport(true, false, full, printing, signing);
        }
        catch (BadPasswordException)
        {
            // A password is needed just to open it; permissions cannot be read without it.
            return new PermissionReport(true, true, false, false, false);
        }
    }
}
