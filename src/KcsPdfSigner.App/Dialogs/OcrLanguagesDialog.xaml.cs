using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>A row in the scan languages list.</summary>
public sealed class OcrLanguageRow
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required OcrLanguageSource Source { get; init; }
    public OcrLanguage? Language { get; init; }

    /// <summary>"Add" (not installed, in the catalogue), "Remove" (added by the user) or "" (came with the program).</summary>
    public string ActionText => Source switch
    {
        OcrLanguageSource.NotInstalled when Language is not null => "Add",
        OcrLanguageSource.AddedByUser => "Remove",
        _ => ""
    };

    public Visibility ActionVisibility => ActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Scan languages: English and the ten Indian languages, installed or not, with their size. Add downloads
/// the official file (after showing the address and size) into %APPDATA%\KcsPdfStudio\tessdata and checks
/// it (size, header, SHA-256) before use; "Add from a file..." does the same for a file copied from
/// another PC; Remove deletes a language the user added. Languages from the installer cannot be removed here.
/// </summary>
public partial class OcrLanguagesDialog : Window
{
    private CancellationTokenSource? _download;

    public OcrLanguagesDialog()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        IntroText.Text =
            "English and page orientation detection always come with PB&J Studio. Add a language to read scans written in it; " +
            "you can tick several together in the OCR tool. Files come from the official Tesseract project (tessdata_fast on github.com) " +
            "and are checked before they are used. Added languages are kept in " + OcrLanguages.UserFolder + ".";
        Closing += OnClosingWindow;
        Fill();
    }

    /// <summary>The rows shown (for tests): the eleven reading languages, then any other installed pack.</summary>
    public IReadOnlyList<OcrLanguageRow> Rows { get; private set; } = [];

    private void Fill()
    {
        var installed = OcrLanguages.FindInstalled();
        var rows = new List<OcrLanguageRow>();
        foreach (var language in OcrLanguages.ReadingLanguages)
        {
            var source = OcrLanguages.SourceOf(language.Code, installed);
            var size = PanelSize(language.SizeBytes);
            var detail = source switch
            {
                OcrLanguageSource.WithProgram => $"Installed with the program - {size}",
                OcrLanguageSource.AddedByUser => $"Installed (added by you) - {size}",
                _ => $"Not installed - {size} download"
            };
            rows.Add(new OcrLanguageRow { Code = language.Code, Name = language.Name, Detail = detail, Source = source, Language = language });
        }

        foreach (var (code, path) in installed.Where(i => i.Key != OcrLanguages.OsdCode && OcrLanguages.Find(i.Key) is null).OrderBy(i => i.Key))
        {
            var source = OcrLanguages.SourceOf(code, installed);
            rows.Add(new OcrLanguageRow
            {
                Code = code, Name = code, Source = source,
                Detail = $"Other language pack ({(source == OcrLanguageSource.AddedByUser ? "added by you" : "with the program")}) - {PanelSize(new FileInfo(path).Length)}"
            });
        }

        Rows = rows;
        LanguageRows.ItemsSource = rows;
    }

    private static string PanelSize(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";

    private async void OnRowAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OcrLanguageRow row }) return;
        if (row.Source == OcrLanguageSource.AddedByUser) Remove(row);
        else if (row.Language is not null) await DownloadAsync(row.Language);
    }

    private void Remove(OcrLanguageRow row)
    {
        if (MessageDialog.Show(this, $"Remove {row.Name}? You can add it again later.", "Scan languages",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            OcrLanguages.RemoveUserLanguage(row.Code);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not remove it:\n\n" + ex.Message, "Scan languages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Fill();
    }

    /// <summary>Asks first (address and size), then downloads with progress, checks and installs.</summary>
    internal async Task DownloadAsync(OcrLanguage language)
    {
        var question =
            $"Download {language.Name} ({PanelSize(language.SizeBytes)})?\n\n" +
            $"From: {language.Url}\n" +
            $"To: {Path.Combine(OcrLanguages.UserFolder, language.FileName)}\n\n" +
            "This is the official Tesseract language file. PB&J Studio checks its size and checksum before using it.";
        if (MessageDialog.Show(this, question, "Add scan language", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        _download = new CancellationTokenSource();
        SetBusy(true, $"Downloading {language.Name}...");
        DownloadProgress.Maximum = language.SizeBytes;
        DownloadProgress.Value = 0;
        var progress = new Progress<long>(bytes =>
        {
            DownloadProgress.Value = Math.Min(bytes, language.SizeBytes);
            DownloadText.Text = $"Downloading {language.Name}: {PanelSize(bytes)} of {PanelSize(language.SizeBytes)}";
        });

        try
        {
            await OcrLanguages.DownloadAsync(language, null, progress, _download.Token);
            Fill();
            MessageDialog.Show(this, $"{language.Name} was added. Tick it in the OCR tool to read scans in {language.Name}.",
                "Scan languages", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: nothing was kept.
        }
        catch (InvalidOperationException ex)
        {
            MessageDialog.Show(this, ex.Message, "Scan languages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            App.LogError("Download scan language", ex);
            MessageDialog.Show(this, "The download failed:\n\n" + ex.Message, "Scan languages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _download.Dispose();
            _download = null;
            SetBusy(false, "");
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _download?.Cancel();

    private void OnAddFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a scan language file",
            Filter = "Tesseract language files (*.traineddata)|*.traineddata"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var code = OcrLanguages.InstallFromFile(dialog.FileName);
            Fill();
            var name = OcrLanguages.Find(code)?.Name ?? code;
            MessageDialog.Show(this, $"{name} was added.", "Scan languages", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "That file was not added:\n\n" + ex.Message, "Scan languages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetBusy(bool busy, string text)
    {
        DownloadPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadText.Text = text;
        LanguageRows.IsEnabled = !busy;
        AddFromFileButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        // Closing during a download stops it (the half-downloaded file is removed).
        _download?.Cancel();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
