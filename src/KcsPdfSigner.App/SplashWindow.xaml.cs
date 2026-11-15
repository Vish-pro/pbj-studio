using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KcsPdfSigner.App;

/// <summary>
/// Plays the brand animation (pre-rendered JPEG frames in Assets/splash.zip) once at start-up,
/// before the main window shows. Mirrors the pattern used by the sibling DBC app: a zip of
/// pre-rendered frames decoded on demand, not a video file or MediaElement, which keeps this
/// free of codec and timing flakiness.
/// </summary>
public partial class SplashWindow : Window
{
    private const double Fps = 25;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(0.8);

    private readonly List<byte[]> _frames = new();
    private readonly BitmapImage?[] _cache;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private int _shown = -1;
    private bool _closing;

    public SplashWindow()
    {
        InitializeComponent();

        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/splash.zip"));
            using var zip = new ZipArchive(resource!.Stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                _frames.Add(buffer.ToArray());
            }
        }
        catch
        {
            // No animation available: the window closes straight away in Loaded below.
        }

        _cache = new BitmapImage?[_frames.Count];
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) =>
        {
            if (_frames.Count == 0)
            {
                Close();
                return;
            }

            Activate();
            _clock.Start();
            _timer.Start();
            Tick();
        };
    }

    private void Tick()
    {
        var index = (int)(_clock.Elapsed.TotalSeconds * Fps);
        if (index >= _frames.Count)
        {
            Show(_frames.Count - 1);
            if (_clock.Elapsed.TotalSeconds >= _frames.Count / Fps + Hold.TotalSeconds) FadeOut();
            return;
        }

        Show(index);
    }

    private void Show(int index)
    {
        if (index == _shown) return;
        _shown = index;
        FrameImage.Source = _cache[index] ??= Decode(_frames[index]);
        if (index > 0) _cache[index - 1] = null; // keep memory flat
    }

    private static BitmapImage Decode(byte[] jpeg)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(jpeg);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Skip(object sender, EventArgs e) => FadeOut();

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        ((UIElement)Content).BeginAnimation(OpacityProperty, fade);
    }
}
