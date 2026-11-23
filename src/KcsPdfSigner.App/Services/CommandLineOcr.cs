using System.IO;
using System.Runtime.InteropServices;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Services;

/// <summary>
/// "PBJStudio.exe --ocr input.pdf output.pdf [--lang hin+eng]": makes a scanned PDF searchable without
/// opening a window (for batch files, and to check an installed or published build). Uses the same
/// engine and language folders as the OCR tool. Prints a one-line result when started from a console;
/// the exit code is 0 on success, 1 on failure, 2 for wrong arguments. Password-protected files are
/// not supported here (use the OCR tool).
/// </summary>
internal static class CommandLineOcr
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        try { AttachConsole(-1); } catch { /* no console: the exit code still tells */ }

        if (args.Length < 3)
        {
            Write("Usage: PBJStudio.exe --ocr input.pdf output.pdf [--lang eng+hin]");
            return 2;
        }

        var input = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var languages = new List<string> { OcrLanguages.EnglishCode };
        for (var i = 3; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase))
                languages = args[i + 1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        try
        {
            var result = PdfOcrOps.MakeSearchable(input, output, new OcrOptions { Languages = languages });
            Write($"OCR done: {result.PagesRead.Count} page(s) read, {result.PagesSkipped.Count} already had text, " +
                  $"{result.PagesWithoutText.Count} without text found, {result.TurnedForReading.Count} turned for reading, " +
                  $"{result.Words} words, confidence {result.MeanConfidence:0}%, {result.Elapsed.TotalSeconds:0.0}s -> {output}");
            return 0;
        }
        catch (Exception ex)
        {
            App.LogError("Command line OCR", ex);
            Write("OCR failed: " + ex.Message);
            return 1;
        }
    }

    private static void Write(string line)
    {
        try { Console.WriteLine(line); } catch { /* no console */ }
    }
}
