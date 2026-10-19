using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KinaView;

internal static class SelfTest
{
    internal static async Task<int> Run(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            string fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            var sample = MakeSample();
            Save(sample, Path.Combine(fixtures, "사진1.png"), new PngBitmapEncoder());
            Save(sample, Path.Combine(fixtures, "사진2.jpg"), new JpegBitmapEncoder());
            Save(sample, Path.Combine(fixtures, "사진10.bmp"), new BmpBitmapEncoder());
            Save(sample, Path.Combine(fixtures, "사진11.gif"), new GifBitmapEncoder());
            var tiff = new TiffBitmapEncoder();
            tiff.Frames.Add(BitmapFrame.Create(sample));
            Save(sample, Path.Combine(fixtures, "사진12.tiff"), tiff);
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var jpeg = new JpegBitmapEncoder();
            jpeg.Frames.Add(BitmapFrame.Create(sample, null, metadata, null));
            using (var file = File.Create(Path.Combine(fixtures, "사진13-oriented.jpg"))) jpeg.Save(file);
            byte[] smallPixels = [1, 2, 3, 4, 5, 6];
            var small = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray8, null, smallPixels, 3);
            byte[][] expected = [
                [1,2,3,4,5,6], [3,2,1,6,5,4], [6,5,4,3,2,1], [4,5,6,1,2,3],
                [1,4,2,5,3,6], [4,1,5,2,6,3], [6,3,5,2,4,1], [3,6,2,5,1,4]
            ];
            for (int i = 1; i <= 8; i++)
            {
                var oriented = ImageFiles.Orient(small, i);
                var actual = new byte[6];
                oriented.CopyPixels(actual, oriented.PixelWidth, 0);
                Check(actual.SequenceEqual(expected[i - 1]), $"EXIF orientation {i} pixel mapping");
            }
            var list = ImageFiles.List(fixtures);
            Check(list.Select(Path.GetFileName).Take(3).SequenceEqual(new[] { "사진1.png", "사진2.jpg", "사진10.bmp" }), "Natural filename sorting, Korean paths");
            Check(ImageFiles.IsImage("UPPER.JPG") && !ImageFiles.IsImage("file.txt"), "Case-insensitive extension filter");
            Check(ImageFiles.Step(0, -1, 3) == 2 && ImageFiles.Step(2, 1, 3) == 0 && ImageFiles.Step(0, 1, 0) == -1, "Navigation wraps and empty list");
            Check(ImageFiles.Fit(2000, 1000, 1002, 1002) == 0.5 && ImageFiles.Fit(10, 10, 100, 100) == 9.8, "Exact fit preserves aspect ratio and enlarges small images");
            var hashes = list.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            var cache = new ImageCache();
            var cached = cache.Load(list[0]);
            Check(ReferenceEquals(cached, cache.Load(list[0])), "Recent image uses decoded bitmap cache");
            cache.Load(list[1]); cache.Load(list[2]); cache.Load(list[3]);
            Check(!ReferenceEquals(cached, cache.Load(list[0])), "Decoded cache evicts least recently used image after three entries");
            string changing = Path.Combine(fixtures, "cache-change.png");
            File.Copy(list[0], changing);
            var oldCached = cache.Load(changing);
            File.SetLastWriteTimeUtc(changing, File.GetLastWriteTimeUtc(changing).AddSeconds(2));
            Check(!ReferenceEquals(oldCached, cache.Load(changing)), "Changed file timestamp invalidates decoded cache");
            File.Delete(changing);
            bool missingCached = false;
            try { cache.Load(changing); } catch (FileNotFoundException) { missingCached = true; }
            Check(missingCached, "Deleted cached image reports file error instead of stale data");
            foreach (string path in list)
            {
                var image = await Task.Run(() => ImageFiles.Load(path));
                bool rotated = path.EndsWith("oriented.jpg");
                Check(image.Bitmap.PixelWidth == (rotated ? 800 : 1200) && image.Bitmap.PixelHeight == (rotated ? 1200 : 800), "Decode " + Path.GetFileName(path));
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check(exclusive.Length > 0, "Original file unlocked: " + Path.GetExtension(path));
            }
            Check(ImageFiles.Load(list.Single(p => p.EndsWith(".tiff"))).FrameCount == 2, "TIFF frame count retained");
            await window.OpenPathAsync(fixtures);
            Check(((FrameworkElement)window.FindName("Sidebar")).Visibility == Visibility.Collapsed && !window.IsInfoVisible, "Minimal UI starts without sidebar or information overlay");
            window.ToggleInfo();
            Check(window.IsInfoVisible, "File information toggles on");
            window.ToggleInfo();
            Check(window.CurrentPath == list[0] && window.Displayed != null && window.Error == null, "Open directory in live window");
            await window.NavigateAsync(-1);
            Check(window.CurrentPath == list[^1], "Previous wraps in UI");
            await window.NavigateAsync(1);
            Check(window.CurrentPath == list[0], "Next wraps in UI");
            window.SetZoom(500);
            Check(window.Zoom == 16, "Maximum zoom clamp");
            window.SetZoom(0);
            Check(window.Zoom == 0.01, "Minimum zoom clamp");
            window.SetZoom(1);
            Check(window.Zoom == 1, "Actual size");
            var picture = (System.Windows.Controls.Image)window.FindName("Picture");
            Check(Math.Abs(picture.Width * VisualTreeHelper.GetDpi(picture).DpiScaleX - 1200) < 0.01, "100 percent maps to physical pixels at current Windows DPI");
            window.Rotate();
            window.UpdateLayout();
            Check(picture.LayoutTransform is RotateTransform { Angle: 90 }, "Rotation applied to displayed image");
            for (int i = 0; i < 3; i++) window.Rotate();
            window.FitImage();
            window.ToggleFullscreen();
            Check(window.IsFullScreen && window.WindowStyle == WindowStyle.None, "Enter fullscreen");
            Check(((FrameworkElement)window.FindName("TopBar")).Visibility == Visibility.Collapsed, "Fullscreen hides menu chrome");
            window.ToggleFullscreen();
            Check(!window.IsFullScreen && window.WindowStyle == WindowStyle.SingleBorderWindow, "Restore window");
            Check(((FrameworkElement)window.FindName("TopBar")).Visibility == Visibility.Visible, "Leaving fullscreen restores compact menu");
            window.ToggleSlideshow();
            Check(window.IsPlaying, "Start slideshow");
            await window.NavigateAsync(1);
            Check(!window.IsPlaying, "Manual navigation stops slideshow");
            window.ToggleSlideshow();
            window.ToggleSlideshow();
            Check(!window.IsPlaying, "Slideshow toggle stops");
            await Task.WhenAll(window.ShowIndexAsync(0), window.ShowIndexAsync(1), window.ShowIndexAsync(2));
            Check(window.CurrentPath == list[2] && window.Displayed != null && window.Error == null, "Rapid requests keep latest selection");
            string bad = Path.Combine(fixtures, "손상.png");
            File.WriteAllText(bad, "not an image");
            await window.OpenPathAsync(bad);
            Check(window.Displayed == null && window.Error != null && !window.IsPlaying, "Corrupt image shows error without stale image");
            await window.OpenPathAsync(list[0]);
            Check(window.Displayed != null && window.Error == null, "Recover after corrupt image");
            await window.OpenPathAsync(Path.Combine(fixtures, "missing.jpg"));
            Check(window.Error != null, "Missing file is handled");
            string empty = Path.Combine(fixtures, "empty");
            Directory.CreateDirectory(empty);
            await window.OpenPathAsync(empty);
            Check(window.Error != null, "Empty directory is handled");
            await window.OpenPathAsync(list[0]);
            Check(hashes.All(kv => SHA256.HashData(File.ReadAllBytes(kv.Key)).SequenceEqual(kv.Value)), "Original fixture bytes unchanged after all viewer actions");
            await FormatTests.Run(window, output, Check);
            await ComparisonTests.Run(window, output, list, bad, Check);
            var mainMenu = (System.Windows.Controls.Menu)window.FindName("MainMenu");
            var fileMenu = (System.Windows.Controls.MenuItem)mainMenu.Items[0];
            Check(window.OpenMenu(System.Windows.Input.Key.F), "Restyled menu retains F shortcut mapping without visible suffix");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(fileMenu.IsSubmenuOpen, "Shortcut command opens the restyled file menu");
            var popup = (System.Windows.Controls.Primitives.Popup)fileMenu.Template.FindName("PART_Popup", fileMenu);
            var popupContent = (FrameworkElement)popup.Child;
            popupContent.UpdateLayout();
            Check(popup.IsOpen && popupContent.ActualWidth >= 220 && popupContent.ActualHeight > 180, "Restyled popup renders all file commands");
            var menuPreview = new RenderTargetBitmap((int)Math.Ceiling(popupContent.ActualWidth), (int)Math.Ceiling(popupContent.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            menuPreview.Render(popupContent);
            Save(menuPreview, Path.Combine(output, "preview-menu.png"), new PngBitmapEncoder());
            fileMenu.IsSubmenuOpen = false;
            System.Windows.Input.Keyboard.ClearFocus();
            await window.OpenPathAsync(list[0]);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var preview = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(content);
            Save(preview, Path.Combine(output, "preview.png"), new PngBitmapEncoder());
            window.ToggleInfo();
            window.UpdateLayout();
            preview = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(content);
            Save(preview, Path.Combine(output, "preview-info.png"), new PngBitmapEncoder());
            window.ToggleInfo();
            window.Width = window.MinWidth;
            window.Height = 480;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            preview = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(content);
            Save(preview, Path.Combine(output, "preview-compact.png"), new PngBitmapEncoder());
            File.WriteAllText(Path.Combine(output, "tests.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, fixtures }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "tests.json"), JsonSerializer.Serialize(new { passed = false, count = checks.Count, checks, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static void Save(BitmapSource image, string path, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource MakeSample()
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var gradient = new LinearGradientBrush(Color.FromRgb(36, 67, 99), Color.FromRgb(223, 178, 141), 90);
            context.DrawRectangle(gradient, null, new Rect(0, 0, 1200, 800));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(248, 215, 167)), null, new Point(865, 260), 88, 88);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(67, 93, 110)), null, Geometry.Parse("M0,570 L210,310 L360,490 L560,240 L860,600 L1030,390 L1200,530 L1200,800 L0,800 Z"));
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(31, 62, 76)), null, Geometry.Parse("M0,690 L240,510 L460,670 L740,420 L1080,660 L1200,590 L1200,800 L0,800 Z"));
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(17, 43, 56)), null, Geometry.Parse("M0,730 L300,650 L520,745 L900,620 L1200,720 L1200,800 L0,800 Z"));
        }
        var image = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        image.Freeze();
        return image;
    }
}
