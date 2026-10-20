using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Formats;
using Microsoft.Win32;

namespace KinaView;

internal static class FormatTests
{
    internal static async Task Run(MainWindow window, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(output, "advanced-fixtures");
        Directory.CreateDirectory(fixture);
        string gif = Path.Combine(fixture, "animation.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 20, 12) { AnimationDelay = 10, AnimationTicksPerSecond = 100, GifDisposeMethod = GifDisposeMethod.None });
            frames.Add(new MagickImage(MagickColors.Lime, 2, 2) { AnimationDelay = 20, AnimationTicksPerSecond = 100, Page = new MagickGeometry(2, 2, 20, 12), GifDisposeMethod = GifDisposeMethod.Previous });
            frames.Add(new MagickImage(MagickColors.Blue, 2, 2) { AnimationDelay = 30, AnimationTicksPerSecond = 100, Page = new MagickGeometry(8, 2, 20, 12), GifDisposeMethod = GifDisposeMethod.None });
            frames[0].AnimationIterations = 2;
            frames.Write(gif);
        }
        using (var finite = new MagickImageCollection())
        {
            finite.Ping(gif);
            check(finite[0].AnimationIterations == 2, "GIF finite loop metadata is preserved");
        }
        var animation = ImageFiles.Load(gif);
        check(animation.Animation?.Length == 3, "GIF finite loop still decodes all frames");
        var decoded = animation.Animation!;
        check(decoded.Select(f => f.DelayMilliseconds).SequenceEqual(new[] { 100, 200, 300 }), "GIF frame-specific timing");
        check(Pixel(decoded[0].Bitmap, 2, 2).SequenceEqual(new byte[] { 0, 0, 255, 255 }), "GIF first frame red");
        check(Pixel(decoded[1].Bitmap, 2, 2).SequenceEqual(new byte[] { 0, 255, 0, 255 }), "GIF partial-frame offset composited");
        check(Pixel(decoded[2].Bitmap, 2, 2).SequenceEqual(new byte[] { 0, 0, 255, 255 }), "GIF restore-previous removes transient region");
        check(Pixel(decoded[2].Bitmap, 8, 2).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "GIF next partial frame retained");
        check(AdvancedImages.Delay(0, 100) == 100 && AdvancedImages.Delay(1, 100) == 20, "GIF zero and short delay bounds");
        bool oversized = false;
        try { AdvancedImages.CheckAnimationSize(4000, 4000, 100); } catch (InvalidDataException) { oversized = true; }
        check(oversized, "Animation memory budget rejects oversized sequence");
        string webp = Path.Combine(fixture, "animation.webp");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(new MagickColor(Quantum.Max, 0, 0, Quantum.Max / 2), 20, 12) { AnimationDelay = 10, AnimationTicksPerSecond = 100, Quality = 100 });
            frames.Add(new MagickImage(MagickColors.Lime, 20, 12) { AnimationDelay = 20, AnimationTicksPerSecond = 100, Quality = 100 });
            frames.Add(new MagickImage(MagickColors.Blue, 20, 12) { AnimationDelay = 30, AnimationTicksPerSecond = 100, Quality = 100 });
            frames[0].AnimationIterations = 2;
            frames.Write(webp, new WebPWriteDefines { Lossless = true, AlphaQuality = 100 });
        }
        using (var finite = new MagickImageCollection())
        {
            finite.Ping(webp);
            check(finite[0].AnimationIterations == 2, "WebP finite loop metadata is preserved");
        }
        var webpImage = ImageFiles.Load(webp);
        using (var exclusive = File.Open(webp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) check(exclusive.Length > 0, "Animated WebP releases source file");
        check(webpImage.Animation?.Length == 3, "Animated WebP decodes all frames");
        var webpDecoded = webpImage.Animation!;
        check(webpDecoded.Select(f => f.DelayMilliseconds).SequenceEqual(new[] { 100, 200, 300 }), "WebP frame-specific timing");
        check(Pixel(webpDecoded[0].Bitmap, 2, 2)[2] > 200 && Pixel(webpDecoded[0].Bitmap, 2, 2)[0] < 20, "WebP first frame red");
        check(Pixel(webpDecoded[1].Bitmap, 2, 2)[1] > 200 && Pixel(webpDecoded[1].Bitmap, 2, 2)[0] < 20, "WebP second frame green");
        var translucent = Pixel(webpDecoded[0].Bitmap, 2, 2);
        check(translucent[2] > 100 && translucent[3] < 255, "WebP preserves translucent alpha");
        string staticWebpPath = Path.Combine(fixture, "static.webp");
        using (var still = new MagickImage(MagickColors.Gold, 20, 12)) still.Write(staticWebpPath);
        var staticWebp = ImageFiles.Load(staticWebpPath);
        check(staticWebp.Animation == null && staticWebp.FrameCount == 1, "Static WebP remains non-animated");
        await window.OpenPathAsync(gif);
        check(window.IsAnimating, "GIF automatically plays in live window");
        window.SetZoom(2);
        window.Rotate();
        await Task.Delay(160);
        check(window.AnimationIndex != 0 && window.Zoom == 2, "Live animation timer advances without resetting zoom");
        window.ToggleAnimation();
        int paused = window.AnimationIndex;
        await Task.Delay(240);
        check(!window.IsAnimating && window.AnimationIndex == paused, "GIF pause holds the current frame");
        for (int i = 0; i < decoded.Length; i++) window.AdvanceAnimation();
        check(window.AnimationIndex == paused, "GIF continuously wraps");
        window.ToggleAnimation();
        check(window.IsAnimating, "GIF resumes");
        await window.OpenPathAsync(webp);
        check(window.IsAnimating, "Animated WebP automatically plays in live window");
        window.ToggleAnimation();
        int webpPaused = window.AnimationIndex;
        await Task.Delay(240);
        check(!window.IsAnimating && window.AnimationIndex == webpPaused, "WebP pause holds the current frame");
        for (int i = 0; i < webpDecoded.Length; i++) window.AdvanceAnimation();
        check(window.AnimationIndex == webpPaused, "WebP continuously wraps");
        window.ToggleAnimation();
        check(window.IsAnimating, "WebP resumes");
        await window.OpenPathAsync(staticWebpPath);
        check(!window.IsAnimating && window.Displayed != null, "Opening static WebP stops animation");
        string hdr = Path.Combine(fixture, "highlights.hdr");
        using (var stream = File.Create(hdr))
        {
            stream.Write(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 3\n"));
            byte[] row = [128,128,128,127, 128,128,128,129, 128,128,128,131];
            stream.Write(row); stream.Write(row);
        }
        var hdrImage = ImageFiles.Load(hdr);
        byte low = Pixel(hdrImage.Bitmap, 0, 0)[0], middle = Pixel(hdrImage.Bitmap, 1, 0)[0], high = Pixel(hdrImage.Bitmap, 2, 0)[0];
        check(Math.Abs(low - 124) <= 2 && Math.Abs(middle - 188) <= 2 && Math.Abs(high - 231) <= 2, $"HDR linear 0.25/1/4 tone mapping: {low}/{middle}/{high}");
        check(hdrImage.Note!.Contains("SDR"), "HDR display discloses SDR tone mapping");
        check(AdvancedImages.ToneMapChannel(float.NaN) == 0 && AdvancedImages.ToneMapChannel(-1) == 0 && AdvancedImages.ToneMapChannel(float.PositiveInfinity) == 255, "HDR nonfinite and negative values handled");
        string exr = Path.Combine(fixture, "highlights.exr");
        using (var image = new MagickImage(hdr)) image.Write(exr);
        var exrImage = ImageFiles.Load(exr);
        check(Pixel(exrImage.Bitmap, 2, 0)[0] > Pixel(exrImage.Bitmap, 1, 0)[0], "EXR retains values above SDR white before mapping");
        await window.OpenPathAsync(hdr);
        check(!window.IsAnimating && window.Displayed != null, "Opening static image stops GIF timer");
        await Task.WhenAll(window.OpenPathAsync(gif), window.OpenPathAsync(exr));
        check(window.CurrentPath == exr && !window.IsAnimating && window.Displayed != null, "Rapid GIF/static navigation leaves no animation running");
        var sourceFolder = Path.Combine(Path.GetDirectoryName(output)!, "format-samples");
        foreach (string name in new[] { "DSC8695.dng", "StillLife.cr2", "layers.psd" })
        {
            string path = Path.Combine(sourceFolder, name);
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            var image = await Task.Run(() => ImageFiles.Load(path));
            check(image.Bitmap.PixelWidth > 100 && image.Bitmap.PixelHeight > 100, $"Real {name} decoded at {image.Bitmap.PixelWidth}x{image.Bitmap.PixelHeight}");
            check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Read-only decoder preserves " + name);
            using (var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) check(exclusive.Length > 0, "Decoder releases " + name);
            await window.OpenPathAsync(path);
            check(window.Displayed != null && window.Error == null, "Live UI opens " + name);
        }
        string sandbox = @"Software\KinaView\Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(sandbox);
            using (var ext = root.CreateSubKey(@"Software\Classes\.jpg")) ext.SetValue("", "Existing.Viewer");
            using (var other = root.CreateSubKey(@"Software\Classes\.jpg\OpenWithProgids")) other.SetValue("Existing.Viewer", "");
            using (var other = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\OpenWithProgids")) other.SetValue("Existing.Viewer", "");
            using (var legacy = root.CreateSubKey(@"Software\Classes\SystemFileAssociations\.jpg\shell\KinaView")) legacy.SetValue("", "old");
            using (var choice = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\UserChoice")) choice.SetValue("ProgId", "Existing.Viewer");
            ShellIntegration.Register(root, Environment.ProcessPath!);
            check(ShellIntegration.IsRegistered(root, Environment.ProcessPath!), "Explorer registration targets exact executable");
            check(ShellIntegration.OpenCommand(@"C:\Program Files\KinaView\KinaView.exe") == "\"C:\\Program Files\\KinaView\\KinaView.exe\" \"%1\"", "Shell command quotes executable and image path");
            foreach (string extension in ImageFiles.Extensions)
            {
                using var key = root.OpenSubKey(@"Software\Classes\*\shell\KinaView\command");
                using var context = root.OpenSubKey(@"Software\Classes\*\shell\KinaView");
                check(key?.GetValue("") as string == ShellIntegration.OpenCommand(Environment.ProcessPath!) &&
                    (context?.GetValue("AppliesTo") as string)?.Split(" OR ").Contains("System.FileExtension:=" + extension) == true, "Context menu for " + extension);
                using var openWith = root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
                using var supported = root.OpenSubKey(@"Software\Classes\Applications\KinaView.exe\SupportedTypes");
                using var associations = root.OpenSubKey(@"Software\KinaView\Capabilities\FileAssociations");
                using var explorerOpen = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\OpenWithProgids");
                check(openWith?.GetValue("KinaView.Image") != null && explorerOpen?.GetValue("KinaView.Image") != null && supported?.GetValue(extension) != null &&
                    associations?.GetValue(extension) as string == "KinaView.Image", "Open With and default-app candidate for " + extension);
            }
            ShellIntegration.Register(root, Environment.ProcessPath!);
            using (var legacy = root.OpenSubKey(@"Software\Classes\SystemFileAssociations\.jpg\shell\KinaView"))
                check(legacy == null, "Registration migrates old context menu without duplication");
            ShellIntegration.Unregister(root);
            using (var context = root.OpenSubKey(@"Software\Classes\*\shell\KinaView"))
                check(context == null, "Unregister removes shared context verb");
            using (var explorerOpen = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\OpenWithProgids"))
                check(explorerOpen?.GetValue("Existing.Viewer") != null && explorerOpen.GetValue("KinaView.Image") == null, "Unregister preserves other Explorer app candidates");
            check(!ShellIntegration.IsRegistered(root, Environment.ProcessPath!), "Explorer unregister removes only KinaView registration");
            using var existing = root.OpenSubKey(@"Software\Classes\.jpg");
            using var existingOpen = root.OpenSubKey(@"Software\Classes\.jpg\OpenWithProgids");
            using var existingChoice = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\UserChoice");
            check(existing?.GetValue("") as string == "Existing.Viewer" && existingChoice?.GetValue("ProgId") as string == "Existing.Viewer" && existingOpen?.GetValue("Existing.Viewer") != null, "Register/unregister preserve defaults, UserChoice and other apps");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); }
        window.Width = window.MinWidth;
        window.UpdateLayout();
        var menu = (FrameworkElement)window.FindName("MainMenu");
        var zoom = (FrameworkElement)window.FindName("ZoomLabel");
        check(menu.ActualWidth > 250 && zoom.TranslatePoint(new Point(), window).X > 250, "Compact menu and zoom fit at 640px minimum width");
        check(MagickNET.Features.Contains("OpenMP") && ResourceLimits.Thread == (uint)Math.Clamp(Environment.ProcessorCount, 1, 4), "Native OpenMP enabled with bounded multicore worker count");
        window.Width = 1160;
    }

    private static byte[] Pixel(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
}
