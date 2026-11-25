using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FkYouTube.Controls;

namespace FkYouTube;

public partial class MainWindow
{
    private TrayIcon? _trayIcon;
    private Window? _trayHiddenPopout;
    private WindowState _trayUnminimizedState = WindowState.Normal;
    private bool _trayHideQueued, _trayExitRequested, _traySessionEnding, _trayWindowClosed;
    private long _trayVisibilityGeneration;
    private int? _futurePrefetchResetGeneration, _futurePrefetchSuppressedGeneration;
    private Task _futurePrefetchResetTask = Task.CompletedTask;

    private bool IsInTray { get; set; }
    private bool EffectiveNextAudioOnly => _settings.AudioOnly || IsInTray;
    private bool IsTrayPrefetchResetPending => _futurePrefetchResetGeneration == _videoGeneration ||
        _futurePrefetchSuppressedGeneration == _videoGeneration;

    // The hidden popout can still own the player HWND. Hidden/visible is a
    // separate decision from whether the host would need to be moved.
    private bool HasPlaybackPopout => !_popoutShutdown && _popoutWindow is not null &&
        _popoutSurface is not null && ReferenceEquals(VideoHost.Parent, _popoutSurface);

    private void InitializeTray()
    {
        SourceInitialized += (_, _) =>
        {
            try
            {
                _trayIcon = new TrayIcon(Dispatcher, new WindowInteropHelper(this).Handle,
                    GetTrayMenu, InvokeTrayCommand, RestoreIfTrayIconUnavailable);
                _trayIcon.TryRegister();
            }
            catch (Win32Exception error)
            {
                Trace.TraceWarning("FK_YT notification icon: native error {0}", error.NativeErrorCode);
            }
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _trayUnminimizedState = WindowState;
        };
        var app = Application.Current;
        if (app is not null) app.SessionEnding += TraySessionEnding;
        Closed += (_, _) =>
        {
            _trayWindowClosed = true;
            if (app is not null) app.SessionEnding -= TraySessionEnding;
            DisposeTrayIcon();
        };
    }

    private TrayIcon.MenuItem[] GetTrayMenu()
    {
        // Resolve labels and enabled state just as the native menu opens. This
        // remains current after a language change, with no cached translations.
        var canControl = !AccountWorkBlocked && _current is not null;
        var paused = _playPauseIntent?.Paused ?? _snapshot?.Paused ?? _pausedPlaybackIntent;
        return
        [
            new(TrayIcon.Command.Restore, UiText.T("창 복원", "Restore", "ウィンドウを復元"), !_closing),
            new(TrayIcon.Command.TogglePlayback, paused ? UiText.T("재생", "Play", "再生") :
                UiText.T("일시정지", "Pause", "一時停止"), canControl && (_playerStarted || _videoCts is not null)),
            new(TrayIcon.Command.Next, UiText.T("다음 영상", "Next", "次の動画"), canControl && NextVideoButton.IsEnabled),
            new(TrayIcon.Command.Exit, UiText.T("종료", "Exit", "終了"), !_closing)
        ];
    }

    private async void InvokeTrayCommand(TrayIcon.Command command)
    {
        if (_trayWindowClosed || _traySessionEnding || _closing) return;
        switch (command)
        {
            case TrayIcon.Command.Restore: RestoreFromTray(); break;
            case TrayIcon.Command.TogglePlayback: await TogglePlaybackPauseAsync(); break;
            case TrayIcon.Command.Next when !AccountWorkBlocked && NextVideoButton.IsEnabled:
                NextVideo_Click(this, new RoutedEventArgs()); break;
            case TrayIcon.Command.Exit: ExitFromTray(); break;
        }
    }

    private bool TryHandleTrayClose(CancelEventArgs closing)
    {
        if (_trayExitRequested || _traySessionEnding || _closing || Dispatcher.HasShutdownStarted) return false;
        if (IsInTray || _trayHideQueued) { closing.Cancel = true; return true; }
        // A hidden window needs a route back. If Explorer isn't available,
        // fall through to the original graceful shutdown path.
        if (_trayIcon?.TryRegister() != true) return false;
        closing.Cancel = true;
        _trayHideQueued = true;
        // WPF is still evaluating Closing; leave that call before Hide/Show.
        var generation=++_trayVisibilityGeneration;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if(generation==_trayVisibilityGeneration)HideToTray();
        }, DispatcherPriority.Normal);
        return true;
    }

    private void HideToTray()
    {
        _trayHideQueued = false;
        if (_trayWindowClosed || _traySessionEnding || _closing || IsInTray || _trayIcon?.TryRegister() != true) return;
        if (WindowState != WindowState.Minimized) _trayUnminimizedState = WindowState;
        var previousMode = EffectiveNextAudioOnly;
        IsInTray = true;
        // Don't transfer the video child or touch pause, position, or the media.
        // WPF Hide preserves the native HWND in both windows.
        _trayHiddenPopout = _popoutWindow is { IsVisible: true } popup ? popup : null;
        CloseDescriptionPopup();
        CloseSubtitlePopup();
        _qualityPopupRevision++;
        QualityCombo.IsDropDownOpen = false;
        SpeedCombo.IsDropDownOpen = false;
        _seekDragging = _popoutSeekDragging = false;
        if (Mouse.Captured == SeekSlider) SeekSlider.ReleaseMouseCapture();
        else if (Mouse.Captured == _popoutSeek) _popoutSeek?.ReleaseMouseCapture();
        _trayHiddenPopout?.Hide();
        Hide();
        ResetTrayPrefetchIfNeeded(previousMode);
    }

    private void RestoreFromTray()
    {
        if (_trayWindowClosed || _traySessionEnding || _closing || Dispatcher.HasShutdownStarted) return;
        ++_trayVisibilityGeneration;
        _trayHideQueued=false;
        var wasInTray = IsInTray;
        var previousMode = EffectiveNextAudioOnly;
        IsInTray = false;
        if (wasInTray) Show();
        if (WindowState == WindowState.Minimized) WindowState = _trayUnminimizedState;
        if (wasInTray)
        {
            var popup = _trayHiddenPopout;
            _trayHiddenPopout = null;
            if (popup is not null && ReferenceEquals(popup, _popoutWindow)) popup.Show();
            UpdateLayout();
            VideoHost.UpdateWindowPos();
            ResetTrayPrefetchIfNeeded(previousMode);
        }
        Activate();
    }

    internal void RestoreFromExternalLaunch() => RestoreFromTray();

    private void RestoreIfTrayIconUnavailable()
    {
        if (!IsInTray || _closing || _traySessionEnding) return;
        RestoreFromTray();
        SetStatus(() => UiText.T("알림 영역 아이콘을 복구하지 못해 창을 다시 표시했습니다.",
            "The window was restored because the notification icon could not be recovered.",
            "通知領域のアイコンを復旧できなかったため、ウィンドウを表示しました。"));
    }

    private void ResetTrayPrefetchIfNeeded(bool previousMode)
    {
        if (previousMode != EffectiveNextAudioOnly) InvalidateFuturePrefetch();
    }

    private void InvalidateFuturePrefetch()
    {
        var hadPrefetch = _prefetchCts is not null || _prefetchAttempt is not null || _preparedItem is not null;
        // Cancel extraction and invalidate this request's UI completion. Native
        // append uses the account token, so its exact ID can be confirmed before
        // the removal below takes the same load gate.
        _prefetchCts?.Cancel();
        _prefetchAttempt = null;
        _preparedItem = null;
        _preparedPlayback = null;
        _preparedResolutions = [];
        _preparedCaptions = [];
        if (hadPrefetch) SetPrefetchUiText(() => "");
        if (IsTrayPrefetchResetPending) return;
        if (_playerStarted && hadPrefetch && _expectedMediaGeneration >= 0)
            _ = ClearFuturePrefetchSafelyAsync();
        else _ = EnsurePrefetchAsync();
    }

    private Task ClearFuturePrefetchSafelyAsync()
    {
        if (_futurePrefetchResetGeneration == _videoGeneration) return _futurePrefetchResetTask;
        if (_futurePrefetchSuppressedGeneration == _videoGeneration || _closing ||
            !_playerStarted || _expectedMediaGeneration < 0) return Task.CompletedTask;
        _futurePrefetchResetGeneration = _videoGeneration;
        return _futurePrefetchResetTask = FinishFuturePrefetchResetAsync(
            _videoGeneration, _expectedMediaGeneration, _accountCts.Token);
    }

    private async Task FinishFuturePrefetchResetAsync(int generation, long mediaGeneration, CancellationToken token)
    {
        bool cleared;
        try { cleared = await _player.TryClearPreparedNextAsync(mediaGeneration, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        finally
        {
            if (_futurePrefetchResetGeneration == generation) _futurePrefetchResetGeneration = null;
        }
        if (_closing || token.IsCancellationRequested || _videoGeneration != generation || _expectedMediaGeneration != mediaGeneration) return;
        if (cleared) await EnsurePrefetchAsync();
        else
        {
            // Refuse a second append to an unverified playlist while allowing
            // the normal next request to open its own correctly resolved media.
            _futurePrefetchSuppressedGeneration = generation;
            SetPrefetchUiText(() => UiText.T("다음 영상은 미리 준비하지 않고 재생합니다.",
                "The next video will play without preloading.", "次の動画は事前準備せずに再生します。"));
        }
    }

    private void ExitFromTray()
    {
        if (_trayWindowClosed || _traySessionEnding || _closing) return;
        _trayExitRequested = true;
        try { Close(); }
        finally { _trayExitRequested = false; }
    }

    private void RestoreAfterAbortedExit()
    {
        if (IsInTray && !_traySessionEnding)
            _ = Dispatcher.BeginInvoke(RestoreFromTray, DispatcherPriority.Normal);
    }

    private void TraySessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Leave e.Cancel alone. WPF's application shutdown may close this
        // window regardless of Closing cancellation. Never hide it again.
        _traySessionEnding = true;
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
