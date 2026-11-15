using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>
/// Read Aloud: speaks the current page or the whole document with the Windows voices
/// (System.Speech). Stops whenever the panel is closed or the document changes.
/// </summary>
public partial class ReadAloudPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private SpeechSynthesizer? _synth;
    private bool _voicesLoaded;
    private int _requestId;

    public ReadAloudPanel()
    {
        InitializeComponent();
        SpeedText.Text = "0";
        UpdateState();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Read Aloud";

    public string Description => "Hear the current page or the whole document.";

    public string IconKey => "Icon.Speaker";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host)
    {
        _host = host;
        host.Window.Closed += (_, _) => DisposeSynth();
    }

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;
        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        LoadVoices();
        UpdateState();
    }

    public void Detach()
    {
        Stop();
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        UpdateState();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => Stop();

    // ---- Synthesizer -------------------------------------------------------

    private SpeechSynthesizer? Synth
    {
        get
        {
            if (_synth is not null) return _synth;
            try
            {
                _synth = new SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
                _synth.Rate = (int)SpeedSlider.Value;
                _synth.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
                _synth.SpeakCompleted += (_, _) => Dispatcher.BeginInvoke(UpdateState);
            }
            catch (Exception ex)
            {
                _synth = null;
                StateText.Text = "Speech is not available on this PC: " + ex.Message;
            }

            return _synth;
        }
    }

    private void DisposeSynth()
    {
        try
        {
            _synth?.SpeakAsyncCancelAll();
            _synth?.Dispose();
        }
        catch
        {
            // Shutting down.
        }

        _synth = null;
    }

    private void LoadVoices()
    {
        if (_voicesLoaded) return;
        var synth = Synth;
        if (synth is null) return;

        try
        {
            var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            VoiceBox.ItemsSource = voices.Select(v => new VoiceChoice(v.Name, $"{v.Name} ({v.Culture.Name})")).ToList();
            VoiceBox.DisplayMemberPath = nameof(VoiceChoice.Label);
            var current = synth.Voice?.Name;
            VoiceBox.SelectedItem = ((List<VoiceChoice>)VoiceBox.ItemsSource).FirstOrDefault(v => v.Name == current);
            _voicesLoaded = true;
        }
        catch (Exception ex)
        {
            StateText.Text = "Could not list voices: " + ex.Message;
        }
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceBox.SelectedItem is not VoiceChoice choice || _synth is null) return;
        try
        {
            if (_synth.State == SynthesizerState.Ready) _synth.SelectVoice(choice.Name);
        }
        catch (Exception ex)
        {
            StateText.Text = "Could not select that voice: " + ex.Message;
        }
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText is null) return;
        SpeedText.Text = ((int)e.NewValue).ToString();
        if (_synth is not null) _synth.Rate = (int)e.NewValue;
    }

    // ---- Controls ----------------------------------------------------------

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        var synth = Synth;
        var session = _session;
        if (synth is null || session is null) return;

        if (synth.State == SynthesizerState.Paused)
        {
            synth.Resume();
            UpdateState();
            return;
        }

        Stop();
        var request = ++_requestId;
        int? page = CurrentPageRadio.IsChecked == true ? Math.Max(1, _viewer?.CurrentPage ?? 1) : null;
        var file = session.CurrentFile;
        var password = session.Password;

        StateText.Text = "Reading the text...";
        string text;
        try
        {
            text = await Task.Run(() => PdfTextSearch.ExtractText(file, page, password));
        }
        catch (Exception ex)
        {
            if (request == _requestId) StateText.Text = "Could not read the text: " + ex.Message;
            return;
        }

        // Stopped, closed or restarted while the text was being read.
        if (request != _requestId || _session != session) return;

        text = text.Replace('\f', '\n').Trim();
        if (text.Length == 0)
        {
            StateText.Text = page is null
                ? "This document has no text to read (it may be scanned images)."
                : $"Page {page} has no text to read (it may be a scanned image).";
            return;
        }

        if (VoiceBox.SelectedItem is VoiceChoice choice)
        {
            try { synth.SelectVoice(choice.Name); } catch { /* keep the default voice */ }
        }

        synth.Rate = (int)SpeedSlider.Value;
        synth.SpeakAsync(text);
        _host?.SetStatus(page is null ? "Reading the document aloud." : $"Reading page {page} aloud.");
        UpdateState();
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_synth is null) return;
        if (_synth.State == SynthesizerState.Speaking) _synth.Pause();
        else if (_synth.State == SynthesizerState.Paused) _synth.Resume();
        UpdateState();
    }

    private void OnStop(object sender, RoutedEventArgs e) => Stop();

    /// <summary>Stops speech and cancels any text still being prepared.</summary>
    internal void Stop()
    {
        _requestId++;
        if (_synth is null)
        {
            UpdateState();
            return;
        }

        try
        {
            if (_synth.State == SynthesizerState.Paused) _synth.Resume();
            _synth.SpeakAsyncCancelAll();
        }
        catch
        {
            // Nothing to stop.
        }

        UpdateState();
    }

    internal SynthesizerState State => _synth?.State ?? SynthesizerState.Ready;

    private void UpdateState()
    {
        if (!IsInitialized || StateText is null) return;

        var hasDocument = _session is not null;
        NoDocumentText.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        Sections.IsEnabled = hasDocument;

        var state = State;
        PlayText.Text = state == SynthesizerState.Paused ? "Resume" : "Play";
        PlayButton.IsEnabled = hasDocument && state != SynthesizerState.Speaking;
        PauseButton.IsEnabled = state is SynthesizerState.Speaking or SynthesizerState.Paused;
        PauseButton.Content = state == SynthesizerState.Paused ? "Resume" : "Pause";
        StopButton.IsEnabled = state != SynthesizerState.Ready;
        VoiceBox.IsEnabled = state == SynthesizerState.Ready;

        if (state == SynthesizerState.Speaking) StateText.Text = "Speaking.";
        else if (state == SynthesizerState.Paused) StateText.Text = "Paused.";
        else if (StateText.Text is "Speaking." or "Paused.") StateText.Text = "";
    }

    public sealed record VoiceChoice(string Name, string Label);
}
