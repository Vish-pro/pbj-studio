using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>A row in the trusted certificates list.</summary>
public sealed class TrustedCertificateRow
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required TrustedCertificateEntry Entry { get; init; }
}

/// <summary>Lists, adds and removes the certificates PB&amp;J Studio trusts for signature checks.</summary>
public partial class TrustedCertificatesDialog : Window
{
    public TrustedCertificatesDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        Fill();
    }

    private void Fill()
    {
        var rows = TrustedCertificates.Entries.Select(e =>
        {
            var summary = Summary(e);
            return new TrustedCertificateRow
            {
                Title = summary?.CommonName ?? e.Subject,
                Detail = $"Issued by {summary?.IssuerCommonName ?? e.Issuer}. Valid to {e.ValidTo}. Thumbprint {e.Thumbprint}. Added {e.Added}.",
                Entry = e
            };
        }).ToList();

        CertificateList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = CertificateList.SelectedItem is TrustedCertificateRow;
        ViewButton.IsEnabled = selected;
        RemoveButton.IsEnabled = selected;
    }

    private static CertificateSummary? Summary(TrustedCertificateEntry entry)
    {
        try
        {
            using var certificate = new X509Certificate2(Convert.FromBase64String(entry.Certificate));
            return CertificateSummary.From(certificate);
        }
        catch
        {
            return null;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a trusted certificate",
            Filter = "Certificates (*.cer;*.crt;*.der;*.pem;*.p7b)|*.cer;*.crt;*.der;*.pem;*.p7b|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var der = TrustedCertificates.ReadCertificateFile(dialog.FileName);
            using var certificate = new X509Certificate2(der);
            SignatureUi.ConfirmAndTrust(this, CertificateSummary.From(certificate));
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not read the certificate.\n\n" + ex.Message, "Add certificate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Fill();
    }

    private void OnView(object sender, RoutedEventArgs e)
    {
        if (CertificateList.SelectedItem is TrustedCertificateRow { Entry: var entry } && Summary(entry) is { } summary)
            SignatureUi.ViewCertificate(this, summary);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (CertificateList.SelectedItem is not TrustedCertificateRow row) return;
        var answer = MessageDialog.Show(this, $"Stop trusting '{row.Title}' for signature checks?", "Remove certificate",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            TrustedCertificates.Remove(row.Entry.Thumbprint);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not save the list.\n\n" + ex.Message, "Remove certificate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Fill();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
