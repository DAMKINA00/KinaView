using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Configuration;
using ImageMagick.Formats;

namespace KinaView;

internal static class AdvancedImages
{
    static AdvancedImages()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*"/>
              <policy domain="coder" rights="none" pattern="*"/>
              <policy domain="coder" rights="read|write" pattern="{GIF,PSD,PSB,DNG,CR2,CR3,CRW,NEF,NRW,ARW,SR2,SRF,RAF,RW2,RAW,ORF,PEF,SRW,ERF,MEF,MOS,KDC,DCR,3FR,FFF,IIQ,RWL,HDR,EXR,PNG,JPEG,TIFF,BMP,ICO,WEBP,HEIC,AVIF,RGB,RGBA,BGRA,XC}"/>
              <policy domain="path" rights="none" pattern="@*"/>
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        ResourceLimits.Memory = 1536UL * 1024 * 1024;
        ResourceLimits.Disk = 512UL * 1024 * 1024;
        ResourceLimits.ListLength = 2048;
        ResourceLimits.Width = 50000;
        ResourceLimits.Height = 50000;
        ResourceLimits.Thread = (uint)Math.Clamp(Environment.ProcessorCount, 1, 4);
    }

    internal static void Initialize() { }

    internal static LoadedImage Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long bytes = file.Length;
        if (extension is ".gif" or ".webp") return LoadAnimation(file, bytes);
        var settings = new MagickReadSettings { FrameCount = 1 };
        bool raw = extension is not (".psd" or ".psb" or ".hdr" or ".exr" or ".webp" or ".heic" or ".heif" or ".avif");
        // TIFF-shaped RAW containers otherwise select their embedded preview instead of LibRaw.
        if (raw) settings.Format = MagickFormat.Dng;
        settings.SetDefines(new DngReadDefines { UseCameraWhiteBalance = true });
        settings.SetDefine(MagickFormat.Dng, "max-raw-memory", 1024);
        // PSD scene zero is Photoshop's saved composite, including layer effects and visibility.
        using var image = new MagickImage();
        image.Ping(file, settings);
        CheckSize(image.Width, image.Height);
        file.Position = 0;
        image.Read(file, settings);
        CheckSize(image.Width, image.Height);
        image.AutoOrient();
        bool hdr = extension is ".hdr" or ".exr";
        if (!hdr)
        {
            if (image.GetColorProfile() != null) image.TransformColorSpace(ColorProfiles.SRGB);
            else image.ColorSpace = ColorSpace.sRGB;
        }
        string? note = hdr ? "HDR → SDR 톤 매핑" : extension is ".psd" or ".psb" ? "Photoshop 합성 이미지" : raw ? "RAW 현상 · 카메라 화이트밸런스" : null;
        return new LoadedImage(hdr ? ToneMap(image) : ToBitmap(image), bytes, 1, Note: note);
    }

    private static LoadedImage LoadAnimation(Stream file, long bytes)
    {
        using var collection = new MagickImageCollection();
        collection.Ping(file);
        if (collection.Count == 0) throw new InvalidDataException("애니메이션 프레임이 없습니다.");
        uint width = collection.Max(f => Math.Max(f.Width, f.Page.Width));
        uint height = collection.Max(f => Math.Max(f.Height, f.Page.Height));
        CheckAnimationSize(width, height, collection.Count);
        file.Position = 0;
        collection.Read(file);
        // Coalesce applies frame offsets, transparency and restore-background/previous disposal.
        collection.Coalesce();
        CheckAnimationSize(collection[0].Width, collection[0].Height, collection.Count);
        var frames = collection.Select(f =>
        {
            if (f.GetColorProfile() != null) f.TransformColorSpace(ColorProfiles.SRGB);
            else f.ColorSpace = ColorSpace.sRGB;
            return new AnimationFrame(ToBitmap(f), Delay(f.AnimationDelay, f.AnimationTicksPerSecond));
        }).ToArray();
        return new LoadedImage(frames[0].Bitmap, bytes, frames.Length, frames.Length > 1 ? frames : null);
    }

    internal static int Delay(uint delay, int ticks) => delay == 0 ? 100 : (int)Math.Clamp(1000L * delay / Math.Max(1, ticks), 20, int.MaxValue);
    internal static void CheckAnimationSize(uint width, uint height, int count)
    {
        CheckSize(width, height);
        if (count < 1 || count > 2048 || (ulong)width * height * (ulong)count * 4 > 128UL * 1024 * 1024)
            throw new InvalidDataException("애니메이션이 재생 메모리 한도(128 MiB / 2048 프레임)를 넘습니다.");
    }
    private static void CheckSize(uint width, uint height)
    {
        if (width == 0 || height == 0 || (ulong)width * height > 100_000_000)
            throw new InvalidDataException("유효한 이미지가 아니거나 1억 픽셀을 넘습니다.");
    }

    internal static byte ToneMapChannel(float quantum)
    {
        double linear = float.IsNaN(quantum) || quantum <= 0 ? 0 : quantum / (double)Quantum.Max;
        double mapped = double.IsPositiveInfinity(linear) ? 1 : linear / (1 + linear);
        double srgb = mapped <= 0.0031308 ? 12.92 * mapped : 1.055 * Math.Pow(mapped, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(srgb * 255), 0, 255);
    }

    private static BitmapSource ToneMap(MagickImage image)
    {
        image.ColorSpace = ColorSpace.RGB;
        image.ColorType = ColorType.TrueColorAlpha;
        var data = new byte[checked((int)(image.Width * image.Height * 4))];
        using var pixels = image.GetPixels();
        // Map HDR floats before any 8-bit export so highlights above SDR white survive.
        int channels = (int)image.ChannelCount;
        var options = new ParallelOptions { MaxDegreeOfParallelism = (int)ResourceLimits.Thread };
        // Read bounded tiles with one native cache view; parallel workers only touch managed arrays.
        for (int tileY = 0; tileY < image.Height; tileY += 128)
        {
            int rows = Math.Min(128, (int)image.Height - tileY);
            var tile = pixels.GetArea(0, tileY, image.Width, (uint)rows)!;
            int startY = tileY;
            Parallel.For(0, rows, options, y =>
            {
                for (int x = 0; x < image.Width; x++)
                {
                    int source = (y * (int)image.Width + x) * channels;
                    int target = ((startY + y) * (int)image.Width + x) * 4;
                    data[target] = ToneMapChannel(tile[source + 2]);
                    data[target + 1] = ToneMapChannel(tile[source + 1]);
                    data[target + 2] = ToneMapChannel(tile[source]);
                    float alpha = tile[source + 3] / Quantum.Max;
                    data[target + 3] = float.IsFinite(alpha) ? (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255) : (byte)0;
                }
            });
        }
        var bitmap = BitmapSource.Create((int)image.Width, (int)image.Height, 96, 96, PixelFormats.Bgra32, null, data, (int)image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ToBitmap(IMagickImage<float> image)
    {
        using var pixels = image.GetPixels();
        byte[] data = pixels.ToByteArray(PixelMapping.BGRA)!;
        var bitmap = BitmapSource.Create((int)image.Width, (int)image.Height, 96, 96, PixelFormats.Bgra32, null, data, checked((int)image.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }
}
