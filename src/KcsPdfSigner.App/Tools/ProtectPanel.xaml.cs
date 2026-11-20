using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Protect: AES-256 encryption with a password to open and a permissions password, and removing
/// protection. Passwords live only in the PasswordBoxes and the session; they are never shown,
/// logged or put in status text.
/// </summary>
public partial class ProtectPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private bool _fileEncrypted;

    public ProtectPanel()
    {
        InitializeComponent();
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Protect";

    public string Description => "Add or remove a password and restrict printing, copying and changes.";

    public string IconKey => "Icon.Lock";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        RefreshEncryption();
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        ClearPasswords();
        RefreshEncryption();
    }

    // ---- State -------------------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e) => RefreshEncryption();

    private void RefreshEncryption()
    {
        _fileEncrypted = false;
        if (_session is not null)
        {
            try
            {
                _fileEncrypted = PdfInfoReader.IsEncrypted(_session.CurrentFile);
            }
            catch
            {
                _fileEncrypted = _session.IsEncrypted;
            }
        }

        UpdateState();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateState();

    /// <summary>Null when the form is ready to encrypt, otherwise what is missing.</summary>
    internal string? ValidationProblem()
    {
        var open = OpenPasswordBox.Password;
        var owner = OwnerPasswordBox.Password;

        if (open != OpenPasswordRepeatBox.Password) return "The two passwords to open do not match.";
        if (owner != OwnerPasswordRepeatBox.Password) return "The two permissions passwords do not match.";
        if (open.Length == 0 && owner.Length == 0) return "Enter a password to open, a permissions password, or both.";
        if (open.Length == 0 && AllowPrintBox.IsChecked == true && AllowCopyBox.IsChecked == true &&
            AllowModifyBox.IsChecked == true)
            return "With no password to open, untick at least one permission (otherwise nothing is protected).";
        return null;
    }

    private void UpdateState()
    {
        if (!IsInitialized || StatusLine is null) return;

        var session = _session;
        EncryptSection.IsEnabled = session is not null;
        RemoveSection.IsEnabled = session is not null && _fileEncrypted;

        StatusLine.Text = session is null
            ? "Open a PDF to protect it."
            : session.IsEncrypted
                ? session.FileName + " is protected with a password."
                : _fileEncrypted
                    ? session.FileName + " has restrictions but no password to open."
                    : session.FileName + " is not protected.";

        var problem = session is null ? null : ValidationProblem();
        ValidationText.Text = problem ?? "";
        ValidationText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
        EncryptButton.IsEnabled = session is not null && problem is null;
    }

    private void ClearPasswords()
    {
        OpenPasswordBox.Clear();
        OpenPasswordRepeatBox.Clear();
        OwnerPasswordBox.Clear();
        OwnerPasswordRepeatBox.Clear();
    }

    // ---- Actions -----------------------------------------------------------

    private async void OnEncrypt(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || ValidationProblem() is { } problem)
        {
            if (session is not null) PanelHelpers.ShowInfo(_host, "Protect", ValidationProblem()!);
            return;
        }

        var open = OpenPasswordBox.Password;
        // Core would pick a random permissions password when none is given, which would lock this
        // session out of further edits; use the password to open instead.
        var owner = OwnerPasswordBox.Password.Length > 0 ? OwnerPasswordBox.Password : open;
        var allowPrint = AllowPrintBox.IsChecked == true;
        var allowCopy = AllowCopyBox.IsChecked == true;
        var allowModify = AllowModifyBox.IsChecked == true;
        var current = session.Password;

        // The session keeps the permissions password, which opens the file for both viewing and editing.
        var ok = await PanelHelpers.RunBusyAsync(_host, this, "Encrypt",
            () => session.ApplyAsync(
                (input, output) => PdfSecurityOps.Encrypt(input, output, open.Length == 0 ? null : open, owner,
                    allowPrint, allowCopy, allowModify, current),
                "Encrypt", passwordAfter: owner));
        if (!ok) return;

        ClearPasswords();
        RefreshEncryption();
        _host?.SetStatus("Encrypted. Save the document to keep the protection; Ctrl+Z undoes it.");
    }

    private async void OnRemovePassword(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var password = session.Password;
        if (string.IsNullOrEmpty(password))
        {
            password = AskOwnerPassword("This document has restrictions. Enter its permissions password to remove them.");
            if (password is null) return;
        }

        while (true)
        {
            var attempt = password;
            Exception? failure = null;
            await PanelHelpers.RunBusyQuietAsync(this, async () =>
            {
                try
                {
                    await session.ApplyAsync((input, output) => PdfSecurityOps.Decrypt(input, output, attempt),
                        "Remove password", passwordAfter: "");
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (failure is null) break;
            if (failure is SignedChangeDeclinedException declined)
            {
                _host?.SetStatus(declined.Message);
                return;
            }

            // The password to open is often not enough: ask for the permissions password and retry.
            password = AskOwnerPassword(failure.Message + "\n\nEnter the permissions (owner) password to try again.");
            if (password is null) return;
        }

        RefreshEncryption();
        _host?.SetStatus("Password removed. Save the document to keep the change; Ctrl+Z undoes it.");
    }

    private string? AskOwnerPassword(string message)
    {
        var value = MessageDialog.Prompt(_host?.Window, message, "Permissions password", isPassword: true);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
