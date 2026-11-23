using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App;

/// <summary>
/// Signature validation in the main window: the banner above the document, the status badge on
/// each signature box, and the Sign menu commands. The check itself runs once per document
/// version in <see cref="SignatureChecks"/>; the banner, the Signatures pane and the badges share it.
/// </summary>
public partial class MainWindow
{
    private static readonly object SignatureBadgeTag = new();
    private int _bannerGeneration;
    private RelayCommand? _validateSignaturesCommand;

    public RelayCommand ValidateSignaturesCommand => _validateSignaturesCommand ??= new RelayCommand(ValidateSignatures, HasDocument);

    private void InitSignatures()
    {
        TrustedCertificates.Changed += (_, _) => RefreshSignatureViews();
        NavPane.SignaturesRechecked += (_, _) => UpdateSignatureBanner();
    }

    /// <summary>Hooks one document tab: re-check after every change, redraw badges with the page layout.</summary>
    private void AttachSignatureChecks(DocumentTab tab)
    {
        tab.Session.DocumentChanged += (_, _) =>
        {
            tab.Viewer.ClearOverlay(SignatureBadgeTag);
            if (ReferenceEquals(tab, _active)) UpdateSignatureBanner();
        };
        tab.Viewer.OverlayLayoutChanged += (_, _) => DrawSignatureBadges(tab);
    }

    /// <summary>Trust list changed: every open document is checked again.</summary>
    private void RefreshSignatureViews()
    {
        NavPane.LoadSignatures();
        UpdateSignatureBanner();
    }

    private void ValidateSignatures()
    {
        if (_active is null) return;
        if (!NavPane.IsVisible && !_isFullScreen) SetNavPaneVisible(true, remember: true);
        NavPane.ShowSignaturesTab();
        NavPane.ValidateAll();
        SetStatus("Checking the signatures...");
    }

    private void OnSignaturePanel(object sender, RoutedEventArgs e)
    {
        if (!NavPane.IsVisible && !_isFullScreen) SetNavPaneVisible(true, remember: true);
        NavPane.ShowSignaturesTab();
    }

    private void OnTrustedCertificates(object sender, RoutedEventArgs e) =>
        new TrustedCertificatesDialog { Owner = this }.ShowDialog();

    /// <summary>Shows the banner for the active document once its check has finished.</summary>
    private async void UpdateSignatureBanner()
    {
        var generation = ++_bannerGeneration;
        var tab = _active;
        if (tab is null)
        {
            SignatureBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var file = tab.Session.CurrentFile;
        DocumentVerification result;
        try
        {
            result = await SignatureChecks.GetAsync(tab.Session);
        }
        catch (Exception ex)
        {
            App.LogError("Check signatures", ex);
            return;
        }

        if (generation != _bannerGeneration || !ReferenceEquals(tab, _active) || file != tab.Session.CurrentFile) return;

        var show = result.IsSigned || result.UnsignedFields.Count > 0 || !string.IsNullOrEmpty(result.DocumentError);
        SignatureBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BannerText.Text = result.BannerText;
        BannerDetail.Text = result.BannerDetail;
        BannerGlyph.Data = TryFindResource(SignatureUi.GlyphKey(result.Overall)) as Geometry;
        DrawSignatureBadges(tab);

        if (result.IsSigned && result.Overall is { } overall)
            SetStatus(overall == SignatureValidity.Valid ? "Signatures checked: all valid." : "Signatures checked: " + result.BannerText);
    }

    /// <summary>A small tick, question mark or cross at the corner of each signature box on the page.</summary>
    private void DrawSignatureBadges(DocumentTab tab)
    {
        var viewer = tab.Viewer;
        viewer.ClearOverlay(SignatureBadgeTag);
        var result = SignatureChecks.Peek(tab.Session);
        if (result is null) return;

        foreach (var signature in result.Signatures)
        {
            if (signature.PageNumber <= 0 || signature.Width <= 0 || signature.Height <= 0) continue;
            var overlay = viewer.GetOverlay(signature.PageNumber);
            var rect = viewer.PdfRectToOverlay(signature.PageNumber, signature.Left, signature.Bottom, signature.Width, signature.Height);
            if (overlay is null || rect.IsEmpty) continue;

            var glyph = new Path
            {
                Style = (Style)FindResource("IconPath"),
                Data = TryFindResource(SignatureUi.GlyphKey(signature.Status)) as Geometry,
                Stroke = Brushes.Black
            };
            var badge = new Border
            {
                Width = 20,
                Height = 20,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Child = glyph,
                IsHitTestVisible = false,
                Tag = SignatureBadgeTag
            };
            Canvas.SetLeft(badge, Math.Max(0, rect.Left - 10));
            Canvas.SetTop(badge, Math.Max(0, rect.Top - 10));
            overlay.Children.Add(badge);
        }
    }
}
