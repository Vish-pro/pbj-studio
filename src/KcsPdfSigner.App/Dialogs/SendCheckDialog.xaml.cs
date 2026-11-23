using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Dialogs;

/// <summary>
/// "Send check" for a signed PDF: signatures valid or at least intact, every signature box prints and
/// is drawn, the file size, no password to open, no damaged structure, and how many signatures by whom.
/// </summary>
public partial class SendCheckDialog : Window
{
    public enum Mark
    {
        Ok,
        Warning,
        Problem
    }

    public sealed record CheckLine(Mark Mark, string Text);

    public SendCheckDialog(string fileName, DocumentAnalysis analysis, DocumentVerification verification,
        bool needsPassword, long largeFileBytes)
    {
        InitializeComponent();
        WindowTheme.Apply(this);

        Lines = BuildLines(analysis, verification, needsPassword, largeFileBytes);
        HeadingText.Text = "Before you send " + fileName;
        foreach (var line in Lines) ChecksPanel.Children.Add(Row(line));
        Verdict = Lines.All(l => l.Mark == Mark.Ok) ? "Looks good to send." : "Check the items marked ! before sending.";
        VerdictText.Text = Verdict;
    }

    public IReadOnlyList<CheckLine> Lines { get; }

    public string Verdict { get; }

    /// <summary>The checks, also used by the tests.</summary>
    public static List<CheckLine> BuildLines(DocumentAnalysis analysis, DocumentVerification verification,
        bool needsPassword, long largeFileBytes)
    {
        var lines = new List<CheckLine>();
        var signatures = verification.Signatures;
        var count = signatures.Count;

        // 1. Signatures valid, or at least intact.
        if (count == 0)
        {
            lines.Add(new CheckLine(Mark.Problem, "Problem: no digital signature was found in this file."));
        }
        else if (signatures.Any(s => s.Status == SignatureValidity.Invalid))
        {
            var bad = signatures.Count(s => s.Status == SignatureValidity.Invalid);
            lines.Add(new CheckLine(Mark.Problem, $"Problem: {bad} of {count} signature(s) are INVALID. Open the Signatures pane for details."));
        }
        else if (signatures.All(s => s.Status == SignatureValidity.Valid))
        {
            lines.Add(new CheckLine(Mark.Ok, count == 1 ? "The signature is valid." : $"All {count} signatures are valid."));
        }
        else if (signatures.All(s => s.IntegrityOk))
        {
            lines.Add(new CheckLine(Mark.Ok,
                $"{(count == 1 ? "The signature is" : $"All {count} signatures are")} intact (nothing changed since signing). " +
                "The signer's certificate is not trusted on this PC, so validity shows as unknown here; " +
                "for Indian DSC signatures add the CCA India root in Sign > Trusted certificates."));
        }
        else
        {
            lines.Add(new CheckLine(Mark.Warning, "Warning: some signatures could not be confirmed. Open the Signatures pane for details."));
        }

        // 2. Signature boxes print and are drawn.
        var boxes = analysis.SignatureBoxes;
        var visible = boxes.Where(b => b.IsVisible).ToList();
        var notPrinting = visible.Count(b => !b.IsPrintable);
        var notDrawn = visible.Count(b => !b.HasAppearance);
        if (boxes.Count == 0)
        {
            lines.Add(new CheckLine(Mark.Warning, "Warning: the signature boxes could not be read."));
        }
        else if (notPrinting > 0)
        {
            lines.Add(new CheckLine(Mark.Problem, $"Problem: {notPrinting} signature box(es) will not appear when printed (Print flag missing)."));
        }
        else if (notDrawn > 0)
        {
            lines.Add(new CheckLine(Mark.Warning, $"Warning: {notDrawn} signature box(es) have no picture, so they may look blank."));
        }
        else
        {
            var invisible = boxes.Count - visible.Count;
            lines.Add(new CheckLine(Mark.Ok, visible.Count == 0
                ? "The signatures are invisible (no box on the page); nothing needs to print."
                : $"Every signature box shows on screen and on paper ({visible.Count} box(es)" +
                  (invisible > 0 ? $", {invisible} invisible signature(s))." : ").")));
        }

        // 3. File size.
        var mb = analysis.FileSizeBytes / (1024.0 * 1024.0);
        var limit = largeFileBytes / (1024.0 * 1024.0);
        var size = mb.ToString(mb < 10 ? "0.0#" : "0.0", CultureInfo.InvariantCulture);
        lines.Add(largeFileBytes > 0 && analysis.FileSizeBytes > largeFileBytes
            ? new CheckLine(Mark.Warning, $"Warning: the file is {size} MB, above your {limit.ToString("0.##", CultureInfo.InvariantCulture)} MB limit. " +
                                          "Many portals refuse large files. Optimizing it now would break the signatures; reduce the size before signing.")
            : new CheckLine(Mark.Ok, $"File size {size} MB."));

        // 4. Opens without a password.
        if (needsPassword)
            lines.Add(new CheckLine(Mark.Warning, "Warning: the file needs a password to open. Most bank portals cannot read protected files."));
        else if (analysis.Permissions?.IsEncrypted == true)
            lines.Add(new CheckLine(Mark.Warning, "Warning: the file opens freely but its owner has restricted it (for example printing or copying)."));
        else
            lines.Add(new CheckLine(Mark.Ok, "Opens without a password."));

        // 5. Not damaged.
        lines.Add(analysis.StructureRepaired || verification.StructureRepaired
            ? new CheckLine(Mark.Problem, "Problem: the file structure is damaged (it was repaired to open it). Sign a clean copy again.")
            : new CheckLine(Mark.Ok, "The file structure is sound (not damaged or repaired)."));

        // 6. Who signed.
        var signers = signatures.Select(s => s.SignerName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (count > 0)
        {
            lines.Add(new CheckLine(Mark.Ok,
                $"{count} signature(s)" + (signers.Count > 0 ? " by " + string.Join(", ", signers) + "." : ".")));
        }

        return lines;
    }

    private static UIElement Row(CheckLine line)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var mark = new TextBlock
        {
            Text = line.Mark == Mark.Ok ? "OK" : "!",
            FontWeight = FontWeights.Bold,
            Width = 32,
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(mark, Dock.Left);
        panel.Children.Add(mark);
        panel.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
