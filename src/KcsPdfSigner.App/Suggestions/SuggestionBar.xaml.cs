using System.Windows;
using System.Windows.Controls;

namespace KcsPdfSigner.App.Suggestions;

/// <summary>One suggestion bar. The main window decides what Dismiss and "Don't show this again" do.</summary>
public partial class SuggestionBar : UserControl
{
    public SuggestionBar(Suggestion suggestion)
    {
        InitializeComponent();
        Suggestion = suggestion;
        MessageText.Text = suggestion.Message;

        if (suggestion.Run is null || string.IsNullOrEmpty(suggestion.ActionLabel))
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActionButton.Content = suggestion.ActionLabel;
        }
    }

    public Suggestion Suggestion { get; }

    public event EventHandler? ActionClicked;

    public event EventHandler? Dismissed;

    public event EventHandler? NeverAgain;

    private void OnAction(object sender, RoutedEventArgs e) => ActionClicked?.Invoke(this, EventArgs.Empty);

    private void OnDismiss(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);

    private void OnNever(object sender, RoutedEventArgs e) => NeverAgain?.Invoke(this, EventArgs.Empty);
}
