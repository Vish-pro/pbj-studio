using SkiaSharp;

namespace KcsPdfSigner.Core.Scanning;

/// <summary>A picture as separate 8-bit planes: one (grey) or three (red, green, blue).</summary>
public sealed class Raster
{
    public Raster(int width, int height, int planes)
    {
        Width = width;
        Height = height;
        Planes = new byte[planes][];
        for (var i = 0; i < planes; i++) Planes[i] = new byte[width * height];
    }

    public Raster(int width, int height, byte[][] planes)
    {
        Width = width;
        Height = height;
        Planes = planes;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[][] Planes { get; }

    public bool IsGrey => Planes.Length == 1;
}

/// <summary>A point in picture coordinates.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>The four corners of a page in a picture: top-left, top-right, bottom-right, bottom-left.</summary>
public sealed record PageQuad(PointD TopLeft, PointD TopRight, PointD BottomRight, PointD BottomLeft)
{
    public PointD[] Corners => [TopLeft, TopRight, BottomRight, BottomLeft];

    public PageQuad Scaled(double factor) => new(
        new(TopLeft.X * factor, TopLeft.Y * factor), new(TopRight.X * factor, TopRight.Y * factor),
        new(BottomRight.X * factor, BottomRight.Y * factor), new(BottomLeft.X * factor, BottomLeft.Y * factor));
}

/// <summary>
/// The image-processing steps behind the Scan Cleaner, each usable and testable on its own: loading,
/// finding the page edges, flattening the perspective, measuring skew, whitening the background,
/// reducing noise, sharpening and turning into black and white. Written without native image libraries
/// beyond SkiaSharp (which PDF rendering already needs).
/// </summary>
public static class ScanImageOps
{
    /// <summary>
    /// Set by the app to read picture types SkiaSharp cannot (TIFF, HEIC): returns PNG bytes, or null.
    /// </summary>
    public static Func<string, byte[]?>? ExternalDecoder { get; set; }

    // ---- Loading ---------------------------------------------------------------

    /// <summary>
    /// Reads a picture, applies its camera orientation (EXIF) and returns RGB planes. A JPEG much larger
    /// than <paramref name="maxLongSide"/> is decoded at a reduced size straight away, so a 48-megapixel
    /// photo never needs a 200 MB buffer. Throws <see cref="InvalidDataException"/> for unreadable files.
    /// </summary>
    public static Raster Load(string path, int maxLongSide = 0)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Picture not found.", path);
        SKBitmap? bitmap = null;
        var origin = SKEncodedOrigin.TopLeft;
        try
        {
            using (var codec = SKCodec.Create(path))
            {
                if (codec is not null)
                {
                    origin = codec.EncodedOrigin;
                    var size = codec.Info.Size;
                    var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    if (maxLongSide > 0)
                    {
                        var scale = 1f;
                        while (Math.Max(size.Width, size.Height) * scale / 2 >= maxLongSide && scale > 0.125f) scale /= 2;
                        if (scale < 1f)
                        {
                            var scaled = codec.GetScaledDimensions(scale);
                            if (scaled.Width > 0 && scaled.Height > 0)
                                info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                        }
                    }

                    bitmap = new SKBitmap(info);
                    var code = codec.GetPixels(info, bitmap.GetPixels());
                    if (code is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        bitmap.Dispose();
                        bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
                        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                        {
                            bitmap.Dispose();
                            bitmap = null;
                        }
                    }
                }
            }

            if (bitmap is null)
            {
                var png = ExternalDecoder?.Invoke(path);
                if (png is not null)
                {
                    origin = SKEncodedOrigin.TopLeft;
                    using var decoded = SKBitmap.Decode(png);
                    if (decoded is not null)
                        bitmap = decoded.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("Cannot read this picture.");
                }
            }

            if (bitmap is null) throw new InvalidDataException("Cannot read this picture: " + Path.GetFileName(path));
            return FromBitmap(bitmap, origin);
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>RGB planes from a bitmap in Rgba8888 or Bgra8888, turned upright for an EXIF origin.</summary>
    public static Raster FromBitmap(SKBitmap bitmap, SKEncodedOrigin origin = SKEncodedOrigin.TopLeft)
    {
        var bgra = bitmap.ColorType == SKColorType.Bgra8888;
        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
        {
            using var copy = bitmap.Copy(SKColorType.Rgba8888)!;
            return FromBitmap(copy, origin);
        }

        int sw = bitmap.Width, sh = bitmap.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int dw = swap ? sh : sw, dh = swap ? sw : sh;
        var result = new Raster(dw, dh, 3);
        var row = bitmap.RowBytes;
        var pixels = new byte[row * sh];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        int ri = bgra ? 2 : 0, bi = bgra ? 0 : 2;

        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < sw; x++)
            {
                int dx, dy;
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight: dx = sw - 1 - x; dy = y; break;
                    case SKEncodedOrigin.BottomRight: dx = sw - 1 - x; dy = sh - 1 - y; break;
                    case SKEncodedOrigin.BottomLeft: dx = x; dy = sh - 1 - y; break;
                    case SKEncodedOrigin.LeftTop: dx = y; dy = x; break;
                    case SKEncodedOrigin.RightTop: dx = sh - 1 - y; dy = x; break;
                    case SKEncodedOrigin.RightBottom: dx = sh - 1 - y; dy = sw - 1 - x; break;
                    case SKEncodedOrigin.LeftBottom: dx = y; dy = sw - 1 - x; break;
                    default: dx = x; dy = y; break;
                }

                var s = y * row + x * 4;
                var d = dy * dw + dx;
                result.Planes[0][d] = pixels[s + ri];
                result.Planes[1][d] = pixels[s + 1];
                result.Planes[2][d] = pixels[s + bi];
            }
        }

        return result;
    }

    // ---- Basic operations --------------------------------------------------------

    /// <summary>Brightness plane (0 to 255) of a picture.</summary>
    public static Raster ToGrey(Raster source)
    {
        if (source.IsGrey) return source;
        var n = source.Width * source.Height;
        var grey = new byte[n];
        byte[] r = source.Planes[0], g = source.Planes[1], b = source.Planes[2];
        for (var i = 0; i < n; i++) grey[i] = (byte)((77 * r[i] + 150 * g[i] + 29 * b[i] + 128) >> 8);
        return new Raster(source.Width, source.Height, [grey]);
    }

    /// <summary>Averages whole blocks: the picture divided by an integer factor.</summary>
    public static Raster DownscaleBy(Raster source, int factor)
    {
        if (factor <= 1) return source;
        int w = Math.Max(1, source.Width / factor), h = Math.Max(1, source.Height / factor);
        var result = new Raster(w, h, source.Planes.Length);
        for (var p = 0; p < source.Planes.Length; p++)
        {
            var src = source.Planes[p];
            var dst = result.Planes[p];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    var count = 0;
                    for (var yy = y * factor; yy < Math.Min(source.Height, (y + 1) * factor); yy++)
                    for (var xx = x * factor; xx < Math.Min(source.Width, (x + 1) * factor); xx++)
                    {
                        sum += src[yy * source.Width + xx];
                        count++;
                    }

                    dst[y * w + x] = (byte)((sum + count / 2) / Math.Max(1, count));
                }
            });
        }

        return result;
    }

    /// <summary>Divides so that the longer side is at most <paramref name="maxSide"/> (whole-number factor, at least 1).</summary>
    public static Raster DownscaleToFit(Raster source, int maxSide, out int factor)
    {
        factor = Math.Max(1, (int)Math.Ceiling(Math.Max(source.Width, source.Height) / (double)maxSide));
        return DownscaleBy(source, factor);
    }

    /// <summary>Box blur of one plane (radius in pixels), edges repeated.</summary>
    public static byte[] BoxBlur(byte[] plane, int width, int height, int radius)
    {
        if (radius <= 0) return (byte[])plane.Clone();
        var temp = new byte[plane.Length];
        var result = new byte[plane.Length];
        var window = 2 * radius + 1;

        Parallel.For(0, height, y =>
        {
            var o = y * width;
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += plane[o + Math.Clamp(k, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                temp[o + x] = (byte)((sum + window / 2) / window);
                sum += plane[o + Math.Min(width - 1, x + radius + 1)] - plane[o + Math.Max(0, x - radius)];
            }
        });

        Parallel.For(0, width, x =>
        {
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += temp[Math.Clamp(k, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                result[y * width + x] = (byte)((sum + window / 2) / window);
                sum += temp[Math.Min(height - 1, y + radius + 1) * width + x] - temp[Math.Max(0, y - radius) * width + x];
            }
        });

        return result;
    }

    /// <summary>Grey-scale dilation (maximum filter) with a square window; used to erase dark text from a background estimate.</summary>
    public static byte[] Dilate(byte[] plane, int width, int height, int radius)
    {
        var temp = new byte[plane.Length];
        var result = new byte[plane.Length];
        Parallel.For(0, height, y =>
        {
            var o = y * width;
            for (var x = 0; x < width; x++)
            {
                byte max = 0;
                for (var k = Math.Max(0, x - radius); k <= Math.Min(width - 1, x + radius); k++)
                    if (plane[o + k] > max) max = plane[o + k];
                temp[o + x] = max;
            }
        });
        Parallel.For(0, width, x =>
        {
            for (var y = 0; y < height; y++)
            {
                byte max = 0;
                for (var k = Math.Max(0, y - radius); k <= Math.Min(height - 1, y + radius); k++)
                    if (temp[k * width + x] > max) max = temp[k * width + x];
                result[y * width + x] = max;
            }
        });
        return result;
    }

    /// <summary>Otsu's threshold of a plane: values above it are the lighter group.</summary>
    public static int OtsuThreshold(byte[] plane)
    {
        var histogram = new long[256];
        foreach (var v in plane) histogram[v]++;
        double total = plane.Length, sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];
        double sumB = 0, weightB = 0, best = -1;
        var threshold = 127;
        for (var t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0) continue;
            var weightF = total - weightB;
            if (weightF == 0) break;
            sumB += t * (double)histogram[t];
            var meanB = sumB / weightB;
            var meanF = (sumAll - sumB) / weightF;
            var between = weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }

        return threshold;
    }

    /// <summary>The value below which the given share (0 to 1) of the plane lies.</summary>
    public static int Percentile(byte[] plane, double share)
    {
        var histogram = new long[256];
        foreach (var v in plane) histogram[v]++;
        var target = share * plane.Length;
        long running = 0;
        for (var i = 0; i < 256; i++)
        {
            running += histogram[i];
            if (running >= target) return i;
        }

        return 255;
    }

    // ---- Finding the page ----------------------------------------------------------

    /// <summary>
    /// Finds the four corners of a bright page on a darker background: the largest bright area, then its
    /// extreme points. Returns null when no page-shaped area is found (a flat scan that fills the picture,
    /// a white page on a white table, no page at all). <paramref name="grey"/> should be a reduced picture
    /// (about 600 pixels); the corners are in its coordinates.
    /// </summary>
    public static PageQuad? FindPageQuad(Raster grey)
    {
        int w = grey.Width, h = grey.Height;
        if (w < 16 || h < 16) return null;
        var plane = BoxBlur(grey.Planes[0], w, h, 1);
        var threshold = OtsuThreshold(plane);

        // The desk must be clearly darker than the paper, or there is no edge to find.
        long sumLight = 0, sumDark = 0, nLight = 0, nDark = 0;
        foreach (var v in plane)
        {
            if (v > threshold) { sumLight += v; nLight++; }
            else { sumDark += v; nDark++; }
        }

        if (nLight == 0 || nDark == 0) return null;

        // A desk surrounds the page: dark pixels must be found along the picture's border.
        var ring = 0;
        var ringDark = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x >= 4 && x < w - 4 && y >= 4 && y < h - 4) continue;
                ring++;
                if (plane[y * w + x] <= threshold) ringDark++;
            }
        }

        if (ringDark < 0.15 * ring) return null;
        if (sumLight / (double)nLight - sumDark / (double)nDark < 35) return null;

        // Largest connected bright area (4-neighbours, breadth-first).
        var label = new int[plane.Length];
        var queue = new int[plane.Length];
        var best = 0;
        var bestLabel = 0;
        var next = 0;
        for (var start = 0; start < plane.Length; start++)
        {
            if (plane[start] <= threshold || label[start] != 0) continue;
            next++;
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail)
            {
                var cur = queue[head++];
                int cx = cur % w, cy = cur / w;
                if (cx > 0 && plane[cur - 1] > threshold && label[cur - 1] == 0) { label[cur - 1] = next; queue[tail++] = cur - 1; }
                if (cx < w - 1 && plane[cur + 1] > threshold && label[cur + 1] == 0) { label[cur + 1] = next; queue[tail++] = cur + 1; }
                if (cy > 0 && plane[cur - w] > threshold && label[cur - w] == 0) { label[cur - w] = next; queue[tail++] = cur - w; }
                if (cy < h - 1 && plane[cur + w] > threshold && label[cur + w] == 0) { label[cur + w] = next; queue[tail++] = cur + w; }
            }

            if (tail > best)
            {
                best = tail;
                bestLabel = next;
            }
        }

        if (best < 0.15 * w * h) return null;

        // Extreme points along the two diagonals.
        double minSum = double.MaxValue, maxSum = double.MinValue, minDiff = double.MaxValue, maxDiff = double.MinValue;
        PointD tl = default, br = default, tr = default, bl = default;
        var touchesBorder = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (label[y * w + x] != bestLabel) continue;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touchesBorder++;
                double s = x + y, d = x - y;
                if (s < minSum) { minSum = s; tl = new(x, y); }
                if (s > maxSum) { maxSum = s; br = new(x, y); }
                if (d > maxDiff) { maxDiff = d; tr = new(x, y); }
                if (d < minDiff) { minDiff = d; bl = new(x, y); }
            }
        }

        // The bright area is the whole picture (a flat scan): there is no page edge inside it.
        if (best > 0.93 * w * h) return null;

        var quad = new PageQuad(tl, tr, br, bl);
        if (!IsConvex(quad)) return null;
        var area = PolygonArea(quad.Corners);
        if (area < 0.2 * w * h) return null;
        // A page-shaped area fills most of its corner quadrilateral; a ragged blob does not.
        if (best < 0.7 * area) return null;
        if (Distance(tl, tr) < 0.15 * w || Distance(tl, bl) < 0.15 * h) return null;
        return quad;
    }

    private static bool IsConvex(PageQuad quad)
    {
        var c = quad.Corners;
        double sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % 4];
            var d = c[(i + 2) % 4];
            var cross = (b.X - a.X) * (d.Y - b.Y) - (b.Y - a.Y) * (d.X - b.X);
            if (Math.Abs(cross) < 1e-6) return false;
            if (sign == 0) sign = Math.Sign(cross);
            else if (Math.Sign(cross) != sign) return false;
        }

        return true;
    }

    private static double PolygonArea(PointD[] p)
    {
        double sum = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return Math.Abs(sum) / 2;
    }

    public static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Moves the corners towards the centre by a share of the sides, so the dark page edge stays out.</summary>
    public static PageQuad Shrink(PageQuad quad, double share)
    {
        var cx = quad.Corners.Average(p => p.X);
        var cy = quad.Corners.Average(p => p.Y);
        PointD Move(PointD p) => new(p.X + (cx - p.X) * share, p.Y + (cy - p.Y) * share);
        return new PageQuad(Move(quad.TopLeft), Move(quad.TopRight), Move(quad.BottomRight), Move(quad.BottomLeft));
    }

    // ---- Flattening and straightening ------------------------------------------------

    /// <summary>
    /// Solves the projective map that sends the corners of the rectangle (0,0)-(width,height) to the quad;
    /// returns eight coefficients a..h so that u = (a x + b y + c) / (g x + h y + 1), v = (d x + e y + f) / (...).
    /// </summary>
    public static double[] SolveHomography(double width, double height, PageQuad quad)
    {
        PointD[] from = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
        var to = quad.Corners;
        var m = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            var r = i * 2;
            m[r, 0] = x; m[r, 1] = y; m[r, 2] = 1; m[r, 6] = -x * u; m[r, 7] = -y * u; m[r, 8] = u;
            m[r + 1, 3] = x; m[r + 1, 4] = y; m[r + 1, 5] = 1; m[r + 1, 6] = -x * v; m[r + 1, 7] = -y * v; m[r + 1, 8] = v;
        }

        for (var col = 0; col < 8; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 8; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) throw new InvalidOperationException("The page corners do not form a flat page.");
            if (pivot != col)
                for (var k = 0; k < 9; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (var r = 0; r < 8; r++)
            {
                if (r == col) continue;
                var factor = m[r, col] / m[col, col];
                for (var k = col; k < 9; k++) m[r, k] -= factor * m[col, k];
            }
        }

        var result = new double[8];
        for (var i = 0; i < 8; i++) result[i] = m[i, 8] / m[i, i];
        return result;
    }

    /// <summary>
    /// Cuts the page out of the source: an output of <paramref name="width"/> by <paramref name="height"/>
    /// pixels sampling the quad (bilinear), with the content turned by <paramref name="turnDegrees"/> about its
    /// centre (straightening; positive turns the lines' tilt back, see <see cref="EstimateSkew"/>). Samples
    /// outside the page repeat the edge.
    /// </summary>
    public static Raster Warp(Raster source, PageQuad quad, int width, int height, double turnDegrees)
    {
        var hm = SolveHomography(width, height, quad);
        double a = hm[0], b = hm[1], c = hm[2], d = hm[3], e = hm[4], f = hm[5], g = hm[6], hh = hm[7];
        var radians = turnDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double cx = width / 2.0, cy = height / 2.0;
        var result = new Raster(width, height, source.Planes.Length);
        int sw = source.Width, sh = source.Height;
        var planeCount = source.Planes.Length;

        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                double px = x + 0.5 - cx, py = y + 0.5 - cy;
                // Where this output pixel sits in the (unturned) page frame.
                var qx = Math.Clamp(cos * px - sin * py + cx, 0, width - 1e-6);
                var qy = Math.Clamp(sin * px + cos * py + cy, 0, height - 1e-6);
                var den = g * qx + hh * qy + 1;
                var u = (a * qx + b * qy + c) / den - 0.5;
                var v = (d * qx + e * qy + f) / den - 0.5;
                u = Math.Clamp(u, 0, sw - 1.001);
                v = Math.Clamp(v, 0, sh - 1.001);
                int x0 = (int)u, y0 = (int)v;
                double fx = u - x0, fy = v - y0;
                var i00 = y0 * sw + x0;
                for (var p = 0; p < planeCount; p++)
                {
                    var s = source.Planes[p];
                    var top = s[i00] + (s[i00 + 1] - s[i00]) * fx;
                    var bottom = s[i00 + sw] + (s[i00 + sw + 1] - s[i00 + sw]) * fx;
                    result.Planes[p][y * width + x] = (byte)Math.Clamp(top + (bottom - top) * fy + 0.5, 0, 255);
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Measures how far the text lines tilt, in degrees (positive: the lines run down towards the right).
    /// Dark pixels are projected across lines at trial angles; the angle that makes the rows most uneven
    /// (lines separated by white gaps) is the tilt. Searches -15 to 15 degrees to 0.05 degree. Returns 0
    /// with <paramref name="confidence"/> 0 when there is too little text to measure.
    /// </summary>
    public static double EstimateSkew(Raster grey, out double confidence)
    {
        confidence = 0;
        int w = grey.Width, h = grey.Height;
        // Level the lighting first, so a shadow does not read as ink.
        var levelled = NormalizeBackground(new Raster(w, h, [(byte[])grey.Planes[0].Clone()])).Planes[0];
        var threshold = Math.Min(OtsuThreshold(levelled), 200);
        var xs = new List<float>();
        var ys = new List<float>();
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(w * (double)h / 160_000)));
        for (var y = 0; y < h; y += 1)
        {
            if (y % stride != 0) continue;
            for (var x = 0; x < w; x += stride)
            {
                if (levelled[y * w + x] < threshold - 20)
                {
                    // Grid units, so a sampled grid projects without gaps in the row counts.
                    xs.Add((x - w / 2f) / stride);
                    ys.Add((y - h / 2f) / stride);
                }
            }
        }

        if (xs.Count < 200) return 0;

        var bins = (int)Math.Ceiling(Math.Sqrt((double)w * w + (double)h * h) / stride) + 4;
        var histogram = new int[bins];

        double Score(double degrees)
        {
            Array.Clear(histogram);
            var r = degrees * Math.PI / 180;
            float cosA = (float)Math.Cos(r), sinA = (float)Math.Sin(r);
            var offset = bins / 2;
            for (var i = 0; i < xs.Count; i++)
                histogram[(int)(-xs[i] * sinA + ys[i] * cosA + offset + 0.5f)]++;
            double sum = 0;
            for (var i = 1; i < bins; i++)
            {
                double diff = histogram[i] - histogram[i - 1];
                sum += diff * diff;
            }

            return sum;
        }

        double bestAngle = 0, bestScore = -1, total = 0;
        var count = 0;
        for (var a = -15.0; a <= 15.0001; a += 0.5)
        {
            var s = Score(a);
            total += s;
            count++;
            if (s > bestScore) { bestScore = s; bestAngle = a; }
        }

        var mean = total / count;
        for (var pass = 0; pass < 2; pass++)
        {
            var step = pass == 0 ? 0.1 : 0.02;
            var centre = bestAngle;
            var span = pass == 0 ? 0.5 : 0.1;
            for (var a = centre - span; a <= centre + span + 1e-9; a += step)
            {
                var s = Score(a);
                if (s > bestScore) { bestScore = s; bestAngle = a; }
            }
        }

        // A real text page has one sharp peak far above the average; a photo or a blank page does not.
        confidence = mean <= 0 ? 0 : bestScore / mean;
        return bestAngle;
    }

    // ---- Cleaning ---------------------------------------------------------------------

    /// <summary>
    /// Makes the paper white and even: estimates the paper brightness (dark text erased, then smoothed) on
    /// a reduced copy and divides it out of each plane. Removes shadows, vignetting and tinted lighting.
    /// </summary>
    public static Raster NormalizeBackground(Raster source)
    {
        int w = source.Width, h = source.Height;
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / 400.0));
        var result = new Raster(w, h, source.Planes.Length);
        for (var p = 0; p < source.Planes.Length; p++)
        {
            var small = DownscaleBy(new Raster(w, h, [source.Planes[p]]), factor);
            int sw = small.Width, sh = small.Height;
            var background = Dilate(small.Planes[0], sw, sh, 3);
            background = BoxBlur(background, sw, sh, 3);
            background = BoxBlur(background, sw, sh, 3);

            var src = source.Planes[p];
            var dst = result.Planes[p];
            Parallel.For(0, h, y =>
            {
                var fy = Math.Clamp((y + 0.5) / factor - 0.5, 0, sh - 1.001);
                var y0 = (int)fy;
                var ty = fy - y0;
                for (var x = 0; x < w; x++)
                {
                    var fx = Math.Clamp((x + 0.5) / factor - 0.5, 0, sw - 1.001);
                    var x0 = (int)fx;
                    var tx = fx - x0;
                    var i = y0 * sw + x0;
                    var top = background[i] + (background[i + 1] - background[i]) * tx;
                    var bottom = background[i + sw] + (background[i + sw + 1] - background[i + sw]) * tx;
                    var bg = Math.Max(48.0, top + (bottom - top) * ty);
                    dst[y * w + x] = (byte)Math.Min(255, src[y * w + x] * 255.0 / bg + 0.5);
                }
            });
        }

        return result;
    }

    /// <summary>Light noise reduction: each plane is blended 55:45 with a 3x3 weighted average.</summary>
    public static Raster Denoise(Raster source)
    {
        var result = new Raster(source.Width, source.Height, source.Planes.Length);
        for (var p = 0; p < source.Planes.Length; p++)
        {
            var src = source.Planes[p];
            var blur = Binomial3(src, source.Width, source.Height);
            var dst = result.Planes[p];
            for (var i = 0; i < src.Length; i++) dst[i] = (byte)((src[i] * 45 + blur[i] * 55 + 50) / 100);
        }

        return result;
    }

    private static byte[] Binomial3(byte[] plane, int w, int h)
    {
        var temp = new byte[plane.Length];
        var result = new byte[plane.Length];
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            for (var x = 0; x < w; x++)
                temp[o + x] = (byte)((plane[o + Math.Max(0, x - 1)] + 2 * plane[o + x] + plane[o + Math.Min(w - 1, x + 1)] + 2) >> 2);
        });
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                result[y * w + x] = (byte)((temp[Math.Max(0, y - 1) * w + x] + 2 * temp[y * w + x] + temp[Math.Min(h - 1, y + 1) * w + x] + 2) >> 2);
        });
        return result;
    }

    /// <summary>Unsharp mask: adds <paramref name="amount"/> times the difference from a blurred copy.</summary>
    public static Raster Sharpen(Raster source, double amount, int radius)
    {
        var result = new Raster(source.Width, source.Height, source.Planes.Length);
        for (var p = 0; p < source.Planes.Length; p++)
        {
            var src = source.Planes[p];
            var blur = BoxBlur(src, source.Width, source.Height, radius);
            var dst = result.Planes[p];
            for (var i = 0; i < src.Length; i++)
                dst[i] = (byte)Math.Clamp(src[i] + amount * (src[i] - blur[i]) + 0.5, 0, 255);
        }

        return result;
    }

    /// <summary>
    /// Stretches the range: darkest ink to black, paper (anything light enough) to pure white. Black and
    /// white points come from the brightness of the whole picture.
    /// </summary>
    public static Raster Levels(Raster source, int whitePoint = 232)
    {
        var grey = ToGrey(source);
        var black = Math.Min(Percentile(grey.Planes[0], 0.004), 110);
        var range = Math.Max(30, whitePoint - black);
        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)Math.Clamp((i - black) * 255.0 / range + 0.5, 0, 255);
        var result = new Raster(source.Width, source.Height, source.Planes.Length);
        for (var p = 0; p < source.Planes.Length; p++)
        {
            var src = source.Planes[p];
            var dst = result.Planes[p];
            for (var i = 0; i < src.Length; i++) dst[i] = table[src[i]];
        }

        return result;
    }

    /// <summary>
    /// Pure black and white: Otsu's threshold on the (already levelled) brightness, then single stray black
    /// dots (no black neighbour) are removed. Result values are 0 or 255.
    /// </summary>
    public static Raster ToBlackAndWhite(Raster source, bool despeckle = true)
    {
        var grey = ToGrey(source);
        int w = grey.Width, h = grey.Height;
        var plane = grey.Planes[0];
        var threshold = OtsuThreshold(plane);
        // Keep thin or faint strokes: the cut sits a little towards the paper.
        threshold = Math.Min(250, threshold + 8);
        var result = new byte[plane.Length];
        for (var i = 0; i < plane.Length; i++) result[i] = (byte)(plane[i] > threshold ? 255 : 0);

        if (despeckle)
        {
            var copy = (byte[])result.Clone();
            for (var y = 1; y < h - 1; y++)
            {
                for (var x = 1; x < w - 1; x++)
                {
                    var i = y * w + x;
                    if (copy[i] != 0) continue;
                    if (copy[i - 1] == 255 && copy[i + 1] == 255 && copy[i - w] == 255 && copy[i + w] == 255
                        && copy[i - w - 1] == 255 && copy[i - w + 1] == 255 && copy[i + w - 1] == 255 && copy[i + w + 1] == 255)
                        result[i] = 255;
                }
            }
        }

        return new Raster(w, h, [result]);
    }

    // ---- Output ---------------------------------------------------------------------------

    /// <summary>Encodes a picture as JPEG (grey stays one channel), for a PDF image.</summary>
    public static byte[] ToJpeg(Raster image, int quality)
    {
        using var bitmap = ToBitmap(image, forJpeg: true);
        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>Encodes a picture as PNG (previews).</summary>
    public static byte[] ToPng(Raster image)
    {
        using var bitmap = ToBitmap(image, forJpeg: false);
        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap ToBitmap(Raster image, bool forJpeg)
    {
        int w = image.Width, h = image.Height;
        if (image.IsGrey && forJpeg)
        {
            var grey = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(image.Planes[0], 0, grey.GetPixels(), image.Planes[0].Length);
            return grey;
        }

        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new byte[w * h * 4];
        var r = image.Planes[0];
        var g = image.IsGrey ? r : image.Planes[1];
        var b = image.IsGrey ? r : image.Planes[2];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 4] = r[i];
            pixels[i * 4 + 1] = g[i];
            pixels[i * 4 + 2] = b[i];
            pixels[i * 4 + 3] = 255;
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    /// <summary>Packs a 0/255 plane into 1 bit per pixel (1 = white), rows padded to whole bytes, for a PDF image.</summary>
    public static byte[] PackBits(Raster blackAndWhite)
    {
        int w = blackAndWhite.Width, h = blackAndWhite.Height;
        var stride = (w + 7) / 8;
        var packed = new byte[stride * h];
        var plane = blackAndWhite.Planes[0];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (plane[y * w + x] > 127) packed[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return packed;
    }
}
