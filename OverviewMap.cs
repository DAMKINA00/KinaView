using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KinaView;

public sealed class OverviewMap : FrameworkElement
{
    private BitmapSource? source;
    private ImageSource? thumbnail;
    private int angle;
    private Rect imageRect;
    internal Rect VisibleRegion { get; private set; }
    internal event Action<Point>? MoveRequested;

    public OverviewMap()
    {
        Width = 156; Height = 112; Margin = new Thickness(12);
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Bottom;
        Visibility = Visibility.Collapsed; Focusable = true;
        Cursor = Cursors.Cross;
        ToolTip = "미니맵 · 클릭하거나 끌어서 이동 · 방향키로 미세 이동";
        System.Windows.Automation.AutomationProperties.SetName(this, "이미지 미니맵");
    }

    internal static Point Center(ScrollViewer viewport, Size size) => new(
        size.Width <= viewport.ViewportWidth ? .5 : (viewport.HorizontalOffset + viewport.ViewportWidth / 2) / size.Width,
        size.Height <= viewport.ViewportHeight ? .5 : (viewport.VerticalOffset + viewport.ViewportHeight / 2) / size.Height);

    internal static void MoveTo(ScrollViewer viewport, Size size, Point center)
    {
        viewport.ScrollToHorizontalOffset(Math.Clamp(center.X * size.Width - viewport.ViewportWidth / 2, 0, Math.Max(0, size.Width - viewport.ViewportWidth)));
        viewport.ScrollToVerticalOffset(Math.Clamp(center.Y * size.Height - viewport.ViewportHeight / 2, 0, Math.Max(0, size.Height - viewport.ViewportHeight)));
    }

    internal void Refresh(BitmapSource? bitmap, int rotation, ScrollViewer viewport, Size size)
    {
        Visibility = bitmap != null && (size.Width > viewport.ViewportWidth + 1 || size.Height > viewport.ViewportHeight + 1)
            && viewport.ViewportWidth > 0 && viewport.ViewportHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (bitmap == null) { source = null; thumbnail = null; return; }
        Width = Math.Min(156, Math.Max(48, viewport.ViewportWidth * .3));
        Height = Math.Min(112, Math.Max(36, viewport.ViewportHeight * .3));
        if (source != bitmap || angle != rotation)
        {
            source = bitmap; angle = rotation;
            thumbnail = rotation == 0 ? bitmap : new TransformedBitmap(bitmap, new RotateTransform(rotation));
        }
        if (size.Width <= 0 || size.Height <= 0) return;
        double w = Math.Min(1, viewport.ViewportWidth / size.Width), h = Math.Min(1, viewport.ViewportHeight / size.Height);
        var center = Center(viewport, size);
        VisibleRegion = new Rect(Math.Clamp(center.X - w / 2, 0, 1 - w), Math.Clamp(center.Y - h / 2, 0, 1 - h), w, h);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (thumbnail == null) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, new Rect(RenderSize), 4, 4);
        double scale = Math.Min((ActualWidth - 12) / thumbnail.Width, (ActualHeight - 12) / thumbnail.Height);
        imageRect = new Rect((ActualWidth - thumbnail.Width * scale) / 2, (ActualHeight - thumbnail.Height * scale) / 2, thumbnail.Width * scale, thumbnail.Height * scale);
        dc.DrawImage(thumbnail, imageRect);
        var visible = new Rect(imageRect.X + VisibleRegion.X * imageRect.Width, imageRect.Y + VisibleRegion.Y * imageRect.Height,
            VisibleRegion.Width * imageRect.Width, VisibleRegion.Height * imageRect.Height);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(38, 128, 255, 212)), new Pen(Brushes.Aquamarine, IsKeyboardFocused ? 3 : 1.5), visible);
    }

    private void Seek(Point point)
    {
        if (imageRect.Width <= 0 || imageRect.Height <= 0) return;
        MoveRequested?.Invoke(new Point(Math.Clamp((point.X - imageRect.X) / imageRect.Width, 0, 1), Math.Clamp((point.Y - imageRect.Y) / imageRect.Height, 0, 1)));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Seek(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Seek(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var center = new Point(VisibleRegion.X + VisibleRegion.Width / 2, VisibleRegion.Y + VisibleRegion.Height / 2);
        switch (e.Key)
        {
            case Key.Left: center.X -= .02; break;
            case Key.Right: center.X += .02; break;
            case Key.Up: center.Y -= .02; break;
            case Key.Down: center.Y += .02; break;
            default: base.OnKeyDown(e); return;
        }
        MoveRequested?.Invoke(center); e.Handled = true;
    }
}
