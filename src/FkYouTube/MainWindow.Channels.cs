using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace FkYouTube;

public partial class MainWindow
{
    private bool _channelUiUpdating,_recommendationBusy,_channelReloadBusy;
    private string? _channelReloadError;
    private Func<string>? _channelReloadErrorUiText;
    private void SetChannelReloadError(Func<string> text)
    { _channelReloadErrorUiText=text;_channelReloadError=text();SetStatus(text); }
    private string? _profileListWarning;
    private void UpdateAccountControls()
    {
        Sidebar.IsEnabled=Header.IsEnabled=SettingsPanel.IsEnabled=PlayerPanel.IsEnabled=BrowsePanel.IsEnabled=
            !AccountWorkBlocked && !_channelReloadBusy;
    }
    private bool CanChangeAccountSettings()
    {
        try{_store.EnsureSettingsWritable();return true;}
        catch(Exception error)when(IsStorageError(error)){SetStatus(error.Message);return false;}
    }
    private async Task PersistSessionChangeAsync(Func<Task> change,bool allowRecovery=false)
    {
        using var lifetime=BeginAccountMutation();
        _store.EnsureSettingsWritable();
        var previous=_settings;
        using var mutation=_session.BeginMutation(allowRecovery);
        try
        {
            _settings=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(previous,JsonDefaults.Options),JsonDefaults.Options)!;
            await change();
            if(_closing)throw new OperationCanceledException();
            _store.SaveSettings(_settings);
            mutation.Commit();
        }
        catch{if(!mutation.IsCommitted)_settings=previous;throw;}
    }
    private readonly HashSet<string> _dismissedVideos=[];
    private readonly HashSet<string> _dismissedChannels=[];
    private void DisplaySelectedChannel()
    {
        _channelUiUpdating=true;
        if(_session.SelectedChannel is {} selected){ChannelCombo.ItemsSource=new[]{selected};ChannelCombo.SelectedItem=selected;}
        else{ChannelCombo.ItemsSource=null;ChannelCombo.SelectedItem=null;}
        _channelUiUpdating=false;
    }
    private async Task<bool> ReloadChannelsAsync(bool duringTransition=false)
    {
        if(!_session.IsConnected || _settings.Incognito || _channelReloadBusy || _closing || (!duringTransition && AccountWorkBlocked))return false;
        _historyAccountValidated=false;
        _channelReloadBusy=true;_channelReloadError=null;UpdateAccountControls();UpdateSessionLabel();SetStatus(()=>UiText.T("YouTube 채널 목록을 확인하는 중…","Checking YouTube channels…","YouTube チャンネルを確認中…"));
        try
        {
            var token=_accountCts.Token;
            IReadOnlyList<YouTubeChannel> channels;
            // A failed read must not cancel playback, the current feed cursor or autoplay.
            // End this read lifetime before ResetAccountAsync drains account readers.
            using(BeginAccountRead()){channels=await _session.ListChannelsAsync(token);token.ThrowIfCancellationRequested();}
            if(_closing)return false;
            var original=channels.FirstOrDefault(c=>c.IsSelected);
            var selected=channels.FirstOrDefault(c=>c.Id==_settings.SelectedChannelId);
            if(selected==null && _settings.SelectedChannelId.Length==0)selected=channels.FirstOrDefault(c=>c.IsSelected);
            _channelUiUpdating=true;ChannelCombo.ItemsSource=channels;ChannelCombo.SelectedItem=selected;_channelUiUpdating=false;
            if(selected!=null)
            {
                _store.EnsureSettingsWritable();SetAccountBusy(true);
                try{await ApplyChannelCoreAsync(selected,original);return true;}
                finally{SetAccountBusy(false);}
            }
            else SetChannelReloadError(()=>UiText.T("저장된 채널을 찾지 못했습니다. 왼쪽에서 사용할 채널을 선택하세요.","The saved channel was not found. Select a channel on the left.","保存されたチャンネルが見つかりません。左側で使用するチャンネルを選択してください。"));
        }
        catch(Exception ex)when(IsStorageError(ex) || ex is InvalidDataException or JsonException or Win32Exception or OperationCanceledException)
        {if(!_closing)SetChannelReloadError(()=>UiText.T("채널 목록을 확인하지 못했습니다. 로그인 연결 또는 채널 목록을 갱신하세요.","Could not check channels. Reconnect your account or refresh the channel list.","チャンネルを確認できませんでした。ログイン接続またはチャンネル一覧を更新してください。"));DisplaySelectedChannel();}
        finally{_channelUiUpdating=false;_channelReloadBusy=false;UpdateAccountControls();UpdateSessionLabel();StartHistoryOutboxWorker();RefreshHistoryOutboxUi();}
        return false;
    }
    private Task ApplyChannelAsync(YouTubeChannel channel)=>ApplyChannelCoreAsync(channel,null);
    private async Task ApplyChannelCoreAsync(YouTubeChannel channel,YouTubeChannel? original)
    {
        _store.EnsureSettingsWritable();
        if(channel.Id!=_settings.SelectedChannelId)PauseHistoryOutbox();
        await ResetAccountAsync();
        var token=_accountCts.Token;
        await PersistSessionChangeAsync(async()=>
        {
            await _session.SelectChannelAsync(channel,token);token.ThrowIfCancellationRequested();
            if(_settings.ChannelHistoryKeys.Count==0 && _settings.AccountKey!="guest" && original!=null)
                _settings.ChannelHistoryKeys[original.Id]=_settings.AccountKey;
            _settings.SelectedChannelId=channel.Id;
            if(!_settings.ChannelHistoryKeys.TryGetValue(channel.Id,out var key))
                _settings.ChannelHistoryKeys[channel.Id]=key="channel-"+Guid.NewGuid().ToString("N");
            _settings.AccountKey=key;
        });
        _channelReloadError=null;_qualityKey="";_lastSavedKey="";
        _historyAccountValidated=true;
        UpdateSessionLabel();SetStatus(()=>UiText.F("{0} 채널로 연결되었습니다.","Connected to channel {0}.","{0} チャンネルに接続しました。",channel.Name));
    }
    private async void ReloadChannels_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _channelReloadBusy)return;
        var reloaded=await ReloadChannelsAsync();
        if(reloaded && !AccountWorkBlocked && !_settings.Incognito && _session.IsConnected)await OpenFeedAsync("home",UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));
    }
    private async void Channel_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!_ready || _channelUiUpdating || _channelReloadBusy || AccountWorkBlocked || _settings.Incognito || ChannelCombo.SelectedItem is not YouTubeChannel channel)return;
        if(!CanChangeAccountSettings()){DisplaySelectedChannel();return;}
        var openHome=false;
        SetAccountBusy(true);
        try{await ApplyChannelAsync(channel);openHome=true;}
        catch(Exception ex)when(IsStorageError(ex) || ex is InvalidDataException or JsonException or Win32Exception or OperationCanceledException)
        {DisplaySelectedChannel();SetStatus(()=>UiText.T("선택한 채널을 확인하지 못했습니다. 채널 목록을 갱신한 뒤 다시 선택하세요.","Could not verify the selected channel. Refresh the channel list and select it again.","選択したチャンネルを確認できませんでした。チャンネル一覧を更新して、もう一度選択してください。"));}
        finally{SetAccountBusy(false);}
        if(openHome && !AccountWorkBlocked && !_settings.Incognito)await OpenFeedAsync("home",UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));
    }
    private void ForgetChannelSelection(bool differentAccount)
    {
        _session.ForgetSelectedChannel();_settings.SelectedChannelId="";
        if(differentAccount)_settings.ChannelHistoryKeys.Clear();
        DisplaySelectedChannel();
    }
    private async void Recommendation_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _recommendationBusy || _settings.Incognito || !_session.IsConnected ||
           sender is not MenuItem{Tag:string action,DataContext:VideoItem item} ||
           item.RecommendationActions?.TryGetValue(action,out var handle)!=true || string.IsNullOrEmpty(handle))return;
        var generation=_feedGeneration;var account=_settings.AccountKey;
        _recommendationBusy=true;SetStatus(()=>UiText.T("YouTube 추천 설정을 적용하는 중…","Applying YouTube recommendation preferences…","YouTube のおすすめ設定を適用中…"));
        try
        {
            await _feed.RecommendAsync(generation,item,action,handle,_session.SelectedChannel,_accountCts.Token);
            if(_closing || generation!=_feedGeneration || account!=_settings.AccountKey)return;
            if(action=="dont_recommend_channel" && !string.IsNullOrEmpty(item.ChannelId))_dismissedChannels.Add(item.ChannelId);
            else _dismissedVideos.Add(item.Id);
            _items.RemoveAll(IsDismissed);_rows.Clear();
            if(_playingListGeneration==_feedGeneration)_playingIndex=_current==null?-1:_items.FindIndex(video=>ReferenceEquals(video,_current));
            UpdateUpNext();
            foreach(var video in _items){if(_rows.Count==0 || _rows[^1].Items.Count>=4)_rows.Add(new());_rows[^1].Items.Add(video);}
            ItemCount.Text=UiText.F("{0:N0}개","{0:N0} items","{0:N0} 件",_items.Count);
            SetStatus(()=>action=="not_interested"?UiText.T("관심 없음이 YouTube에 적용되었습니다.","Not interested was applied on YouTube.","YouTube に「興味なし」を適用しました。"):UiText.T("이 채널 추천 안 함이 YouTube에 적용되었습니다.","Don’t recommend channel was applied on YouTube.","YouTube に「チャンネルをおすすめに表示しない」を適用しました。"));
        }
        catch(OperationCanceledException){if(generation==_feedGeneration)SetStatus(()=>UiText.T("추천 설정 결과를 확인하지 못했습니다. 목록을 새로고침하세요.","Could not confirm the recommendation change. Refresh the list.","おすすめ設定の結果を確認できませんでした。一覧を更新してください。"));}
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or Win32Exception or InvalidOperationException)
        {if(generation==_feedGeneration)SetStatus(()=>UiText.T("추천 설정을 적용하지 못했습니다. 카드는 그대로 유지했습니다. ","Could not apply the recommendation preference. The card was kept. ","おすすめ設定を適用できませんでした。カードはそのまま残しています。")+(ex is IOException?ex.Message:UiText.T("목록을 새로고침하세요.","Refresh the list.","一覧を更新してください。")));}
        finally{_recommendationBusy=false;}
    }
    private bool IsDismissed(VideoItem video)=>_dismissedVideos.Contains(video.Id) || video.ChannelId!=null && _dismissedChannels.Contains(video.ChannelId);
}
