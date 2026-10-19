using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KinaView;

public partial class ComparePane : UserControl, IDisposable
{
    private readonly SemaphoreSlim decodeGate;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render);
    private LoadedImage? loaded;
    private int request, frame, rotation;
    private bool disposed, fit = true;
    private Point? drag;
    private Point dragOffset;
    internal string? FilePath { get; private set; }
    internal BitmapSource? Displayed => Picture.Source as BitmapSource;
    internal double Zoom { get; private set; } = 1;
    internal bool Selected { get => Selection.IsChecked == true; set => Selection.IsChecked = value; }
    internal bool IsAnimating => timer.IsEnabled;
    internal bool IsDisposed => disposed;
    internal string Error => Message.Text;
    internal Size DisplaySize => rotation % 180 == 0 ? new(Picture.Width, Picture.Height) : new(Picture.Height, Picture.Width);
    internal Point Center => Displayed == null ? new(.5, .5) : OverviewMap.Center(Viewport, DisplaySize);
    internal event Action<ComparePane>? Activated;
    internal event Action<ComparePane, double>? ZoomStepRequested;
    internal event Action<ComparePane, Point>? PanRequested;
    internal event Action<ComparePane>? FitRequested;
    internal event Action? ViewChanged;

    internal ComparePane(char label, SemaphoreSlim gate)
    {
        decodeGate = gate;
        InitializeComponent();
        Selection.Content = label.ToString();
        System.Windows.Automation.AutomationProperties.SetName(Selection, $"{label} 패널 함께 조작 선택");
        timer.Tick += (_, _) =>
        {
            if (loaded?.Animation is not { Length: > 1 } frames || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
            frame = (frame + 1) % frames.Length;
            Picture.Source = frames[frame].Bitmap;
            timer.Interval = TimeSpan.FromMilliseconds(frames[frame].DelayMilliseconds);
            RefreshMap();
        };
        Overview.MoveRequested += center => PanRequested?.Invoke(this, center);
    }

    internal async Task OpenAsync(string path)
    {
        if (disposed) return;
        int ticket = ++request;
        timer.Stop(); loaded = null; Picture.Source = null; RefreshMap();
        Message.Text = "불러오는 중…";
        FilePath = path; Filename.Text = Path.GetFileName(path); Filename.ToolTip = path;
        Details.Text = "";
        await decodeGate.WaitAsync();
        try
        {
            if (disposed || ticket != request) return;
            if (!ImageFiles.IsImage(path)) throw new InvalidDataException("지원하는 이미지 파일을 선택해 주세요.");
            // Each pane retains only its current image; the shared gate bounds native decoding memory.
            var result = await Task.Run(() => ImageFiles.Load(path));
            if (disposed || ticket != request) return;
            loaded = result; Picture.Source = result.Bitmap; frame = rotation = 0; fit = true;
            Message.Text = "";
            Details.Text = $"{Path.GetFileName(path)}\n{result.Bitmap.PixelWidth:N0} × {result.Bitmap.PixelHeight:N0} · {result.FileBytes / 1048576d:0.0} MB" + (result.Note == null ? "" : "\n" + result.Note);
            ApplySize(); MoveTo(new(.5, .5));
            if (result.Animation is { Length: > 1 } frames)
            {
                timer.Interval = TimeSpan.FromMilliseconds(frames[0].DelayMilliseconds); timer.Start();
            }
        }
        catch (Exception ex) when (MainWindow.IsFileError(ex))
        {
            if (!disposed && ticket == request) Message.Text = "열 수 없습니다: " + ex.Message;
        }
        finally { decodeGate.Release(); }
    }

    internal double FitZoom()
    {
        if (Displayed is not { } bitmap) return 1;
        var dpi = VisualTreeHelper.GetDpi(Picture);
        return ImageFiles.Fit(rotation % 180 == 0 ? bitmap.PixelWidth : bitmap.PixelHeight,
            rotation % 180 == 0 ? bitmap.PixelHeight : bitmap.PixelWidth, Viewport.ActualWidth * dpi.DpiScaleX, Viewport.ActualHeight * dpi.DpiScaleY);
    }
    private void ApplySize()
    {
        if (Displayed is not { } bitmap) return;
        if (fit) Zoom = FitZoom();
        var dpi = VisualTreeHelper.GetDpi(Picture);
        Picture.Width = bitmap.PixelWidth * Zoom / dpi.DpiScaleX; Picture.Height = bitmap.PixelHeight * Zoom / dpi.DpiScaleY;
        Picture.LayoutTransform = new RotateTransform(rotation);
        Stage.MinWidth = Math.Max(0, Viewport.ActualWidth); Stage.MinHeight = Math.Max(0, Viewport.ActualHeight);
        Scale.Content = $"{Zoom * 100:0.##}%";
        Viewport.UpdateLayout(); RefreshMap();
        ViewChanged?.Invoke();
    }
    internal void SetZoom(double value, Point center, bool fitValue = false)
    {
        if (Displayed == null) return;
        fit = false; Zoom = fitValue ? value : ImageFiles.RoundZoom(value);
        ApplySize(); MoveTo(center);
    }
    internal void MoveTo(Point center)
    {
        if (Displayed == null) return;
        OverviewMap.MoveTo(Viewport, DisplaySize, center); Viewport.UpdateLayout(); RefreshMap();
    }
    internal void Rotate()
    {
        if (Displayed == null) return;
        rotation = (rotation + 90) % 360; ApplySize(); MoveTo(new(.5, .5));
    }
    internal void ToggleAnimation() { if (timer.IsEnabled) timer.Stop(); else if (loaded?.Animation is { Length: > 1 }) timer.Start(); }
    internal void ShowInfo(bool show) => Info.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    private void RefreshMap() => Overview.Refresh(Displayed, rotation, Viewport, Displayed == null ? new Size(0, 0) : DisplaySize);
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => ApplySize();
    private void Scrolled(object sender, ScrollChangedEventArgs e) => RefreshMap();
    private void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (Frame != null) Frame.BorderBrush = new SolidColorBrush(Selected ? Color.FromRgb(73, 100, 91) : Color.FromRgb(53, 60, 69));
    }
    private void Activate(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this);
    private void FocusPane(object sender, KeyboardFocusChangedEventArgs e) => Activated?.Invoke(this);
    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        Activated?.Invoke(this); ZoomStepRequested?.Invoke(this, e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true;
    }
    private void FitClick(object sender, RoutedEventArgs e) { Activated?.Invoke(this); FitRequested?.Invoke(this); }
    internal async Task ChooseFileAsync()
    {
        Activated?.Invoke(this);
        var dialog = new OpenFileDialog { Title = "비교 이미지 열기", Filter = "이미지|" + string.Join(';', ImageFiles.Extensions.Select(x => "*" + x)) + "|모든 파일|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await OpenAsync(dialog.FileName);
    }
    private async void OpenClick(object sender, RoutedEventArgs e) => await ChooseFileAsync();
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; Activated?.Invoke(this);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            if (paths.Length > 1 && Window.GetWindow(this) is MainWindow window) await window.OpenComparisonFiles(paths);
            else await OpenAsync(paths[0]);
        }
    }
    private void PanStart(object sender, MouseButtonEventArgs e)
    {
        if (Displayed == null) return;
        if (e.ClickCount == 2) { (Window.GetWindow(this) as MainWindow)?.ToggleFullscreen(); e.Handled = true; return; }
        drag = e.GetPosition(Viewport); dragOffset = new(Viewport.HorizontalOffset, Viewport.VerticalOffset);
        Viewport.CaptureMouse(); Viewport.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void PanMove(object sender, MouseEventArgs e)
    {
        if (drag is not { } start) return;
        var at = e.GetPosition(Viewport);
        Viewport.ScrollToHorizontalOffset(dragOffset.X + start.X - at.X);
        Viewport.ScrollToVerticalOffset(dragOffset.Y + start.Y - at.Y);
        Viewport.UpdateLayout(); PanRequested?.Invoke(this, Center);
    }
    private void PanEnd(object sender, MouseButtonEventArgs e) => Viewport.ReleaseMouseCapture();
    private void PanLost(object sender, MouseEventArgs e) { drag = null; Viewport.Cursor = Cursors.Arrow; }
    public void Dispose() { disposed = true; request++; timer.Stop(); loaded = null; Picture.Source = null; RefreshMap(); }
}
