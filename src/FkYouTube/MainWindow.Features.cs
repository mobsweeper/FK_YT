using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private Func<string>? _qualityUiText;
    private void SetQualityUiText(Func<string> text)
    { _qualityUiText=text;QualityState.Text=text(); }
    private void RefreshFeatureUiText()
    { if(_qualityUiText is {} text)QualityState.Text=text();RefreshDownloadUiText(); }

    private CookieLease AcquirePlaybackSession()=>_settings.Incognito?new CookieLease(null):_session.Acquire();

    private async Task AutoPlayNextAsync(int videoGeneration,int epoch,bool manual=false)
    {
        var feedGeneration=_playingListGeneration;
        if(AccountWorkBlocked || (!manual && !_settings.AutoPlayNext) || epoch!=_autoplayEpoch ||
            videoGeneration!=_videoGeneration || feedGeneration!=_feedGeneration || PlayerPanel.Visibility!=Visibility.Visible)return;
        var intent=++_autoplayIntentRevision;
        NotifyPlaybackQueueChanged();
        var accountToken=_accountCts.Token;
        bool IsCurrent()=>!AccountWorkBlocked && (manual || _settings.AutoPlayNext) && epoch==_autoplayEpoch && videoGeneration==_videoGeneration &&
            intent==_autoplayIntentRevision && feedGeneration==_feedGeneration && PlayerPanel.Visibility==Visibility.Visible;
        if(!IsCurrent())return;
        try
        {
        for(var page=0;page<=4 && IsCurrent();page++)
        {
            var changed=_playbackQueueChanged.Task;
            var fromFeed=_playingIndex>=0 && _playingListGeneration==_feedGeneration;
            var next=fromFeed?Playback.PlaybackQueue.NextIndex(_items,_playingIndex,_settings.HideMembersOnly):-1;
            if(next>=0)
            {
                if(!IsCurrent())return;
                await PlayVideoAsync(_items[next],explicitStart:0);
                return;
            }
            // Use an already delivered candidate immediately; wait only when it is missing.
            if(fromFeed && _loading){await Task.WhenAny(_feedLoadTask,changed).WaitAsync(accountToken);page--;continue;}
            if(!fromFeed || !_hasFeed || !_hasMore || page==4)break;
            await Task.WhenAny(FetchBatchAsync(false),changed).WaitAsync(accountToken);
        }
        if(!IsCurrent())return;
        if(_playingIndex>=0 && _hasFeed && !_feedExhausted){SetStatus(()=>UiText.T("다음 재생 가능한 영상을 찾지 못했습니다. 목록을 새로고침하거나 직접 선택해 주세요.","Could not find the next playable video. Refresh the list or choose a video.","次に再生できる動画が見つかりませんでした。一覧を更新するか、動画を選択してください。"));return;}
        while(IsCurrent())
        {
            var changed=_playbackQueueChanged.Task;
            // The load starts related metadata only after native playback succeeds.
            // Keep an early EOF's intent until that request is assigned or loading ends.
            if(!_relatedRequested)
            {
                if(_videoCts is null)break;
                await changed.WaitAsync(accountToken);continue;
            }
            var pendingRelated=_relatedTask;
            await Task.WhenAny(pendingRelated,changed).WaitAsync(accountToken);
            if(!IsCurrent())return;
            // Refresh replaces both the task and CTS. Follow the newest request,
            // including when the canceled old request has already completed.
            if(!ReferenceEquals(pendingRelated,_relatedTask) || !pendingRelated.IsCompleted || _relatedCts is not null)continue;
            await pendingRelated;break;
        }
        if(!IsCurrent())return;UpdateUpNext();
        if(_upNext.FirstOrDefault() is {} related)await PlayVideoAsync(related,explicitStart:0);
        else SetStatus(()=>UiText.T("목록의 다음 재생 가능한 영상이 없습니다.","There are no more playable videos in the list.","一覧に次に再生できる動画がありません。"));
        }
        catch(OperationCanceledException)when(accountToken.IsCancellationRequested || !IsCurrent()){}
    }

    private void AutoPlay_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        _autoplayEpoch++;_settings.AutoPlayNext=AutoPlayCheck.IsChecked==true;TrySaveSettings();
        NotifyPlaybackQueueChanged();
        if(_authenticationNoticeGeneration==_videoGeneration)
        {if(_settings.AutoPlayNext)StartAuthenticationSkip();else CancelAuthenticationSkip();}
        if(!_settings.AutoPlayNext){_prefetchCts?.Cancel();_prefetchAttempt=null;_preparedItem=null;SetPrefetchUiText(()=>UiText.T("자동재생 꺼짐","Autoplay off","自動再生オフ"));_=ClearPrefetchSafelyAsync();}
        else{SetPrefetchUiText(()=>"");_=EnsurePrefetchAsync();}
    }
    private async void HideMembers_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        _settings.HideMembersOnly=HideMembersCheck.IsChecked==true;TrySaveSettings();
        if(_hasFeed){var view=++_viewGeneration;var source=_source;var title=_browseTitle;if(await StopPlaybackAsync() && !_closing && view==_viewGeneration)await OpenFeedAsync(source,title);}
    }
    private async void Incognito_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready || AccountWorkBlocked)return;
        var enabled=IncognitoCheck.IsChecked==true;
        var openHome=false;
        SetAccountBusy(true);
        try
        {
            // Drain/cancel the old account's work before admitting any private playback.
            if(enabled)PauseHistoryOutbox();
            await ResetAccountAsync();
            _settings.Incognito=enabled;_qualityKey="";_lastSavedKey="";
            Controls.ThumbnailImage.Cache?.SetPrivateMode(enabled);
            var saved=TrySaveSettings();UpdateSessionLabel();
            if(enabled)
            {
                ShowIncognitoLanding();
            }
            else openHome=_session.IsConnected;
            if(!saved)SetStatus(()=>UiText.T("시크릿 모드 변경은 현재 실행에 적용했습니다. 설정 파일 저장은 실패했으므로 다음 실행 때 다시 확인해 주세요.","Incognito mode was changed for this session. Settings could not be saved; check the mode again next time.","シークレットモードの変更を今回の起動中に適用しました。設定の保存に失敗したため、次回起動時にもう一度確認してください。"));
        }
        catch(OperationCanceledException)when(_closing){}
        catch(Exception error)when(IsStorageError(error) || error is InvalidOperationException or InvalidDataException)
        {SetStatus(()=>UiText.T("모드 전환을 완료하지 못했습니다. 현재 적용된 모드 표시를 유지합니다. 저장소 또는 팝아웃 상태를 확인해 주세요.","Could not finish switching modes. The active mode is still shown. Check storage or the popout window.","モードの切り替えを完了できませんでした。現在適用されているモードの表示を維持します。保存先または別ウィンドウの状態を確認してください。"));}
        finally{IncognitoCheck.IsChecked=_settings.Incognito;SetAccountBusy(false);UpdateSessionLabel();}
        if(openHome && !AccountWorkBlocked && !_settings.Incognito)await OpenFeedAsync("home",UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));
    }
    private void ShowIncognitoLanding()
    {
        ShowPanel("browse");SetPageUiText(()=>UiText.T("시크릿 모드","Incognito mode","シークレットモード"));WelcomePanel.Visibility=Visibility.Collapsed;
        MoreButton.Visibility=Visibility.Collapsed;HistoryActions.Visibility=Visibility.Collapsed;
        SetFeedUiText(()=>UiText.T("검색어·영상·공개 재생목록 주소로 시작하세요.","Start with a search, video URL, or public playlist URL.","検索語・動画 URL・公開再生リスト URL を入力してください。"));
        SetStatus(()=>UiText.T("로그인 쿠키를 사용하지 않으며, YouTube와 앱에 시청 기록·이어보기 위치를 저장하지 않습니다.","Login cookies are not used. Watch history and resume positions are not saved to YouTube or the app.","ログイン Cookie を使用せず、YouTube とアプリに視聴履歴・再生位置を保存しません。"));
    }

    private void ResetQualityDisplay(VideoItem item)
    {
        if(_qualityKey==$"{_settings.AccountKey}:{_settings.Incognito}:{item.Id}" && _resolutions.Count>0){DisplayResolutions();return;}
        _updatingQuality=true;QualityCombo.ItemsSource=null;QualityCombo.IsEnabled=false;_updatingQuality=false;
        QualityCombo.Visibility=Visibility.Collapsed;QualityLoadButton.Visibility=Visibility.Visible;QualityLoadButton.IsEnabled=true;
        SetQualityUiText(()=>_settings.MaxHeight==0?UiText.T("최고 화질 (자동)","Highest quality (Auto)","最高画質（自動）"):UiText.F("최대 {0}p","Up to {0}p","最大 {0}p",_settings.MaxHeight));
    }
    private async void QualityLoad_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _current==null || _qualityCts is {IsCancellationRequested:false})return;
        var generation=_videoMetadataGeneration;
        await LoadResolutionsAsync(_current,generation);
        if(!_closing && generation==_videoMetadataGeneration && QualityCombo.IsEnabled)QualityCombo.IsDropDownOpen=true;
    }
    private async Task LoadResolutionsAsync(VideoItem item,int generation)
    {
        using var readLifetime=BeginAccountRead();
        var key=$"{_settings.AccountKey}:{_settings.Incognito}:{item.Id}";
        if(key==_qualityKey && _resolutions.Count>0){DisplayResolutions();return;}
        _qualityCts?.Cancel();using var cts=new CancellationTokenSource();_qualityCts=cts;QualityLoadButton.IsEnabled=false;
        _updatingQuality=true;QualityCombo.ItemsSource=null;QualityCombo.IsEnabled=false;SetQualityUiText(()=>UiText.T("화질 조회 중…","Checking video quality…","画質を確認中…"));_updatingQuality=false;
        try
        {
            var resolutions=await _youtube.GetResolutionsAsync(item.Url,cts.Token,_settings.Incognito);
            if(_closing || cts.IsCancellationRequested || generation!=_videoMetadataGeneration)return;
            _qualityKey=key;_resolutions=resolutions;DisplayResolutions();
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or JsonException or InvalidDataException or Win32Exception)
        {if(!_closing && generation==_videoMetadataGeneration && !cts.IsCancellationRequested)SetQualityUiText(()=>UiText.T("화질 조회 실패 · 재시도","Quality lookup failed · Retry","画質取得失敗 · 再試行"));}
        finally{if(ReferenceEquals(_qualityCts,cts)){_qualityCts=null;QualityLoadButton.IsEnabled=true;}}
    }
    private void DisplayResolutions()
    {
        _updatingQuality=true;
        try
        {
            QualityCombo.ItemsSource=_resolutions.Count>0?_resolutions.Prepend(0).ToArray():_resolutions;
            if(_resolutions.Count>0)
            {
                QualityCombo.SelectedItem=_settings.MaxHeight==0?0:_resolutions.Where(h=>h<=_settings.MaxHeight).Select(h=>(int?)h).Max();
                SetQualityUiText(()=>QualityCombo.SelectedItem is null?UiText.F("최대 {0}p","Up to {0}p","最大 {0}p",_settings.MaxHeight):"");QualityCombo.IsEnabled=true;
                QualityCombo.Visibility=Visibility.Visible;QualityLoadButton.Visibility=Visibility.Collapsed;
            }
            else{SetQualityUiText(()=>UiText.T("제공되는 영상 화질 정보 없음","No video quality information available","利用できる画質情報がありません"));QualityCombo.IsEnabled=false;}
        }
        finally{_updatingQuality=false;}
    }
}
