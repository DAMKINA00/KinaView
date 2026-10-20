using System.IO;
using System.Diagnostics;
using ImageMagick;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KinaView;

public partial class MainWindow : Window
{
    private string[] files = [];
    private int index = -1;
    private int request;
    private bool syncing;
    private bool fit = true;
    private bool fullScreen;
    private bool playing;
    private WindowState previousState;
    private double zoom = 1;
    private int rotation;
    private LoadedImage? loaded;
    private readonly DisplayResizer displayResizer = new();
    private bool comicMode;
    private readonly SemaphoreSlim decodeGate = new(1, 1);
    private readonly ImageCache imageCache = new();
    private readonly DispatcherTimer slideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer animationTimer = new(DispatcherPriority.Render);
    private int animationIndex;
    private Point? panOrigin;
    private Point panOffset;
    internal string? CurrentPath => index >= 0 && index < files.Length ? files[index] : null;
    internal BitmapSource? Displayed => Picture.Source as BitmapSource;
    private BitmapSource? OriginalFrame => loaded?.Animation is { } frames && animationIndex < frames.Length ? frames[animationIndex].Bitmap : loaded?.Bitmap;
    internal bool ComicViewEnabled => comicMode;
    internal bool IsFullScreen => fullScreen;
    internal bool IsPlaying => playing;
    internal bool IsAnimating => animationTimer.IsEnabled;
    internal int AnimationIndex => animationIndex;
    internal double Zoom => activePane?.Zoom ?? zoom;
    internal string? Error => Notice.Visibility == Visibility.Visible ? NoticeText.Text : null;
    internal bool IsInfoVisible => InfoOverlay.Visibility == Visibility.Visible;

    public MainWindow()
    {
        InitializeComponent();
        Overview.MoveRequested += center => { OverviewMap.MoveTo(Viewport, DisplaySize, center); Viewport.UpdateLayout(); RefreshOverview(); };
        slideTimer.Tick += async (_, _) =>
        {
            slideTimer.Stop();
            await ShowIndexAsync(ImageFiles.Step(index, 1, files.Length));
            if (playing) slideTimer.Start();
        };
        animationTimer.Tick += (_, _) => AdvanceAnimation();
        Closed += (_, _) => { request++; StopSlideshow(); animationTimer.Stop(); displayResizer.Dispose(); loaded = null; foreach (var pane in panes) pane.Dispose(); };
        UpdateNavigation();
    }

    internal async Task OpenPathAsync(string path)
    {
        if (Comparing && activePane != null && !Directory.Exists(path)) { await activePane.OpenAsync(path); UpdateComparisonTitle(); return; }
        if (Comparing) EndComparison();
        StopSlideshow();
        int ticket = ++request;
        ShowNotice("폴더를 읽는 중…");
        try
        {
            path = Path.GetFullPath(path);
            bool directory = Directory.Exists(path);
            if (!directory && !File.Exists(path)) throw new FileNotFoundException("파일을 찾을 수 없습니다.");
            if (!directory && !ImageFiles.IsImage(path)) throw new InvalidDataException("지원하는 이미지 파일을 선택해 주세요.");
            string folder = directory ? path : Path.GetDirectoryName(path)!;
            if (!directory)
            {
                files = [path];
                index = 0;
                UpdateFileList(folder);
                int imageTicket = request + 1;
                await ShowIndexAsync(0);
                if (request != imageTicket) return;
                ticket = imageTicket;
            }
            var found = await Task.Run(() => ImageFiles.List(folder));
            if (ticket != request) return;
            if (found.Length == 0) throw new InvalidDataException("이 폴더에는 지원하는 이미지가 없습니다.");
            files = found;
            int selected = directory ? 0 : Array.FindIndex(files, f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            index = Math.Max(0, selected);
            UpdateFileList(folder);
            if (directory) await ShowIndexAsync(index);
            else UpdateTitle();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            if (ticket == request) ShowNotice("열 수 없습니다: " + ex.Message);
        }
    }

    private void UpdateFileList(string folder)
    {
        syncing = true;
        FileList.ItemsSource = files.Select(f => new { Name = Path.GetFileName(f) }).ToArray();
        FileList.SelectedIndex = index;
        syncing = false;
        FolderLabel.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        FolderLabel.ToolTip = folder;
        FolderCount.Text = $"{files.Length:N0}개 이미지";
        UpdateNavigation();
    }

    private void UpdateTitle()
    {
        CountLabel.Text = $"{index + 1:N0} / {files.Length:N0}";
        Title = $"{Path.GetFileName(CurrentPath)} [{index + 1}/{files.Length}] · 키나뷰";
    }

    internal async Task ShowIndexAsync(int next)
    {
        if (Comparing) return;
        if (next < 0 || next >= files.Length) return;
        int ticket = ++request;
        index = next;
        syncing = true;
        FileList.SelectedIndex = next;
        FileList.ScrollIntoView(FileList.SelectedItem);
        syncing = false;
        string path = files[next];
        animationTimer.Stop();
        AnimationButton.IsEnabled = false;
        AnimationLabel.Text = "";
        ModifiedLabel.Text = "";
        displayResizer.Clear();
        ComicStatus.Visibility = Visibility.Collapsed;
        loaded = null;
        Picture.Source = null;
        RefreshOverview();
        Welcome.Visibility = Visibility.Collapsed;
        Status.Text = Path.GetFileName(path) + " · 불러오는 중…";
        ShowNotice("이미지를 불러오는 중…");
        UpdateTitle();
        ImageName.Text = Path.GetFileName(path);
        ImageName.ToolTip = path;
        UpdateNavigation();
        await decodeGate.WaitAsync();
        try
        {
            if (ticket != request) return;
            var result = await Task.Run(() => imageCache.Load(path));
            if (ticket != request) return;
            loaded = result;
            animationIndex = 0;
            rotation = 0;
            fit = true;
            Picture.Source = result.Bitmap;
            HideNotice();
            ApplySize();
            Status.Text = $"{result.Bitmap.PixelWidth:N0} × {result.Bitmap.PixelHeight:N0} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {FormatBytes(result.FileBytes)}" +
                (result.Note != null ? $"   ·   {result.Note}" : result.Animation != null ? $"   ·   GIF/WebP {result.FrameCount} 프레임" : result.FrameCount > 1 ? $"   ·   첫 페이지 (총 {result.FrameCount})" : "");
            ModifiedLabel.Text = $"수정한 날짜: {File.GetLastWriteTime(path):yyyy/MM/dd HH:mm:ss}";
            if (result.Animation is { Length: > 1 })
            {
                AnimationButton.IsEnabled = true;
                UpdateAnimationFrame();
                animationTimer.Start();
                AnimationButton.Header = "GIF/WebP 일시 정지";
            }
            Viewport.ScrollToHorizontalOffset(0);
            Viewport.ScrollToVerticalOffset(0);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            if (ticket != request) return;
            StopSlideshow();
            ShowNotice("이미지를 열지 못했습니다. 손상된 파일, 지원하지 않는 변형 또는 메모리 제한일 수 있습니다.\n" + ex.Message);
            Status.Text = Path.GetFileName(path) + " · 열기 실패";
        }
        finally { decodeGate.Release(); }
    }

    internal static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or System.Security.SecurityException or InvalidOperationException or MagickException;
    private static string FormatBytes(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
    private void ShowNotice(string message) { NoticeText.Text = message; Notice.Visibility = Visibility.Visible; }
    private void HideNotice() => Notice.Visibility = Visibility.Collapsed;
    private void UpdateNavigation() { PreviousButton.IsEnabled = files.Length > 1; NextButton.IsEnabled = files.Length > 1; SlideshowButton.IsEnabled = files.Length > 1; }

    internal async Task NavigateAsync(int delta)
    {
        StopSlideshow();
        if (Comparing || files.Length == 0) return;
        await ShowIndexAsync(ImageFiles.Step(index, delta, files.Length));
    }

    private void ApplySize()
    {
        if (Comparing || OriginalFrame is not { } original) return;
        var dpi = VisualTreeHelper.GetDpi(Picture);
        if (fit) zoom = FitZoom();
        Picture.Width = original.PixelWidth * zoom / dpi.DpiScaleX;
        Picture.Height = original.PixelHeight * zoom / dpi.DpiScaleY;
        Picture.LayoutTransform = new RotateTransform(rotation);
        ImageStage.MinWidth = Math.Max(0, Viewport.ActualWidth);
        ImageStage.MinHeight = Math.Max(0, Viewport.ActualHeight);
        ZoomLabel.Text = $"{zoom * 100:0.##}%";
        if (!comicMode && !ReferenceEquals(Picture.Source, original)) Picture.Source = original;
        Viewport.UpdateLayout();
        RefreshOverview();
        if (comicMode) RequestComicResize(original, loaded?.Animation != null);
    }

    private void RequestComicResize(BitmapSource original, bool animated)
    {
        int width = Math.Max(1, (int)Math.Round(original.PixelWidth * zoom));
        int height = Math.Max(1, (int)Math.Round(original.PixelHeight * zoom));
        ComicStatusText.Text = "만화 보기 · 보정 중…";
        ComicStatus.Visibility = Visibility.Visible;
        displayResizer.Request(original, width, height, !animated, result =>
        {
            if (!comicMode || !ReferenceEquals(OriginalFrame, original)) return;
            Picture.Source = result.Bitmap;
            ComicStatusText.Text = result.FallbackReason is { Length: > 0 } ? "만화 보기 · 보정 대체 표시" : "만화 보기 · 보정 완료";
            ComicStatus.Visibility = Visibility.Visible;
            RefreshOverview();
        });
    }

    internal double FitZoom()
    {
        if (OriginalFrame is not { } original) return 1;
        var dpi = VisualTreeHelper.GetDpi(Picture);
        return ImageFiles.Fit(rotation % 180 == 0 ? original.PixelWidth : original.PixelHeight,
            rotation % 180 == 0 ? original.PixelHeight : original.PixelWidth, Viewport.ActualWidth * dpi.DpiScaleX, Viewport.ActualHeight * dpi.DpiScaleY,
            comicMode ? double.PositiveInfinity : 16);
    }
    internal void StepZoom(double factor)
    {
        if (activePane != null) { StepComparisonZoom(activePane, factor); return; }
        if (OriginalFrame != null) SetZoom(ImageFiles.StepZoom(zoom, factor, FitZoom()), exact: true);
    }
    internal void SetZoom(double value, bool exact = false)
    {
        if (activePane != null) { ZoomComparison(activePane, value, exact); return; }
        if (OriginalFrame == null) return;
        var center = OverviewMap.Center(Viewport, DisplaySize);
        fit = false;
        zoom = exact ? value : ImageFiles.RoundZoom(value);
        ApplySize();
        OverviewMap.MoveTo(Viewport, DisplaySize, center);
        Viewport.UpdateLayout(); RefreshOverview();
    }
    internal void FitImage() { if (activePane != null) { FitComparison(activePane); return; } fit = true; ApplySize(); }
    internal void Rotate() { if (activePane != null) { foreach (var p in Targets(activePane)) p.Rotate(); return; } if (OriginalFrame == null) return; rotation = (rotation + 90) % 360; ApplySize(); }
    internal void ToggleFullscreen()
    {
        if (!fullScreen)
        {
            previousState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = previousState;
        }
        fullScreen = !fullScreen;
        TopBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        SetSidebar(!fullScreen && !Comparing && SidebarMenu.IsChecked);
    }

    internal void ToggleSlideshow()
    {
        if (Comparing) return;
        if (playing) StopSlideshow();
        else if (files.Length > 1 && loaded != null) { playing = true; slideTimer.Start(); SlideshowButton.Header = "슬라이드쇼 정지"; }
    }
    private void StopSlideshow() { playing = false; slideTimer.Stop(); SlideshowButton.Header = "슬라이드쇼"; }
    internal void AdvanceAnimation()
    {
        if (WindowState == WindowState.Minimized || loaded?.Animation is not { Length: > 1 } frames) return;
        animationIndex = (animationIndex + 1) % frames.Length;
        UpdateAnimationFrame();
    }
    private void UpdateAnimationFrame()
    {
        if (loaded?.Animation is not { } frames) return;
        Picture.Source = frames[animationIndex].Bitmap;
        animationTimer.Interval = TimeSpan.FromMilliseconds(frames[animationIndex].DelayMilliseconds);
        ApplySize();
        RefreshOverview();
        AnimationLabel.Text = $"{animationIndex + 1} / {frames.Length} 프레임 · 반복 재생";
    }
    internal void ToggleAnimation()
    {
        if (activePane != null) { foreach (var p in Targets(activePane)) p.ToggleAnimation(); return; }
        if (loaded?.Animation is not { Length: > 1 }) return;
        if (animationTimer.IsEnabled) { animationTimer.Stop(); AnimationButton.Header = "GIF/WebP 재생"; }
        else { animationTimer.Start(); AnimationButton.Header = "GIF/WebP 일시 정지"; }
    }
    private void AnimationClick(object sender, RoutedEventArgs e) => ToggleAnimation();
    private void ComicClick(object sender, RoutedEventArgs e) => SetComicMode(ComicMenu.IsChecked);
    internal void ToggleComicView() => SetComicMode(!comicMode);
    internal void SetComicMode(bool enabled)
    {
        comicMode = enabled;
        if (enabled) fit = true;
        ComicMenu.IsChecked = enabled;
        displayResizer.Clear();
        if (OriginalFrame is { } original)
        {
            Picture.Source = original;
            if (!enabled) ComicStatus.Visibility = Visibility.Collapsed;
        }
        else ComicStatus.Visibility = Visibility.Collapsed;
        foreach (var pane in panes) pane.SetComicMode(enabled);
        if (!Comparing && OriginalFrame != null)
        {
            ApplySize();
            if (enabled) OverviewMap.MoveTo(Viewport, DisplaySize, new(.5, .5));
        }
    }
    private void SidebarClick(object sender, RoutedEventArgs e)
    {
        if (Comparing) return;
        bool show = Sidebar.Visibility != Visibility.Visible;
        SidebarMenu.IsChecked = show;
        SetSidebar(show);
    }
    private void SetSidebar(bool show)
    {
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(show ? 220 : 0);
    }
    internal void ToggleInfo()
    {
        if (Comparing) { InfoMenu.IsChecked = !InfoMenu.IsChecked; foreach (var p in panes) p.ShowInfo(InfoMenu.IsChecked); return; }
        InfoOverlay.Visibility = IsInfoVisible ? Visibility.Collapsed : Visibility.Visible;
        InfoMenu.IsChecked = IsInfoVisible;
    }
    private void InfoClick(object sender, RoutedEventArgs e)
    {
        if (Comparing) { foreach (var p in panes) p.ShowInfo(InfoMenu.IsChecked); } else ToggleInfo();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if ((activePane != null ? activePane.Displayed : Displayed) is { } image) try { Clipboard.SetImage(image); }
        catch (System.Runtime.InteropServices.COMException) { ShowNotice("다른 앱이 클립보드를 사용 중입니다. 다시 시도해 주세요."); }
    }
    private void RegisterClick(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Register(Registry.CurrentUser, Environment.ProcessPath!); ShellIntegration.RefreshExplorer(); ShowNotice("탐색기 ‘키나뷰로 열기’와 연결 프로그램에 등록했습니다. 기본 앱은 변경하지 않았습니다."); }
        catch (Exception ex) when (IsFileError(ex)) { ShowNotice("등록 실패: " + ex.Message); }
    }
    private void UnregisterClick(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Unregister(Registry.CurrentUser); ShellIntegration.RefreshExplorer(); ShowNotice("키나뷰의 탐색기 등록을 해제했습니다."); }
        catch (Exception ex) when (IsFileError(ex)) { ShowNotice("등록 해제 실패: " + ex.Message); }
    }
    private void DefaultAppsClick(object sender, RoutedEventArgs e) => OpenShell("ms-settings:defaultapps");
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        if ((activePane != null ? activePane.FilePath : CurrentPath) is { } path) OpenShell("explorer.exe", $"/select,\"{path}\"");
    }
    private void OpenShell(string target, string arguments = "")
    {
        try { Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowNotice(ex.Message); }
    }
    private async void OpenFileClick(object sender, RoutedEventArgs e)
    {
        if (activePane != null) { await activePane.ChooseFileAsync(); UpdateComparisonTitle(); return; }
        var dialog = new OpenFileDialog { Title = "키나뷰 · 이미지 열기", Filter = "이미지|" + string.Join(';', ImageFiles.Extensions.Select(x => "*" + x)) + "|모든 파일|*.*" };
        if (dialog.ShowDialog(this) == true) await OpenPathAsync(dialog.FileName);
    }
    private async void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "키나뷰 · 폴더 선택" };
        if (dialog.ShowDialog(this) == true) await OpenPathAsync(dialog.FolderName);
    }
    private async void PreviousClick(object sender, RoutedEventArgs e) => await NavigateAsync(-1);
    private async void NextClick(object sender, RoutedEventArgs e) => await NavigateAsync(1);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => StepZoom(1 / 1.25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => StepZoom(1.25);
    private void FitClick(object sender, RoutedEventArgs e) => FitImage();
    private void ActualClick(object sender, RoutedEventArgs e) => SetZoom(1);
    private void RotateClick(object sender, RoutedEventArgs e) => Rotate();
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void SlideshowClick(object sender, RoutedEventArgs e) => ToggleSlideshow();
    private async void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && FileList.SelectedIndex >= 0) { StopSlideshow(); await ShowIndexAsync(FileList.SelectedIndex); }
    }
    private void ViewportChanged(object sender, SizeChangedEventArgs e) => ApplySize();
    private void OnWheel(object sender, MouseWheelEventArgs e) { StepZoom(e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; }
    private void PanStart(object sender, MouseButtonEventArgs e)
    {
        if (Displayed == null || e.OriginalSource is not Image && e.OriginalSource != ImageStage) return;
        if (e.ClickCount == 2) { ToggleFullscreen(); e.Handled = true; return; }
        panOrigin = e.GetPosition(Viewport);
        panOffset = new Point(Viewport.HorizontalOffset, Viewport.VerticalOffset);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }
    private void PanMove(object sender, MouseEventArgs e)
    {
        if (panOrigin is not { } start) return;
        Point at = e.GetPosition(Viewport);
        Viewport.ScrollToHorizontalOffset(panOffset.X + start.X - at.X);
        Viewport.ScrollToVerticalOffset(panOffset.Y + start.Y - at.Y);
    }
    private void PanEnd(object sender, MouseButtonEventArgs e) { Viewport.ReleaseMouseCapture(); }
    private void PanLost(object sender, MouseEventArgs e) { panOrigin = null; Viewport.Cursor = Cursors.Arrow; }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            if (Comparing && paths.Length > 1) await OpenComparisonFiles(paths);
            else await OpenPathAsync(paths[0]);
        }
        e.Handled = true;
    }
    internal bool OpenMenu(Key key)
    {
        if (TopBar.Visibility != Visibility.Visible) return false;
        var item = MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == key.ToString());
        if (item == null) return false;
        item.Focus(); item.IsSubmenuOpen = true;
        return true;
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && OpenMenu(e.SystemKey)) { e.Handled = true; return; }
        if (MainMenu.IsKeyboardFocusWithin) return;
        if (Keyboard.FocusedElement is OverviewMap && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab) return;
        if (Comparing && (e.Key == Key.Tab || (e.Key == Key.Space && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase))) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.O)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OpenFolderClick(this, e); else OpenFileClick(this, e);
        }
        else if (ctrl && e.Key == Key.C)
        {
            CopyClick(this, e);
        }
        else if (ctrl && e.Key == Key.R) Rotate();
        else if (ctrl && e.Key == Key.B)
            SidebarClick(this, e);
        else if (ctrl && e.Key == Key.M)
            ToggleComicView();
        else if (ctrl && e.Key == Key.Enter) RevealClick(this, e);
        else if (e.Key == Key.OemPlus && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift)) StepZoom(1.25);
        else if (Keyboard.Modifiers != ModifierKeys.None) return;
        else switch (e.Key)
        {
            case Key.Right: case Key.PageDown: await NavigateAsync(1); break;
            case Key.Left: case Key.PageUp: await NavigateAsync(-1); break;
            case Key.Home: StopSlideshow(); await ShowIndexAsync(0); break;
            case Key.End: StopSlideshow(); await ShowIndexAsync(files.Length - 1); break;
            case Key.Add: case Key.OemPlus: StepZoom(1.25); break;
            case Key.Subtract: case Key.OemMinus: StepZoom(1 / 1.25); break;
            case Key.D0: case Key.NumPad0: SetZoom(1); break;
            case Key.D1: case Key.NumPad1: FitImage(); break;
            case Key.R: Rotate(); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.F5: ToggleSlideshow(); break;
            case Key.Space: ToggleAnimation(); break;
            case Key.Tab: if (FileList.IsKeyboardFocusWithin) return; ToggleInfo(); break;
            case Key.Escape: StopSlideshow(); if (fullScreen) ToggleFullscreen(); else if (Comparing) EndComparison(); else { HideNotice(); if (IsInfoVisible) ToggleInfo(); } break;
            case Key.F1: HelpClick(this, e); break;
            default: return;
        }
        e.Handled = true;
    }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Ctrl+O  파일 열기     Ctrl+Shift+O  폴더 열기\n← / → · PageUp / PageDown  이전 / 다음 이미지\nHome / End  첫 / 마지막 이미지\n마우스 휠 · + / -  확대 / 축소     마우스로 끌기  이미지 이동\n1  화면 맞춤     0  100%     R  오른쪽 회전\nCtrl+M  만화 보기     F11 · 더블클릭  전체화면     Esc  슬라이드쇼 / 전체화면 종료\nSpace  GIF/WebP 재생 / 일시 정지     F5  3초 슬라이드쇼\n비교 메뉴  A~D 패널 · 체크한 패널끼리 확대와 이동 동기화\n미니맵  확대 시 왼쪽 아래 · 클릭/드래그로 이동\nTab  파일 정보 표시 / 숨기기\nCtrl+B  파일 목록 표시     Ctrl+Enter  파일 위치\nCtrl+C  현재 프레임 복사 (보기 회전 제외)\n\nGIF/WebP는 프레임별 지연·투명도·부분 프레임을 적용해 반복 재생합니다.\nRAW/DNG는 카메라 화이트밸런스로 현상합니다.\nPSD/PSB는 저장된 합성 이미지, HDR/EXR은 SDR 톤 매핑으로 표시합니다.\nJPG·PNG·BMP·TIFF·ICO·WebP·HEIC·AVIF도 열 수 있습니다.\n다중 페이지 TIFF/ICO와 GIF/WebP 이외 애니메이션은 첫 이미지 표시.\n최대 1억 픽셀. GIF/WebP는 128 MiB / 2048 프레임 제한.\n\n도구 메뉴에서 탐색기 및 연결 프로그램 등록·해제가 가능합니다.\nWindows 11에서는 ‘더 많은 옵션 표시’에 메뉴가 나타날 수 있습니다.\n원본과 기존 기본 앱은 변경하지 않습니다.\n\n키나뷰 0.4.3 · Magick.NET / ImageMagick / LibRaw 사용\n타사 라이선스: 설치 폴더의 ThirdPartyNotices.txt",
        "키나뷰 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
}
