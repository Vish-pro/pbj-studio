using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.Core;
using Microsoft.Win32;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// State and commands of the Multi-Sign (DSC) tool: certificate, detection of signing spots on the
/// active document, the per-spot review list, and the all-or-nothing signing run.
/// </summary>
public sealed class MultiSignViewModel : INotifyPropertyChanged
{
    /// <summary>Placements and output path are remembered per open document.</summary>
    private sealed class SessionState
    {
        public List<PlacementRow> Placements { get; } = [];
        public string OutputPath { get; set; } = string.Empty;
    }

    private readonly MultiSignatureSigner _signer = new();
    private readonly StringBuilder _log = new();
    private readonly Dictionary<DocumentSession, SessionState> _states = [];

    private DocumentSession? _session;
    private CancellationTokenSource? _cancellation;

    public MultiSignViewModel()
    {
        RefreshCertificatesCommand = new RelayCommand(RefreshCertificates);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => HasPdf && IsIdle);
        DetectCommand = new RelayCommand(Detect, () => HasPdf && !IsBusy);
        ClearPlacementsCommand = new RelayCommand(ClearPlacements, () => Placements.Count > 0 && !IsBusy);
        SignCommand = new RelayCommand(async void () => await SignAsync(), CanSign);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);

        // Reading the certificate store can be slow with a token plugged in; do it once the window is up.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            RefreshCertificates, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Set by the panel; used for dialogs and to open the signed file.</summary>
    public IStudioHost? Host { get; set; }

    private Window? Owner => Host?.Window;

    // ---- Certificate -------------------------------------------------------

    public ObservableCollection<SigningCertificate> Certificates { get; } = [];

    private SigningCertificate? _selectedCertificate;
    public SigningCertificate? SelectedCertificate
    {
        get => _selectedCertificate;
        set
        {
            _selectedCertificate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CertificateDetails));
        }
    }

    private bool _includeExpiredCertificates;
    public bool IncludeExpiredCertificates
    {
        get => _includeExpiredCertificates;
        set { _includeExpiredCertificates = value; OnPropertyChanged(); RefreshCertificates(); }
    }

    public string CertificateDetails
    {
        get
        {
            if (SelectedCertificate is null) return "No certificate selected.";

            var certificate = SelectedCertificate;
            var kind = certificate.IsHardwareToken
                ? $"Hardware token ({certificate.ProviderName})"
                : $"Software key ({certificate.ProviderName})";

            return $"Issued to: {certificate.CommonName}\n" +
                   $"Issued by: {certificate.IssuerName}\n" +
                   $"Valid: {certificate.ValidFrom:dd-MMM-yyyy} to {certificate.ValidTo:dd-MMM-yyyy}\n" +
                   $"Serial: {certificate.SerialNumber}\n" +
                   kind;
        }
    }

    // ---- Document ----------------------------------------------------------

    public DocumentSession? Session => _session;

    public bool HasPdf => _session is not null && File.Exists(_session.CurrentFile);

    public string DocumentName => _session is null
        ? "No document open. Open a PDF to sign it."
        : _session.DisplayName + $"  ({_session.PageCount} page(s))";

    public string OutputPath
    {
        get => _session is not null && _states.TryGetValue(_session, out var state) ? state.OutputPath : string.Empty;
        set
        {
            if (_session is null) return;
            StateFor(_session).OutputPath = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Switches to another document (or none). Placements of each document are kept.</summary>
    public void SetSession(DocumentSession? session)
    {
        if (ReferenceEquals(session, _session)) return;

        if (_session is not null)
        {
            _session.DocumentChanged -= OnDocumentChanged;
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }

        _session = session;
        Placements.Clear();

        if (session is not null)
        {
            session.DocumentChanged += OnDocumentChanged;
            session.PropertyChanged += OnSessionPropertyChanged;

            var state = StateFor(session);
            if (string.IsNullOrWhiteSpace(state.OutputPath)) state.OutputPath = DefaultOutputPath(session);
            foreach (var row in state.Placements) Placements.Add(row);
        }

        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasPdf));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(OutputPath));
        PlacementsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forget a closed document.</summary>
    public void Forget(DocumentSession session)
    {
        _states.Remove(session);
        if (ReferenceEquals(session, _session)) SetSession(null);
    }

    private SessionState StateFor(DocumentSession session)
    {
        if (!_states.TryGetValue(session, out var state))
        {
            state = new SessionState();
            _states[session] = state;
        }

        return state;
    }

    private static string DefaultOutputPath(DocumentSession session)
    {
        var folder = Path.GetDirectoryName(session.OriginalPath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(session.OriginalPath);
        return Path.Combine(folder, name + "_signed.pdf");
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentSession.DisplayName)) OnPropertyChanged(nameof(DocumentName));
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // Spots found on the previous version may no longer line up with the content.
        if (_session is null) return;
        var state = StateFor(_session);
        if (state.Placements.Count > 0)
        {
            state.Placements.Clear();
            Placements.Clear();
            Append("The document changed, so the signing spots were cleared. Detect again.");
            PlacementsChanged?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(DocumentName));
    }

    // ---- Placements --------------------------------------------------------

    public ObservableCollection<PlacementRow> Placements { get; } = [];

    /// <summary>Raised when the overlay needs redrawing.</summary>
    public event EventHandler? PlacementsChanged;

    public DetectionSettings Settings { get; } = new();

    public DetectionMode SelectedMode
    {
        get => Settings.Mode;
        set
        {
            if (Settings.Mode == value) return;
            Settings.Mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnchorMode));
            OnPropertyChanged(nameof(IsFixedMode));
            OnPropertyChanged(nameof(IsExistingFieldMode));
            OnPropertyChanged(nameof(IsManualMode));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public bool IsAnchorMode
    {
        get => SelectedMode == DetectionMode.TextAnchors;
        set { if (value) SelectedMode = DetectionMode.TextAnchors; }
    }

    public bool IsExistingFieldMode
    {
        get => SelectedMode == DetectionMode.ExistingFields;
        set { if (value) SelectedMode = DetectionMode.ExistingFields; }
    }

    public bool IsFixedMode
    {
        get => SelectedMode == DetectionMode.FixedPosition;
        set { if (value) SelectedMode = DetectionMode.FixedPosition; }
    }

    public bool IsManualMode
    {
        get => SelectedMode == DetectionMode.Manual;
        set { if (value) SelectedMode = DetectionMode.Manual; }
    }

    public string ModeHint => SelectedMode switch
    {
        DetectionMode.TextAnchors =>
            "Finds each keyword in the page text and puts a signature beside it. " +
            "Always check the results below before signing.",
        DetectionMode.ExistingFields =>
            "Uses unsigned signature fields the PDF already contains. The most reliable mode, " +
            "but only works when the document was built with signature fields.",
        DetectionMode.FixedPosition =>
            "Ignores the content and stamps the same spot on the pages you choose.",
        _ => "Click on any page in the document to drop a signature there. Press Detect to start fresh."
    };

    public string AnchorPhrasesText
    {
        get => string.Join(Environment.NewLine, Settings.AnchorPhrases);
        set
        {
            Settings.AnchorPhrases = value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            OnPropertyChanged();
        }
    }

    public Array AlignOptions => Enum.GetValues<AnchorAlign>();
    public Array ScopeOptions => Enum.GetValues<PageScope>();
    public Array CornerOptions => Enum.GetValues<StampCorner>();

    public AnchorAlign Align
    {
        get => Settings.Align;
        set { Settings.Align = value; OnPropertyChanged(); }
    }

    public PageScope FixedScope
    {
        get => Settings.FixedScope;
        set { Settings.FixedScope = value; OnPropertyChanged(); }
    }

    public StampCorner FixedCorner
    {
        get => Settings.FixedCorner;
        set { Settings.FixedCorner = value; OnPropertyChanged(); }
    }

    public float StampWidth
    {
        get => Settings.StampWidth;
        set { Settings.StampWidth = value; OnPropertyChanged(); }
    }

    public float StampHeight
    {
        get => Settings.StampHeight;
        set { Settings.StampHeight = value; OnPropertyChanged(); }
    }

    public float OffsetX
    {
        get => Settings.OffsetX;
        set { Settings.OffsetX = value; OnPropertyChanged(); }
    }

    public float OffsetY
    {
        get => Settings.OffsetY;
        set { Settings.OffsetY = value; OnPropertyChanged(); }
    }

    // ---- Signature details -------------------------------------------------

    public SigningOptions Options { get; } = new();

    public string Reason
    {
        get => Options.Reason;
        set { Options.Reason = value; OnPropertyChanged(); }
    }

    public string Location
    {
        get => Options.Location;
        set { Options.Location = value; OnPropertyChanged(); }
    }

    public bool VisibleAppearance
    {
        get => Options.VisibleAppearance;
        set { Options.VisibleAppearance = value; OnPropertyChanged(); }
    }

    public string TimestampUrl
    {
        get => Options.TimestampUrl;
        set { Options.TimestampUrl = value; OnPropertyChanged(); }
    }

    // ---- Run state ---------------------------------------------------------

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;

    private string _statusText = "Ready.";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set { _progressValue = value; OnPropertyChanged(); }
    }

    private double _progressMaximum = 1;
    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set { _progressMaximum = value; OnPropertyChanged(); }
    }

    public string LogText => _log.ToString();

    // ---- Commands ----------------------------------------------------------

    public RelayCommand RefreshCertificatesCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand ClearPlacementsCommand { get; }
    public RelayCommand SignCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void RefreshCertificates()
    {
        var previousThumbprint = SelectedCertificate?.Certificate.Thumbprint;

        Certificates.Clear();
        foreach (var certificate in SigningCertificate.Discover(IncludeExpiredCertificates))
        {
            Certificates.Add(certificate);
        }

        SelectedCertificate =
            Certificates.FirstOrDefault(c => c.Certificate.Thumbprint == previousThumbprint)
            ?? Certificates.FirstOrDefault();

        Append(Certificates.Count == 0
            ? "No signing certificates found. Plug in the DSC token and press Refresh."
            : $"Found {Certificates.Count} signing certificate(s).");
    }

    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the signed PDF as",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = string.IsNullOrWhiteSpace(OutputPath)
                ? "signed.pdf"
                : Path.GetFileName(OutputPath)
        };

        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            var folder = Path.GetDirectoryName(OutputPath);
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(Owner) == true) OutputPath = dialog.FileName;
    }

    private void Detect()
    {
        if (_session is null) return;
        var state = StateFor(_session);

        Placements.Clear();
        state.Placements.Clear();

        if (SelectedMode == DetectionMode.Manual)
        {
            Append("Manual mode: click on the document to place signatures.");
            PlacementsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var found = PlacementDetector.Detect(_session.CurrentFile, Settings);
            foreach (var placement in found)
            {
                var row = new PlacementRow(placement);
                state.Placements.Add(row);
                Placements.Add(row);
            }

            Append(found.Count == 0
                ? $"{SelectedMode} found nothing. Try another mode, or place signatures by hand."
                : $"{SelectedMode} found {found.Count} spot(s). Review them before signing.");

            if (found.Count > 0) FocusRequested?.Invoke(this, found[0].PageNumber);
        }
        catch (Exception ex)
        {
            Append("Detection failed: " + ex.Message);
        }

        PlacementsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the panel to bring a page into view (first detected spot).</summary>
    public event EventHandler<int>? FocusRequested;

    private void ClearPlacements()
    {
        if (_session is not null) StateFor(_session).Placements.Clear();
        Placements.Clear();
        Append("Cleared all placements.");
        PlacementsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a placement centred where the user clicked on a page (PDF points).</summary>
    public void AddManualPlacement(PdfPageInfo page, float x, float y)
    {
        if (_session is null) return;

        var placement = PlacementDetector.CreateManual(page, x, y, Settings);
        var row = new PlacementRow(placement);
        StateFor(_session).Placements.Add(row);
        Placements.Add(row);
        Append($"Placed a signature by hand on page {placement.PageNumber}.");
        PlacementsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemovePlacement(PlacementRow row)
    {
        if (_session is not null) StateFor(_session).Placements.Remove(row);
        Placements.Remove(row);
        PlacementsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSign() =>
        !IsBusy && HasPdf && SelectedCertificate is not null && Placements.Any(p => p.Enabled);

    private async Task SignAsync()
    {
        if (SelectedCertificate is null || _session is null) return;
        var session = _session;

        if (session.IsEncrypted)
        {
            MessageDialog.Show(Owner,
                "This PDF is protected by a password. Remove the password first, then sign the unprotected copy.",
                "Check the settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The working copy holds any unsaved edits, so those are signed too.
        Options.InputPath = session.CurrentFile;
        Options.OutputPath = OutputPath;

        var problem = Options.Validate();
        if (problem is null &&
            string.Equals(Path.GetFullPath(OutputPath), Path.GetFullPath(session.OriginalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            problem = "The output file must differ from the input file.";
        }

        if (problem is not null)
        {
            MessageDialog.Show(Owner, problem, "Check the settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var enabled = Placements.Where(p => p.Enabled).ToList();

        var confirmation =
            $"Sign {enabled.Count} location(s) in {session.FileName}?\n\n" +
            $"Certificate: {SelectedCertificate.CommonName}\n" +
            $"Located by: {SelectedMode}\n" +
            $"Output: {OutputPath}\n\n" +
            (session.IsDirty ? "Unsaved changes in this document are included in the signed copy.\n\n" : string.Empty) +
            "Each location gets its own signature, so the token performs " +
            $"{enabled.Count} signing operation(s). Your token may ask for the PIN more than once.";

        if (MessageDialog.Show(Owner, confirmation, "Confirm signing", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        ProgressMaximum = enabled.Count;
        StatusText = "Signing...";
        Append($"Signing {enabled.Count} location(s) with {SelectedCertificate.CommonName}.");

        _cancellation = new CancellationTokenSource();

        var progress = new Progress<SignProgress>(update =>
        {
            ProgressValue = update.Completed;
            StatusText = $"Signed {update.Completed} of {update.Total}...";
        });

        try
        {
            var placements = Placements.Select(p => p.Placement).ToList();
            var result = await _signer.SignAsync(
                SelectedCertificate, Options, placements, progress, _cancellation.Token);

            ReportResult(result);
        }
        catch (OperationCanceledException)
        {
            Append("Cancelled. Nothing was written.");
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Append("Signing failed: " + ex.Message);
            StatusText = "Failed.";
            MessageDialog.Show(Owner, ex.Message, "Signing failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    private void ReportResult(SignRunResult result)
    {
        foreach (var failure in result.Results.Where(r => r.Status == SignStatus.Failed))
        {
            Append($"  Page {failure.Placement.PageNumber} [{failure.FieldName}]: {failure.Message}");
        }

        if (result.Aborted)
        {
            var reason = result.Cancelled
                ? "Cancelled part way through, so nothing was written."
                : "A signature failed, so the whole run was discarded and nothing was written.";

            Append(reason);
            StatusText = result.Cancelled ? "Cancelled." : "Failed.";
            MessageDialog.Show(Owner, reason, "Not signed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Append($"Done. {result.Signed} signature(s) written to {result.OutputPath} " +
               $"in {result.Elapsed.TotalSeconds:0.0}s.");
        StatusText = $"Signed {result.Signed} location(s).";

        MessageDialog.Show(Owner,
            $"Signed {result.Signed} location(s).\n\nSaved to:\n{result.OutputPath}\n\nThe signed copy opens in a new tab.",
            "Signed", MessageBoxButton.OK, MessageBoxImage.Information);

        if (!string.IsNullOrWhiteSpace(result.OutputPath) && File.Exists(result.OutputPath))
        {
            Host?.OpenDocument(result.OutputPath);
        }
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        Append("Cancelling after the current signature...");
    }

    private void Append(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        OnPropertyChanged(nameof(LogText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
