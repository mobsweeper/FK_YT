using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FkYouTube.Controls;
using FkYouTube.Playback;

namespace FkYouTube;

public partial class MainWindow
{
    private Window? _popoutWindow;
    private Border? _popoutSurface;
    private Slider? _popoutSeek;
    private TextBlock? _popoutTime;
    private Button? _popoutPause;
    private Button? _popoutLiveEdge;
    private Button? _popoutPrevious,_popoutNext;
    private PlaybackControlsAutoHide? _popoutControlsAutoHide;
    private bool _popoutSeekDragging;
    private bool _popoutShutdown;
    private bool _popoutCloseWithoutActivation;
    private Action? _refreshPopoutUiText;

    private void RefreshPopoutUiText()
    {
        PopoutLaunchButton.SetCurrentValue(ContentControl.ContentProperty, _popoutWindow is null
            ? UiText.T("팝아웃", "Pop out", "別ウィンドウ") : UiText.T("복귀", "Return", "戻す"));
        _refreshPopoutUiText?.Invoke();
    }

    private async void Popout_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_popoutWindow is { } existing)
        {
            existing.Close();
            return;
        }
        if (!_playerStarted || _current is null)
        {
            SetStatus(()=>UiText.T("영상을 재생한 뒤 별도 창으로 열 수 있습니다.","Start a video before opening a popout window.","動画を再生してから別ウィンドウで開けます。"));
            return;
        }
        if (_fullscreen) ToggleFullscreen();
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_closing || !_playerStarted || _current is null) return;
        if (_popoutWindow is { } opened)
        {
            opened.Close();
            return;
        }

        var surface = new Border { Background = Brushes.Black, MinHeight = 80 };
        surface.PreviewMouseLeftButtonDown += VideoSurface_Down;
        var layout = new Grid { Background = new SolidColorBrush(Color.FromRgb(16, 17, 21)) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(surface);

        var controls = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        var tray = new Border { Child = controls, ClipToBounds = true };
        Grid.SetRow(tray, 1);
        layout.Children.Add(tray);
        var wakeZone = new Border { Height = 8, Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed };
        Grid.SetRow(wakeZone, 2);
        layout.Children.Add(wakeZone);
        var seek = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false,
            IsMoveToPointEnabled = true, SmallChange = 5, LargeChange = 30,
            Margin = new Thickness(0, 0, 0, 8) };
        seek.PreviewKeyDown += SeekSlider_KeyDown;
        seek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(PopoutSeek_Down), true);
        seek.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(PopoutSeek_Up), true);
        seek.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(PopoutSeek_Move), true);
        seek.LostMouseCapture += PopoutSeek_LostCapture;
        controls.Children.Add(seek);

        var toolbar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(toolbar);
        _popoutPrevious=PopoutButton(UiText.T("⏮ 이전","⏮ Previous","⏮ 前へ"));_popoutPrevious.Click+=PreviousVideo_Click;toolbar.Children.Add(_popoutPrevious);
        _popoutNext=PopoutButton(UiText.T("다음 ⏭","Next ⏭","次へ ⏭"));_popoutNext.Click+=NextVideo_Click;toolbar.Children.Add(_popoutNext);
        var pause = PopoutButton(UiText.T("재생 / 일시정지","Play / Pause","再生 / 一時停止"));
        pause.Click += Pause_Click;
        toolbar.Children.Add(pause);
        var back = PopoutButton(UiText.T("−5초","−5 sec","−5秒"));
        back.Click += SeekBack_Click;
        toolbar.Children.Add(back);
        var forward = PopoutButton(UiText.T("+5초","+5 sec","+5秒"));
        forward.Click += SeekForward_Click;
        toolbar.Children.Add(forward);
        var time = new TextBlock { Text = "0:00 / 0:00", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 10, 0) };
        toolbar.Children.Add(time);
        var liveEdge = PopoutButton("● LIVE");
        liveEdge.Visibility = Visibility.Collapsed;
        liveEdge.Click += LiveEdge_Click;
        toolbar.Children.Add(liveEdge);
        var volumeLabel = new TextBlock { Text = UiText.T("음량","Volume","音量"), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(volumeLabel);
        var volume = new Slider { Minimum = 0, Maximum = 100, Width = 80, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        VolumeTrackDrag.SetEnabled(volume, true);
        volume.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value))
            { Source = VolumeSlider, Mode = BindingMode.TwoWay });
        toolbar.Children.Add(volume);
        var share = PopoutButton(UiText.T("공유","Share","共有"));
        share.ToolTip = UiText.T("영상 주소만 복사","Copy video URL only","動画 URL のみコピー");
        share.Click += Share_Click;
        toolbar.Children.Add(share);
        var subtitles = PopoutButton(UiText.T("자막 ▾","Subtitles ▾","字幕 ▾"));
        subtitles.Click += Subtitles_Click;
        toolbar.Children.Add(subtitles);
        var pin = new CheckBox { Content = UiText.T("항상 위","Always on top","常に手前に表示"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 10, 0) };
        toolbar.Children.Add(pin);
        var restore = PopoutButton(UiText.T("원래 창으로","Return to main window","元のウィンドウへ"));
        toolbar.Children.Add(restore);

        var popup = new Window
        {
            Owner = this, Title = "FK_YT · " + _current.Title, Content = layout,
            Width = 900, Height = 570, MinWidth = 400, MinHeight = 270,
            ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = layout.Background, Foreground = Foreground, FontFamily = FontFamily,
            FontSize = FontSize, Icon = Icon
        };
        pin.Checked += (_, _) => popup.Topmost = true;
        pin.Unchecked += (_, _) => popup.Topmost = false;
        restore.Click += (_, _) => popup.Close();
        popup.PreviewKeyDown += Popout_KeyDown;
        popup.Closing += Popout_Closing;
        var mainRestoreState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        EventHandler trackMainState = (_, _) =>
        {
            if (WindowState != WindowState.Minimized) mainRestoreState = WindowState;
        };
        StateChanged += trackMainState;
        popup.Closed += (_, _) =>
        {
            StateChanged -= trackMainState;
            _popoutControlsAutoHide?.Dispose(); _popoutControlsAutoHide = null;
            _refreshPopoutUiText = null;
            _popoutWindow = null; _popoutSurface = null; _popoutSeek = null;
            _popoutTime = null; _popoutPause = null; _popoutLiveEdge = null; _popoutPrevious=null;_popoutNext=null;_popoutSeekDragging = false;
            ApplyPlayerViewLayout();
            RefreshPopoutUiText();
            if (!_closing && !_popoutShutdown && !_popoutCloseWithoutActivation)
            {
                if (WindowState == WindowState.Minimized) WindowState = mainRestoreState;
                Activate();
                PlayPauseButton.Focus();
            }
        };
        _popoutWindow = popup; _popoutSurface = surface; _popoutSeek = seek;
        _popoutTime = time; _popoutPause = pause; _popoutLiveEdge = liveEdge;
        ApplyPlayerViewLayout();
        _refreshPopoutUiText = () =>
        {
            if(_popoutPrevious is {} previous)previous.Content = UiText.T("⏮ 이전","⏮ Previous","⏮ 前へ");
            if(_popoutNext is {} next)next.Content = UiText.T("다음 ⏭","Next ⏭","次へ ⏭");
            UpdatePauseControls();
            back.Content = UiText.T("−5초","−5 sec","−5秒");
            forward.Content = UiText.T("+5초","+5 sec","+5秒");
            var liveLabel = UiText.T("실시간 지점으로 이동", "Go to live edge", "ライブ位置へ移動");
            liveEdge.ToolTip = liveLabel;
            AutomationProperties.SetName(liveEdge, liveLabel);
            volumeLabel.Text = UiText.T("음량","Volume","音量");
            share.Content = UiText.T("공유","Share","共有");
            share.ToolTip = UiText.T("영상 주소만 복사","Copy video URL only","動画 URL のみコピー");
            subtitles.Content = UiText.T("자막 ▾","Subtitles ▾","字幕 ▾");
            pin.Content = UiText.T("항상 위","Always on top","常に手前に表示");
            restore.Content = UiText.T("원래 창으로","Return to main window","元のウィンドウへ");
            if (_snapshot is { } snapshot) UpdatePopoutSnapshot(snapshot);
        };
        RefreshPopoutUiText();
        _popoutControlsAutoHide = new PlaybackControlsAutoHide(popup, layout, tray, wakeZone,
            () => _popoutSeekDragging || SubtitleControlsBusy);
        var nativeHandle = VideoHost.Handle;
        try
        {
            // HwndHost reuses its HWND when the PresentationSource changes.
            // Move the existing control, keeping mpv's --wid and IPC session.
            popup.Show();
            // CenterOwner is only the startup placement. Detach native ownership
            // so minimizing the main window leaves this player visible.
            popup.Owner = null;
            VideoSurface.Child = null;
            surface.Child = VideoHost;
            popup.UpdateLayout();
            VideoHost.UpdateWindowPos();
            if (nativeHandle == 0 || VideoHost.Handle != nativeHandle)
                throw new InvalidOperationException("The native video window changed while opening the popout.");
            UpdatePopoutSnapshot(_snapshot ?? _player.Snapshot);
            pause.Focus();
            _popoutControlsAutoHide.Enabled = true;
            SetStatus(()=>UiText.T("별도 창에서 재생 중 · Space: 재생/일시정지, ←/→: 5초 이동, ↑/↓: 음량, Esc: 원래 창으로","Playing in popout · Space: Play/Pause, ←/→: Seek 5 sec, ↑/↓: Volume, Esc: Return to main window","別ウィンドウで再生中 · Space: 再生/一時停止、←/→: 5秒移動、↑/↓: 音量、Esc: 元のウィンドウへ"));
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            var restored = ClosePopoutForPlaybackStop();
            SetStatus(()=>restored
                ? UiText.T("별도 창을 열지 못했습니다. 영상 영역을 원래 창으로 복귀했습니다.","Could not open the popout. The video was returned to the main window.","別ウィンドウを開けませんでした。動画領域を元のウィンドウに戻しました。")
                : UiText.T("별도 창을 열거나 영상 영역을 복귀하지 못했습니다. 창을 다시 닫아 주세요.","Could not open the popout or restore the video. Close the window again.","別ウィンドウを開くか動画領域を戻すことができませんでした。もう一度ウィンドウを閉じてください。"));
        }
    }

    private static Button PopoutButton(string label) => new()
        { Content = label, Padding = new Thickness(8, 5, 8, 5), MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };

    private void Popout_Closing(object? sender, CancelEventArgs e)
    {
        if (!_popoutShutdown && sender is Window popup)
        {
            // A later Closing subscriber may cancel after our transfer. Run
            // after all subscribers and keep the still-open popout's host there.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!e.Cancel || !ReferenceEquals(_popoutWindow, popup) ||
                    _popoutSurface is null || !ReferenceEquals(VideoHost.Parent, VideoSurface)) return;
                try
                {
                    VideoSurface.Child = null;
                    _popoutSurface.Child = VideoHost;
                    popup.UpdateLayout();
                    VideoHost.UpdateWindowPos();
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    if (!_closing) SetStatus(()=>UiText.T("영상 영역을 별도 창에 유지하지 못했습니다. 별도 창을 다시 닫아 주세요.","Could not keep the video in the popout. Close the popout again.","動画領域を別ウィンドウに維持できませんでした。もう一度別ウィンドウを閉じてください。"));
                }
            }, DispatcherPriority.Loaded);
        }
        _popoutSeekDragging = false;
        if (Mouse.Captured == _popoutSeek) _popoutSeek?.ReleaseMouseCapture();
        try
        {
            if (VideoSurface.Child != VideoHost)
            {
                if (_popoutSurface?.Child == VideoHost) _popoutSurface.Child = null;
                VideoSurface.Child = VideoHost;
                UpdateLayout();
                VideoHost.UpdateWindowPos();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            e.Cancel = !_popoutShutdown;
            // A failed reattachment must leave the existing host visible and
            // permit a later retry rather than closing its last video surface.
            if (!_popoutShutdown && VideoHost.Parent is null && _popoutSurface is not null)
            {
                try { _popoutSurface.Child = VideoHost; }
                catch (Exception fallback) when (fallback is InvalidOperationException or Win32Exception) { }
            }
            if (!_closing) SetStatus(()=>UiText.T("영상 영역을 원래 창으로 복귀하지 못했습니다. 별도 창을 다시 닫아 주세요.","Could not return the video to the main window. Close the popout again.","動画領域を元のウィンドウに戻せませんでした。もう一度別ウィンドウを閉じてください。"));
        }
    }

    private bool ClosePopoutForPlaybackStop()
    {
        var previous = _popoutCloseWithoutActivation;
        _popoutCloseWithoutActivation = true;
        try
        {
            _popoutWindow?.Close();
            return _popoutWindow is null;
        }
        finally { _popoutCloseWithoutActivation = previous; }
    }

    private void ClosePopoutForShutdown()
    {
        _popoutShutdown = true;
        _popoutControlsAutoHide?.Dispose(); _popoutControlsAutoHide = null;
        _popoutWindow?.Close();
    }

    private void UpdatePopoutSnapshot(PlaybackSnapshot snapshot)
    {
        if (_popoutWindow is null || _popoutSeek is null) return;
        if (_current is not null) _popoutWindow.Title = "FK_YT · " + _current.Title;
        var live = ConfigureLiveSeek(_popoutSeek, snapshot);
        if (!live)
        {
            _popoutSeek.IsEnabled = snapshot.Loaded && snapshot.Duration > 0;
            if (!_popoutSeekDragging && !_pendingSeek.HasValue)
            {
                _popoutSeek.Minimum = 0;
                _popoutSeek.Maximum = Math.Max(1, snapshot.Duration);
                _popoutSeek.Value = Math.Clamp(snapshot.Position, 0, _popoutSeek.Maximum);
            }
        }
        RefreshLiveControls(snapshot);
        if (_popoutTime is not null)
            _popoutTime.Text = live && ConfirmedLiveEndStatus(snapshot) is null
                ? UiText.T("실시간", "LIVE", "ライブ") + " · " + VideoItem.FormatTime(snapshot.Position)
                : VideoItem.FormatTime(snapshot.Position) + " / " + VideoItem.FormatTime(snapshot.Duration);
        UpdatePauseControls();
        UpdatePopoutSkipButtons();
    }
    private void UpdatePopoutSkipButtons()
    {
        if(_popoutPrevious!=null)_popoutPrevious.IsEnabled=PreviousVideoButton.IsEnabled;
        if(_popoutNext!=null)_popoutNext.IsEnabled=NextVideoButton.IsEnabled;
    }

    private async void Popout_KeyDown(object sender, KeyEventArgs e)
    {
        if (ShouldPreserveControlKey(e)) return;
        switch (e.Key)
        {
            case Key.Space: e.Handled = true; await TogglePlaybackPauseAsync(); break;
            case Key.Left: e.Handled = true; await SeekRelativeAsync(-5); break;
            case Key.Right: e.Handled = true; await SeekRelativeAsync(5); break;
            case Key.Up: e.Handled = true; VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); break;
            case Key.Down: e.Handled = true; VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
            case Key.Escape: e.Handled = true; _popoutWindow?.Close(); break;
        }
    }

    private void PopoutSeek_Down(object sender, MouseButtonEventArgs e)
    {
        _popoutSeekDragging = true;
        if (e.Handled && Mouse.Captured is null && _popoutSeek is not null)
            Mouse.Capture(_popoutSeek, CaptureMode.SubTree);
    }

    private async void PopoutSeek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_popoutSeekDragging) return;
        var seek = CommitPopoutSeekAsync();
        if (Mouse.Captured == _popoutSeek) _popoutSeek?.ReleaseMouseCapture();
        await seek;
    }

    private void PopoutSeek_Move(object sender, MouseEventArgs e)
    {
        if (!_popoutSeekDragging || _popoutSeek is null || Mouse.Captured != _popoutSeek) return;
        if (_popoutSeek.Template.FindName("PART_Track", _popoutSeek) is not Track track) return;
        track.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (double.IsFinite(value)) _popoutSeek.Value = Math.Clamp(value, _popoutSeek.Minimum, _popoutSeek.Maximum);
        e.Handled = true;
    }

    private async void PopoutSeek_LostCapture(object sender, MouseEventArgs e)
    {
        if (_popoutSeekDragging) await CommitPopoutSeekAsync();
    }

    private async Task CommitPopoutSeekAsync()
    {
        _popoutSeekDragging = false;
        if (!_playerStarted || _snapshot?.Loaded != true || _popoutSeek is null) return;
        var clamped = ClampPlaybackSeek(_popoutSeek.Value);
        if (clamped is not { } target) return;
        if (IsLivePlayback) { await SeekLiveAsync(target); return; }
        _pendingSeek = target; _pendingSeekAt = _clock.Elapsed.TotalSeconds;
        await PlayerCommandAsync("seek", target, "absolute+exact");
    }
}
