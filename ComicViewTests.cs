using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KinaView;

internal static class ComicViewTests
{
    internal static async Task Run(MainWindow window, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(output, "comic-fixtures");
        Directory.CreateDirectory(fixture);
        var large = Pattern(2400, 1200);
        var small = Pattern(24, 12);
        var alpha = AlphaPattern();

        byte[] largePixels = Pixels(large);
        var down = DisplayResizer.ResizeBitmap(large, 96, 48);
        check(down.PixelWidth == 96 && down.PixelHeight == 48 && down.IsFrozen, "Comic resize downsizes large image to exact frozen dimensions");
        check(Pixels(large).SequenceEqual(largePixels), "Comic downresize leaves source pixels unchanged");

        var up = DisplayResizer.ResizeBitmap(small, 384, 192);
        check(up.PixelWidth == 384 && up.PixelHeight == 192 && up.IsFrozen, "Comic resize upsizes small image to exact frozen dimensions");

        var alphaResult = DisplayResizer.ResizeBitmap(alpha, 32, 16);
        byte[] alphaBytes = Pixels(alphaResult);
        check(alphaResult.IsFrozen && alphaResult.Format.BitsPerPixel == 32 &&
            alphaBytes.Where((_, i) => i % 4 == 3).Any(a => a == 0) &&
            alphaBytes.Where((_, i) => i % 4 == 3).Any(a => a > 0 && a < 255),
            "Comic resize preserves transparent and partial alpha");

        var bound = DisplayResizer.BoundSize(100_000, 50_000);
        check((long)bound.Width * bound.Height <= 16_000_000 && Math.Max(bound.Width, bound.Height) <= 16_000 &&
            Math.Abs(bound.Width / (double)bound.Height - 2) < .001,
            "Comic resize bounds pixel budget, maximum dimension, and aspect ratio");
        var minimum = DisplayResizer.BoundSize(0, 0);
        check(minimum == (1, 1), "Comic resize clamps invalid target dimensions");
        var roundedBudget = DisplayResizer.BoundSize(14000, 9000);
        check((long)roundedBudget.Width * roundedBudget.Height <= 16_000_000,
            "Comic resize rounding cannot exceed the pixel budget");

        var rendered = await Task.Run(() => DisplayResizer.RenderAsync(large, 120, 60, false));
        check(!rendered.UsedAi && rendered.FallbackReason == null && rendered.Bitmap.PixelWidth == 120 && rendered.Bitmap.PixelHeight == 60,
            "Comic render uses bounded Lanczos path without AI when disabled");
        var reduced = await Task.Run(() => DisplayResizer.RenderAsync(large, 120, 60, true));
        check(!reduced.UsedAi && reduced.FallbackReason == null && reduced.Bitmap.PixelWidth == 120 && reduced.Bitmap.PixelHeight == 60,
            "Comic mode also processes reduction with Lanczos while AI is enabled");

        await TestRequestLifecycle(small, large, check);
        await TestRealAi(Pattern(4, 4), check);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var enhancedAlpha = await Task.Run(() => DisplayResizer.RenderAsync(alpha, 32, 16, true, timeout.Token));
            var alphaPixels = Pixels(enhancedAlpha.Bitmap);
            check(enhancedAlpha.UsedAi && alphaPixels.Where((_, i) => i % 4 == 3).Any(a => a == 0) &&
                alphaPixels.Where((_, i) => i % 4 == 3).Any(a => a > 0 && a < 255),
                "AI Comic View preserves transparent and translucent pixels");
        }

        string largePath = Path.Combine(fixture, "large.png");
        string smallPath = Path.Combine(fixture, "small.png");
        Save(large, largePath);
        Save(small, smallPath);
        byte[] largeFileHash = SHA256.HashData(File.ReadAllBytes(largePath));
        byte[] smallFileHash = SHA256.HashData(File.ReadAllBytes(smallPath));

        var comicMenu = (MenuItem)window.FindName("ComicMenu")!;
        check(!window.ComicViewEnabled && !comicMenu.IsChecked && comicMenu.IsCheckable &&
            (string)comicMenu.Header == "만화 보기" && comicMenu.InputGestureText == "Ctrl+M",
            "Comic View menu starts unchecked with Ctrl+M shortcut");

        window.SetComicMode(false);
        await window.OpenPathAsync(largePath);
        await Layout(window);
        var originalLarge = window.Displayed!;
        check(Math.Abs(window.Zoom - window.FitZoom()) < 1e-9 && ReferenceEquals(window.Displayed, originalLarge),
            "Normal viewer fits large image using original bitmap");
        window.SetZoom(1.25, exact: true);
        await Layout(window);
        CheckGeometry(window, originalLarge, check, "Normal viewer geometry uses large original pixels");

        await window.OpenPathAsync(smallPath);
        await Layout(window);
        var originalSmall = window.Displayed!;
        check(Math.Abs(window.Zoom - window.FitZoom()) < 1e-9 && ReferenceEquals(window.Displayed, originalSmall),
            "Normal viewer fits small image using original bitmap");
        window.SetZoom(1.5, exact: true);
        await Layout(window);
        CheckGeometry(window, originalSmall, check, "Normal viewer geometry resizes small original pixels");

        await window.OpenPathAsync(largePath);
        await Layout(window);
        originalLarge = window.Displayed!;
        window.SetZoom(1.25, exact: true);
        window.ToggleComicView();
        check(window.ComicViewEnabled, "Comic View enables from the viewer");
        check(Math.Abs(window.Zoom - window.FitZoom()) < 1e-9 && window.Zoom < 1,
            "Enabling Comic View after manual zoom fits and reduces the large image");
        await WaitFor(() => window.Displayed != null && !ReferenceEquals(window.Displayed, originalLarge), "large Comic View result");
        await Layout(window);
        CheckGeometry(window, originalLarge, check, "Comic View keeps large image geometry in original pixels");
        var content = (FrameworkElement)window.Content;
        var preview = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(content);
        Save(preview, Path.Combine(output, "preview-comic.png"));
        check(SHA256.HashData(File.ReadAllBytes(largePath)).SequenceEqual(largeFileHash),
            "Enabling Comic View leaves the large original file unchanged");
        window.ToggleComicView();
        await Layout(window);
        check(!window.ComicViewEnabled && ReferenceEquals(window.Displayed, originalLarge),
            "Disabling Comic View restores the original large bitmap");

        await window.OpenPathAsync(smallPath);
        await Layout(window);
        originalSmall = window.Displayed!;
        window.SetZoom(1.5, exact: true);
        window.ToggleComicView();
        check(Math.Abs(window.Zoom - window.FitZoom()) < 1e-9 && window.Zoom > 16,
            "Comic View fills the viewport even for images requiring more than 1600 percent enlargement");
        await WaitFor(() => window.Displayed != null && !ReferenceEquals(window.Displayed, originalSmall), "small Comic View result");
        await Layout(window);
        CheckGeometry(window, originalSmall, check, "Comic View keeps small image geometry in original pixels");
        check(SHA256.HashData(File.ReadAllBytes(smallPath)).SequenceEqual(smallFileHash),
            "Enabling Comic View leaves the small original file unchanged");
        window.ToggleComicView();
        await Layout(window);
        check(!window.ComicViewEnabled && ReferenceEquals(window.Displayed, originalSmall),
            "Disabling Comic View restores the original small bitmap");

        await TestNavigationRace(window, fixture, largePath, smallPath, check);
        await TestAnimation(window, output, check);
        await TestComparison(window, largePath, smallPath, check);
        window.SetComicMode(false);
    }

    private static async Task TestRequestLifecycle(BitmapSource first, BitmapSource second, Action<bool, string> check)
    {
        using (var resizer = new DisplayResizer())
        {
            var callbacks = new List<int>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resizer.Request(first, 8, 4, true, result => callbacks.Add(result.Bitmap.PixelWidth));
            resizer.Request(second, 80, 40, false, result => { callbacks.Add(result.Bitmap.PixelWidth); completed.TrySetResult(); });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(callbacks.SequenceEqual(new[] { 80 }), "Display resizer latest request wins asynchronously");
        }

        int cleared = 0;
        using (var resizer = new DisplayResizer())
        {
            resizer.Request(first, 8, 4, true, _ => Interlocked.Increment(ref cleared));
            resizer.Clear();
            await Task.Delay(300);
        }
        check(cleared == 0, "Display resizer Clear suppresses pending result");

        int disposed = 0;
        var disposedResizer = new DisplayResizer();
        disposedResizer.Request(first, 8, 4, true, _ => Interlocked.Increment(ref disposed));
        disposedResizer.Dispose();
        await Task.Delay(300);
        check(disposed == 0, "Display resizer Dispose suppresses pending result");
    }

    private static async Task TestRealAi(BitmapSource source, Action<bool, string> check)
    {
        string runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", "RealESRGAN");
        string executable = Path.Combine(runtime, "realesrgan-ncnn-vulkan.exe");
        string model = Path.Combine(runtime, "models", "realesr-animevideov3-x3.bin");
        string parameters = Path.Combine(runtime, "models", "realesr-animevideov3-x3.param");
        if (!File.Exists(executable) || !File.Exists(model) || !File.Exists(parameters))
        {
            check(false, $"RealESRGAN runtime unavailable under {runtime}; AI Comic View test was not run");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ResizeResult result;
        try { result = await Task.Run(() => DisplayResizer.RenderAsync(source, 12, 12, true, timeout.Token)); }
        catch (OperationCanceledException ex) { throw new InvalidOperationException("RealESRGAN AI Comic View test timed out.", ex); }
        check(result.UsedAi && result.FallbackReason == null && result.Bitmap.PixelWidth == 12 && result.Bitmap.PixelHeight == 12,
            $"RealESRGAN AI resize produced 12x12 output (UsedAi={result.UsedAi}, fallback={result.FallbackReason ?? "none"})");
    }

    private static async Task TestNavigationRace(MainWindow window, string fixture, string largePath, string smallPath, Action<bool, string> check)
    {
        string folder = Path.Combine(fixture, "navigation");
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "a-small.png");
        string second = Path.Combine(folder, "b-large.png");
        File.Copy(smallPath, first, true);
        File.Copy(largePath, second, true);
        window.SetComicMode(false);
        await window.OpenPathAsync(folder);
        await Layout(window);
        window.ToggleComicView();
        await Task.Delay(200);
        await window.ShowIndexAsync(1);
        await WaitFor(() => string.Equals(window.CurrentPath, second, StringComparison.OrdinalIgnoreCase) && window.Displayed != null,
            "latest navigation result");
        check(string.Equals(window.CurrentPath, second, StringComparison.OrdinalIgnoreCase) &&
            window.Displayed!.PixelWidth > 100 && window.Displayed.PixelHeight > 100,
            "Navigation cannot let a stale Comic View resize overwrite the newest image");
        window.SetComicMode(false);
    }

    private static async Task TestAnimation(MainWindow window, string output, Action<bool, string> check)
    {
        string? gif = Directory.GetFiles(output, "*.gif", SearchOption.AllDirectories).FirstOrDefault();
        if (gif == null)
        {
            check(true, "Comic animation resize skipped because no animation fixture is available");
            return;
        }
        window.SetComicMode(false);
        await window.OpenPathAsync(gif);
        await Layout(window);
        check(window.IsAnimating, "Animation fixture starts playing before Comic View");
        window.ToggleComicView();
        await WaitFor(() => window.Displayed != null && window.ComicViewEnabled, "animated Comic View result");
        int before = window.AnimationIndex;
        await Task.Delay(450);
        check(window.IsAnimating && window.AnimationIndex != before,
            "Animation advances while Comic View resizes frames without AI");
        window.SetComicMode(false);
    }

    private static async Task TestComparison(MainWindow window, string largePath, string smallPath, Action<bool, string> check)
    {
        await window.OpenPathAsync(largePath);
        await Layout(window);
        window.SetComicMode(true);
        await WaitFor(() => window.Displayed != null && window.ComicViewEnabled, "comparison source Comic View result");
        await window.StartComparison();
        await window.OpenComparisonFiles([largePath, smallPath]);
        await Layout(window);
        check(window.ComparisonPanes.Count == 2 && window.ComparisonPanes.All(p => p.ComicViewEnabled),
            "Comparison panes inherit Comic View mode");
        await WaitFor(() => window.ComparisonPanes.All(p => p.Displayed != null), "comparison Comic View results");
        check(window.ComparisonPanes.All(p => p.Displayed!.PixelWidth > 0 && p.Displayed.PixelHeight > 0),
            "Comparison panes display bounded Comic View bitmaps");
        window.SetComicMode(false);
        await Layout(window);
        check(window.ComparisonPanes.All(p => !p.ComicViewEnabled) &&
            window.ComparisonPanes[0].Displayed?.PixelWidth == 2400 && window.ComparisonPanes[1].Displayed?.PixelWidth == 24,
            "Disabling Comic View restores original comparison pane bitmaps");
        foreach (var pane in window.ComparisonPanes) pane.SetZoom(1, new(.5, .5));
        window.SetComicMode(true);
        check(window.ComparisonPanes.All(p => Math.Abs(p.Zoom - p.FitZoom()) < 1e-9),
            "Enabling Comic View fits every comparison pane after manual zoom");
        window.SetComicMode(false);
        window.EndComparison();
        await Layout(window);
    }

    private static void CheckGeometry(MainWindow window, BitmapSource original, Action<bool, string> check, string name)
    {
        var picture = (Image)window.FindName("Picture")!;
        var dpi = VisualTreeHelper.GetDpi(picture);
        check(Math.Abs(picture.Width - original.PixelWidth * window.Zoom / dpi.DpiScaleX) < .01 &&
            Math.Abs(picture.Height - original.PixelHeight * window.Zoom / dpi.DpiScaleY) < .01, name);
    }

    private static async Task Layout(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task WaitFor(Func<bool> condition, string name)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for " + name + ".");
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 17 + y * 3);
                pixels[i + 1] = (byte)(y * 19 + x * 5);
                pixels[i + 2] = (byte)(x * 7 + y * 23);
                pixels[i + 3] = 255;
            }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private static BitmapSource AlphaPattern()
    {
        var pixels = new byte[16 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                pixels[i] = (byte)(x * 13);
                pixels[i + 1] = (byte)(y * 29);
                pixels[i + 2] = 220;
                pixels[i + 3] = (byte)(x < 5 ? 0 : x < 10 ? 128 : 255);
            }
        var result = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        result.Freeze();
        return result;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
