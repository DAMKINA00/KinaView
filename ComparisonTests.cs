using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KinaView;

internal static class ComparisonTests
{
    internal static async Task Run(MainWindow window, string output, string[] files, string bad, Action<bool, string> check)
    {
        async Task Layout() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        void Capture(string name)
        {
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
        }
        static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < .002 && Math.Abs(a.Y - b.Y) < .002;
        check(ImageFiles.RoundZoom(1.749) == 1.7 && ImageFiles.RoundZoom(double.NaN) == 1, "Zoom uses 10/50 percent steps and rejects nonfinite values");
        check(ImageFiles.StepZoom(.01, 1.15) == .02 && ImageFiles.StepZoom(.02, 1 / 1.15) == .01, "Wheel zoom progresses at small whole percentages without getting stuck");
        check(ImageFiles.Fit(1200, 800, 641, 481) == 639d / 1200, "Fit retains fractional scale without cropping");
        check(ImageFiles.RoundZoom(3.47) == 3.5, "347 percent snaps to 350 percent");
        check(ImageFiles.StepZoom(1.9, 1.15) == 2 && ImageFiles.StepZoom(2, 1.15) == 2.5 && ImageFiles.StepZoom(2, .8) == 1.9, "Wheel crosses 200 percent boundary with 10 and 50 point steps");
        check(ImageFiles.StepZoom(.09, 1.15) == .1 && ImageFiles.StepZoom(.1, .8) == .09 && ImageFiles.StepZoom(.1, 1.15) == .2, "Fine zoom below 10 percent crosses boundary without skipping");
        check(ImageFiles.StepZoom(16, 1.15) == 16 && ImageFiles.StepZoom(.01, .8) == .01, "Stepped zoom preserves minimum and maximum");
        check(ImageFiles.Fit(50000, 2000, 100, 100) < .01, "Fit below one percent remains small enough to fit");
        check(ImageFiles.StepZoom(.8, 1.15, .87325) == .87325 && ImageFiles.StepZoom(.9, .8, .87325) == .87325, "Zoom stops at fractional fit from either direction");
        check(ImageFiles.StepZoom(.87325, 1.15, .87325) == .9 && ImageFiles.StepZoom(.87325, .8, .87325) == .8, "Next input leaves fit stop for the regular zoom grid");
        check(ImageFiles.StepZoom(.7, 1.15, .87325) == .8 && ImageFiles.StepZoom(1, .8, .87325) == .9, "Fit stop does not interrupt steps outside the crossing interval");
        check(ImageFiles.StepZoom(1, 1.15, 1) == 1.1 && ImageFiles.StepZoom(1, .8, 1) == .9, "Fit on a regular zoom step does not trap navigation");
        check(ImageFiles.StepZoom(.01, .8, .003) == .003 && ImageFiles.StepZoom(.003, .8, .003) == .003 && ImageFiles.StepZoom(.003, 1.15, .003) == .01, "Sub one percent fit stop respects zoom direction and minimum");
        check(ImageFiles.Fit(1, 1, 1000, 1000) == 16, "Exact fit retains maximum zoom safety limit");
        await window.OpenPathAsync(files[0]); await Layout();
        double exactFit = window.FitZoom();
        check(Math.Abs(window.Zoom - exactFit) < 1e-9 && Math.Abs(exactFit * 10 - Math.Round(exactFit * 10)) > .001, "Opening a file starts at exact fractional viewport fit");
        window.SetZoom(ImageFiles.StepZoom(exactFit, .8), exact: true); window.StepZoom(1.25); await Layout();
        check(Math.Abs(window.Zoom - exactFit) < 1e-9, "Live normal zoom in pauses at exact fit without snapping it away");
        window.StepZoom(1.25); await Layout();
        check(window.Zoom > exactFit, "Live normal viewer continues beyond fit on next zoom in");
        window.StepZoom(.8); await Layout();
        check(Math.Abs(window.Zoom - exactFit) < 1e-9, "Live normal zoom out returns to exact fit");
        window.StepZoom(.8); await Layout();
        check(window.Zoom < exactFit, "Live normal viewer continues below fit on next zoom out");
        window.SetZoom(1.749);
        check(window.Zoom == 1.7, "Live normal viewer applies actual stepped zoom");
        var overview = (OverviewMap)window.FindName("Overview");
        check(overview.Visibility == Visibility.Visible && overview.VisibleRegion.Width < 1, "Normal viewer shows minimap when enlarged beyond viewport");
        window.Rotate(); await Layout();
        check(overview.VisibleRegion.Width > 0 && overview.VisibleRegion.Height > 0, "Minimap handles rotated image dimensions");
        window.ToggleInfo(); await Layout(); Capture("preview-minimap.png"); window.ToggleInfo();
        window.FitImage(); await Layout();
        check(overview.Visibility == Visibility.Collapsed, "Minimap hides when whole image fits");
        check(((SolidColorBrush)((Border)window.FindName("InfoOverlay")).Background).Color.A == 112, "Info background opacity reduced to 44 percent");

        await window.OpenPathAsync(files[0]);
        await window.StartComparison(); await Layout();
        check(window.ComparisonPanes.Count == 2 && window.ComparisonPanes[0].FilePath == files[0], "Comparison starts with A and B and preserves current image in A");
        var a = window.ComparisonPanes[0]; var b = window.ComparisonPanes[1];
        await b.OpenAsync(files[^1]); await Layout();
        check(Math.Abs(a.Zoom - a.FitZoom()) < 1e-9 && Math.Abs(b.Zoom - b.FitZoom()) < 1e-9, "Each comparison file initially opens at its own exact fit");
        double commonFit = Math.Min(a.FitZoom(), b.FitZoom());
        window.ZoomComparison(a, ImageFiles.StepZoom(commonFit, .8), exact: true);
        window.StepComparisonZoom(a, 1.15); await Layout();
        check(Math.Abs(a.Zoom - commonFit) < 1e-9 && a.Zoom == b.Zoom, "Selected panes stop together at exact common fit");
        window.StepComparisonZoom(a, 1.15); await Layout();
        check(a.Zoom > commonFit && a.Zoom == b.Zoom, "Selected panes leave fit together on next zoom step");
        window.StepComparisonZoom(a, .8); await Layout();
        check(Math.Abs(a.Zoom - commonFit) < 1e-9 && a.Zoom == b.Zoom, "Selected panes stop at common fit while zooming out");
        b.Selected = false; double heldZoom = b.Zoom;
        window.ZoomComparison(a, ImageFiles.StepZoom(a.FitZoom(), 1.15), exact: true);
        window.StepComparisonZoom(a, .8); await Layout();
        check(Math.Abs(a.Zoom - a.FitZoom()) < 1e-9 && b.Zoom == heldZoom, "Unchecked pane stays unchanged while source stops at its own fit");
        b.Selected = true;
        check(a.Displayed?.PixelWidth == 1200 && b.Displayed?.PixelWidth == 800, "Comparison opens independent images with different dimensions");
        window.ZoomComparison(a, 2.349); await Layout();
        check(a.Zoom == 2.5 && b.Zoom == 2.5, "Selected comparison panes share stepped zoom");
        window.PanComparison(a, new(.6, .55)); await Layout();
        check(Near(a.Center, b.Center) && Near(a.Center, new(.6, .55)), "Selected panes pan to matching normalized image position");
        var map = (OverviewMap)a.FindName("Overview");
        check(map.Visibility == Visibility.Visible && map.VisibleRegion.Right <= 1 && map.VisibleRegion.Bottom <= 1, "Comparison minimap viewport rectangle stays within image");
        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(map)!, 0, System.Windows.Input.Key.Right)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
        map.RaiseEvent(key); await Layout();
        check(key.Handled && Near(a.Center, b.Center) && a.Center.X > .61, "Minimap keyboard navigation moves the selected group through routed events");
        b.Selected = false; double oldZoom = b.Zoom; Point oldCenter = b.Center;
        window.ZoomComparison(a, 3); window.PanComparison(a, new(.65, .6)); await Layout();
        check(b.Zoom == oldZoom && Near(b.Center, oldCenter), "Unchecked panel is unaffected by group zoom and pan");
        window.ZoomComparison(b, 4); await Layout();
        check(a.Zoom == 3 && b.Zoom == 4, "Unchecked source can still zoom independently");
        b.Selected = true;
        window.ZoomComparison(a, 3); window.PanComparison(a, new(.6, .55)); await Layout(); Capture("preview-compare-ab.png");
        window.PanComparison(a, new(-2, 5)); await Layout();
        check(a.Center.X >= 0 && a.Center.Y <= 1 && b.Center.X >= 0 && b.Center.Y <= 1, "Panning clamps independently at each image boundary");
        window.FitImage(); await Layout();
        check(a.Zoom == b.Zoom && ((OverviewMap)a.FindName("Overview")).Visibility == Visibility.Collapsed && ((OverviewMap)b.FindName("Overview")).Visibility == Visibility.Collapsed, "Group fit uses a common scale fitting all selected images");

        window.AddComparisonPane(); window.AddComparisonPane(); window.AddComparisonPane(); await Layout();
        check(window.ComparisonPanes.Count == 4 && Grid.GetRow(window.ComparisonPanes[2]) == 1, "Comparison expands to A B C D in a two by two grid and caps at four");
        await window.OpenComparisonFiles([files[0], files[^1], files[1], files[2]]); await Layout();
        check(window.ComparisonPanes.All(p => p.Displayed != null && p.Error == ""), "Multiple files populate all four independent panels");
        window.ZoomComparison(a, 2); window.PanComparison(a, new(.6, .6)); await Layout();
        check(window.ComparisonPanes.All(p => p.Zoom == 2 && Near(p.Center, a.Center)), "All four selected panes share zoom and pan");
        Capture("preview-compare-abcd.png");
        window.Width = window.MinWidth; window.Height = 480; await Layout();
        check(window.ComparisonPanes.All(p => p.ActualWidth >= 280 && ((FrameworkElement)p.FindName("Filename")).ActualWidth > 20), "Four pane controls fit at minimum window size");
        Capture("preview-compare-compact.png");
        window.Width = 1160; window.Height = 820; await Layout();
        await Task.WhenAll(a.OpenAsync(files[1]), a.OpenAsync(files[0]));
        check(a.FilePath == files[0] && a.Displayed != null, "Rapid comparison file requests retain newest image");
        await b.OpenAsync(bad);
        check(b.Displayed == null && b.Error.Length > 0 && a.Displayed != null, "Corrupt comparison file clears only its own image");
        await b.OpenAsync(files[0]);
        check(b.Error == "" && b.Displayed != null, "Comparison panel recovers from decode failure");
        string gif = Directory.GetFiles(output, "*.gif", SearchOption.AllDirectories).First(p => ImageFiles.Load(p).Animation is { Length: > 1 });
        string webp = Directory.GetFiles(output, "*.webp", SearchOption.AllDirectories).First(p => ImageFiles.Load(p).Animation is { Length: > 1 });
        var d = window.ComparisonPanes[^1];
        await d.OpenAsync(webp); check(d.IsAnimating, "Comparison WebP plays continuously");
        await d.OpenAsync(gif); check(d.IsAnimating, "Comparison GIF plays continuously");
        var pending = d.OpenAsync(files[0]); window.RemoveComparisonPane(); await pending;
        check(d.IsDisposed && !d.IsAnimating && d.Displayed == null && window.ComparisonPanes.Count == 3, "Removing a loading pane cancels stale results and animation");
        window.RemoveComparisonPane(); window.RemoveComparisonPane();
        check(window.ComparisonPanes.Count == 2, "Comparison retains a minimum of two panels");
        await a.OpenAsync(gif);
        window.EndComparison(); await Layout();
        check(a.IsDisposed && !a.IsAnimating && window.ComparisonPanes.Count == 0 && window.Displayed != null, "Leaving comparison releases panels and restores normal viewer");
    }
}
