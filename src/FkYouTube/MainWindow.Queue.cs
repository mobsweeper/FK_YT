using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FkYouTube.Services;
using FkYouTube.Playback;

namespace FkYouTube;

public partial class MainWindow
{
    private readonly ObservableCollection<VideoItem> _upNext=[];
    private readonly ObservableCollection<VideoItem> _relatedItems=[];
    private readonly HashSet<string> _autoplaySeen=[];
    private CancellationTokenSource? _relatedCts,_prefetchCts,_captionCts;
    private Task _relatedTask=Task.CompletedTask;
    private VideoItem? _prefetchAttempt,_preparedItem;
    private bool _prefetchAudioOnly,_preparedAudioOnly;
    private IReadOnlyList<int> _preparedResolutions=[];
    private IReadOnlyList<PreparedCaption> _preparedCaptions=[];
    private bool _commentsRequested,_relatedRequested;
    private int _nextPageAttempts;
    private readonly List<VideoItem> _playbackTrail=[];
    private int _playbackTrailRevision;
    private CancellationTokenSource? _authenticationSkipCts;
    private Task _authenticationSkipTask=Task.CompletedTask;
    private int _authenticationNoticeGeneration=-1;
    private TaskCompletionSource _playbackQueueChanged=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _autoplayIntentRevision;
    private const int MaxPreparedCaptionRequests=3;

    private VideoItem ReplaceCurrentMetadata(VideoItem updated)
    {
        var previous=_current;
        _current=updated;
        // Metadata versions belong to one exact occurrence, even when the
        // feed deliberately contains several records for the same video ID.
        for(var index=0;index<_items.Count;index++)if(ReferenceEquals(_items[index],previous))_items[index]=updated;
        foreach(var row in _rows)for(var index=0;index<row.Items.Count;index++)if(ReferenceEquals(row.Items[index],previous))row.Items[index]=updated;
        for(var index=0;index<_playbackTrail.Count;index++)if(ReferenceEquals(_playbackTrail[index],previous))_playbackTrail[index]=updated;
        return updated;
    }

    private void NotifyPlaybackQueueChanged()
    {
        var previous=_playbackQueueChanged;
        _playbackQueueChanged=new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private Func<string>? _prefetchUiText, _relatedUiText;
    private void SetPrefetchUiText(Func<string> text)
    { _prefetchUiText=text;PrefetchState.Text=text(); }
    private void SetRelatedUiText(Func<string> text)
    { _relatedUiText=text;RelatedState.Text=text(); }

    private void CancelAuthenticationSkip(bool clearNotice=false)
    {
        if(_authenticationSkipCts is {IsCancellationRequested:false} pending){pending.Cancel();_autoplayEpoch++;}
        if(clearNotice){_authenticationNoticeGeneration=-1;HideAuthenticationNotice();}
        else if(_authenticationNoticeGeneration==_videoGeneration)
            ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 다음 영상 자동재생을 취소했습니다.","Authenticated cookies are required. Autoplay for the next video was canceled.","認証済みの Cookie が必要です。次の動画の自動再生をキャンセルしました。"),false);
    }

    private void HandleAuthenticationRequired()
    {
        if(_closing || _ratingsAccountBusy || _current is null || PlayerPanel.Visibility!=Visibility.Visible ||
            _authenticationNoticeGeneration==_videoGeneration)return;
        _authenticationNoticeGeneration=_videoGeneration;
        if(IsLivePlayback)
        {
            CancelLiveRecovery();
            ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 라이브 재연결을 중단했습니다. 일반 모드에서 인증된 계정을 연결하세요.",
                "Authenticated cookies are required. Live reconnection stopped. Connect an authenticated account in normal mode.",
                "認証済みの Cookie が必要です。ライブの再接続を停止しました。通常モードで認証済みアカウントを接続してください。"),false);
            return;
        }
        if(_settings.AutoPlayNext)StartAuthenticationSkip();
        else ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 일반 모드에서 인증된 계정을 연결하세요. 자동재생은 꺼져 있습니다.","Authenticated cookies are required. Connect an authenticated account in normal mode. Autoplay is off.","認証済みの Cookie が必要です。通常モードで認証済みのアカウントを接続してください。自動再生はオフです。"),false);
    }

    private void StartAuthenticationSkip()
    {
        if(IsLivePlayback || _closing || _ratingsAccountBusy || !_settings.AutoPlayNext || _current is null ||
            _authenticationNoticeGeneration!=_videoGeneration)return;
        _authenticationSkipCts?.Cancel();
        var cts=CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _authenticationSkipCts=cts;
        _authenticationSkipTask=RunAuthenticationSkipAsync(_videoGeneration,_autoplayEpoch,_viewGeneration,RatingAccountStamp,cts);
    }

    private async Task RunAuthenticationSkipAsync(int generation,int epoch,int view,string account,CancellationTokenSource cts)
    {
        bool Current()=>!IsLivePlayback && !_closing && !_ratingsAccountBusy && !cts.IsCancellationRequested && _settings.AutoPlayNext &&
            generation==_videoGeneration && epoch==_autoplayEpoch && view==_viewGeneration && account==RatingAccountStamp &&
            _authenticationNoticeGeneration==generation && PlayerPanel.Visibility==Visibility.Visible;
        try
        {
            for(var seconds=7;seconds>0;seconds--)
            {
                if(!Current())return;
                var remainingSeconds=seconds;
                ShowAuthenticationNotice(()=>UiText.F("인증이 완료된 쿠키가 필요합니다. {0}초 후 다음 영상을 재생합니다.","Authenticated cookies are required. Playing the next video in {0} seconds.","認証済みの Cookie が必要です。{0} 秒後に次の動画を再生します。",remainingSeconds),true);
                await Task.Delay(TimeSpan.FromSeconds(1),cts.Token);
            }
            if(!Current())return;
            ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 다음 영상을 확인하는 중입니다…","Authenticated cookies are required. Checking the next video…","認証済みの Cookie が必要です。次の動画を確認中…"),true);
            await AutoPlayNextAsync(generation,epoch);
            if(Current())ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 다음 재생할 영상이 없거나 목록을 확인하지 못했습니다.","Authenticated cookies are required. There is no next video, or the list could not be checked.","認証済みの Cookie が必要です。次に再生する動画がないか、一覧を確認できませんでした。"),false);
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or InvalidOperationException or TimeoutException or Win32Exception)
        {if(Current())ShowAuthenticationNotice(()=>UiText.T("인증이 완료된 쿠키가 필요합니다. 다음 영상을 열지 못했습니다. 목록에서 직접 선택해 주세요.","Authenticated cookies are required. Could not open the next video. Select one from the list.","認証済みの Cookie が必要です。次の動画を開けませんでした。一覧から選択してください。"),false);}
        finally{if(ReferenceEquals(_authenticationSkipCts,cts))_authenticationSkipCts=null;cts.Dispose();}
    }

    private async void NextVideo_Click(object sender,RoutedEventArgs e)
    {
        CancelLiveRecovery();
        CancelAuthenticationSkip(clearNotice:true);
        if(_current!=null && !_closing)await AutoPlayNextAsync(_videoGeneration,_autoplayEpoch,true);
    }
    private VideoItem? PreviousVideo()
    {
        var prior=_playbackTrail.LastOrDefault(v=>!IsDismissed(v) && (!_settings.HideMembersOnly || !v.MembersOnly));
        if(prior!=null)return prior;
        if(_playingIndex>=0 && _playingListGeneration==_feedGeneration)
        {
            for(var index=_playingIndex-1;index>=0;index--)
                if(_items[index].Kind=="video" && (!_settings.HideMembersOnly || !_items[index].MembersOnly) && !IsDismissed(_items[index]))return _items[index];
            return null;
        }
        return null;
    }
    private async void PreviousVideo_Click(object sender,RoutedEventArgs e)=>await PreviousVideoAsync();
    private async Task PreviousVideoAsync()
    {
        if(_closing || PreviousVideo() is not {} previous)return;
        _autoplayIntentRevision++;NotifyPlaybackQueueChanged();
        var trailRevision=_playbackTrailRevision;var trailCount=_playbackTrail.Count;
        var trailIndex=_playbackTrail.FindLastIndex(v=>ReferenceEquals(v,previous));
        var accepted=await PlayVideoAsync(previous,explicitStart:0,rememberHistory:false);
        // Metadata may replace references while the accepted load is awaited.
        // Only structural history edits invalidate this navigation's boundary.
        if(accepted && trailIndex>=0 && _playbackTrailRevision==trailRevision && _playbackTrail.Count==trailCount)
        {_playbackTrail.RemoveRange(trailIndex,_playbackTrail.Count-trailIndex);_playbackTrailRevision++;}
        UpdateSkipButtons();
    }
    private void UpdateSkipButtons()
    {
        PreviousVideoButton.IsEnabled=_current!=null && PreviousVideo()!=null;
        NextVideoButton.IsEnabled=_current!=null && (_upNext.Count>0 || _relatedCts!=null || (_playingIndex>=0 && _hasMore));
        UpdatePopoutSkipButtons();
    }

    private void ResetPlaybackExtras(bool keepPrepared=false,bool preserveMetadata=false)
    {
        _subtitleLoadError=null;_subtitleLoadErrorUiText=null;
        CancelAuthenticationSkip(clearNotice:true);
        CancelSubtitleOperations();
        CloseDescriptionPopup();
        _prefetchCts?.Cancel();_captionCts?.Cancel();
        _prefetchAttempt=null;_preparedItem=null;_preparedResolutions=[];_preparedCaptions=[];
        SetPrefetchUiText(()=>"");
        if(!preserveMetadata)
        {
            ResetSubtitleCatalog();
            ResetVideoDescription();
            ResetVideoRatings();
            _relatedCts?.Cancel();_relatedItems.Clear();_upNext.Clear();_commentsRequested=false;_relatedRequested=false;_nextPageAttempts=0;
            _relatedTask=Task.CompletedTask;
            SetCommentUiText(()=>UiText.T("댓글 보기를 누르면 불러옵니다.","Click Comments to load them.","「コメント」を押すと読み込みます。"));SelectPlayerSide("next");
        }
        if(!keepPrepared && _playerStarted)_=ClearPrefetchSafelyAsync();
        NotifyPlaybackQueueChanged();
    }
    private async Task ClearPrefetchSafelyAsync()
    {
        try{await _player.ClearPreparedNextAsync();}
        catch(Exception ex)when(ex is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException){}
    }
    private void SelectPlayerSide(string panel)
    {
        NextBody.Visibility=panel=="next"?Visibility.Visible:Visibility.Collapsed;
        RelatedBody.Visibility=panel=="related"?Visibility.Visible:Visibility.Collapsed;
        CommentsBody.Visibility=panel=="comments"?Visibility.Visible:Visibility.Collapsed;
        NextTab.Opacity=panel=="next"?1:.65;RelatedTab.Opacity=panel=="related"?1:.65;CommentsTab.Opacity=panel=="comments"?1:.65;
    }
    private void NextTab_Click(object sender,RoutedEventArgs e)=>SelectPlayerSide("next");
    private void RelatedTab_Click(object sender,RoutedEventArgs e)=>SelectPlayerSide("related");
    private async void CommentsTab_Click(object sender,RoutedEventArgs e)
    {
        SelectPlayerSide("comments");
        if(!_commentsRequested){_commentsRequested=true;await LoadCommentsAsync();}
    }
    private async void ReloadComments_Click(object sender,RoutedEventArgs e){_commentsRequested=true;await LoadCommentsAsync();}
    private async void SideVideo_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button{Tag:VideoItem item})await PlayVideoAsync(item,explicitStart:0);
    }
    private async void PlayList_Click(object sender,RoutedEventArgs e)=>await PlayListAsync();
    private async Task PlayListAsync()
    {
        var generation=_feedGeneration;
        if(_loading && Playback.PlaybackQueue.NextIndex(_items,-1,_settings.HideMembersOnly)<0)await _feedLoadTask;
        if(_closing || generation!=_feedGeneration)return;
        var first=Playback.PlaybackQueue.NextIndex(_items,-1,_settings.HideMembersOnly);
        if(first<0){SetStatus(()=>UiText.T("재생할 영상이 아직 없습니다. 목록을 다시 불러와 주세요.","There are no videos to play yet. Reload the list.","再生する動画がまだありません。一覧を読み込み直してください。"));return;}
        await PlayVideoAsync(_items[first],explicitStart:0);
    }
    private async Task LoadRelatedAsync(VideoItem item,int generation)
    {
        if(AccountWorkBlocked)return;
        using var readLifetime=BeginAccountRead();
        var ratingRevision=RelatedRatingRevision;var ratingAccount=RatingAccountStamp;
        _relatedRequested=true;
        _relatedCts?.Cancel();using var cts=new CancellationTokenSource();_relatedCts=cts;
        NotifyPlaybackQueueChanged();
        UpdateSkipButtons();
        SetRelatedUiText(()=>UiText.T("YouTube 관련 추천을 불러오는 중…","Loading related YouTube recommendations…","YouTube の関連するおすすめを読み込み中…"));
        try
        {
            var result=await _youtube.GetRelatedPageAsync(item.Url,cts.Token,_settings.Incognito);var items=result.Items;
            if(_closing || cts.IsCancellationRequested || generation!=_videoMetadataGeneration || _current?.Id!=item.Id)return;
            ApplyVideoRating(result.Rating,item,generation,ratingAccount,ratingRevision);
            if(!_ratingsAccountBusy && ratingAccount==RatingAccountStamp)SetVideoDescription(result.Description??string.Empty);
            if(result.Channel is {} owner && YouTubeService.TryChannelUrl(owner.Url,out var channelUrl))
            {
                var previous=_current;
                ReplaceCurrentMetadata(previous with{Channel=string.IsNullOrWhiteSpace(owner.Title)?previous.Channel:owner.Title,
                    ChannelId=string.IsNullOrWhiteSpace(owner.Id)?previous.ChannelId:owner.Id,ChannelUrl=channelUrl,ChannelThumbnail=owner.Thumbnail??previous.ChannelThumbnail});
                _lastSavedKey="";UpdatePlayingChannel(_current);
            }
            _relatedItems.Clear();var seen=new HashSet<string>();
            foreach(var video in items.Where(v=>v.Kind=="video" && v.Id!=item.Id && (!_settings.HideMembersOnly || !v.MembersOnly) && !IsDismissed(v)))
                if(seen.Add(video.Id))_relatedItems.Add(video);
            SetRelatedUiText(()=>_relatedItems.Count>0?UiText.F("YouTube 관련 추천 · {0}개","Related YouTube recommendations · {0}","YouTube の関連するおすすめ · {0} 件",_relatedItems.Count):UiText.T("YouTube가 관련 추천을 제공하지 않았습니다.","YouTube did not provide related recommendations.","YouTube から関連するおすすめが提供されませんでした。"));
            UpdateUpNext();
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or JsonException or InvalidDataException or InvalidOperationException or Win32Exception)
        {if(!_closing && !cts.IsCancellationRequested && generation==_videoMetadataGeneration){SetRelatedUiText(()=>UiText.T("관련 추천을 불러오지 못했습니다. 상단 새로고침으로 다시 시도하세요.","Could not load related recommendations. Use Refresh at the top to retry.","関連するおすすめを読み込めませんでした。上部の更新ボタンで再試行してください。"));if(!_ratingsAccountBusy && ratingAccount==RatingAccountStamp && _current?.Id==item.Id)SetVideoDescription(null,failed:true);UpdateUpNext();}}
        finally{if(ReferenceEquals(_relatedCts,cts)){_relatedCts=null;if(!_closing && generation==_videoMetadataGeneration)UpdateUpNext();}NotifyPlaybackQueueChanged();}
    }
    private void UpdateUpNext()
    {
        NotifyPlaybackQueueChanged();
        if(_current==null)return;
        var items=new List<VideoItem>();
        var fromFeed=_playingIndex>=0 && _playingListGeneration==_feedGeneration;
        if(fromFeed)
            items.AddRange(_items.Skip(_playingIndex+1).Where(v=>v.Kind=="video" && (!_settings.HideMembersOnly || !v.MembersOnly) && !IsDismissed(v)).Take(20));
        // A playlist keeps its order (including intentional duplicates) through its last page.
        if(items.Count==0 && (!fromFeed || !_hasFeed || _feedExhausted))
            items.AddRange(_relatedItems.Where(v=>v.Id!=_current.Id && !_autoplaySeen.Contains(v.Id)).Take(20));
        _upNext.Clear();foreach(var item in items)_upNext.Add(item);
        UpdateSkipButtons();
        RefreshQueueUiText();
        if(items.Count==0 && fromFeed && _hasFeed && _hasMore && !_loading && _nextPageAttempts<4)
        {_nextPageAttempts++;_=FetchBatchAsync(false);}
        if(_videoCts==null && (!ReferenceEquals(_prefetchAttempt,_upNext.FirstOrDefault()) || _prefetchAudioOnly!=_settings.AudioOnly))
        {
            _prefetchCts?.Cancel();_prefetchAttempt=null;_preparedItem=null;_preparedResolutions=[];_preparedCaptions=[];SetPrefetchUiText(()=>"");
            if(_playerStarted)_=ClearPrefetchSafelyAsync();
        }
        _=EnsurePrefetchAsync();
    }
    private void RefreshQueueUiText()
    {
        if(_prefetchUiText is {} prefetchText)PrefetchState.Text=prefetchText();
        if(_relatedUiText is {} relatedText)RelatedState.Text=relatedText();
        if(_current==null)return;
        var items=_upNext;
        var fromFeed=_playingIndex>=0 && _playingListGeneration==_feedGeneration;
        NextState.Text=items.Count>0?(fromFeed && _items.Contains(items[0])?UiText.T("현재 목록 순서대로 재생 · 맨 위가 다음 영상","Playing in list order · The top video is next","現在の一覧順に再生 · 一番上が次の動画"):UiText.T("관련 추천에서 이어서 재생 · 맨 위가 다음 영상","Continue with related recommendations · The top video is next","関連するおすすめから続けて再生 · 一番上が次の動画")):
            fromFeed && _hasMore?UiText.T("목록의 다음 페이지를 확인하는 중…","Checking the next page of the list…","一覧の次のページを確認中…"):fromFeed && _hasFeed && !_feedExhausted?UiText.T("다음 목록 확인에 실패했습니다. 목록으로 돌아가 새로고침해 주세요.","Could not check the next page. Return to the list and refresh it.","次の一覧を確認できませんでした。一覧に戻って更新してください。"):_relatedCts!=null?UiText.T("관련 추천을 불러오는 중…","Loading related recommendations…","関連するおすすめを読み込み中…"):UiText.T("다음 재생할 영상이 없습니다.","There is no next video to play.","次に再生する動画がありません。");
    }

    private async Task EnsurePrefetchAsync()
    {
        if(AccountWorkBlocked || _current?.LiveStatus=="is_live" || _snapshot?.IsLive==true || SubtitlePreferencePending || !_settings.AutoPlayNext || _videoCts!=null || _snapshot?.Loaded!=true || _snapshot.Duration<=0 || _snapshot.Duration-_snapshot.Position>60)return;
        var candidate=_upNext.FirstOrDefault();
        if(candidate==null || candidate.LiveStatus is "is_live" or "is_upcoming" or "post_live" ||
            ReferenceEquals(candidate,_prefetchAttempt) && _prefetchAudioOnly==_settings.AudioOnly)return;
        using var readLifetime=BeginAccountRead();
        _prefetchCts?.Cancel();using var cts=new CancellationTokenSource();_prefetchCts=cts;_prefetchAttempt=candidate;
        var generation=_videoGeneration;var mediaGeneration=_expectedMediaGeneration;var height=_settings.MaxHeight;
        var audioOnly=_settings.AudioOnly;_prefetchAudioOnly=audioOnly;
        var preferenceRevision=_subtitlePreferenceRevision;var language=_player.PreferredSubtitleLanguage;
        SetPrefetchUiText(()=>UiText.T("다음 영상 미리 준비 중…","Preparing the next video…","次の動画を準備中…"));
        bool Current()=>!AccountWorkBlocked && _current?.LiveStatus!="is_live" && _snapshot?.IsLive!=true &&
            !cts.IsCancellationRequested && generation==_videoGeneration &&
            mediaGeneration==_expectedMediaGeneration && preferenceRevision==_subtitlePreferenceRevision && audioOnly==_settings.AudioOnly &&
            _settings.AutoPlayNext && ReferenceEquals(candidate,_upNext.FirstOrDefault());
        try
        {
            var prepared=await _youtube.ResolveNextAsync(candidate.Url,height,cts.Token,_settings.Incognito,language,audioOnly);
            if(!Current())return;
            var ready=await _player.PrepareNextAsync(candidate.Url,prepared.MediaUrl,mediaGeneration,height,prepared.Headers,cts.Token,prepared.HttpChunkSize,audioOnly);
            if(Current())
            {
                if(ready){_preparedItem=candidate;_preparedAudioOnly=audioOnly;_preparedResolutions=prepared.Resolutions;_preparedCaptions=audioOnly?[]:prepared.Captions;SetPrefetchUiText(()=>UiText.T("다음 영상 준비됨 · 영상 끝부분에서 미리 버퍼링","Next video ready · Buffering near the end of this video","次の動画の準備完了 · この動画の終わり頃に先行バッファリング"));}
                else SetPrefetchUiText(()=>UiText.T("다음 영상은 일반 방식으로 이어 재생합니다.","The next video will continue with normal playback.","次の動画は通常の方法で続けて再生します。"));
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or JsonException or InvalidDataException or InvalidOperationException or TimeoutException or Win32Exception)
        {if(Current())
            {
                var playerCleared=_player.Snapshot is{Ended:true,ErrorMessage:not null};
                SetPrefetchUiText(()=>playerCleared?UiText.T("플레이어가 정리되었습니다. 현재 영상을 다시 재생해 주세요.","The player was cleared. Play the current video again.","再生機が終了しました。現在の動画を再生し直してください。"):UiText.T("다음 영상은 일반 방식으로 이어 재생합니다.","The next video will continue with normal playback.","次の動画は通常の方法で続けて再生します。"));
            }
        }
        finally{if(ReferenceEquals(_prefetchCts,cts))_prefetchCts=null;}
    }
    private async Task LoadPreparedCaptionsAsync(IReadOnlyList<PreparedCaption> captions,int generation)
    {
        if(generation!=_videoGeneration || AccountWorkBlocked || _activeAudioOnly || SubtitlePreferencePending || _player.SubtitlesOff)return;
        _subtitleLoadError=null;_subtitleLoadErrorUiText=null;
        _captionCts?.Cancel();using var cts=CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);_captionCts=cts;
        var ct=cts.Token;var mediaGeneration=_expectedMediaGeneration;var selectionRevision=_subtitleSelectionRevision;
        var preferred=_player.PreferredSubtitleLanguage;
        bool Current()=>!AccountWorkBlocked && !ct.IsCancellationRequested && ReferenceEquals(_captionCts,cts) &&
            selectionRevision==_subtitleSelectionRevision && !SubtitlePreferencePending &&
            !_activeAudioOnly && generation==_videoGeneration && mediaGeneration==_expectedMediaGeneration && _player.Snapshot.MediaGeneration==mediaGeneration;
        try
        {
            var tracks=await _player.GetPropertyAsync<JsonElement>("track-list",ct);
            if(!Current())return;
            if(tracks.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("The subtitle track list is invalid.");
            var loaded=tracks.EnumerateArray().Where(t=>t.ValueKind==JsonValueKind.Object &&
                t.TryGetProperty("type",out var kind) && kind.ValueKind==JsonValueKind.String && kind.GetString()=="sub").ToArray();
            var languages=loaded.Where(t=>t.TryGetProperty("lang",out var language) && language.ValueKind==JsonValueKind.String)
                .Select(t=>t.GetProperty("lang").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources=loaded.Where(t=>t.TryGetProperty("external-filename",out var source) && source.ValueKind==JsonValueKind.String)
                .Select(t=>t.GetProperty("external-filename").GetString()!).ToHashSet(StringComparer.Ordinal);
            var wanted=preferred??"ko";
            var wantedBase=wanted.EndsWith("-orig",StringComparison.OrdinalIgnoreCase)?wanted[..^5]:wanted;
            bool IsWantedLanguage(string? language)=>
                string.Equals(language,wantedBase,StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language,wantedBase+"-orig",StringComparison.OrdinalIgnoreCase);
            if(languages.Any(IsWantedLanguage))return;
            var available=loaded.Length>0;var rejected=false;var requests=0;
            var ordered=captions.DistinctBy(c=>c.Url,StringComparer.Ordinal)
                .OrderBy(c=>string.Equals(c.Language,wanted,StringComparison.OrdinalIgnoreCase)?0:
                    IsWantedLanguage(c.Language)?1:
                    c.Language.EndsWith("-orig",StringComparison.OrdinalIgnoreCase)?2:3)
                .ThenBy(c=>SubtitleLanguagePriority(c.Language));
            foreach(var caption in ordered)
            {
                if(!Current())return;
                var desired=IsWantedLanguage(caption.Language);
                if(languages.Contains(caption.Language) || sources.Contains(caption.Url))
                {if(desired)return;available=true;continue;}
                // Once a fallback is available, only the requested language needs
                // another request. A successful new fallback also ends this budget.
                if(available && !desired)continue;
                if(requests==MaxPreparedCaptionRequests)break;
                requests++;
                try
                {
                    await _player.AddPreparedSubtitleAsync(caption,mediaGeneration,ct);
                    if(!Current())return;
                    available=true;
                    if(desired || preferred is null)return;
                    break;
                }
                catch(MpvPlayerService.SubtitleCommandRejectedException)
                {if(!Current())return;rejected=true;}
            }
            if(Current() && preferred is not null && available)
                SetSubtitleLoadError(()=>UiText.T("선호 언어 자막을 불러오지 못했습니다. 현재 자막은 유지했습니다. 자막 메뉴에서 다시 선택해 주세요.","Could not load the preferred subtitle language. Existing subtitles were kept. Select the language again in the subtitles menu.","優先言語の字幕を読み込めませんでした。現在の字幕は保持しました。字幕メニューでもう一度選択してください。"));
            else if(Current() && rejected && !available)
            {
                SetSubtitleLoadError(()=>UiText.T("준비된 자막을 불러오지 못했습니다. 자막 메뉴에서 다시 선택해 주세요.","Could not load prepared subtitles. Select them again in the subtitles menu.","準備済みの字幕を読み込めませんでした。字幕メニューでもう一度選択してください。"));
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or TimeoutException)
        {
            if(Current())
            {
                var mediaAvailable=_player.HasActiveMedia(mediaGeneration);
                SetSubtitleLoadError(()=>mediaAvailable?
                    UiText.T("준비된 자막을 불러오지 못했습니다. 자막 메뉴에서 다시 선택해 주세요.","Could not load prepared subtitles. Select them again in the subtitles menu.","準備済みの字幕を読み込めませんでした。字幕メニューでもう一度選択してください。"):
                    UiText.T("준비된 자막을 불러오지 못했고 재생기에 연결할 수 없습니다. 영상을 다시 재생해 주세요.","Could not load prepared subtitles or connect to the player. Play the video again.","準備済みの字幕を読み込めず、再生機に接続できません。動画を再生し直してください。"));
            }
        }
        finally{if(ReferenceEquals(_captionCts,cts))_captionCts=null;}
    }
}
