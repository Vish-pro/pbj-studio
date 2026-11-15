using System.Text;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Password protection with AES-256.</summary>
public static class PdfSecurityOps
{
    /// <summary>
    /// Encrypts with AES-256. userPassword is needed to open the file (empty = opens freely but
    /// the permissions still apply); ownerPassword unlocks the restrictions (empty = a random one,
    /// so the restrictions cannot be lifted). password is the current password if input is
    /// already encrypted (its owner password).
    /// </summary>
    public static void Encrypt(
        string input, string output, string? userPassword, string? ownerPassword, bool allowPrint,
        bool allowCopy, bool allowModify, string? password = null)
    {
        if (string.IsNullOrEmpty(userPassword) && string.IsNullOrEmpty(ownerPassword) && allowPrint && allowCopy && allowModify)
            throw new ArgumentException("Give a password or restrict at least one permission.");

        var permissions = 0;
        if (allowPrint) permissions |= EncryptionConstants.ALLOW_PRINTING | EncryptionConstants.ALLOW_DEGRADED_PRINTING;
        if (allowCopy) permissions |= EncryptionConstants.ALLOW_COPY | EncryptionConstants.ALLOW_SCREENREADERS;
        if (allowModify)
            permissions |= EncryptionConstants.ALLOW_MODIFY_CONTENTS | EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS |
                           EncryptionConstants.ALLOW_FILL_IN | EncryptionConstants.ALLOW_ASSEMBLY;

        var writerProperties = new WriterProperties().SetStandardEncryption(
            Bytes(userPassword), Bytes(ownerPassword), permissions, EncryptionConstants.ENCRYPTION_AES_256);

        PdfIo.Edit(input, output, password, _ => { }, writerProperties, preserveEncryption: false);
    }

    /// <summary>Writes an unencrypted copy. Needs the owner (permissions) password if the file has one.</summary>
    public static void Decrypt(string input, string output, string password)
    {
        PdfIo.Edit(input, output, password, _ => { }, preserveEncryption: false);
    }

    private static byte[]? Bytes(string? value) =>
        string.IsNullOrEmpty(value) ? null : Encoding.UTF8.GetBytes(value);
}
