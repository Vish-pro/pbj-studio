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
    }

    private void OnResetWarnings(object sender, RoutedEventArgs e)
    {
        HideWarnings.IsChecked = false;
        MessageDialog.Show(this, "Edit warnings will be shown again. Press OK to keep this change.", "Warnings reset");
    }

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
