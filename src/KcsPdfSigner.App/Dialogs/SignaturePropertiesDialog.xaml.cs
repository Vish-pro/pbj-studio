using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>
/// Acrobat-style Signature Properties: status, signer, times, certificate details, the list of
/// checks, and buttons to view or trust a certificate, validate again and copy the details.
/// </summary>
public partial class SignaturePropertiesDialog : Window
{
    private readonly DocumentSession _session;
    private readonly string _fieldName;
    private SignatureVerification? _signature;

    public SignaturePropertiesDialog(DocumentSession session, DocumentVerification result, string fieldName)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _session = session;
        _fieldName = fieldName;
        Render(result);
        ChainBox.SelectionChanged += (_, _) => ShowCertificate();
    }

    private void Render(DocumentVerification result)
    {
        _signature = result.Signatures.FirstOrDefault(s => s.FieldName == _fieldName);
        Details.Children.Clear();
        ChecksPanel.Children.Clear();
        ChainBox.Items.Clear();
        CertificateDetails.Children.Clear();

        if (_signature is null)
        {
            StatusTitle.Text = "This signature is no longer in the document.";
            StatusText.Text = result.DocumentError;
            StatusGlyph.Data = (Geometry)FindResource("Icon.Question");
            ViewButton.IsEnabled = TrustButton.IsEnabled = false;
            return;
        }

        var s = _signature;
        Title = (s.IsDocumentTimestamp ? "Document Timestamp Properties - " : "Signature Properties - ") + s.FieldName;
        StatusGlyph.Data = (Geometry)FindResource(SignatureUi.GlyphKey(s.Status));
        StatusTitle.Text = s.Summary;
        StatusText.Text = $"Checked {result.CheckedAt:dd MMM yyyy HH:mm:ss}." +
                          (result.CertificationLevel > 0 && s.CertificationLevel > 0
                              ? $" This is a certification signature: {DocumentVerification.CertificationDescription(s.CertificationLevel)}."
                              : "");

        Section("Signer");
        Line("Signed by", string.IsNullOrWhiteSpace(s.SignerName) ? "(not given)" : s.SignerName);
        if (s.ClaimedSigningTime is { } claimed)
            Line("Signing time", $"{claimed:dd MMM yyyy HH:mm:ss zzz}" +
                                 (s.TimestampTrusted ? "" : "  (from the clock on the signer's computer)"));
        if (s.TimestampTime is { } stamp)
            Line("Timestamp", $"{stamp:dd MMM yyyy HH:mm:ss zzz} by {s.TimestampAuthority} (RFC 3161)" +
                              (s.TimestampVerified ? s.TimestampTrusted ? ", verified and trusted" : ", verified; authority not trusted" : ", NOT verified"));
        else if (!s.IsDocumentTimestamp)
            Line("Timestamp", "None. The signing time is from the clock on the signer's computer.");
        if (!string.IsNullOrWhiteSpace(s.Reason)) Line("Reason", s.Reason);
        if (!string.IsNullOrWhiteSpace(s.Location)) Line("Location", s.Location);
        if (!string.IsNullOrWhiteSpace(s.ContactInfo)) Line("Contact", s.ContactInfo);

        Section("Signature");
        Line("Field", s.FieldName);
        Line("Type", (s.IsDocumentTimestamp ? "Document timestamp, " : "") + (string.IsNullOrEmpty(s.SubFilter) ? "(unknown)" : s.SubFilter) +
                     (s.SubFilter.Contains("CAdES", StringComparison.OrdinalIgnoreCase) ? " (CAdES / PAdES)" : ""));
        if (!string.IsNullOrEmpty(s.DigestAlgorithm))
            Line("Algorithms", s.DigestAlgorithm + (string.IsNullOrEmpty(s.SignatureAlgorithm) ? "" : ", " + s.SignatureAlgorithm));
        Line("Covers", s.CoversWholeDocument ? "The whole document" :
            $"Revision {s.Revision} of {s.TotalRevisions} (the document was changed after this signature)");
        Line("Position", s.PageNumber > 0
            ? $"Page {s.PageNumber}, {s.Left:0}, {s.Bottom:0} ({s.Width:0} x {s.Height:0} pt)"
            : "Invisible signature (not shown on a page)");

        foreach (var certificate in s.Chain)
            ChainBox.Items.Add(new ComboBoxItem { Content = certificate.CommonName + (certificate.IsSelfSigned ? "  (root)" : ""), Tag = certificate });
        if (ChainBox.Items.Count > 0) ChainBox.SelectedIndex = 0;
        ViewButton.IsEnabled = TrustButton.IsEnabled = ChainBox.Items.Count > 0;
        ShowCertificate();

        foreach (var check in s.Checks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var indented = check.Text.StartsWith("  ", StringComparison.Ordinal);
            var glyph = new Path
            {
                Style = (Style)FindResource("IconPath"),
                Data = (Geometry)FindResource(SignatureUi.GlyphKey(check.Outcome)),
                Margin = new Thickness(indented ? 24 : 0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = check.Outcome == CheckOutcome.Info ? 0.6 : 1
            };
            if (indented) glyph.Visibility = Visibility.Hidden;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(new TextBlock
            {
                Text = (check.Outcome == CheckOutcome.Fail ? "FAILED: " : check.Outcome == CheckOutcome.Warning ? "Warning: " : "") + check.Text.Trim(),
                TextWrapping = TextWrapping.Wrap
            });
            ChecksPanel.Children.Add(row);
        }
    }

    private void ShowCertificate()
    {
        CertificateDetails.Children.Clear();
        if (ChainBox.SelectedItem is not ComboBoxItem { Tag: CertificateSummary c }) return;

        AddTo(CertificateDetails, "Issued to", c.Subject);
        AddTo(CertificateDetails, "Issued by", c.Issuer);
        AddTo(CertificateDetails, "Serial number", c.SerialNumber);
        AddTo(CertificateDetails, "Valid", $"{c.NotBefore:dd MMM yyyy} to {c.NotAfter:dd MMM yyyy}");
        AddTo(CertificateDetails, "Thumbprint", c.Thumbprint);
        AddTo(CertificateDetails, "Trusted", TrustedCertificates.Contains(c.Thumbprint)
            ? "Yes, in your trusted certificates (PB&J Studio)"
            : "Not in your PB&J Studio list (it may still be trusted through Windows)");
    }

    private void Section(string title) =>
        Details.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionHeading") });

    private void Line(string label, string value) => AddTo(Details, label, value);

    private static void AddTo(Panel panel, string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var name = new TextBlock { Text = label, Width = 120, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0)
        });
        panel.Children.Add(row);
    }

    private void OnViewCertificate(object sender, RoutedEventArgs e)
    {
        if (ChainBox.SelectedItem is ComboBoxItem { Tag: CertificateSummary c }) SignatureUi.ViewCertificate(this, c);
    }

    private async void OnTrust(object sender, RoutedEventArgs e)
    {
        if (ChainBox.SelectedItem is not ComboBoxItem { Tag: CertificateSummary c }) return;
        if (SignatureUi.ConfirmAndTrust(this, c)) await Revalidate(force: false);
    }

    private async void OnValidate(object sender, RoutedEventArgs e) => await Revalidate(force: true);

    private async Task Revalidate(bool force)
    {
        ValidateButton.IsEnabled = false;
        StatusText.Text = "Checking...";
        try
        {
            Render(await SignatureChecks.GetAsync(_session, force));
        }
        catch (Exception ex)
        {
            App.LogError("Validate signature", ex);
            StatusText.Text = "Could not check the signature: " + ex.Message;
        }
        finally
        {
            ValidateButton.IsEnabled = true;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_signature is null) return;
        var text = SignatureVerifier.Describe(_signature);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                StatusText.Text = "Details copied to the clipboard.";
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }

        StatusText.Text = "The clipboard is busy. Try again.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
