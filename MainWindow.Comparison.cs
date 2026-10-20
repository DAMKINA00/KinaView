using System.Windows;
using System.Windows.Controls;

namespace KinaView;

public partial class MainWindow
{
    private readonly List<ComparePane> panes = [];
    private ComparePane? activePane;
    private bool resumeNormalAnimation;
    internal IReadOnlyList<ComparePane> ComparisonPanes => panes;
    private bool Comparing => panes.Count != 0;
    private IEnumerable<ComparePane> Targets(ComparePane source) => source.Selected ? panes.Where(p => p.Selected) : [source];

    internal async Task StartComparison()
    {
        if (Comparing) return;
        string? path = CurrentPath;
        ++request; StopSlideshow(); resumeNormalAnimation = animationTimer.IsEnabled; animationTimer.Stop(); displayResizer.Clear();
        NormalView.Visibility = Visibility.Collapsed;
        SetSidebar(false); SidebarMenu.IsEnabled = false;
        ComparisonHost.Visibility = Visibility.Visible;
        AddComparisonPane(); AddComparisonPane();
        activePane = panes[0];
        UpdateLayout();
        if (path != null) await panes[0].OpenAsync(path);
        UpdateComparisonTitle();
    }

    internal void AddComparisonPane()
    {
        if (panes.Count >= 4) return;
        var pane = new ComparePane((char)('A' + panes.Count), decodeGate);
        pane.Activated += p => { activePane = p; UpdateComparisonTitle(); };
        pane.ZoomStepRequested += StepComparisonZoom;
        pane.PanRequested += PanComparison;
        pane.FitRequested += FitComparison;
        pane.ViewChanged += UpdateComparisonTitle;
        pane.ShowInfo(InfoMenu.IsChecked);
        pane.SetComicMode(comicMode);
        panes.Add(pane); ComparisonHost.Children.Add(pane);
        ArrangeComparison();
    }

    private void ArrangeComparison()
    {
        ComparisonHost.ColumnDefinitions.Clear(); ComparisonHost.RowDefinitions.Clear();
        int columns = panes.Count == 4 ? 2 : panes.Count;
        for (int i = 0; i < columns; i++) ComparisonHost.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (panes.Count == 4 ? 2 : 1); i++) ComparisonHost.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < panes.Count; i++) { Grid.SetColumn(panes[i], i % columns); Grid.SetRow(panes[i], i / columns); }
        AddCompareMenu.IsEnabled = panes.Count < 4;
        RemoveCompareMenu.IsEnabled = panes.Count > 2;
        EndCompareMenu.IsEnabled = SelectAllCompareMenu.IsEnabled = true;
        PreviousButton.IsEnabled = NextButton.IsEnabled = SlideshowButton.IsEnabled = false;
        AnimationButton.IsEnabled = true;
        UpdateComparisonTitle();
    }

    internal void RemoveComparisonPane()
    {
        if (panes.Count <= 2) return;
        var last = panes[^1];
        last.Dispose(); ComparisonHost.Children.Remove(last); panes.Remove(last);
        if (activePane == last) activePane = panes[0];
        ArrangeComparison();
    }

    internal void EndComparison()
    {
        foreach (var pane in panes) pane.Dispose();
        panes.Clear(); ComparisonHost.Children.Clear(); activePane = null;
        ComparisonHost.Visibility = Visibility.Collapsed; NormalView.Visibility = Visibility.Visible;
        SidebarMenu.IsEnabled = true; SetSidebar(!fullScreen && SidebarMenu.IsChecked);
        AddCompareMenu.IsEnabled = RemoveCompareMenu.IsEnabled = EndCompareMenu.IsEnabled = SelectAllCompareMenu.IsEnabled = false;
        AnimationButton.IsEnabled = loaded?.Animation is { Length: > 1 };
        if (AnimationButton.IsEnabled && resumeNormalAnimation) animationTimer.Start();
        InfoOverlay.Visibility = InfoMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigation(); UpdateTitle(); ApplySize();
    }

    internal async Task OpenComparisonFiles(string[] paths)
    {
        if (!Comparing) await StartComparison();
        if (!Comparing) return;
        while (panes.Count < Math.Min(4, paths.Length)) AddComparisonPane();
        UpdateLayout();
        await Task.WhenAll(paths.Take(4).Select((path, i) => panes[i].OpenAsync(path)));
        UpdateComparisonTitle();
    }

    internal void ZoomComparison(ComparePane source, double value, bool exact = false)
    {
        var center = source.Center;
        foreach (var target in Targets(source)) target.SetZoom(value, center, fitValue: exact);
        UpdateComparisonTitle();
    }
    internal void StepComparisonZoom(ComparePane source, double factor)
    {
        if (source.Displayed == null) return;
        double fitZoom = Targets(source).Where(p => p.Displayed != null).Min(p => p.FitZoom());
        ZoomComparison(source, ImageFiles.StepZoom(source.Zoom, factor, fitZoom), exact: true);
    }
    internal void PanComparison(ComparePane source, Point center)
    {
        foreach (var target in Targets(source)) target.MoveTo(center);
    }
    private void FitComparison(ComparePane source)
    {
        var targets = Targets(source).Where(p => p.Displayed != null).ToArray();
        if (targets.Length == 0) return;
        double value = targets.Min(p => p.FitZoom());
        foreach (var target in targets) target.SetZoom(value, new(.5, .5), fitValue: true);
        UpdateComparisonTitle();
    }
    private void UpdateComparisonTitle()
    {
        if (!Comparing) return;
        Title = $"{string.Join(" | ", panes.Select((p, i) => ((char)('A' + i)).ToString()))} 비교 · 키나뷰";
        CountLabel.Text = $"{panes.Count}장 비교";
        ZoomLabel.Text = activePane == null ? "" : $"{(char)('A' + panes.IndexOf(activePane))} · {activePane.Zoom * 100:0.##}%";
    }
    private async void StartCompareClick(object sender, RoutedEventArgs e) => await StartComparison();
    private void AddCompareClick(object sender, RoutedEventArgs e) => AddComparisonPane();
    private void RemoveCompareClick(object sender, RoutedEventArgs e) => RemoveComparisonPane();
    private void EndCompareClick(object sender, RoutedEventArgs e) => EndComparison();
    private void SelectAllCompareClick(object sender, RoutedEventArgs e) { foreach (var pane in panes) pane.Selected = true; }

    private Size DisplaySize => Displayed == null ? new(0, 0) : rotation % 180 == 0 ? new(Picture.Width, Picture.Height) : new(Picture.Height, Picture.Width);
    private void RefreshOverview() => Overview?.Refresh(Displayed, rotation, Viewport, DisplaySize);
    private void ViewportScrolled(object sender, ScrollChangedEventArgs e) => RefreshOverview();
}
