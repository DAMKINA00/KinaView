using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KinaView;

internal sealed record AnimationFrame(BitmapSource Bitmap, int DelayMilliseconds);
internal sealed record LoadedImage(BitmapSource Bitmap, long FileBytes, int FrameCount, AnimationFrame[]? Animation = null, string? Note = null);

internal static class ImageFiles
{
    internal static readonly string[] Extensions = [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".psd", ".psb", ".hdr", ".exr", ".dng", ".raw", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".sr2", ".srf", ".raf", ".rw2", ".orf", ".pef", ".srw", ".erf", ".mef", ".mos", ".kdc", ".dcr", ".3fr", ".fff", ".iiq", ".rwl"];
    internal static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    internal static string[] List(string directory) => Directory.EnumerateFiles(directory)
        .Where(IsImage).OrderBy(p => Path.GetFileName(p), Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b)))
        .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static int Step(int index, int delta, int count) => count == 0 ? -1 : (int)(((long)index + delta % count + count) % count);

    internal static double Fit(int width, int height, double viewportWidth, double viewportHeight, double maximum = 16)
    {
        return Math.Min(maximum, Math.Min(Math.Max(1, viewportWidth - 2) / width, Math.Max(1, viewportHeight - 2) / height));
    }

    internal static double RoundZoom(double value)
    {
        double percent = Math.Clamp(double.IsFinite(value) ? value * 100 : 100, 1, 1600);
        double step = percent >= 200 ? 50 : percent >= 10 ? 10 : 1;
        return Math.Round(percent / step, MidpointRounding.AwayFromZero) * step / 100;
    }

    internal static double StepZoom(double current, double factor, double fitZoom = double.NaN)
    {
        double percent = Math.Round(current * 100, 6);
        bool increase = factor > 1;
        double step = percent > 200 || (increase && percent >= 200) ? 50 : percent > 10 || (increase && percent >= 10) ? 10 : 1;
        double next = increase ? (Math.Floor(percent / step) + 1) * step : (Math.Ceiling(percent / step) - 1) * step;
        double minimum = double.IsFinite(fitZoom) && fitZoom > 0 ? Math.Min(.01, fitZoom) : .01;
        double value = Math.Clamp(next / 100, minimum, 16);
        // A fit stop belongs between the regular steps, not to the rounded step grid.
        if (double.IsFinite(fitZoom) && fitZoom > 0 && fitZoom <= 16 && Math.Abs(current - fitZoom) > 1e-9 &&
            (increase ? current < fitZoom && value >= fitZoom : current > fitZoom && value <= fitZoom)) return fitZoom;
        return value;
    }

    internal static LoadedImage Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tif" or ".tiff" or ".ico"))
            return AdvancedImages.Load(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // OnDemand lets us check dimensions before allocating the decoded pixel buffer.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
            throw new InvalidDataException("1억 픽셀을 넘는 이미지는 이 버전에서 열 수 없습니다.");
        int orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                object? value = metadata.GetQuery("/app1/ifd/{ushort=274}") ?? metadata.GetQuery("/ifd/{ushort=274}");
                if (value != null) orientation = Convert.ToInt32(value);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or COMException) { }
        // CopyPixels detaches the image from its file; viewing never keeps an original locked.
        int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
        long pixelBytes = (long)stride * frame.PixelHeight;
        if (pixelBytes > 512L * 1024 * 1024) throw new InvalidDataException("디코딩된 이미지가 512 MB를 넘습니다.");
        var pixels = new byte[(int)pixelBytes];
        frame.CopyPixels(pixels, stride, 0);
        BitmapSource bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, frame.Palette, pixels, stride);
        bitmap = Orient(bitmap, orientation);
        bitmap.Freeze();
        return new LoadedImage(bitmap, stream.Length, decoder.Frames.Count);
    }

    internal static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };
        return transform == Transform.Identity ? source : new TransformedBitmap(source, transform);
    }
}
