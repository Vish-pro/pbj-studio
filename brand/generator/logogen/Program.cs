using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "pbj-logo");
        Directory.CreateDirectory(outDir);

        var sealPath = @"C:\Users\Vishwanath\OneDrive\Downloads\Glossy Crimson Wax Seal Document Emblem.png";

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(sealPath);
        img.EndInit();
        img.Freeze();

        Console.WriteLine($"Seal: {img.PixelWidth} x {img.PixelHeight}, format {img.Format}");

        // Check whether the artwork already carries an alpha channel (corners transparent).
        var converted = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var row = new byte[stride];

        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, converted.PixelWidth, 1), row, stride, 0);
        Console.WriteLine($"Top-left pixel BGRA: {row[0]},{row[1]},{row[2]},{row[3]}");

        converted.CopyPixels(new System.Windows.Int32Rect(0, converted.PixelHeight / 2, converted.PixelWidth, 1), row, stride, 0);
        Console.WriteLine($"Mid-left pixel BGRA: {row[0]},{row[1]},{row[2]},{row[3]}");
        var mid = (converted.PixelWidth / 2) * 4;
        Console.WriteLine($"Centre pixel BGRA:   {row[mid]},{row[mid + 1]},{row[mid + 2]},{row[mid + 3]}");

        // Count transparent pixels across the whole image.
        var all = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(all, stride, 0);
        long transparent = 0;
        for (var i = 3; i < all.Length; i += 4) if (all[i] < 16) transparent++;
        Console.WriteLine($"Fully/near transparent pixels: {transparent:N0} of {all.Length / 4:N0}");

        File.Copy(sealPath, Path.Combine(outDir, "seal2.png"), true);
        Console.WriteLine("Copied to " + Path.Combine(outDir, "seal2.png"));
    }
}
