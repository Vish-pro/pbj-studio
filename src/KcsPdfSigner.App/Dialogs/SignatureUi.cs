using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Interop;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>Shared wording, glyphs and actions for signature status in the banner, pane and dialogs.</summary>
public static class SignatureUi
{
    public static string GlyphKey(SignatureValidity? status) => status switch
    {
        SignatureValidity.Valid => "Icon.SigValid",
        SignatureValidity.Invalid => "Icon.Error",
        SignatureValidity.Unknown => "Icon.Question",
        _ => "Icon.SigField"
    };

    public static string GlyphKey(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Pass => "Icon.SigValid",
        CheckOutcome.Fail => "Icon.Error",
        CheckOutcome.Warning => "Icon.Warning",
        _ => "Icon.Info"
    };

    public static string StatusWord(SignatureValidity status) => status switch
    {
        SignatureValidity.Valid => "Valid",
        SignatureValidity.Invalid => "INVALID",
        _ => "Validity unknown"
    };

    /// <summary>
    /// Shows the Windows certificate window (read-only view; nothing is installed by this app).
    /// Falls back to a plain text view if Windows cannot show it.
    /// </summary>
    public static void ViewCertificate(Window owner, CertificateSummary summary)
    {
        try
        {
            using var certificate = new X509Certificate2(summary.RawData);
            var handle = new WindowInteropHelper(owner).Handle;
            if (CryptUIDlgViewContext(CertStoreCertificateContext, certificate.Handle, handle, "Certificate", 0, IntPtr.Zero)) return;
        }
        catch (Exception ex)
        {
            App.LogError("Show certificate", ex);
        }

        MessageDialog.Show(owner, Describe(summary), "Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static string Describe(CertificateSummary c) =>
        $"Issued to: {c.Subject}\nIssued by: {c.Issuer}\nSerial number: {c.SerialNumber}\n" +
        $"Valid from {c.NotBefore:dd MMM yyyy HH:mm} to {c.NotAfter:dd MMM yyyy HH:mm}\nSHA-1 thumbprint: {c.Thumbprint}";

    /// <summary>
    /// Asks before adding a certificate to the app's trusted list. Returns true when it was added.
    /// Trust is never given without this question.
    /// </summary>
    public static bool ConfirmAndTrust(Window owner, CertificateSummary certificate)
    {
        if (TrustedCertificates.Contains(certificate.Thumbprint))
        {
            MessageDialog.Show(owner, $"'{certificate.CommonName}' is already in your trusted certificates.", "Trust certificate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var kind = certificate.IsSelfSigned
            ? "This is a root certificate. Every signature whose certificate was issued under it will be treated as trusted."
            : "This is not a root certificate. Signatures whose certificate chain passes through it will be treated as trusted.";

        var message =
            $"Trust '{certificate.CommonName}' for checking signatures in PB&J Studio?\n\n{kind}\n\n" +
            Describe(certificate) + "\n\n" +
            "Only trust a certificate you have confirmed is genuine, for example the CCA India root downloaded from cca.gov.in, " +
            "with the thumbprint compared against the one published there. This affects PB&J Studio only; the Windows " +
            "certificate store is not changed.";

        if (MessageDialog.Show(owner, message, "Trust certificate", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;

        try
        {
            return TrustedCertificates.Add(certificate.RawData);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, "Could not save the trusted certificate.\n\n" + ex.Message, "Trust certificate",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private const uint CertStoreCertificateContext = 1;

    [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUIDlgViewContext(uint contextType, IntPtr context, IntPtr owner, string? title, uint flags, IntPtr reserved);
}
