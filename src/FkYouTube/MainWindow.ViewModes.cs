using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FkYouTube.Controls;

namespace FkYouTube;

public partial class MainWindow
{
    private PlaybackControlsAutoHide? _controlsAutoHide;
    private bool _theaterMode, _sidePanelHidden;

    private void InitializePlayerViewModes()
    {
        _controlsAutoHide = new(this, VideoColumn, PlaybackControlsTray, PlaybackWakeZone,
            () => _seekDragging || QualityCombo.IsDropDownOpen || SpeedCombo.IsDropDownOpen ||
                  SubtitleControlsBusy || DescriptionPopup.IsOpen);
        Closed += (_, _) => DisposePlayerViewModes();
    }

    private void HideSidePanel_Click(object sender, RoutedEventArgs e)
    {
        _sidePanelHidden = true;
        ApplyPlayerViewLayout();
        ShowSidePanelButton.Focus();
    }

    private void ShowSidePanel_Click(object sender, RoutedEventArgs e)
    {
        _sidePanelHidden = false;
        // Explicitly reopening the panel also leaves the wide theater layout.
        _theaterMode = false;
        ApplyPlayerViewLayout();
        CommentsTab.Focus();
    }

    private void Theater_Click(object sender, RoutedEventArgs e)
    {
        if (AccountWorkBlocked) return;
        _theaterMode = _fullscreen || !_theaterMode;
        if (_fullscreen) ToggleFullscreen();
        else ApplyPlayerViewLayout();
        TheaterButton.Focus();
    }

    private void ApplyPlayerViewLayout()
    {
        var playing = PlayerPanel.Visibility == Visibility.Visible;
        var wide = playing && (_fullscreen || _theaterMode);
        var hideSide = playing && (_fullscreen || _theaterMode || _sidePanelHidden);
        SidebarColumn.Width = new(wide ? 0 : 184);
        Sidebar.Visibility = Header.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeading.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        MainContent.Margin = _fullscreen ? new(0) : _theaterMode && playing ? new(12) : new(24, 20, 24, 14);
        StatusPanel.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        CommentColumn.Width = new(hideSide ? 0 : 360);
        CommentsPanel.Visibility = hideSide ? Visibility.Collapsed : Visibility.Visible;
        ShowSidePanelButton.Visibility = playing && hideSide && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;
        PlayingChannelPanel.Visibility = RatingPanel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        RefreshPlayerViewModeUiText();
        FullscreenIcon.Data = Geometry.Parse(_fullscreen
            ? "M1,7 L7,7 7,1 M13,1 L13,7 19,7 M19,13 L13,13 13,19 M7,19 L7,13 1,13"
            : "M1,7 L1,1 7,1 M13,1 L19,1 19,7 M19,13 L19,19 13,19 M7,19 L1,19 1,13");
        TheaterButton.Background = (Brush)FindResource(_theaterMode ? "Accent" : "Panel");
        TheaterButton.Foreground = _theaterMode ? Brushes.Black : Brushes.White;
        if (_controlsAutoHide is { } hide) hide.Enabled = playing && _fullscreen;
    }

    private void RefreshPlayerViewModeUiText()
    {
        FullscreenButton.ToolTip = _fullscreen ? UiText.T("전체화면 해제 (Esc)","Exit fullscreen (Esc)","全画面表示を解除 (Esc)") : UiText.T("전체화면 (F)","Fullscreen (F)","全画面表示 (F)");
        TheaterButton.ToolTip = _theaterMode ? UiText.T("영화관 모드 해제","Exit theater mode","シアターモードを解除") : UiText.T("영화관 모드 · 주변 메뉴 숨기기","Theater mode · Hide surrounding menus","シアターモード · 周囲のメニューを非表示");
    }

    private void DisposePlayerViewModes() => _controlsAutoHide?.Dispose();

    private async void SeekSlider_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not Slider slider) return;
        var target = e.Key switch
        {
            Key.Left or Key.Down => slider.Value - slider.SmallChange,
            Key.Right or Key.Up => slider.Value + slider.SmallChange,
            Key.PageDown => slider.Value - slider.LargeChange,
            Key.PageUp => slider.Value + slider.LargeChange,
            Key.Home => slider.Minimum,
            Key.End => slider.Maximum,
            _ => double.NaN
        };
        if (!double.IsFinite(target)) return;
        // The focused seek bar owns navigation. Submit only this user key, never
        // the periodic Value updates from playback snapshots or a volume slider.
        e.Handled = true;
        if (AccountWorkBlocked || !_playerStarted || _snapshot?.Loaded != true ||
            !slider.IsEnabled || _seekDragging || _popoutSeekDragging) return;
        target = Math.Clamp(target, slider.Minimum, slider.Maximum);
        if (target == slider.Value) return;
        slider.SetCurrentValue(RangeBase.ValueProperty, target);
        if (ReferenceEquals(slider, SeekSlider)) await CommitSeekAsync();
        else if (ReferenceEquals(slider, _popoutSeek)) await CommitPopoutSeekAsync();
    }

    private static bool ShouldPreserveControlKey(KeyEventArgs args)
    {
        if (args.Handled || Keyboard.Modifiers != ModifierKeys.None) return true;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var navigation = key is Key.Left or Key.Right or Key.Up or Key.Down or
            Key.Home or Key.End or Key.PageUp or Key.PageDown;
        var current = args.OriginalSource as DependencyObject;
        if (current is null or Window) current = Keyboard.FocusedElement as DependencyObject;
        for (; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase or PasswordBox or ComboBox or MenuBase or MenuItem) return true;
            if (current is ButtonBase && (key is Key.Space or Key.Enter)) return true;
            if (current is Slider && navigation) return true;
            if (current is Selector && (navigation || key == Key.Space)) return true;
            if (current is ScrollViewer && navigation) return true;
        }
        return false;
    }
}
