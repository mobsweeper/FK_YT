using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FkYouTube;

public partial class MainWindow
{
    private const double CompactNavigationBreakpoint = 1250;
    private const double PreferredFeedCardWidth = 270;

    private static readonly DependencyPropertyKey FeedColumnCountPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(FeedColumnCount), typeof(int), typeof(MainWindow), new PropertyMetadata(4));
    public static readonly DependencyProperty FeedColumnCountProperty = FeedColumnCountPropertyKey.DependencyProperty;
    public int FeedColumnCount => (int)GetValue(FeedColumnCountProperty);

    private static readonly DependencyPropertyKey IsCompactNavigationPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsCompactNavigation), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactNavigationProperty = IsCompactNavigationPropertyKey.DependencyProperty;
    public bool IsCompactNavigation => (bool)GetValue(IsCompactNavigationProperty);

    private readonly DispatcherTimer _feedReflowTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(140) };
    private bool _appearanceReady, _reflowingFeed;

    private double ResponsiveSidebarWidth => IsCompactNavigation ? 76 : 188;
    private double ResponsiveCommentWidth => IsCompactNavigation ? 270 : 310;

    private static readonly Geometry PlayButtonGeometry = FreezeGeometry("M4,2 L16,9 4,16 Z");
    private static readonly Geometry PauseButtonGeometry = FreezeGeometry("M4,2 H8 V16 H4 Z M11,2 H15 V16 H11 Z");

    private static Geometry FreezeGeometry(string markup)
    {
        var geometry = Geometry.Parse(markup);
        geometry.Freeze();
        return geometry;
    }

    private void InitializeAppearance()
    {
        _appearanceReady = true;
        SelectSettingsSection(SettingsAccountTab);
        UpdatePauseControls();
        UpdateResponsiveNavigation(ActualWidth > 0 ? ActualWidth : Width, applyInitialLayout: true);
        SizeChanged += (_, args) =>
        {
            if (args.WidthChanged) UpdateResponsiveNavigation(args.NewSize.Width);
        };
        VideoList.SizeChanged += (_, _) => ScheduleFeedReflow();
        VideoList.IsVisibleChanged += (_, _) => ScheduleFeedReflow();
        _feedReflowTimer.Tick += ReflowFeedForWidth;
        Closed += (_, _) => _feedReflowTimer.Stop();
    }

    private void UpdateResponsiveNavigation(double width, bool applyInitialLayout = false)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        var compact = width < CompactNavigationBreakpoint;
        if (compact == IsCompactNavigation && !applyInitialLayout) return;
        SetValue(IsCompactNavigationPropertyKey, compact);
        ApplyPlayerViewLayout();
    }

    private void ScheduleFeedReflow()
    {
        _feedReflowTimer.Stop();
        if (!_closing && VideoList.IsVisible && VideoList.ActualWidth > 0) _feedReflowTimer.Start();
    }

    private void ReflowFeedForWidth(object? sender, EventArgs e)
    {
        _feedReflowTimer.Stop();
        if (_closing || !VideoList.IsVisible || VideoList.ActualWidth <= 0) return;
        var columns = Math.Clamp((int)(VideoList.ActualWidth / PreferredFeedCardWidth), 2, 5);
        if (columns == FeedColumnCount) return;

        var scroll = FindFeedVisual<ScrollViewer>(VideoList);
        var anchor = scroll is null ? null : CaptureFeedScrollAnchor(scroll);
        _reflowingFeed = true;
        try
        {
            SetValue(FeedColumnCountPropertyKey, columns);
            RebuildFeedRows();
            if (scroll is not null && anchor is { } position)
                RestoreFeedScrollAnchor(scroll, position.ItemIndex / columns, position.RowFraction);
        }
        finally
        {
            // ScrollIntoView and offset restoration also queue ScrollChanged during layout.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _reflowingFeed = false));
        }
    }

    private (int ItemIndex, double RowFraction)? CaptureFeedScrollAnchor(ScrollViewer scroll)
    {
        if (scroll.VerticalOffset <= 0 || _rows.Count == 0) return null;
        var viewport = FindFeedVisual<ScrollContentPresenter>(scroll);
        var panel = viewport is null ? null : FindFeedVisual<VirtualizingStackPanel>(viewport);
        if (viewport is not null && panel is not null)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            {
                if (VisualTreeHelper.GetChild(panel, i) is not ListBoxItem row || row.ActualHeight <= 0) continue;
                var top = row.TranslatePoint(new Point(), viewport).Y;
                if (top + row.ActualHeight <= 0 || top >= viewport.ActualHeight) continue;
                var index = VideoList.ItemContainerGenerator.IndexFromContainer(row);
                if (index >= 0) return (index * FeedColumnCount, Math.Clamp(-top / row.ActualHeight, 0, 1));
            }
        }

        // A freshly resized, virtualized list may not yet have a realized row at the offset.
        if (scroll.ExtentHeight <= 0) return null;
        var rowPosition = scroll.VerticalOffset / scroll.ExtentHeight * _rows.Count;
        var rowIndex = Math.Clamp((int)rowPosition, 0, _rows.Count - 1);
        return (rowIndex * FeedColumnCount, Math.Clamp(rowPosition - rowIndex, 0, 1));
    }

    private void RestoreFeedScrollAnchor(ScrollViewer scroll, int rowIndex, double rowFraction)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        VideoList.UpdateLayout();
        VideoList.ScrollIntoView(_rows[rowIndex]);
        VideoList.UpdateLayout();
        if (VideoList.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not ListBoxItem row ||
            FindFeedVisual<ScrollContentPresenter>(scroll) is not { } viewport) return;
        var top = row.TranslatePoint(new Point(), viewport).Y;
        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top + row.ActualHeight * rowFraction));
    }

    private static T? FindFeedVisual<T>(DependencyObject root) where T : Visual
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindFeedVisual<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void AppendFeedRowItem(VideoItem item)
    {
        if (_rows.Count == 0 || _rows[^1].Items.Count >= FeedColumnCount) _rows.Add(new());
        _rows[^1].Items.Add(item);
    }

    private void RebuildFeedRows()
    {
        _rows.Clear();
        foreach (var item in _items) AppendFeedRowItem(item);
    }

    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady || !IsInitialized || sender is not RadioButton { IsChecked: true, Tag: string section }) return;
        ApplySettingsSection(section);
    }

    private void SelectSettingsSection(RadioButton tab)
    {
        if (!_appearanceReady || tab.Tag is not string section) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
        else ApplySettingsSection(section);
    }

    private void ApplySettingsSection(string section)
    {
        SettingsPlaybackSection.Visibility = section == "playback" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAccountSection.Visibility = section == "account" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPrivacySection.Visibility = section == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        SettingsToolsSection.Visibility = section == "tools" ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsScroll.IsLoaded) SettingsScroll.ScrollToTop();
    }

    private async void OpenAccountSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady || AccountWorkBlocked) return;
        var view = ++_viewGeneration;
        if (!await PrepareMainNavigationAsync() || AccountWorkBlocked || view != _viewGeneration) return;
        ShowPanel("settings");
        SelectSettingsSection(SettingsAccountTab);
        SettingsAccountTab.Focus();
        UpdateToolState();
    }
}
