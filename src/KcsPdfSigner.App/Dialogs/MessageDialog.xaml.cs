using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KcsPdfSigner.App.Services;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>
/// The app's own black message box. Use it everywhere instead of <see cref="MessageBox"/>, so every
/// dialog follows the monochrome theme.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private MessageBoxResult _cancelResult = MessageBoxResult.None;

    private MessageDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
    }

    /// <summary>Shows a message and returns the button pressed, like <see cref="MessageBox.Show(string)"/>.</summary>
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title = App.ProductName,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = Create(owner, message, title, image);
        dialog.AddButtons(buttons);
        dialog.ShowDialog();
        return dialog._result == MessageBoxResult.None ? dialog._cancelResult : dialog._result;
    }

    /// <summary>Asks for one line of text. Returns null when cancelled.</summary>
    public static string? Prompt(
        Window? owner,
        string message,
        string title = App.ProductName,
        string initialValue = "",
        bool isPassword = false)
    {
        var dialog = Create(owner, message, title, MessageBoxImage.None);
        dialog.InputHost.Visibility = Visibility.Visible;

        if (isPassword)
        {
            dialog.InputText.Visibility = Visibility.Collapsed;
            dialog.InputPassword.Visibility = Visibility.Visible;
            dialog.Loaded += (_, _) => dialog.InputPassword.Focus();
        }
        else
        {
            dialog.InputText.Text = initialValue;
            dialog.Loaded += (_, _) =>
            {
                dialog.InputText.Focus();
                dialog.InputText.SelectAll();
            };
        }

        dialog.AddButtons(MessageBoxButton.OKCancel);
        dialog.ShowDialog();

        if (dialog._result != MessageBoxResult.OK) return null;
        return isPassword ? dialog.InputPassword.Password : dialog.InputText.Text;
    }

    private static MessageDialog Create(Window? owner, string message, string title, MessageBoxImage image)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;

        if (owner is not null && owner.IsLoaded && owner != dialog)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var glyphKey = image switch
        {
            MessageBoxImage.Error => "Icon.Error",
            MessageBoxImage.Warning => "Icon.Warning",
            MessageBoxImage.Question => "Icon.Question",
            MessageBoxImage.Information => "Icon.Info",
            _ => null
        };

        if (glyphKey is not null && dialog.TryFindResource(glyphKey) is Geometry geometry)
        {
            dialog.Glyph.Data = geometry;
            dialog.Glyph.Visibility = Visibility.Visible;
        }

        return dialog;
    }

    private void AddButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                _cancelResult = MessageBoxResult.OK;
                break;
            case MessageBoxButton.OKCancel:
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: false);
                AddButton("Cancel", MessageBoxResult.Cancel, isDefault: false, isCancel: true);
                _cancelResult = MessageBoxResult.Cancel;
                break;
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isCancel: false);
                AddButton("No", MessageBoxResult.No, isDefault: false, isCancel: true);
                _cancelResult = MessageBoxResult.No;
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isCancel: false);
                AddButton("No", MessageBoxResult.No, isDefault: false, isCancel: false);
                AddButton("Cancel", MessageBoxResult.Cancel, isDefault: false, isCancel: true);
                _cancelResult = MessageBoxResult.Cancel;
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel
        };

        if (isDefault && TryFindResource("PrimaryButton") is Style primary) button.Style = primary;

        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        };

        ButtonRow.Children.Add(button);
        if (isDefault) Loaded += (_, _) => { if (InputHost.Visibility != Visibility.Visible) button.Focus(); };
    }
}
