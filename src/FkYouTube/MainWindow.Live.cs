using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private CancellationTokenSource? _liveRecoveryCts;
    private Task _liveRecoveryTask = Task.CompletedTask;
    private CancellationToken _activePlaybackRecoveryToken;
    private int _liveReconnectAttempts, _liveEndHandledGeneration = -1;
    private string? _lastPlaybackAccessCode;
    private Func<string>? _liveNotice;
    private long _confirmedLiveEndMediaGeneration = -1;
    private string? _confirmedLiveEndStatus;
    private string? ConfirmedLiveEndStatus(PlaybackSnapshot? snapshot) => snapshot is not null &&
        snapshot.MediaGeneration == _confirmedLiveEndMediaGeneration && snapshot.VideoUrl == _current?.Url
            ? _confirmedLiveEndStatus : null;
    private bool IsLivePlayback => ConfirmedLiveEndStatus(_snapshot) is null &&
        (_snapshot?.IsLive == true || _current?.LiveStatus == "is_live");
    private bool CanReloadLive => !AccountWorkBlocked && IsLivePlayback && _current is not null && _videoCts is null;
    private static bool TerminalLiveFailure(PlaybackSnapshot snapshot) => snapshot.AuthenticationRequired ||
        snapshot.ErrorCode is { } code && code is not ("network" or "generic");
    private bool BlockingLiveFailure(PlaybackSnapshot? snapshot) => snapshot is not null && _current is not null &&
        snapshot.MediaGeneration == _expectedMediaGeneration && snapshot.VideoUrl == _current.Url &&
        (snapshot.FailureClassificationPending || TerminalLiveFailure(snapshot));

    private void CancelLiveRecovery()
    {
        _liveRecoveryCts?.Cancel();
        _liveNotice = null;
    }

    private static PlaybackSeekRange[] LiveSeekRanges(PlaybackSnapshot snapshot) =>
        snapshot.Loaded && !snapshot.Ended && (snapshot.Seekable || snapshot.PartiallySeekable)
            ? snapshot.SeekableRanges.Where(r => double.IsFinite(r.Start) && double.IsFinite(r.End) && r.Start >= 0 && r.End > r.Start)
                .OrderBy(r => r.Start).Take(64).ToArray()
            : [];

    // Cache ranges are the only established DVR evidence. Do not turn a duration
    // or a seekable flag alone into a promise of server-side DVR support.
    private bool ConfigureLiveSeek(Slider slider, PlaybackSnapshot snapshot)
    {
        if (!snapshot.IsLive) return false;
        var ranges = LiveSeekRanges(snapshot);
        slider.IsEnabled = !AccountWorkBlocked && ranges.Length > 0;
        var minimum = ranges.Length == 0 ? 0 : ranges[0].Start;
        var maximum = ranges.Length == 0 ? 1 : ranges.Max(r => r.End);
        slider.Minimum = minimum;
        slider.Maximum = Math.Max(minimum + 0.001, maximum);
        var dragging = ReferenceEquals(slider, SeekSlider) ? _seekDragging : _popoutSeekDragging;
        if (!dragging && !_pendingSeek.HasValue)
            slider.Value = Math.Clamp(double.IsFinite(snapshot.Position) ? snapshot.Position : minimum, minimum, slider.Maximum);
        slider.ToolTip = ranges.Length > 0
            ? UiText.T("현재 버퍼 범위만 되감기 가능", "Rewind is limited to the current buffer", "現在のバッファ範囲内でのみ巻き戻せます")
            : UiText.T("현재 탐색 가능한 버퍼가 없습니다", "No seekable buffer is available", "現在シーク可能なバッファはありません");
        return true;
    }

    private double? ClampPlaybackSeek(double target)
    {
        if (!double.IsFinite(target) || _snapshot is not { Loaded: true } snapshot || snapshot.Ended) return null;
        if (!snapshot.IsLive)
            return snapshot.Duration > 0 && double.IsFinite(snapshot.Duration) ? Math.Clamp(target, 0, snapshot.Duration) : null;
        var ranges = LiveSeekRanges(snapshot);
        if (ranges.Length == 0) return null;
        foreach (var range in ranges) if (target >= range.Start && target <= range.End) return target;
        return ranges.SelectMany(range => new[] { range.Start, range.End }).MinBy(value => Math.Abs(target - value));
    }

    private async Task SeekRelativeAsync(double seconds)
    {
        if (AccountWorkBlocked) return;
        if (!IsLivePlayback) { await PlayerCommandAsync("seek", seconds, "relative"); return; }
        var basis = _pendingLiveSeek is { } pending && IsCurrentLiveSeek(pending) &&
            _clock.Elapsed.TotalSeconds - _pendingSeekAt <= 5 ? pending.Target : _snapshot?.Position ?? 0;
        if (ClampPlaybackSeek(basis + seconds) is not { } target) return;
        await SeekLiveAsync(target);
    }

    private bool IsCurrentLiveSeek(LiveSeekRequest request) => !AccountWorkBlocked && _playerStarted &&
        _videoCts is null && request.VideoGeneration == _videoGeneration && request.MediaGeneration == _expectedMediaGeneration &&
        request.VideoUrl == _current?.Url && request.Account == RatingAccountStamp &&
        _player.Snapshot.MediaGeneration == request.MediaGeneration && _player.Snapshot.VideoUrl == request.VideoUrl &&
        _snapshot is { Loaded: true, Ended: false } snapshot && snapshot.MediaGeneration == request.MediaGeneration &&
        snapshot.VideoUrl == request.VideoUrl && snapshot.IsLive;

    private void ShowPendingLiveSeek(LiveSeekRequest request)
    {
        _latestLiveSeek = request; _pendingLiveSeek = request; _pendingSeek = request.Target; _pendingSeekAt = _clock.Elapsed.TotalSeconds;
        _pendingLiveSeekTimer.Stop(); _pendingLiveSeekTimer.Start();
        if (!_seekDragging) SeekSlider.Value = Math.Clamp(request.Target, SeekSlider.Minimum, SeekSlider.Maximum);
        if (_popoutSeek is { } popout && !_popoutSeekDragging)
            popout.Value = Math.Clamp(request.Target, popout.Minimum, popout.Maximum);
    }

    private void ClearPendingLiveSeek(LiveSeekRequest? request)
    {
        if (request is null || !ReferenceEquals(request, _pendingLiveSeek)) return;
        _pendingLiveSeek = null; _pendingSeek = null; _pendingLiveSeekTimer.Stop();
        // The reply may have been lost after mpv executed the seek. Restore only
        // the provisional UI from observations; never send a compensating seek.
        if (_snapshot is { } snapshot && IsCurrentLiveSeek(request))
        { ConfigureLiveSeek(SeekSlider, snapshot); UpdatePopoutSnapshot(snapshot); }
    }

    private async Task SeekLiveAsync(double target)
    {
        if (_current is not { } current) return;
        var request = new LiveSeekRequest(target, _videoGeneration, _expectedMediaGeneration, current.Url, RatingAccountStamp);
        if (!IsCurrentLiveSeek(request)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _liveSeekCommands.Add(cts);
        ShowPendingLiveSeek(request);
        var entered = false; var token = cts.Token;
        try
        {
            // The load gate orders all seek submissions against media changes.
            // Rapid input can supersede an unsent request while retaining its target.
            await _videoStartGate.WaitAsync(token); entered = true;
            if (!IsCurrentLiveSeek(request) || !ReferenceEquals(request, _latestLiveSeek)) { ClearPendingLiveSeek(request); return; }
            if (ClampPlaybackSeek(request.Target) is not { } clamped) { ClearPendingLiveSeek(request); return; }
            if (clamped != request.Target) { request = request with { Target = clamped }; ShowPendingLiveSeek(request); }
            await _player.CommandAsync(token, "seek", request.Target, "absolute+exact");
            request.Acknowledged = true;
        }
        catch (OperationCanceledException) { ClearPendingLiveSeek(request); }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            var currentRequest = ReferenceEquals(request, _pendingLiveSeek) && IsCurrentLiveSeek(request);
            ClearPendingLiveSeek(request);
            if (currentRequest) SetStatus(() => UiText.T("탐색 결과를 확인하지 못했습니다. 현재 관측 위치로 표시합니다.",
                "Could not confirm the seek. Showing the current observed position.", "シーク結果を確認できませんでした。現在の観測位置を表示します。"));
        }
        finally { _liveSeekCommands.Remove(cts); if (entered) _videoStartGate.Release(); }
    }

    private void RefreshLiveControls(PlaybackSnapshot? snapshot)
    {
        var status = ConfirmedLiveEndStatus(snapshot) ??
            (snapshot?.LiveStatus is { } observed && observed != "unknown" ? observed : _current?.LiveStatus);
        var live = status == "is_live";
        LiveEdgeButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LiveEdgeButton.IsEnabled = live && CanReloadLive;
        if (_popoutLiveEdge is { } popoutLiveEdge)
        { popoutLiveEdge.Visibility = LiveEdgeButton.Visibility; popoutLiveEdge.IsEnabled = LiveEdgeButton.IsEnabled; }
        LiveState.Text = status switch
        {
            "is_live" => snapshot is not null && LiveSeekRanges(snapshot).Length > 0
                ? UiText.T("● LIVE · 현재 버퍼 범위만 되감기 가능", "● LIVE · Rewind within the current buffer only", "● LIVE · 現在のバッファ内のみ巻き戻し可能")
                : UiText.T("● LIVE · 탐색 범위 미확인", "● LIVE · No verified seek range", "● LIVE · シーク範囲未確認"),
            "is_upcoming" => UiText.T("방송 예정", "Scheduled broadcast", "配信開始前"),
            "post_live" => UiText.T("방송 종료 · 녹화 처리 중", "Broadcast ended · Recording processing", "配信終了・録画処理中"),
            "was_live" => UiText.T("지난 방송", "Past broadcast", "過去の配信"),
            _ => ""
        };
        LiveState.Visibility = LiveState.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!live) SeekSlider.ToolTip = null;
    }

    private async void LiveEdge_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReloadLive || _current is not { } current) return;
        // Opening a fresh live stream reaches the server's current edge even if
        // there is no local DVR cache. The user's pause intent is preserved.
        await PlayVideoAsync(current with { LiveStatus = "is_live" }, true, 0, false);
    }

    private void ObserveLivePlayback(PlaybackSnapshot snapshot)
    {
        if (BlockingLiveFailure(snapshot))
        {
            // A late native classification can arrive after recovery was armed.
            // Keep terminal access failures from reaching the next load boundary.
            if (TerminalLiveFailure(snapshot))
            { CancelLiveRecovery(); if (snapshot.ErrorMessage is { } error) SetStatus(error); }
            return;
        }
        if (!snapshot.Ended || ConfirmedLiveEndStatus(snapshot) is not null || snapshot.EndReason is not ("eof" or "error") || snapshot.AuthenticationRequired ||
            _videoCts is not null || _playPauseIntent?.Paused == true || AccountWorkBlocked || _current is null ||
            _liveRecoveryCts is not null || _liveEndHandledGeneration == _videoGeneration) return;
        _liveEndHandledGeneration = _videoGeneration;
        if (_liveReconnectAttempts >= 3)
        {
            _liveNotice = () => UiText.T("라이브 재연결 3회에 실패했습니다. LIVE 버튼으로 다시 시도하세요.",
                "The live stream could not be restored after 3 attempts. Use LIVE to retry.",
                "ライブの再接続に3回失敗しました。LIVE ボタンで再試行してください。");
            SetStatus(_liveNotice); return;
        }
        _liveRecoveryTask = RecoverLiveAsync(_current, _videoGeneration, _playPauseIntent, RatingAccountStamp);
    }

    private async Task RecoverLiveAsync(VideoItem video, int generation, MpvPlayerService.PauseIntent? intent, string account)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _liveRecoveryCts = cts;
        using var readLifetime = BeginAccountRead();
        bool Current() => !cts.IsCancellationRequested && !AccountWorkBlocked && !BlockingLiveFailure(_snapshot) && generation == _videoGeneration &&
            _current?.Url == video.Url && ReferenceEquals(intent, _playPauseIntent) && intent?.Paused != true && account == RatingAccountStamp;
        try
        {
            while (Current() && _liveReconnectAttempts < 3)
            {
                var attempt = ++_liveReconnectAttempts;
                _liveNotice = () => UiText.F("라이브 연결 확인 중 · 재시도 {0}/3", "Checking live connection · Retry {0}/3", "ライブ接続を確認中・再試行 {0}/3", attempt);
                SetStatus(_liveNotice);
                await Task.Delay(TimeSpan.FromSeconds(new[] { 2, 5, 10 }[attempt - 1]), cts.Token);
                if (!Current()) return;
                try
                {
                    var status = await _youtube.GetPlaybackStatusAsync(video.Url, cts.Token, _settings.Incognito);
                    if (!Current()) return;
                    if (status.HasEnded)
                    {
                        _confirmedLiveEndMediaGeneration = _expectedMediaGeneration;
                        _confirmedLiveEndStatus = status.LiveStatus;
                        ReplaceCurrentMetadata(_current! with { LiveStatus = status.LiveStatus });
                        _liveNotice = () => UiText.T("방송이 종료되었습니다.", "The broadcast has ended.", "配信が終了しました。");
                        SetStatus(_liveNotice); RefreshLiveControls(null);
                        if (_snapshot is { } endedSnapshot && ConfirmedLiveEndStatus(endedSnapshot) is not null)
                        {
                            TimeLabel.Text = VideoItem.FormatTime(endedSnapshot.Position) + " / " + VideoItem.FormatTime(endedSnapshot.Duration);
                            UpdatePopoutSnapshot(endedSnapshot);
                        }
                        if (_settings.AutoPlayNext && _autoplayHandledGeneration != generation)
                        {
                            _autoplayHandledGeneration = generation;
                            // Schedule after this recovery scope unwinds so its cancellation
                            // and read lifetime cannot wait on itself during the next load.
                            _ = Dispatcher.BeginInvoke(() => _ = AutoPlayNextAsync(generation, _autoplayEpoch));
                        }
                        return;
                    }
                    if (!status.IsLive) break; // Unknown/upcoming is not proof of normal EOF.
                    if (!Current()) return;
                    var restored = await PlayVideoCoreAsync(_current! with { LiveStatus = "is_live" }, true, 0, false, cts.Token);
                    if (cts.IsCancellationRequested || AccountWorkBlocked || !ReferenceEquals(intent, _playPauseIntent) || account != RatingAccountStamp) return;
                    generation = _videoGeneration;
                    if (_snapshot?.AuthenticationRequired == true || _authenticationNoticeGeneration == generation) return;
                    if (_lastPlaybackAccessCode is { } access && access is not ("network" or "generic")) return;
                    var native = _player.Snapshot;
                    if (restored && !(native.MediaGeneration == _expectedMediaGeneration && native.Ended))
                    { _liveNotice = null; return; }
                }
                catch (YouTubeAccessException error) when (error.AuthenticationRequired)
                { if (Current()) HandleAuthenticationRequired(); return; }
                catch (YouTubeAccessException error) when (error.Code is not ("network" or "generic"))
                { if (Current()) { _liveNotice = () => error.Message; SetStatus(_liveNotice); } return; }
                catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
                {
                    if (Current())
                    {
                        _liveNotice = () => UiText.T("라이브 응답 또는 세션을 확인하지 못해 재연결을 중단했습니다. LIVE 버튼으로 다시 시도하세요.",
                            "Live metadata or session verification failed. Reconnection stopped. Use LIVE to retry.",
                            "ライブ情報またはセッションを確認できないため再接続を停止しました。LIVE ボタンで再試行してください。");
                        SetStatus(_liveNotice);
                    }
                    return;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or Win32Exception or System.Text.Json.JsonException)
                { /* A failed bounded attempt is not evidence that the broadcast ended. */ }
            }
            if (Current())
            {
                _liveNotice = () => UiText.T("라이브를 복구하지 못했습니다. 방송 상태를 확인한 뒤 LIVE 버튼으로 다시 시도하세요.",
                    "Could not restore the live stream. Check the broadcast, then use LIVE to retry.",
                    "ライブを復旧できませんでした。配信状態を確認し、LIVE ボタンで再試行してください。");
                SetStatus(_liveNotice);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            var released = ReferenceEquals(_liveRecoveryCts, cts);
            if (released) _liveRecoveryCts = null;
            // An end notification can be observed while the recovery owns its
            // token. Replay that state once after releasing the owner; this is
            // still bounded by the same three-attempt budget.
            // A canceled older task may have temporarily blocked a newer live
            // generation. Re-observe that current owner, but do not re-arm this
            // same generation after a manual Next/pause canceled its recovery.
            if (released && (Current() || generation != _videoGeneration && _activePlaybackRecoveryToken != cts.Token) &&
                _snapshot is { IsLive: true, Ended: true } latest && latest.MediaGeneration == _expectedMediaGeneration &&
                latest.VideoUrl == _current?.Url && ConfirmedLiveEndStatus(latest) is null)
                ObserveLivePlayback(latest);
        }
    }
}
