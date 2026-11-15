using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;

namespace KcsPdfSigner.App.Tools;

/// <summary>Small helpers shared by the Phase C tool panels.</summary>
internal static class ToolUi
{
    /// <summary>White halo used under black outlines so marks read on any page content.</summary>
    public static readonly Brush Halo = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    /// <summary>Light dark wash inside marked areas.</summary>
    public static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)));

    /// <summary>
    /// Runs a Core operation through <see cref="DocumentSession.ApplyAsync"/> with the panel
    /// disabled, and reports failure in a <see cref="MessageDialog"/>. Returns true on success.
    /// </summary>
    public static async Task<bool> ApplyAsync(
        IStudioHost? host, FrameworkElement panel, DocumentSession session,
        Action<string, string> operation, string description)
    {
        panel.IsEnabled = false;
        host?.SetStatus(description + "...");
        try
        {
            await session.ApplyAsync(operation, description);
            host?.SetStatus(description + " - done. Ctrl+Z undoes it.");
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(description, ex);
            host?.SetStatus(description + " failed.");
            ShowError(host, description + " failed:\n\n" + ex.Message);
            return false;
        }
        finally
        {
            panel.IsEnabled = true;
        }
    }

    public static void ShowError(IStudioHost? host, string message)
        => MessageDialog.Show(host?.Window, message, App.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void ShowInfo(IStudioHost? host, string message)
        => MessageDialog.Show(host?.Window, message, App.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Draws a black outline with a white halo on a page overlay. Returns false when not shown.</summary>
    public static bool DrawBox(
        PdfViewer viewer, object owner, int page, double left, double bottom, double width, double height,
        bool dashed = false, bool filled = false, string? caption = null, double thickness = 2)
    {
        var overlay = viewer.GetOverlay(page);
        if (overlay is null) return false;

        var box = viewer.PdfRectToOverlay(page, left, bottom, width, height);
        if (box.IsEmpty) return false;

        var w = Math.Max(box.Width, 2);
        var h = Math.Max(box.Height, 2);

        var halo = new Rectangle
        {
            Width = w, Height = h, Stroke = Halo, StrokeThickness = thickness + 2,
            IsHitTestVisible = false, Tag = owner
        };
        var outline = new Rectangle
        {
            Width = w, Height = h, Stroke = Brushes.Black, StrokeThickness = thickness,
            Fill = filled ? Shade : null, IsHitTestVisible = false, Tag = owner
        };
        if (dashed) outline.StrokeDashArray = [4, 2];

        foreach (var shape in new[] { halo, outline })
        {
            Canvas.SetLeft(shape, box.Left);
            Canvas.SetTop(shape, box.Top);
            overlay.Children.Add(shape);
        }

        if (!string.IsNullOrEmpty(caption))
        {
            var label = new Border
            {
                Background = Brushes.Black,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 0, 4, 1),
                IsHitTestVisible = false,
                Tag = owner,
                Child = new TextBlock { Text = caption, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold }
            };
            Canvas.SetLeft(label, box.Left);
            Canvas.SetTop(label, Math.Max(box.Top - 18, 0));
            overlay.Children.Add(label);
        }

        return true;
    }

    /// <summary>Parses a positive number typed by the user; returns the fallback when it is not one.</summary>
    public static float ParseFloat(string? text, float fallback, float min, float max)
    {
        if (!float.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out var value) &&
            !float.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    public static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
