using System.Windows;
using KcsPdfSigner.App.Services;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>Asks at start-up whether PB&amp;J Studio should open PDF files (see <see cref="DefaultPdfApp"/>).</summary>
public partial class DefaultAppDialog : Window
{
    public DefaultAppDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
    }

    /// <summary>Shows the question when it is wanted and the app is not already the default.</summary>
    public static void AskIfNeeded(Window owner)
    {
        if (!AppSettings.Current.AskDefaultPdfApp || DefaultPdfApp.IsDefault()) return;
        var dialog = new DefaultAppDialog { Owner = owner };
        var yes = dialog.ShowDialog() == true;
        if (dialog.DontAsk.IsChecked == true)
        {
            AppSettings.Current.AskDefaultPdfApp = false;
            AppSettings.Save();
        }

        if (!yes) return;
        try
        {
            DefaultPdfApp.OpenWindowsChoice();
        }
        catch (Exception ex)
        {
            App.LogError("Opening the default apps page", ex);
            MessageDialog.Show(owner, "Open Windows Settings > Apps > Default apps and choose PB&J Studio for .pdf.",
                App.ProductName);
        }
    }

    private void OnYes(object sender, RoutedEventArgs e) => Finish(true);

    private void OnNo(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close(); // shown without ShowDialog (tests)
        }
    }
}
