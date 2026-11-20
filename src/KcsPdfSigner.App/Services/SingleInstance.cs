using System.IO;
using System.IO.Pipes;
using System.Text;

namespace KcsPdfSigner.App.Services;

/// <summary>
/// Keeps one copy of the app per Windows user. A second start (for example a double-click on a PDF
/// in Explorer) hands its file names to the running copy through a named pipe and exits, so the
/// PDF opens as a new tab instead of a second window.
/// </summary>
public static class SingleInstance
{
    private const string Activate = "*ACTIVATE*";
    private static readonly string PipeName = "PBJStudio.Open." + Environment.UserName;
    private static Mutex? _mutex;

    /// <summary>True when this is the first copy running for this user.</summary>
    public static bool TryBecomeFirst()
    {
        // Developers and tests can run extra copies side by side: set PBJ_ALLOW_MULTIPLE=1.
        if (Environment.GetEnvironmentVariable("PBJ_ALLOW_MULTIPLE") == "1") return true;

        _mutex = new Mutex(true, @"Local\PBJStudio.Instance." + Environment.UserName, out var created);
        return created;
    }

    /// <summary>Hands <paramref name="paths"/> to the running copy. Returns false when it cannot be reached.</summary>
    public static bool SendToRunning(IReadOnlyList<string> paths)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                if (paths.Count == 0) writer.WriteLine(Activate);
                foreach (var path in paths) writer.WriteLine(path);
                writer.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(200); // the first copy is still starting up
            }
        }

        return false;
    }

    /// <summary>Starts listening in the background. <paramref name="onMessage"/> runs on a worker thread.</summary>
    public static void Listen(Action<List<string>> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();

                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    var paths = new List<string>();
                    var activateOnly = false;
                    while (reader.ReadLine() is { } line)
                    {
                        if (line == Activate) activateOnly = true;
                        else if (line.Length > 0) paths.Add(line);
                    }

                    if (paths.Count > 0 || activateOnly) onMessage(paths);
                }
                catch (IOException ex) when (ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
                {
                    // Another copy already listens (only possible with PBJ_ALLOW_MULTIPLE). This copy
                    // simply does not receive files from Explorer; no need to keep retrying or logging.
                    return;
                }
                catch (Exception ex)
                {
                    App.LogError("Single-instance pipe", ex);
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PBJ single-instance pipe"
        };
        thread.Start();
    }
}
