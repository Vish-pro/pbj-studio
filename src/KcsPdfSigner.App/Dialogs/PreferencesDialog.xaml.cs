using System.Globalization;
using System.Windows;
using KcsPdfSigner.App.Services;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>Preferences (Ctrl+K). Edits a copy of the settings and applies it when OK is pressed.</summary>
public partial class PreferencesDialog : Window
{
    private readonly AppSettings _settings = AppSettings.Current.Clone();

    public PreferencesDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);

        ReopenLastSession.IsChecked = _settings.ReopenLastSession;
        PromptMultiple.IsChecked = _settings.PromptBeforeClosingMultiple;
        CreateLinks.IsChecked = _settings.CreateLinksFromUrls;
        DetectFields.IsChecked = _settings.DetectFormFields;
        SingleKey.IsChecked = _settings.SingleKeyAccelerators;
        QuickActions.IsChecked = _settings.QuickActionsOnSelection;
        WheelZoom.IsChecked = _settings.MouseWheelZoom;
        FixedResolution.IsChecked = _settings.SnapshotFixedResolution;
        Ppi.Text = _settings.SnapshotPixelsPerInch.ToString(CultureInfo.InvariantCulture);
        OpenForMarkup.IsChecked = _settings.SnapshotOpenForMarkup;
        TouchBox.SelectedIndex = (int)_settings.Touch;
        ScaleBox.SelectedIndex = (int)_settings.Scaling;
        HideWarnings.IsChecked = _settings.HideEditWarnings;
        ShowSuggestionsBox.IsChecked = _settings.ShowSuggestions;
        LargeFileBox.Text = _settings.LargeFileMegabytes.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void OnResetSuggestions(object sender, RoutedEventArgs e)
    {
        var count = _settings.DismissedSuggestions.Count;
        _settings.DismissedSuggestions = [];
        ShowSuggestionsBox.IsChecked = true;
        MessageDialog.Show(this,
            (count == 0 ? "No suggestions were turned off." : $"{count} suggestion(s) will be shown again.") +
            " Press OK to keep this change.", "Suggestions reset");
    }

    private void OnResetWarnings(object sender, RoutedEventArgs e)
    {
        HideWarnings.IsChecked = false;
        MessageDialog.Show(this, "Edit warnings will be shown again. Press OK to keep this change.", "Warnings reset");
    }

    /// <summary>The trust list is saved by its own dialog straight away (OK or Cancel here does not affect it).</summary>
    private void OnTrustedCertificates(object sender, RoutedEventArgs e) =>
        new TrustedCertificatesDialog { Owner = this }.ShowDialog();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Ppi.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppi)
            || ppi < 30 || ppi > 1200)
        {
            MessageDialog.Show(this, "Enter a resolution between 30 and 1200 pixels per inch.", "Preferences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Ppi.Focus();
            return;
        }

        var mbText = LargeFileBox.Text.Trim();
        if (!double.TryParse(mbText, NumberStyles.Float, CultureInfo.CurrentCulture, out var megabytes)
            && !double.TryParse(mbText, NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes)
            || megabytes < AppSettings.MinLargeFileMegabytes || megabytes > 2000)
        {
            MessageDialog.Show(this, "Enter a file size between 0.01 and 2000 MB.", "Preferences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LargeFileBox.Focus();
            return;
        }

        _settings.ShowSuggestions = ShowSuggestionsBox.IsChecked == true;
        _settings.LargeFileMegabytes = megabytes;
        _settings.ReopenLastSession = ReopenLastSession.IsChecked == true;
        _settings.PromptBeforeClosingMultiple = PromptMultiple.IsChecked == true;
        _settings.CreateLinksFromUrls = CreateLinks.IsChecked == true;
        _settings.DetectFormFields = DetectFields.IsChecked == true;
        _settings.SingleKeyAccelerators = SingleKey.IsChecked == true;
        _settings.QuickActionsOnSelection = QuickActions.IsChecked == true;
        _settings.MouseWheelZoom = WheelZoom.IsChecked == true;
        _settings.SnapshotFixedResolution = FixedResolution.IsChecked == true;
        _settings.SnapshotPixelsPerInch = ppi;
        _settings.SnapshotOpenForMarkup = OpenForMarkup.IsChecked == true;
        _settings.Touch = (TouchMode)Math.Max(0, TouchBox.SelectedIndex);
        _settings.Scaling = (ScreenScaling)Math.Max(0, ScaleBox.SelectedIndex);
        _settings.HideEditWarnings = HideWarnings.IsChecked == true;

        // Keep the saved session list; only the Preferences values changed.
        _settings.LastSession = AppSettings.Current.LastSession;
        AppSettings.Apply(_settings);

        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close(); // shown without ShowDialog (tests)
        }
    }
}
