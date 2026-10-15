using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;

namespace FkYouTube;
public sealed class VideoRow { public ObservableCollection<VideoItem> Items {get;}=[]; }
public partial class MainWindow : Window
{
    private readonly RuntimePaths _paths;
    private readonly DataStore _store;
    private readonly SessionService _session;
    private readonly FeedService _feed;
    private readonly YouTubeService _youtube;
    private readonly MpvPlayerService _player;
    private readonly WatchProgressTracker _watch=new();
    private readonly ObservableCollection<VideoRow> _rows=[];
    private readonly List<VideoItem> _items=[];
    private readonly DispatcherTimer _saveTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    private readonly string[] _args;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private AppSettings _settings;
    private CancellationTokenSource? _feedCts,_videoCts;
    private CancellationTokenSource _accountCts=new();
    private CancellationTokenSource _historyCts=new();
    private readonly SemaphoreSlim _historyQueue=new(1,1);
    private readonly SemaphoreSlim _videoStartGate=new(1,1);
    private readonly HashSet<Task> _historyTasks=[];
    private readonly HashSet<Task> _accountReadTasks=[];
    private readonly HashSet<Task> _accountMutationTasks=[];
    private sealed class AccountReadLifetime(Action complete):IDisposable { public void Dispose()=>complete(); }
    private IDisposable BeginAccountRead()
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _accountReadTasks.Add(completion.Task);
        return new AccountReadLifetime(()=>{_accountReadTasks.Remove(completion.Task);completion.TrySetResult();});
    }
    private IDisposable BeginAccountMutation()
    {
        if(_closing)throw new OperationCanceledException();
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _accountMutationTasks.Add(completion.Task);
        return new AccountReadLifetime(()=>{_accountMutationTasks.Remove(completion.Task);completion.TrySetResult();});
    }
    private CancellationTokenSource? _commentsCts;
    private CancellationTokenSource? _qualityCts;
    private Task _feedLoadTask=Task.CompletedTask;
    private sealed record VolumeUpdate(double Value,int VideoGeneration,long MediaGeneration,string Account);
    private VolumeUpdate? _pendingVolume;
    private Task _volumeTask=Task.CompletedTask;
    private bool _volumeDraining;
    private int _playingListGeneration=-1,_playingIndex=-1,_autoplayHandledGeneration=-1,_autoplayEpoch;
    private bool _updatingQuality,_updatingSpeed;
    private string _qualityKey="";
    private IReadOnlyList<int> _resolutions=[];
    private CookieLease? _playCookie;
    private VideoItem? _current;
    private PlaybackSnapshot? _snapshot;
    private bool _pausedPlaybackIntent;
    private MpvPlayerService.PauseIntent? _playPauseIntent;
    private int _feedGeneration,_videoGeneration,_videoMetadataGeneration,_viewGeneration;
    private long _expectedMediaGeneration;
    private string _source="home";
    private string _browseTitle="";
    private FeedNavigationMetadata? _feedNavigationMetadata;
    private bool _loading,_hasMore,_ready,_closing,_seekDragging,_fullscreen,_playerStarted,_demo;
    private int _accountBusyDepth;
    private bool AccountWorkBlocked=>_closing || _accountBusyDepth>0 || _store.HasSettingsLoadFailure;
    private bool _hasFeed,_feedExhausted;
    private bool _hasLoadedCurrent,_hasObservedPositivePosition,_canSaveZeroPosition;
    private sealed record PendingPosition(string Account,VideoItem Video,double Position,double Duration);
    private readonly Dictionary<string,PendingPosition> _pendingPositions=[];
    private Rect _windowBounds;
    private WindowState _previousState;
    private string _syncMessage="";
    private Func<string>? _statusTextFactory,_feedTextFactory,_commentTextFactory,_sessionTextFactory,_syncTextFactory,_pageTextFactory;
    private string? _subtitleLoadError;
    private double _lastValidPosition,_lastValidDuration;
    private string _lastSavedKey="";
    private double _lastSavedPosition=-1;
    private double? _pendingSeek;
    private double _pendingSeekAt;

    public MainWindow(RuntimePaths paths,string[] args)
    {
        _paths=paths;_args=args;_demo=args.Contains("--demo")||args.Contains("--ui-smoke-output")||args.Contains("--review-smoke-output")||args.Contains("--feature-smoke-output");
        _store=new(paths);_settings=_store.LoadSettings();InitializeUiLanguage();
        _browseTitle=UiText.T("홈 추천","Home recommendations","ホームのおすすめ");
        _session=new(paths);_feed=new(paths);_youtube=new(paths,_session);_player=new(paths);
        InitializeComponent();
        InitializeUiLanguageSelector();
        SetPageUiText(()=>UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));
        InitializePlayerViewModes();
        Deactivated+=(_,_)=>{CloseDescriptionPopup();CloseMainSubtitlePopup();};
        LocationChanged+=(_,_)=>{CloseDescriptionPopup();CloseMainSubtitlePopup();};
        StateChanged+=(_,_)=>{CloseDescriptionPopup();CloseMainSubtitlePopup();};
        SizeChanged+=(_,_)=>{CloseDescriptionPopup();CloseMainSubtitlePopup();};
        DescriptionPopup.Child.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(DescriptionPopup_KeyDown),true);
        // Slider handles track clicks before ordinary handlers; keep the drag guard active.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler(Seek_Down),true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler(Seek_Up),true);
        SeekSlider.AddHandler(PreviewMouseMoveEvent,new MouseEventHandler(Seek_Move),true);
        ThumbnailImage.Cache=new(paths,privateMode:_settings.Incognito);VideoList.ItemsSource=_rows;NextList.ItemsSource=_upNext;RelatedList.ItemsSource=_relatedItems;
        ProfileCombo.ItemsSource=_demo?Array.Empty<FirefoxProfile>():SessionService.ListFirefoxProfiles(out _profileListWarning);
        if(ProfileCombo.Items.Count>0)ProfileCombo.SelectedIndex=0;
        foreach(FirefoxProfile p in ProfileCombo.Items)if(p.Path==_settings.FirefoxProfile)ProfileCombo.SelectedItem=p;
        HistorySyncCheck.IsChecked=_settings.SyncHistory;PlaylistUrlBox.Text=_settings.PlaylistUrl;VolumeSlider.Value=_settings.Volume;
        HideMembersCheck.IsChecked=_settings.HideMembersOnly;AutoPlayCheck.IsChecked=_settings.AutoPlayNext;IncognitoCheck.IsChecked=_settings.Incognito;
        foreach(ComboBoxItem i in SpeedCombo.Items)if(i.Tag?.ToString()==_settings.Speed.ToString(CultureInfo.InvariantCulture))SpeedCombo.SelectedItem=i;
        _player.SnapshotChanged+=snapshot=>Dispatcher.BeginInvoke(()=>OnSnapshot(snapshot));
        _player.PlaybackError+=failure=>Dispatcher.BeginInvoke(()=>OnPlaybackFailure(failure));
        _saveTimer.Tick+=(_,_)=>SavePosition();_saveTimer.Start();
        try{if(!_store.HasSettingsLoadFailure)_session.RestoreSelectedChannel(_settings.SelectedChannelId);}catch(InvalidDataException){SetStatus(()=>UiText.T("선택한 채널 세션을 다시 연결해야 합니다.","The selected channel session needs to be reconnected.","選択したチャンネルのセッションを再接続する必要があります。"));}
        DisplaySelectedChannel();_ready=true;UpdateSessionLabel();UpdateToolState();
        if(_store.HasSettingsLoadFailure)UpdateAccountControls();
        Loaded+=OnLoaded;
    }
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        var upgradeSmoke=Array.IndexOf(_args,"--playback-upgrade-smoke-output");
        if(upgradeSmoke>=0 && upgradeSmoke+1<_args.Length){await RunPlaybackUpgradeProbeAsync(_args[upgradeSmoke+1]);Close();return;}
        var featureSmoke=Array.IndexOf(_args,"--feature-smoke-output");
        if(featureSmoke>=0 && featureSmoke+1<_args.Length){await RunFeatureProbeAsync(_args[featureSmoke+1]);Close();return;}
        var nativeFeatureSmoke=Array.IndexOf(_args,"--native-feature-smoke-output");
        if(nativeFeatureSmoke>=0 && nativeFeatureSmoke+1<_args.Length){await RunNativeFeatureProbeAsync(_args[nativeFeatureSmoke+1]);Close();return;}
        var channelSmoke=Array.IndexOf(_args,"--channel-smoke-output");
        if(channelSmoke>=0 && channelSmoke+1<_args.Length){await RunChannelProbeAsync(_args[channelSmoke+1]);Close();return;}
        var reviewSmoke=Array.IndexOf(_args,"--review-smoke-output");
        if(reviewSmoke>=0 && reviewSmoke+1<_args.Length){Title="FK_YT · 오프라인 리뷰 회귀 검사";await RunReviewProbeAsync(_args[reviewSmoke+1]);Close();return;}
        var seekSmoke=Array.IndexOf(_args,"--seek-smoke-output");
        if(seekSmoke>=0 && seekSmoke+1<_args.Length){Title="FK_YT · 자동 재생바 검사";await RunSeekProbeAsync(_args[seekSmoke+1]);Close();return;}
        var accountSmoke=Array.IndexOf(_args,"--account-playback-smoke-output");
        if(accountSmoke>=0 && accountSmoke+1<_args.Length){await RunAccountPlaybackProbeAsync(_args[accountSmoke+1]);Close();return;}
        var playbackSmoke=Array.IndexOf(_args,"--playback-smoke-output");
        if(playbackSmoke>=0 && playbackSmoke+1<_args.Length)
        {
            Title="FK_YT · 자동 재생 검사";PlayingTitle.Text="자동 검사 중 · 일반 실행에서는 재생바로 이동할 수 있습니다.";PlayerPanel.IsEnabled=false;
            ShowPanel("player");UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);
            await PlaybackSmoke.RunAsync(_player,VideoHost.Handle,_args[playbackSmoke+1],CancellationToken.None);Close();return;
        }
        if(_demo)
        {
            ShowDemo();var smoke=Array.IndexOf(_args,"--ui-smoke-output");
            if(smoke>=0 && smoke+1<_args.Length){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await CaptureSmokeAsync(_args[smoke+1]);Close();}
        }
        else if(_store.HasSettingsLoadFailure){ShowPanel("settings");SetStatus(_store.SettingsLoadFailureMessage!);}
        else if(_session.HasSessionRecoveryFailure){ShowPanel("settings");SetStatus(()=>UiText.T("이전 계정 변경이 완료되지 않았습니다. Firefox 연결 또는 쿠키 가져오기로 새로 연결하세요.","The previous account change was not completed. Reconnect through Firefox or cookie import.","前回のアカウント変更が完了していません。Firefox 接続または Cookie のインポートで再接続してください。"));}
        else if(_settings.Incognito)ShowIncognitoLanding();
        else if(_session.IsConnected){if(await ReloadChannelsAsync())await OpenFeedWithUiTitleAsync("home",()=>UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));}
        else {WelcomePanel.Visibility=Visibility.Visible;MoreButton.Visibility=Visibility.Collapsed;SetStatus(()=>UiText.T("검색어 또는 YouTube 주소로 시작하세요. 개인 추천은 로그인 연결이 필요합니다.","Start with a search or YouTube URL. Personal recommendations require a sign-in connection.","検索語または YouTube URL を入力してください。自分へのおすすめにはログイン接続が必要です。"));}
        if(_store.SettingsLoadFailureMessage is {} settingsWarning)SetStatus(settingsWarning);
        if(_profileListWarning is {} profileWarning)
        {
            ProfileCombo.ToolTip=profileWarning;
            // Keep the outcome of startup (including a home-feed failure) first.
            // Profile discovery is optional and must not replace that result.
            var initialStatus=StatusText.Text;var initialStatusFactory=_statusTextFactory;
            SetStatus(()=>{var current=initialStatusFactory?.Invoke()??initialStatus;return string.IsNullOrWhiteSpace(current)?profileWarning:current+Environment.NewLine+profileWarning;});
        }
    }
    private void SetStatus(string text){_statusTextFactory=null;StatusText.Text=text;}
    private void SetStatus(Func<string> text){_statusTextFactory=text;StatusText.Text=text();}
    private void SetFeedUiText(string text){_feedTextFactory=null;FeedState.Text=text;}
    private void SetFeedUiText(Func<string> text){_feedTextFactory=text;FeedState.Text=text();}
    private void SetCommentUiText(string text){_commentTextFactory=null;CommentState.Text=text;}
    private void SetCommentUiText(Func<string> text){_commentTextFactory=text;CommentState.Text=text();}
    private void SetSessionUiText(string text){_sessionTextFactory=null;SessionState.Text=text;}
    private void SetSessionUiText(Func<string> text){_sessionTextFactory=text;SessionState.Text=text();}
    private void SetSyncUiText(Func<string> text){_syncTextFactory=text;_syncMessage=text();}
    private void SetPageUiText(string text){_pageTextFactory=null;_browseTitle=text;PageTitle.Text=text;}
    private void SetPageUiText(Func<string> text){_pageTextFactory=text;_browseTitle=text();PageTitle.Text=_browseTitle;}
    private void RefreshMainWindowTextState()
    {
        if(_feedTextFactory is {} feed)FeedState.Text=feed();
        if(_commentTextFactory is {} comment)CommentState.Text=comment();
        if(_sessionTextFactory is {} session)SessionState.Text=session();
        if(_syncTextFactory is {} sync)_syncMessage=sync();
        if(_pageTextFactory is {} page){_browseTitle=page();PageTitle.Text=_browseTitle;}
        if(ItemCount.Text.Length>0)ItemCount.Text=UiText.F("{0:N0}개","{0:N0} items","{0:N0} 件",_items.Count);
    }
    private void ShowPanel(string panel)
    {
        if(panel!="player"){CloseDescriptionPopup();CancelAuthenticationSkip(clearNotice:true);}
        BrowsePanel.Visibility=panel=="browse"?Visibility.Visible:Visibility.Collapsed;
        PlayerPanel.Visibility=panel=="player"?Visibility.Visible:Visibility.Collapsed;
        SettingsPanel.Visibility=panel=="settings"?Visibility.Visible:Visibility.Collapsed;
        if(panel!="player" && _fullscreen)ToggleFullscreen();
        ApplyPlayerViewLayout();
    }
    private async void Nav_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || sender is not Button{Tag:string tag})return;
        var view=++_viewGeneration;
        if(tag=="settings"){if(!await StopPlaybackAsync() || view!=_viewGeneration)return;ShowPanel("settings");UpdateToolState();return;}
        if(tag=="localhistory"){await ShowLocalHistoryAsync();return;}
        if(!await StopPlaybackAsync() || view!=_viewGeneration)return;await OpenFeedAsync(tag,tag);
    }
    private async void Search_Click(object sender,RoutedEventArgs e)=>await SearchAsync();
    private async void Search_KeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;await SearchAsync();}}
    private async Task SearchAsync()
    {
        if(AccountWorkBlocked)return;
        var input=SearchBox.Text.Trim();if(input.Length==0)return;
        if(YouTubeService.TryPlaylistUrl(input,out var playlist))
        {var playlistView=++_viewGeneration;if(await StopPlaybackAsync() && playlistView==_viewGeneration)await OpenFeedWithUiTitleAsync(playlist,()=>UiText.T("재생목록 · 믹스","Playlist · Mix","再生リスト・ミックス"));return;}
        if(YouTubeService.TryVideoUrl(input,out var id,out var canonical))
        {await PlayVideoAsync(new(){Id=id,Url=canonical,Title=UiText.T("YouTube 영상 · ","YouTube video · ","YouTube 動画・")+id});return;}
        string source=YouTubeService.TryNormalizeYouTubeUrl(input,out var url)?url:"search:"+input;
        var view=++_viewGeneration;if(!await StopPlaybackAsync() || view!=_viewGeneration)return;await OpenFeedWithUiTitleAsync(source,()=>source.StartsWith("search:")?UiText.F("‘{0}’ 검색 결과","Search results for ‘{0}’","「{0}」の検索結果",input):UiText.T("채널 · 재생목록","Channel · Playlist","チャンネル・再生リスト"));
    }
    private Task OpenFeedWithUiTitleAsync(string source,Func<string> title)=>OpenFeedCoreAsync(source,title(),title);
    private Task OpenFeedAsync(string source,string title)=>OpenFeedCoreAsync(source,title);
    private async Task OpenFeedCoreAsync(string source,string title,Func<string>? titleFactory=null)
    {
        if(AccountWorkBlocked)return;
        _channelReloadError=null;
        _viewGeneration++;
        if(!_demo && _settings.Incognito && (source is "home" or "shorts" or "subscriptions" or "playlists" or "watchlater" or "history"))
        {ShowPanel("browse");SetStatus(()=>UiText.T("시크릿 모드에서는 로그인 없이 검색·영상·공개 재생목록 주소를 이용하세요.","In incognito mode, use search, video URLs, or public playlist URLs without signing in.","シークレットモードでは、ログインせずに検索・動画 URL・公開再生リスト URL を利用してください。"));return;}
        if(!_demo && (source is "home" or "shorts" or "subscriptions" or "playlists" or "watchlater" or "history") && !_session.IsConnected)
        {ShowPanel("settings");SetStatus(()=>UiText.T("이 목록을 보려면 먼저 로그인 세션을 연결하세요.","Connect a sign-in session first to view this list.","この一覧を表示するには、先にログインセッションを接続してください。"));return;}
        _feedCts?.Cancel();_feed.Stop();
        titleFactory??=source switch
        {
            "home"=>()=>UiText.T("홈 추천","Home recommendations","ホームのおすすめ"),
            "shorts"=>()=>UiText.T("나의 Shorts","My Shorts","自分の Shorts"),
            "subscriptions"=>()=>UiText.T("구독 채널의 새 영상","New videos from subscriptions","登録チャンネルの新着動画"),
            "playlists"=>()=>UiText.T("내 재생목록","My playlists","自分の再生リスト"),
            "watchlater"=>()=>UiText.T("나중에 볼 영상","Watch later","後で見る"),
            "history"=>()=>UiText.T("YouTube 시청 기록","YouTube watch history","YouTube 再生履歴"),
            _=>null
        };
        _feedGeneration++;_source=source;if(titleFactory is null)SetPageUiText(title);else SetPageUiText(titleFactory);
        _feedNavigationMetadata=null;ResetBrowseMetadata(source);
        PlayListButton.Visibility=YouTubeService.TryPlaylistUrl(source,out _)?Visibility.Visible:Visibility.Collapsed;
        ClearDisplayedItems();_loading=false;_hasMore=false;_hasFeed=true;_feedExhausted=false;
        WelcomePanel.Visibility=Visibility.Collapsed;HistoryActions.Visibility=source=="history"?Visibility.Visible:Visibility.Collapsed;
        MoreButton.Visibility=Visibility.Visible;ShowPanel("browse");
        if(_demo){ShowDemo();return;}
        await FetchBatchAsync(true);
    }
    private Task FetchBatchAsync(bool open,string? filterId=null,int? previousGeneration=null)
    {
        if(_loading)return _feedLoadTask;
        return _feedLoadTask=FetchBatchCoreAsync(open,filterId,previousGeneration);
    }
    private async Task FetchBatchCoreAsync(bool open,string? filterId=null,int? previousGeneration=null)
    {
        if(AccountWorkBlocked || _loading || (!open && !_hasMore) || !_hasFeed)return;
        // Includes reads still waiting for FeedService's gate: their finally
        // may enqueue lease cleanup only after cancellation resumes on the UI.
        using var readLifetime=BeginAccountRead();
        using var cts=new CancellationTokenSource();_feedCts=cts;
        var generation=_feedGeneration;var token=cts.Token;var source=_source;var account=_settings.AccountKey;
        var confirmedHistory=new List<string>();
        _loading=true;MoreButton.IsEnabled=false;BrowseFilterItems.IsEnabled=false;SetFeedUiText(()=>UiText.T("목록을 가져오는 중…","Loading the list…","一覧を取得中…"));
        try
        {
            var result=await _feed.ReadAsync(open,source,generation,open && filterId==null?AcquirePlaybackSession():null,item=>
            {
                if(AccountWorkBlocked || token.IsCancellationRequested || generation!=_feedGeneration)return;
                AppendItem(item);
                if(source=="history")confirmedHistory.Add(item.Id);
            },token,_settings.HideMembersOnly,_settings.Incognito?null:_session.SelectedChannel,metadata=>
            {
                if(AccountWorkBlocked || token.IsCancellationRequested || generation!=_feedGeneration)return;
                _feedNavigationMetadata=metadata;ApplyBrowseMetadata(metadata);
                if(!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    if(source=="home" && metadata.Title=="YouTube 홈 추천")SetPageUiText(()=>UiText.T("YouTube 홈 추천","YouTube home recommendations","YouTube ホームのおすすめ"));
                    else SetPageUiText(metadata.Title);
                }
            },filterId,previousGeneration);
            if(AccountWorkBlocked || token.IsCancellationRequested || generation!=_feedGeneration)return;
            _hasMore=result.HasMore;_feedExhausted=!result.HasMore && result.Warning==null;
            if(result.Warning!=null)_feedNavigationMetadata=null;
            SetFeedUiText(()=>result.Warning!=null?UiText.T("받은 목록은 유지했습니다. 다음 목록 확인에 실패해 새로고침이 필요합니다.","Received items were kept. Refresh is needed because the next list could not be checked.","取得済みの一覧は保持しました。次の一覧を確認できなかったため、更新が必要です。"):result.HasMore?UiText.T("아래로 내리면 다음 목록을 불러옵니다.","Scroll down to load more items.","下にスクロールすると次の一覧を読み込みます。"):UiText.T("목록의 끝입니다.","You have reached the end of the list.","一覧の最後です。"));
            if(_items.Count==0)SetFeedUiText(()=>UiText.T("표시할 목록이 없습니다. 계정 연결 또는 YouTube의 목록 상태를 확인하세요.","There are no items to display. Check your account connection or the list on YouTube.","表示する一覧がありません。アカウント接続または YouTube の一覧の状態を確認してください。"));
            if(result.Warning is {} warning)SetStatus(warning);
            else SetStatus(()=>source=="history"?UiText.T("표시된 영상은 YouTube 시청 기록에서 확인했습니다.","The displayed videos were confirmed in your YouTube watch history.","表示された動画は YouTube の再生履歴で確認済みです。"):UiText.F("{0:N0}개 · 영상 상세 정보는 클릭할 때 조회합니다.","{0:N0} items · Video details load when clicked.","{0:N0} 件・動画の詳細はクリック時に取得します。",_items.Count));
        }
        catch(OperationCanceledException){if(generation==_feedGeneration){_feedNavigationMetadata=null;SetFeedUiText(()=>UiText.T("조회가 취소되었거나 시간이 초과되었습니다.","The request was canceled or timed out.","取得がキャンセルされたか、タイムアウトしました。"));_hasMore=false;SetStatus(()=>UiText.T("새로고침으로 다시 조회하세요.","Refresh to load the list again.","更新してもう一度取得してください。"));}}
        catch(Exception ex)when(ex is IOException or JsonException or InvalidDataException or InvalidOperationException or Win32Exception or ObjectDisposedException)
        {if(generation==_feedGeneration){_feedNavigationMetadata=null;SetFeedUiText(()=>UiText.T("목록을 불러오지 못했습니다.","Could not load the list.","一覧を読み込めませんでした。"));if(ex is JsonException)SetStatus(()=>UiText.T("목록 응답 형식이 달라졌습니다. yt-dlp 버전을 확인하세요.","The list response format has changed. Check the yt-dlp version.","一覧の応答形式が変わりました。yt-dlp のバージョンを確認してください。"));else SetStatus(ex.Message);_hasMore=false;}}
        finally
        {
            // Every received history item is confirmed even if the remainder
            // of this batch fails. Keep that evidence without per-item commits.
            if(confirmedHistory.Count>0 && !AccountWorkBlocked && !token.IsCancellationRequested && generation==_feedGeneration && account==_settings.AccountKey)
            {
                try{await Task.Run(()=>_store.SetSyncStatuses(account,confirmedHistory,"confirmed"),token);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){}
                catch(Exception error)when(IsStorageError(error))
                {
                    if(!AccountWorkBlocked && !token.IsCancellationRequested && generation==_feedGeneration && account==_settings.AccountKey)
                        SetStatus(()=>UiText.T("받은 목록은 유지했지만 로컬 기록 상태를 저장하지 못했습니다.","Received items were kept, but the local history status could not be saved.","取得済みの一覧は保持しましたが、ローカル履歴の状態を保存できませんでした。"));
                }
            }
            if(ReferenceEquals(_feedCts,cts))_feedCts=null;
            if(generation==_feedGeneration){_loading=false;MoreButton.IsEnabled=_hasMore;BrowseFilterItems.IsEnabled=_feedNavigationMetadata!=null;if(_current!=null)UpdateUpNext();}
        }
    }
    private async Task SelectFeedFilterAsync(string filterId)
    {
        if(AccountWorkBlocked || _loading || !_hasFeed || _feedNavigationMetadata?.Filters.Any(f=>f.Id==filterId)!=true)return;
        var previous=_feedGeneration;var view=++_viewGeneration;
        if(!await StopPlaybackAsync())return;
        if(AccountWorkBlocked || view!=_viewGeneration || previous!=_feedGeneration)return;
        _feedGeneration++;ClearDisplayedItems();_hasMore=false;_feedExhausted=false;ShowPanel("browse");
        await FetchBatchAsync(true,filterId,previous);
    }
    private void ClearDisplayedItems(){_items.Clear();_rows.Clear();_dismissedVideos.Clear();_dismissedChannels.Clear();ItemCount.Text="";}
    private void AppendItem(VideoItem item)
    {
        if(IsDismissed(item))return;
        _items.Add(item);if(_rows.Count==0 || _rows[^1].Items.Count>=4)_rows.Add(new());_rows[^1].Items.Add(item);ItemCount.Text=UiText.F("{0:N0}개","{0:N0} items","{0:N0} 件",_items.Count);
        NotifyPlaybackQueueChanged();
    }
    private async void Feed_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(_ready && !_demo && BrowsePanel.Visibility==Visibility.Visible && _hasMore && !_loading && e.VerticalChange>0 && e.ExtentHeight-e.ViewportHeight-e.VerticalOffset<1000)await FetchBatchAsync(false);
    }
    private async void More_Click(object sender,RoutedEventArgs e)=>await FetchBatchAsync(false);
    private async void Refresh_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked)return;
        if(SettingsPanel.Visibility==Visibility.Visible){UpdateToolState();return;}
        if(PlayerPanel.Visibility==Visibility.Visible){if(_current!=null){_relatedTask=LoadRelatedAsync(_current,_videoMetadataGeneration);await _relatedTask;}return;}
        if(!_hasFeed){await ShowLocalHistoryAsync();return;}
        if(_loading && _feedNavigationMetadata!=null){SetStatus(()=>UiText.T("분류 목록을 불러오는 중입니다. 조회가 끝난 뒤 새로고침할 수 있습니다.","The category list is loading. You can refresh after it finishes.","分類一覧を読み込んでいます。取得完了後に更新できます。"));return;}
        if(!_loading && _feedNavigationMetadata?.Filters.FirstOrDefault(f=>f.Selected) is {} selected){await SelectFeedFilterAsync(selected.Id);return;}
        await OpenFeedCoreAsync(_source,_browseTitle,_pageTextFactory);
    }
    private async void Video_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || sender is not Button{Tag:VideoItem item})return;
        if(item.Kind is "playlist" or "channel"){var view=++_viewGeneration;if(await StopPlaybackAsync() && !_closing && view==_viewGeneration)await OpenFeedAsync(item.Url,item.Title);}else await PlayVideoAsync(item);
    }
    private async Task<bool> PlayVideoAsync(VideoItem item,bool preserveWatch=false,double? explicitStart=null,bool rememberHistory=true)
    {
        if(AccountWorkBlocked)return false;
        preserveWatch=preserveWatch && _current is {} previous && previous.Id==item.Id && previous.Url==item.Url;
        if(preserveWatch)item=_current!;
        var priorItem=_current;
        if(!SavePosition())return false;
        double start;
        try
        {
            var key=_settings.AccountKey+":"+item.Id;
            start=explicitStart??(_settings.Incognito?0:_pendingPositions.TryGetValue(key,out var pending)
                ?pending.Duration>0 && pending.Position>=pending.Duration-3?0:pending.Position
                :_store.GetResumePosition(_settings.AccountKey,item.Id));
        }
        catch(Exception error)when(IsStorageError(error))
        {SetStatus(()=>UiText.T("이어보기 위치를 읽지 못했습니다. 현재 영상을 유지합니다. 저장소 접근 문제를 해결한 뒤 다시 시도해 주세요.","Could not read the resume position. The current video was kept. Resolve the storage access issue and try again.","再開位置を読み取れませんでした。現在の動画を維持します。保存先へのアクセスの問題を解決してから、もう一度お試しください。"));return false;}
        // Keep the same reference through rapid same-video reloads. The service
        // reads it at IPC submission, after any load-gate or resolver wait.
        _playPauseIntent=preserveWatch?_playPauseIntent??new(_snapshot?.Paused??_pausedPlaybackIntent):new(false);
        var pauseIntent=_playPauseIntent;
        _pausedPlaybackIntent=pauseIntent.Paused;
        UpdatePauseControls();
        _channelReloadError=null;
        _viewGeneration++;
        _videoCts?.Cancel();
        if(!preserveWatch){_videoMetadataGeneration++;_commentsCts?.Cancel();_qualityCts?.Cancel();}
        var usePrepared=ReferenceEquals(item,_preparedItem) && explicitStart==0 && !preserveWatch;
        var preparedResolutions=usePrepared?_preparedResolutions:null;
        var preparedCaptions=usePrepared?_preparedCaptions:[];
        ResetPlaybackExtras(usePrepared,preserveWatch);
        using var cts=new CancellationTokenSource();_videoCts=cts;
        var token=cts.Token;var entered=false;var succeeded=false;var loadSubmitted=false;var cleanupAttempted=false;
        CookieLease? loadCookie=null;long cookieGeneration=0;MpvPlayerService.PlaybackOwner? loadOwner=null;
        var generation=++_videoGeneration;_expectedMediaGeneration=-1;_current=item;_snapshot=null;_hasLoadedCurrent=false;_hasObservedPositivePosition=false;_canSaveZeroPosition=false;SetSyncUiText(()=>"");ResetSeekInteraction();SeekSlider.IsEnabled=false;
        if(!preserveWatch)ResetQualityDisplay(item);
        if(!preserveWatch){_watch.Reset();_playingListGeneration=_feedGeneration;_playingIndex=_items.FindIndex(candidate=>ReferenceEquals(candidate,item));_autoplaySeen.Add(item.Id);}
        _lastValidPosition=start;_lastValidDuration=item.Duration??0;
        PlayingTitle.Text=item.Title;
        if(!preserveWatch){CommentsList.ItemsSource=null;SetCommentUiText(()=>UiText.T("댓글 보기를 누르면 불러옵니다.","Click Comments to load them.","「コメント」を押すと読み込みます。"));}
        ShowPanel("player");PlayPauseButton.Focus();SetStatus(()=>UiText.T("영상을 준비하는 중…","Preparing the video…","動画を準備中…"));UpdateUpNext();
        UpdatePlayingChannel(item);
        UpdateRatingControls();
        async Task StopFailedOwnerAsync()
        {
            if(!entered || generation!=_videoGeneration || loadOwner is null)return;
            cleanupAttempted=true;
            try{await _player.StopOwnedPlaybackAsync(loadOwner);}
            catch(Exception cleanupError)when(cleanupError is IOException or InvalidOperationException or Win32Exception)
            {Trace.TraceError("FK_YT failed playback cleanup: {0}",cleanupError.GetType().Name);}
        }
        try
        {
            await _videoStartGate.WaitAsync(token);entered=true;token.ThrowIfCancellationRequested();
            loadOwner=_player.CapturePlaybackOwner();
            if(_playCookie is {} previousCookie)
            {
                if(previousCookie.Path is null || _player.CanReleasePlaybackCookies(loadOwner.MediaGeneration))previousCookie.Dispose();
                else _player.RetainPlaybackCookies(loadOwner,previousCookie);
                _playCookie=null;
            }
            if(VideoHost.Handle==0){UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);}
            token.ThrowIfCancellationRequested();
            if(!_playerStarted){await _player.StartAsync(VideoHost.Handle);_playerStarted=true;}
            if(_closing || token.IsCancellationRequested || generation!=_videoGeneration)return false;
            loadOwner=_player.CapturePlaybackOwner();
            var accountContext=_settings.Incognito?null:_session.SelectedChannel;
            PreparedVideo? resolvedStream=null;
            async Task<PreparedVideo> ResolveSelectedStreamAsync(CancellationToken resolveToken)
            {
                resolveToken.ThrowIfCancellationRequested();
                PreparedVideo stream;
                try{stream=await _youtube.ResolveNextAsync(item.Url,_settings.MaxHeight,resolveToken,subtitleLanguage:_player.PreferredSubtitleLanguage);}
                catch(InvalidDataException){throw new IOException(UiText.T("선택한 계정의 영상 스트림을 읽지 못했습니다. 다른 영상을 재생하거나 잠시 후 다시 시도해 주세요.","Could not read the video stream for the selected account. Try another video or try again later.","選択したアカウントの動画ストリームを読み取れませんでした。別の動画を再生するか、しばらくしてから再試行してください。"));}
                preparedResolutions=stream.Resolutions;preparedCaptions=stream.Captions;
                return stream;
            }
            if(_closing || token.IsCancellationRequested || generation!=_videoGeneration)return false;
            _playCookie=AcquirePlaybackSession();_expectedMediaGeneration=_player.Snapshot.MediaGeneration+1;
            loadCookie=_playCookie;cookieGeneration=_expectedMediaGeneration;
            loadSubmitted=true;
            await _player.LoadAsync(item.Url,_playCookie.Path,_settings.MaxHeight,_lastValidPosition,token,accountContext,resolvedStream,
                accountContext is {AuthUser:>0}?ResolveSelectedStreamAsync:null,pauseIntent.Paused,pauseIntent);
            if(_closing || token.IsCancellationRequested || generation!=_videoGeneration)return false;
            loadOwner=_player.CapturePlaybackOwner();
            await _player.CommandAsync(token,"set_property","volume",_settings.Volume);
            if(_closing || token.IsCancellationRequested || generation!=_videoGeneration)return false;
            await _player.CommandAsync(token,"set_property","speed",_settings.Speed);
            if(_closing || token.IsCancellationRequested || generation!=_videoGeneration)return false;
            if(preparedResolutions is {Count:>0} && (!preserveWatch || _resolutions.Count==0)){_qualityKey=$"{_settings.AccountKey}:{_settings.Incognito}:{item.Id}";_resolutions=preparedResolutions;DisplayResolutions();}
            // Ordinary playback resolves only once in mpv. Fetch the full format list on demand.
            if(preparedCaptions.Count>0)_=TrackSubtitleTaskAsync(LoadPreparedCaptionsAsync(preparedCaptions,generation));
            // A quality change can cancel the first load before its metadata request starts.
            // Preserve an existing request, but still start the first request exactly once.
            if(!_relatedRequested)
            {
                _relatedTask=LoadRelatedAsync(item,_videoMetadataGeneration);
                if(!_settings.Incognito && !_demo)ObserveRatingTask(LoadDislikeStatsAsync());
            }
            if(!preserveWatch && rememberHistory && priorItem is not null && !ReferenceEquals(priorItem,item))
            {_playbackTrail.Add(priorItem);if(_playbackTrail.Count>100)_playbackTrail.RemoveAt(0);UpdateSkipButtons();}
            succeeded=true;
        }
        catch(OperationCanceledException){await StopFailedOwnerAsync();}
        catch(Exception ex)when(ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {await StopFailedOwnerAsync();if(generation==_videoGeneration){SetStatus(()=>UiText.F("재생을 시작하지 못했습니다: {0}","Could not start playback: {0}","再生を開始できませんでした: {0}",ex.Message));if(!preserveWatch)SetCommentUiText(()=>UiText.T("댓글 보기를 누르면 별도로 조회할 수 있습니다.","Click Comments to load them separately.","「コメント」を押すと個別に取得できます。"));if(ex is YouTubeAccessException{AuthenticationRequired:true}){if(!_relatedRequested)_relatedTask=LoadRelatedAsync(item,_videoMetadataGeneration);HandleAuthenticationRequired();}}}
        finally
        {
            if(!succeeded && !cleanupAttempted)await StopFailedOwnerAsync();
            // An unsuccessful load may already have drained its owned process tree.
            // Release only that operation's lease after an exact-generation receipt.
            if(loadCookie is not null && ReferenceEquals(_playCookie,loadCookie))
            {
                if(!loadSubmitted || _player.Snapshot.MediaGeneration<cookieGeneration || _player.CanReleasePlaybackCookies(cookieGeneration))
                {loadCookie.Dispose();_playCookie=null;}
                else if(!succeeded && loadOwner is not null)
                {
                    // A failed load may have replaced the native session before it closed.
                    // Capture that exact failed generation even when its termination was unconfirmed.
                    var currentOwner=_player.CapturePlaybackOwner();
                    if(currentOwner.MediaGeneration==cookieGeneration)loadOwner=currentOwner;
                    _player.RetainPlaybackCookies(loadOwner,loadCookie);_playCookie=null;
                }
            }
            if(ReferenceEquals(_videoCts,cts))_videoCts=null;NotifyPlaybackQueueChanged();if(entered)_videoStartGate.Release();
        }
        return succeeded && !_closing && generation==_videoGeneration;
    }
    private void OnPlaybackFailure(PlaybackFailure failure)
    {
        // Dispatcher callbacks can arrive after a new video/account was selected.
        if(AccountWorkBlocked || _current is null || failure.MediaGeneration!=_expectedMediaGeneration ||
            failure.VideoUrl!=_current.Url || _player.Snapshot.MediaGeneration!=failure.MediaGeneration)return;
        SetStatus(failure.Message);
    }
    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        if(AccountWorkBlocked || _current==null || snapshot.MediaGeneration!=_expectedMediaGeneration || snapshot.VideoUrl!=_current.Url)return;
        var previous=_snapshot;_snapshot=snapshot;
        // Native pause observations can describe the preceding file or a prepared
        // entry and arrive through Dispatcher after loading has finished. Playback
        // intent is owned by our load/input paths; observations still drive watch
        // accounting below, but must not rewrite the desired pause state.
        _pausedPlaybackIntent=_playPauseIntent?.Paused??snapshot.Paused;
        UpdatePopoutSnapshot(snapshot);
        if(snapshot.Loaded)_hasLoadedCurrent=true;
        if(_player.CanReleasePlaybackCookies(_expectedMediaGeneration)){_playCookie?.Dispose();_playCookie=null;}
        if(snapshot.Loaded)_=EnsurePrefetchAsync();
        if(snapshot.Loaded && !snapshot.Seeking && double.IsFinite(snapshot.Position))
        {
            if(snapshot.Position>0)_hasObservedPositivePosition=true;
            _canSaveZeroPosition=snapshot.Position==0 && !snapshot.Ended && (_hasObservedPositivePosition || _pendingSeek==0);
            _lastValidPosition=snapshot.Position;_lastValidDuration=snapshot.Duration;
        }
        SeekSlider.IsEnabled=snapshot.Loaded && snapshot.Duration>0;
        if(_pendingSeek.HasValue && ((!snapshot.Seeking && Math.Abs(snapshot.Position-_pendingSeek.Value)<1) || _clock.Elapsed.TotalSeconds-_pendingSeekAt>5))_pendingSeek=null;
        if(!_seekDragging && !_pendingSeek.HasValue){SeekSlider.Maximum=Math.Max(1,snapshot.Duration);SeekSlider.Value=Math.Clamp(snapshot.Position,0,SeekSlider.Maximum);}
        TimeLabel.Text=VideoItem.FormatTime(snapshot.Position)+" / "+VideoItem.FormatTime(snapshot.Duration);
        UpdatePauseControls();
        DecoderLabel.Text=snapshot.HardwareDecoder=="unavailable"?UiText.T("디코더 확인 중","Checking decoder","デコーダーを確認中"):string.IsNullOrWhiteSpace(snapshot.HardwareDecoder)||snapshot.HardwareDecoder=="no"?UiText.T("CPU 디코딩","CPU decoding","CPU デコード"):"HW · "+snapshot.HardwareDecoder;
        if(_watch.Observe(_clock.Elapsed.TotalSeconds,snapshot.Position,snapshot.Duration,snapshot.Speed,snapshot.Paused,snapshot.Buffering,snapshot.Seeking,snapshot.Loaded))
        {SavePosition();if(!_settings.Incognito && _settings.SyncHistory && _session.IsConnected)TrackHistory(SendHistoryAsync(_current,_settings.AccountKey,_accountCts.Token));}
        if((snapshot.Paused && previous?.Paused!=true) || (snapshot.Ended && previous?.Ended!=true))SavePosition();
        SetStatus(()=>_channelReloadError??(snapshot.Ended && snapshot.ErrorMessage is {} playbackError?playbackError:_subtitleLoadError??(snapshot.Buffering?UiText.T("버퍼링 중…","Buffering…","バッファリング中…"):snapshot.Loaded?(_settings.Incognito?UiText.T("시크릿 재생 · 로그인 쿠키와 시청 기록·이어보기 저장을 사용하지 않습니다.","Incognito playback · Sign-in cookies, watch history, and saved resume positions are disabled.","シークレット再生・ログイン Cookie、再生履歴、再開位置の保存は使用しません。"):_syncMessage.Length>0?_syncMessage:UiText.T("재생 중 · 정확한 이어보기 위치는 이 앱에 저장됩니다.","Playing · The exact resume position is saved in this app.","再生中・正確な再開位置はこのアプリに保存されます。")):snapshot.Ended?UiText.T("재생이 끝났습니다.","Playback has ended.","再生が終了しました。"):UiText.T("영상을 준비하는 중…","Preparing the video…","動画を準備中…"))));
        if(snapshot.Ended && snapshot.AuthenticationRequired)HandleAuthenticationRequired();
        if(snapshot.EndReason=="eof" && _hasLoadedCurrent && _settings.AutoPlayNext && _autoplayHandledGeneration!=_videoGeneration)
        {_autoplayHandledGeneration=_videoGeneration;_=AutoPlayNextAsync(_videoGeneration,_autoplayEpoch);}
    }
    private async void TrackHistory(Task task)
    {
        _historyTasks.Add(task);
        try{await task;}catch(OperationCanceledException){}catch(Exception error){Trace.TraceError("FK_YT history completion: {0}",error.GetType().Name);}
        finally{_historyTasks.Remove(task);}
    }
    private async Task CancelAccountWorkAsync()
    {
        _accountCts.Cancel();
        _commentsCts?.Cancel();_qualityCts?.Cancel();_relatedCts?.Cancel();_prefetchCts?.Cancel();
        CancelSubtitleOperations();NotifyPlaybackQueueChanged();
        _pendingVolume=null;
        var pending=Task.WhenAll(_historyTasks.Concat(_ratingTasks).Concat(_subtitleTasks).Concat(_accountReadTasks).Append(_authenticationSkipTask).Append(_volumeTask).ToArray());
        try{await pending.WaitAsync(TimeSpan.FromSeconds(5));}
        catch(TimeoutException error)
        {
            // A deadline is not proof of process exit: keep late work/leases owned,
            // observe its eventual fault, and reject this account transition.
            _=pending.ContinueWith(task=>_ = task.Exception,CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            throw new IOException(UiText.T("이전 계정 작업이 아직 종료되지 않아 전환을 중단했습니다. 잠시 후 다시 시도하세요.","Switching was stopped because the previous account operation has not finished. Try again later.","前のアカウント操作が終了していないため、切り替えを中止しました。しばらくしてからお試しください。"),error);
        }
        catch(OperationCanceledException){}
        catch(Exception error){Trace.TraceError("FK_YT account completion: {0}",error.GetType().Name);if(_closing)throw;}
    }
    private async Task SendHistoryAsync(VideoItem video,string account,CancellationToken ct)
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,_historyCts.Token);
        ct=cancellation.Token;var started=false;
        try
        {
            await _historyQueue.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if(_store.HasSettingsLoadFailure || !_settings.SyncHistory || _settings.Incognito || account!=_settings.AccountKey)return;
                started=true;
                TrySaveSyncStatus(account,video.Id,"sending");
                var sent=await _youtube.MarkWatchedAsync(video.Url,ct);
                ct.ThrowIfCancellationRequested();
                if(account!=_settings.AccountKey)return;
                TrySaveSyncStatus(account,video.Id,sent?"sent":"failed");
                if(_current?.Id==video.Id)SetSyncUiText(()=>sent?UiText.T("시청 기록 전송됨 · 계정 반영은 ‘YouTube 기록 확인’에서 확인할 수 있습니다.","Watch history sent · Check its account status with “Check YouTube history”.","再生履歴を送信済み・アカウントへの反映は「YouTube 履歴を確認」で確認できます。"):UiText.T("시청 기록 전송 실패 · 로컬 이어보기는 저장되었습니다.","Watch history could not be sent · The local resume position was saved.","再生履歴の送信に失敗・ローカルの再開位置は保存済みです。"));
            }
            finally{_historyQueue.Release();}
        }
        catch(OperationCanceledException)
        {
            if(started){TrySaveSyncStatus(account,video.Id,"cancelled");if(account==_settings.AccountKey && _current?.Id==video.Id)SetSyncUiText(()=>UiText.T("시청 기록 전송 취소 · 이미 전송된 요청의 계정 반영 여부는 확인되지 않았습니다.","Watch history sending canceled · Account status of requests already sent has not been confirmed.","再生履歴の送信をキャンセル・送信済みリクエストのアカウントへの反映は未確認です。"));}
        }
        catch(Exception ex)when(ex is IOException or Win32Exception or InvalidDataException){TrySaveSyncStatus(account,video.Id,"failed");}
    }
    private static bool IsStorageError(Exception error)=>error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException;
    private void TrySaveSyncStatus(string account,string videoId,string status)
    {
        try{_store.SetSyncStatus(account,videoId,status);}
        catch(Exception error)when(IsStorageError(error))
        {Trace.TraceError("FK_YT local history status: {0}",error.GetType().Name);}
    }
    private bool TrySaveSettings()
    {
        try{_store.SaveSettings(_settings);return true;}
        catch(Exception error)when(IsStorageError(error))
        {SetStatus(()=>UiText.T("설정을 저장하지 못했습니다. 기존 파일은 보존했습니다. 저장소 접근 문제를 확인해 주세요.","Could not save settings. The existing file was preserved. Check storage access.","設定を保存できませんでした。既存のファイルは保持しました。保存先へのアクセスを確認してください。"));return false;}
    }
    private bool SavePosition()
    {
        if(_store.HasSettingsLoadFailure || _settings.Incognito)return true;
        if(_current==null || _lastValidPosition<0 || (_lastValidPosition==0 && !_canSaveZeroPosition) || !_hasLoadedCurrent)
        {FlushPendingPositions();return true;}
        var key=_settings.AccountKey+":"+_current.Id;
        if(key==_lastSavedKey && Math.Abs(_lastValidPosition-_lastSavedPosition)<0.1 && !_pendingPositions.ContainsKey(key)){FlushPendingPositions();return true;}
        // Bound retained failures without silently evicting a previous video's final position.
        if(!_pendingPositions.ContainsKey(key) && _pendingPositions.Count>=32)
        {
            FlushPendingPositions();
            if(_pendingPositions.Count>=32){SetStatus(()=>UiText.T("저장 대기 중인 이어보기 기록이 많습니다. 저장소 문제를 해결한 뒤 영상을 전환해 주세요.","Many resume records are waiting to be saved. Resolve the storage issue before switching videos.","保存待ちの再開履歴が多数あります。保存先の問題を解決してから動画を切り替えてください。"));return false;}
        }
        _pendingPositions[key]=new(_settings.AccountKey,_current,_lastValidPosition,_lastValidDuration);
        FlushPendingPositions();return true;
    }
    private void FlushPendingPositions()
    {
        if(_store.HasSettingsLoadFailure)return;
        foreach(var (key,position) in _pendingPositions.ToArray())
        {
            try
            {
                _store.SavePosition(position.Account,position.Video,position.Position,position.Duration);
                _pendingPositions.Remove(key);
                _lastSavedKey=key;_lastSavedPosition=position.Position;
            }
            catch(Exception error)when(IsStorageError(error))
            {
                Trace.TraceError("FK_YT position save: {0}",error.GetType().Name);
                SetStatus(()=>UiText.T("이어보기 위치를 저장하지 못했습니다. 앱 실행 중 보관하여 다음 저장 때 다시 시도합니다.","Could not save the resume position. It is kept while the app is open and will be retried on the next save.","再開位置を保存できませんでした。アプリの実行中は保持し、次回の保存時に再試行します。"));
                if(_closing)throw;
                break;
            }
        }
    }
    private async Task LoadCommentsAsync()
    {
        if(AccountWorkBlocked || _current==null)return;_commentsCts?.Cancel();
        using var readLifetime=BeginAccountRead();
        using var cts=new CancellationTokenSource();_commentsCts=cts;var token=cts.Token;
        var generation=_videoMetadataGeneration;var video=_current;SetCommentUiText(()=>UiText.T("댓글을 불러오는 중…","Loading comments…","コメントを読み込み中…"));
        try{var comments=await _youtube.GetCommentsAsync(video.Url,CommentSort.SelectedIndex==1,token,_settings.Incognito);if(_closing || generation!=_videoMetadataGeneration || token.IsCancellationRequested || !ReferenceEquals(_commentsCts,cts))return;CommentsList.ItemsSource=comments;SetCommentUiText(()=>comments.Count==0?UiText.T("댓글이 없거나 댓글이 비활성화되어 있습니다.","There are no comments, or comments are disabled.","コメントがないか、コメントが無効になっています。"):UiText.F("{0}개 · 영상 재생과 별도로 조회","{0} comments · Loaded separately from video playback","{0} 件・動画再生とは別に取得",comments.Count));}
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or JsonException or Win32Exception or InvalidDataException){if(!_closing && generation==_videoMetadataGeneration && !token.IsCancellationRequested && ReferenceEquals(_commentsCts,cts)){if(ex is JsonException)SetCommentUiText(()=>UiText.T("댓글 응답을 읽지 못했습니다.","Could not read the comments response.","コメントの応答を読み取れませんでした。"));else SetCommentUiText(ex.Message);}}
        finally{if(ReferenceEquals(_commentsCts,cts))_commentsCts=null;}
    }
    private async void CommentSort_Changed(object sender,SelectionChangedEventArgs e){if(_ready && _current!=null && _commentsRequested)await LoadCommentsAsync();}
    private async Task<bool> StopPlaybackAsync()
    {
        if(!ClosePopoutForPlaybackStop())return false;
        if(!_closing && !SavePosition())return false;
        var generation=++_videoGeneration;_videoMetadataGeneration++;_videoCts?.Cancel();_commentsCts?.Cancel();_qualityCts?.Cancel();_current=null;_snapshot=null;_pausedPlaybackIntent=false;_playPauseIntent=null;ResetSeekInteraction();
        ResetPlaybackExtras();_autoplaySeen.Clear();_playbackTrail.Clear();UpdateSkipButtons();
        await _videoStartGate.WaitAsync();
        try
        {
            if(generation!=_videoGeneration)return false;
            if(_playCookie is {} cookie)
            {
                _player.RetainPlaybackCookies(_player.CapturePlaybackOwner(),cookie);
                _playCookie=null;
            }
            await _player.StopAsync();_playerStarted=false;
            return true;
        }
        catch(Exception error)when(error is IOException or InvalidOperationException or Win32Exception)
        {Trace.TraceError("FK_YT playback stop: {0}",error.GetType().Name);SetStatus(()=>UiText.T("재생 프로세스의 종료를 확인하지 못했습니다. 정리가 끝난 뒤 다시 시도해 주세요.","Could not verify that the playback process stopped. Wait for cleanup and try again.","再生プロセスの終了を確認できませんでした。クリーンアップ完了後にもう一度お試しください。"));return false;}
        finally{_videoStartGate.Release();}
    }
    private async void Back_Click(object sender,RoutedEventArgs e){if(AccountWorkBlocked)return;var view=++_viewGeneration;if(await StopPlaybackAsync() && !AccountWorkBlocked && view==_viewGeneration)ShowPanel("browse");}
    private async void Pause_Click(object sender,RoutedEventArgs e)=>await TogglePlaybackPauseAsync();
    private void UpdatePauseControls()
    {
        var paused=_playPauseIntent?.Paused??_snapshot?.Paused??_pausedPlaybackIntent;
        var label=paused?UiText.T("재생","Play","再生"):UiText.T("일시정지","Pause","一時停止");
        PlayPauseButton.Content=label;if(_popoutPause is not null)_popoutPause.Content=label;
    }
    private async Task TogglePlaybackPauseAsync()
    {
        if(AccountWorkBlocked || !_playerStarted || _current is null)return;
        var intent=_playPauseIntent??=new(_snapshot?.Paused??_pausedPlaybackIntent);
        var request=intent.Toggle();var generation=_videoGeneration;
        _pausedPlaybackIntent=intent.Paused;UpdatePauseControls();
        var entered=false;
        try
        {
            await _videoStartGate.WaitAsync(_accountCts.Token);entered=true;
            if(AccountWorkBlocked || generation!=_videoGeneration || !ReferenceEquals(intent,_playPauseIntent))return;
            // A request made after loadfile submission is applied after that load.
            // Before submission the load itself already reads this newest intent.
            await _player.SetPausedAsync(intent,request.Revision,_accountCts.Token);
        }
        catch(OperationCanceledException) { }
        catch(Exception error)when(error is IOException or InvalidOperationException or TimeoutException)
        {
            // Only an explicit rejection proves that native pause was unchanged.
            // A lost acknowledgement is ambiguous and keeps the latest intent.
            if(error is MpvPlayerService.PauseCommandRejectedException && ReferenceEquals(intent,_playPauseIntent) && intent.TryRevert(request.Revision))
            {_pausedPlaybackIntent=intent.Paused;UpdatePauseControls();}
            if(!AccountWorkBlocked && generation==_videoGeneration)
                SetStatus(()=>UiText.T("플레이어 연결이 끊겼습니다. 영상을 다시 여세요.","The player connection was lost. Open the video again.","プレーヤーとの接続が切れました。動画を開き直してください。"));
        }
        finally{if(entered)_videoStartGate.Release();}
    }
    private async Task PlayerCommandAsync(params object[] command){if(AccountWorkBlocked)return;try{await _player.CommandAsync(command);}catch(Exception ex)when(ex is IOException or InvalidOperationException or TimeoutException){SetStatus(()=>UiText.T("플레이어 연결이 끊겼습니다. 영상을 다시 여세요.","The player connection was lost. Open the video again.","プレーヤーとの接続が切れました。動画を開き直してください。"));}}
    private async void SeekBack_Click(object sender,RoutedEventArgs e)=>await PlayerCommandAsync("seek",-5,"relative");
    private async void SeekForward_Click(object sender,RoutedEventArgs e)=>await PlayerCommandAsync("seek",5,"relative");
    private void Seek_Down(object sender,MouseButtonEventArgs e)
    {
        _seekDragging=true;
        // Move-to-point track presses do not capture like a Thumb; receive release outside the bar.
        if(e.Handled && Mouse.Captured==null)Mouse.Capture(SeekSlider,CaptureMode.SubTree);
    }
    private async void Seek_Up(object sender,MouseButtonEventArgs e)
    {
        if(!_seekDragging)return;
        var seek=CommitSeekAsync();
        if(Mouse.Captured==SeekSlider)SeekSlider.ReleaseMouseCapture();
        await seek;
    }
    private void Seek_Move(object sender,MouseEventArgs e)
    {
        if(!_seekDragging || Mouse.Captured!=SeekSlider)return;
        if(SeekSlider.Template.FindName("PART_Track",SeekSlider) is not System.Windows.Controls.Primitives.Track track)return;
        // ValueFromPoint uses the arranged thumb center as well as the current Value.
        track.UpdateLayout();
        var value=track.ValueFromPoint(e.GetPosition(track));
        if(double.IsFinite(value))SeekSlider.Value=Math.Clamp(value,SeekSlider.Minimum,SeekSlider.Maximum);
        e.Handled=true;
    }
    private void ResetSeekInteraction()
    {
        _seekDragging=false;_pendingSeek=null;
        if(Mouse.Captured==SeekSlider)SeekSlider.ReleaseMouseCapture();
    }
    private async void Seek_LostCapture(object sender,MouseEventArgs e){if(_seekDragging)await CommitSeekAsync();}
    private async Task CommitSeekAsync()
    {
        var target=Math.Clamp(SeekSlider.Value,0,SeekSlider.Maximum);_seekDragging=false;
        if(!_playerStarted || _snapshot?.Loaded!=true)return;
        _pendingSeek=target;_pendingSeekAt=_clock.Elapsed.TotalSeconds;
        await PlayerCommandAsync("seek",target,"absolute+exact");
    }
    private void Volume_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(!_ready)return;
        _settings.Volume=e.NewValue;
        if(!_playerStarted || AccountWorkBlocked)return;
        _pendingVolume=new(e.NewValue,_videoGeneration,_player.Snapshot.MediaGeneration,RatingAccountStamp);
        if(_volumeDraining)return;
        _volumeDraining=true;
        _volumeTask=DrainVolumeCommandsAsync();
    }
    private bool IsCurrentVolume(VolumeUpdate update)=>!AccountWorkBlocked && _playerStarted &&
        update.VideoGeneration==_videoGeneration && update.MediaGeneration==_player.Snapshot.MediaGeneration &&
        update.Account==RatingAccountStamp;
    private async Task DrainVolumeCommandsAsync()
    {
        try
        {
            // Keep at most one command in flight and one latest pending value.
            // Settings and the two bound sliders still update on every input.
            while(_pendingVolume is { } update)
            {
                _pendingVolume=null;
                if(!IsCurrentVolume(update))continue;
                try{await _player.CommandAsync(_accountCts.Token,"set_property","volume",update.Value);}
                catch(OperationCanceledException){}
                catch(Exception ex)when(ex is IOException or InvalidOperationException or TimeoutException)
                {
                    if(IsCurrentVolume(update))SetStatus(()=>UiText.T("플레이어 연결이 끊겼습니다. 영상을 다시 여세요.","The player connection was lost. Open the video again.","プレーヤーとの接続が切れました。動画を開き直してください。"));
                }
            }
        }
        finally{_volumeDraining=false;}
    }
    private async void Speed_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!_ready || _updatingSpeed || AccountWorkBlocked || SpeedCombo.SelectedItem is not ComboBoxItem{Tag:string tag})return;
        var previous=_settings.Speed;_settings.Speed=double.Parse(tag,CultureInfo.InvariantCulture);
        if(!TrySaveSettings())
        {
            _settings.Speed=previous;_updatingSpeed=true;
            try{SpeedCombo.SelectedItem=SpeedCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>i.Tag?.ToString()==previous.ToString(CultureInfo.InvariantCulture));}
            finally{_updatingSpeed=false;}
            return;
        }
        if(_playerStarted)await PlayerCommandAsync("set_property","speed",_settings.Speed);
    }
    private async void Quality_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!_ready || _updatingQuality || AccountWorkBlocked || QualityCombo.SelectedItem is not int height)return;
        var previous=_settings.MaxHeight;var previousSelection=e.RemovedItems.OfType<int>().Select(value=>(int?)value).FirstOrDefault();_settings.MaxHeight=height;
        if(!TrySaveSettings())
        {_settings.MaxHeight=previous;_updatingQuality=true;try{QualityCombo.SelectedItem=previousSelection;}finally{_updatingQuality=false;}return;}
        SetQualityUiText(()=>"");
        if(_current!=null)await PlayVideoAsync(_current,true,_lastValidPosition);
    }
    private void Fullscreen_Click(object sender,RoutedEventArgs e)=>ToggleFullscreen();
    private void ToggleFullscreen()
    {
        CloseDescriptionPopup();
        if(!_fullscreen && _popoutWindow!=null){_popoutWindow.Close();if(_popoutWindow!=null)return;}
        if(!_fullscreen){_windowBounds=RestoreBounds;_previousState=WindowState;WindowState=WindowState.Normal;WindowStyle=WindowStyle.None;WindowState=WindowState.Maximized;_fullscreen=true;}
        else{WindowState=WindowState.Normal;WindowStyle=WindowStyle.SingleBorderWindow;Left=_windowBounds.Left;Top=_windowBounds.Top;Width=_windowBounds.Width;Height=_windowBounds.Height;WindowState=_previousState;_fullscreen=false;}
        ApplyPlayerViewLayout();
    }
    private async void Window_KeyDown(object sender,KeyEventArgs e)
    {
        DescriptionPopup_KeyDown(sender,e);if(e.Handled)return;
        if(AccountWorkBlocked || _channelReloadBusy || ShouldPreserveControlKey(e) || PlayerPanel.Visibility!=Visibility.Visible)return;
        switch(e.Key){case Key.Space:e.Handled=true;await TogglePlaybackPauseAsync();break;case Key.Left:e.Handled=true;await PlayerCommandAsync("seek",-5,"relative");break;case Key.Right:e.Handled=true;await PlayerCommandAsync("seek",5,"relative");break;case Key.F:e.Handled=true;ToggleFullscreen();break;case Key.Escape:if(_fullscreen){e.Handled=true;ToggleFullscreen();}break;}
    }
    private void UpdateSessionLabel()
    {
        AccountLabel.Text=GetAccountLabelText();
        SetSessionUiText(()=>_session.HasSessionRecoveryFailure?UiText.T("계정 변경 복구가 필요합니다. Firefox 연결 또는 쿠키 가져오기로 새로 연결하세요.","The account change requires recovery. Reconnect through Firefox or cookie import.","アカウント変更の復旧が必要です。Firefox 接続または Cookie のインポートで再接続してください。"):
            _session.IsConnected?UiText.F("연결 방식: {0} · 인증이 만료되면 다시 연결하세요.","Connection: {0} · Reconnect when authentication expires.","接続方式: {0}・認証の期限が切れたら再接続してください。",_settings.AuthMode=="쿠키 파일"?UiText.T("쿠키 파일","Cookie file","Cookie ファイル"):_settings.AuthMode):UiText.T("아직 연결된 세션이 없습니다.","No session is connected yet.","まだ接続されたセッションがありません。"));
        ChannelCombo.IsEnabled=ReloadChannelsButton.IsEnabled=!AccountWorkBlocked && !_channelReloadBusy && _session.IsConnected && !_settings.Incognito;
    }
    private string GetAccountLabelText()=>_store.HasSettingsLoadFailure
        ?UiText.T("설정 복구 필요 · 재생·기록 중지","Settings recovery required · Playback and history paused","設定の復旧が必要・再生と履歴を停止中")
        :_settings.Incognito?UiText.T("시크릿 모드 · 기록 저장 안 함","Incognito mode · History is not saved","シークレットモード・履歴を保存しません")
        :_session.SelectedChannel?.Name??(_session.IsConnected?UiText.T("로그인 세션 연결됨","Sign-in session connected","ログインセッション接続済み"):UiText.T("로그인 연결 안 됨","Not signed in","ログイン未接続"));
    private void UpdateToolState(){ToolState.Text=string.Join("\n",new[]{("mpv",_paths.Mpv),("yt-dlp",_paths.YtDlp),("Deno",_paths.Deno),("Python",_paths.Python),(UiText.T("목록 작업자","List worker","一覧ワーカー"),_paths.FeedWorker)}.Select(t=>$"{t.Item1}: {(File.Exists(t.Item2)?UiText.T("준비됨","Ready","準備完了"):UiText.T("파일 없음 — Bootstrap 필요","File missing — Bootstrap required","ファイルなし — Bootstrap が必要"))}"));}
    private async Task<string> ResetAccountAsync()
    {
        if(_closing)throw new OperationCanceledException();
        await CancelDownloadAsync();
        if(_closing)throw new OperationCanceledException();
        if(!await StopPlaybackAsync())throw new IOException(UiText.T("재생 종료를 완료하지 못해 계정 전환을 중단했습니다. 저장소 또는 팝아웃 상태를 확인해 주세요.","Account switching was stopped because playback could not be fully stopped. Check storage or popout state.","再生の終了を完了できなかったため、アカウントの切り替えを中止しました。保存先またはポップアウトの状態を確認してください。"));
        _feedCts?.Cancel();_feed.Stop();_feedGeneration++;_hasFeed=false;_hasMore=false;ClearDisplayedItems();
        _feedNavigationMetadata=null;ResetBrowseMetadata("");
        try
        {
            try{await CancelAccountWorkAsync();}
            finally
            {
                // Even a stalled reader must reach the bounded feed stop path.
                // A later reader finally still owns its lease; no session changes
                // are admitted if either the read or cleanup barrier fails.
                try{await _feed.StopAndDrainAsync();}
                finally{await _session.DrainCookieCleanupAsync();}
            }
        }
        finally
        {
            // Cleanup failure aborts the switch but must not strand the old account
            // behind a cancelled token. Shutdown still never renews its token.
            if(!_closing){_accountCts.Dispose();_accountCts=new();}
        }
        // A pending account switch can finish draining after Closing cancelled
        // this token. Never renew it and resume account work during shutdown.
        if(_closing)throw new OperationCanceledException();
        return _settings.AccountKey=="guest" || DifferentAccountCheck.IsChecked==true?Guid.NewGuid().ToString("N"):_settings.AccountKey;
    }
    private void SetAccountBusy(bool busy)
    {
        var entering=busy && _accountBusyDepth==0;
        if(busy)_accountBusyDepth++;else if(_accountBusyDepth>0)_accountBusyDepth--;
        UpdateAccountControls();
        _ratingsAccountBusy=_accountBusyDepth>0;
        if(entering)
        {
            _pendingVolume=null;
            // Invalidate operations already awaiting playback/worker completion before
            // draining the old session. UI disabling alone does not stop their continuations.
            _viewGeneration++;_feedGeneration++;_feedCts?.Cancel();_feed.Stop();
            _loading=false;_hasMore=false;_feedExhausted=false;_feedNavigationMetadata=null;
            MoreButton.IsEnabled=false;BrowseFilterItems.IsEnabled=false;ResetBrowseMetadata(_source);
            _videoCts?.Cancel();_commentsCts?.Cancel();_qualityCts?.Cancel();
            _relatedCts?.Cancel();_prefetchCts?.Cancel();_captionCts?.Cancel();
            CancelSubtitleOperations();ResetSubtitleCatalog();ResetVideoDescription();CancelAuthenticationSkip(clearNotice:true);_ratingRevision++;_ratingReadCts?.Cancel();_ratingSendCts?.Cancel();_dislikeStatsCts?.Cancel();
        }
        if(!busy)UpdateSessionLabel();
        UpdateRatingControls();
    }
    private async void ConnectFirefox_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _channelReloadBusy || !CanChangeAccountSettings())return;
        if(ProfileCombo.SelectedItem is not FirefoxProfile profile){SetSessionUiText(()=>UiText.T("먼저 Firefox 프로필을 선택하세요.","Select a Firefox profile first.","先に Firefox プロファイルを選択してください。"));return;}
        SetAccountBusy(true);SetSessionUiText(()=>UiText.T("선택한 Firefox 세션을 연결하는 중…","Connecting the selected Firefox session…","選択した Firefox セッションを接続中…"));
        try
        {
            var recovery=_session.HasSessionRecoveryFailure;var different=DifferentAccountCheck.IsChecked==true || recovery;
            var key=await ResetAccountAsync();if(recovery)key=Guid.NewGuid().ToString("N");
            var token=_accountCts.Token;
            await PersistSessionChangeAsync(async()=>
            {
                await _session.ConnectFirefoxAsync(profile.Path,token);token.ThrowIfCancellationRequested();
                ForgetChannelSelection(different);_settings.AccountKey=key;_settings.AuthMode="Firefox";_settings.FirefoxProfile=profile.Path;
            },allowRecovery:true);
            DifferentAccountCheck.IsChecked=false;UpdateSessionLabel();
            var reloaded=await ReloadChannelsAsync(duringTransition:true);
            if(reloaded && !_closing)SetStatus(()=>UiText.T("연결되었습니다. 왼쪽에서 채널과 목록을 선택하세요.","Connected. Select a channel and list on the left.","接続しました。左側でチャンネルと一覧を選択してください。"));
        }
        catch(Exception ex)when(IsStorageError(ex) || ex is InvalidDataException or Win32Exception or OperationCanceledException)
        {if(ex is OperationCanceledException)SetStatus(()=>UiText.T("연결이 취소되었거나 시간이 초과되었습니다.","The connection was canceled or timed out.","接続がキャンセルされたか、タイムアウトしました。"));else SetStatus(ex.Message);}
        finally{SetAccountBusy(false);DisplaySelectedChannel();UpdateSessionLabel();}
    }
    private async void ImportCookies_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _channelReloadBusy || !CanChangeAccountSettings())return;
        var dialog=new OpenFileDialog{Title=UiText.T("YouTube cookies.txt 선택","Select YouTube cookies.txt","YouTube cookies.txt を選択"),Filter=UiText.T("쿠키 파일 (*.txt)|*.txt|모든 파일|*.*","Cookie files (*.txt)|*.txt|All files|*.*","Cookie ファイル (*.txt)|*.txt|すべてのファイル|*.*")};if(dialog.ShowDialog(this)!=true)return;
        SetAccountBusy(true);
        try
        {
            var recovery=_session.HasSessionRecoveryFailure;var different=DifferentAccountCheck.IsChecked==true || recovery;
            var key=await ResetAccountAsync();if(recovery)key=Guid.NewGuid().ToString("N");
            await PersistSessionChangeAsync(()=>
            {
                _session.ImportFile(dialog.FileName);ForgetChannelSelection(different);
                _settings.AccountKey=key;_settings.AuthMode="쿠키 파일";return Task.CompletedTask;
            },allowRecovery:true);
            DifferentAccountCheck.IsChecked=false;UpdateSessionLabel();
            var reloaded=await ReloadChannelsAsync(duringTransition:true);
            if(reloaded && !_closing)SetStatus(()=>UiText.T("쿠키를 암호화하여 저장했습니다. 원본 파일은 변경하지 않았습니다.","Cookies were encrypted and saved. The original file was not changed.","Cookie を暗号化して保存しました。元のファイルは変更していません。"));
        }
        catch(OperationCanceledException)when(_closing){}
        catch(Exception ex)when(IsStorageError(ex) || ex is InvalidDataException){SetStatus(ex.Message);}
        finally{SetAccountBusy(false);DisplaySelectedChannel();UpdateSessionLabel();}
    }
    private async void Disconnect_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || _channelReloadBusy || !CanChangeAccountSettings())return;
        SetAccountBusy(true);
        try
        {
            await ResetAccountAsync();
            await PersistSessionChangeAsync(()=>
            {
                _session.Disconnect();ForgetChannelSelection(true);_settings.AuthMode="guest";_settings.AccountKey="guest";return Task.CompletedTask;
            });
            UpdateSessionLabel();
        }
        catch(OperationCanceledException)when(_closing){}
        catch(Exception error)when(IsStorageError(error)){SetStatus(()=>UiText.T("연결 해제 또는 설정 저장을 완료하지 못했습니다. 현재 연결 상태를 확인해 주세요.","Could not finish disconnecting or saving settings. Check the current connection state.","接続解除または設定の保存を完了できませんでした。現在の接続状態を確認してください。"));UpdateSessionLabel();}
        finally{SetAccountBusy(false);DisplaySelectedChannel();UpdateSessionLabel();}
    }
    private void BrowseProfile_Click(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title=UiText.T("Firefox 프로필 폴더 선택","Select Firefox profile folder","Firefox プロファイルフォルダーを選択")};if(dialog.ShowDialog(this)==true){var list=SessionService.ListFirefoxProfiles(out _profileListWarning).ToList();var p=new FirefoxProfile(Path.GetFileName(dialog.FolderName),dialog.FolderName);list.Add(p);ProfileCombo.ItemsSource=list;ProfileCombo.SelectedItem=p;ProfileCombo.ToolTip=string.IsNullOrWhiteSpace(_profileListWarning)?null:_profileListWarning;if(_profileListWarning!=null)SetStatus(_profileListWarning);}}
    private void OpenLogin_Click(object sender,RoutedEventArgs e)
    {
        var firefox=new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Mozilla Firefox","firefox.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Mozilla Firefox","firefox.exe")}.FirstOrDefault(File.Exists);
        if(firefox==null){SetStatus(()=>UiText.T("Firefox 실행 파일을 찾을 수 없습니다. 브라우저에서 YouTube에 로그인하세요.","Could not find the Firefox executable. Sign in to YouTube in your browser.","Firefox の実行ファイルが見つかりません。ブラウザーで YouTube にログインしてください。"));return;}
        var start=new ProcessStartInfo(firefox){UseShellExecute=false};if(ProfileCombo.SelectedItem is FirefoxProfile p){start.ArgumentList.Add("-profile");start.ArgumentList.Add(p.Path);}start.ArgumentList.Add("https://www.youtube.com/");Process.Start(start);
    }
    private async void SyncSetting_Changed(object sender,RoutedEventArgs e)
    {
        if(!_ready)return;
        _settings.SyncHistory=HistorySyncCheck.IsChecked==true;TrySaveSettings();
        if(_settings.SyncHistory)return;
        var previous=_historyCts;_historyCts=new();previous.Cancel();
        try{await Task.WhenAll(_historyTasks.ToArray());}catch(Exception error){Trace.TraceError("FK_YT history drain: {0}",error.GetType().Name);}finally{previous.Dispose();}
    }
    private async void PlaylistUrl_Click(object sender,RoutedEventArgs e){if(AccountWorkBlocked)return;if(!YouTubeService.TryPlaylistUrl(PlaylistUrlBox.Text,out var url)){SetStatus(()=>UiText.T("YouTube 재생목록 주소를 입력하세요.","Enter a YouTube playlist URL.","YouTube の再生リスト URL を入力してください。"));return;}_settings.PlaylistUrl=url;PlaylistUrlBox.Text=url;TrySaveSettings();if(await StopPlaybackAsync())await OpenFeedWithUiTitleAsync(url,()=>UiText.T("재생목록","Playlist","再生リスト"));}
    private void OpenData_Click(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={_paths.Data},UseShellExecute=true});
    private async void LocalHistory_Click(object sender,RoutedEventArgs e)=>await ShowLocalHistoryAsync();
    private async void RemoteHistory_Click(object sender,RoutedEventArgs e)=>await OpenFeedAsync("history",UiText.T("YouTube 시청 기록","YouTube watch history","YouTube 再生履歴"));
    private async Task ShowLocalHistoryAsync()
    {
        if(AccountWorkBlocked)return;
        if(_settings.Incognito){SetStatus(()=>UiText.T("시크릿 모드에서는 시청 기록을 조회하거나 저장하지 않습니다.","Watch history is not read or saved in incognito mode.","シークレットモードでは再生履歴の取得や保存を行いません。"));return;}
        if(_closing)return;var view=++_viewGeneration;if(!await StopPlaybackAsync() || _closing || view!=_viewGeneration)return;_feedCts?.Cancel();_feed.Stop();_feedGeneration++;_hasFeed=false;_hasMore=false;_loading=false;
        ClearDisplayedItems();SetPageUiText(()=>UiText.T("시청 기록 · 이 앱","Watch history · This app","再生履歴・このアプリ"));HistoryActions.Visibility=Visibility.Visible;MoreButton.Visibility=Visibility.Collapsed;WelcomePanel.Visibility=Visibility.Collapsed;ShowPanel("browse");
        _feedNavigationMetadata=null;ResetBrowseMetadata("localhistory");
        var account=_settings.AccountKey;SetFeedUiText(()=>UiText.T("로컬 시청 기록을 불러오는 중…","Loading local watch history…","ローカルの再生履歴を読み込み中…"));
        try
        {
            var (history,skipped)=await Task.Run(()=>{var rows=_store.GetHistory(account,out var ignored);return(rows,ignored);});
            if(AccountWorkBlocked || _settings.Incognito || view!=_viewGeneration || account!=_settings.AccountKey)return;
            foreach(var entry in history)
            {
                Func<string> detail=()=>UiText.F("{0} 지점 · {1}","At {0} · {1}","{0} の位置・{1}",VideoItem.FormatTime(entry.Position),SyncLabel(entry.SyncStatus));
                AppendItem(entry.Video with{LocalDetail=detail(),LocalDetailFactory=detail});
            }
            SetFeedUiText(()=>skipped>0?UiText.F("읽을 수 없는 기록 {0:N0}개를 건너뛰었습니다. 원본 기록은 보존했습니다.","Skipped {0:N0} unreadable history entries. The original records were preserved.","読み取れない履歴 {0:N0} 件をスキップしました。元の履歴は保持しました。",skipped):history.Count==0?UiText.T("이 앱에서 시청한 영상이 아직 없습니다.","No videos have been watched in this app yet.","このアプリで視聴した動画はまだありません。"):UiText.T("영상을 누르면 저장된 지점부터 이어봅니다.","Click a video to resume from its saved position.","動画を押すと保存された位置から再開します。"));
            SetStatus(()=>UiText.T("YouTube 계정의 기록과 이 앱의 이어보기 위치는 따로 관리합니다.","Your YouTube account history and this app's resume positions are managed separately.","YouTube アカウントの履歴と、このアプリの再開位置は個別に管理します。"));
        }
        catch(Exception error)when(IsStorageError(error))
        {
            if(!AccountWorkBlocked && view==_viewGeneration && account==_settings.AccountKey){SetFeedUiText(()=>UiText.T("로컬 기록을 읽지 못했습니다. 잠시 후 다시 열어 주세요.","Could not read local history. Open it again later.","ローカル履歴を読み取れませんでした。しばらくしてから開き直してください。"));SetStatus(()=>UiText.T("시청 기록 파일이 사용 중이거나 읽을 수 없습니다.","The watch history file is in use or cannot be read.","再生履歴ファイルが使用中か、読み取りできません。"));}
        }
    }
    private static string SyncLabel(string value)=>value switch{"sent"=>UiText.T("전송됨 · 반영 미확인","Sent · Account update unconfirmed","送信済み・反映未確認"),"confirmed"=>UiText.T("계정 기록에서 확인됨","Confirmed in account history","アカウントの履歴で確認済み"),"failed"=>UiText.T("전송 실패","Sending failed","送信失敗"),"sending"=>UiText.T("전송 중","Sending","送信中"),"cancelled"=>UiText.T("전송 취소 · 반영 미확인","Sending canceled · Account update unconfirmed","送信キャンセル・反映未確認"),_=>UiText.T("로컬 저장","Saved locally","ローカル保存")};
    private bool _allowClose;
    private readonly List<string> _shutdownErrors=[];
    private async void Window_Closing(object? sender,CancelEventArgs e)
    {
        if(_allowClose)return;e.Cancel=true;if(_closing)return;_closing=true;IsEnabled=false;
        void RecordFailure(string operation,Exception error)
        {
            _shutdownErrors.Add(operation+": "+error.GetType().Name);
            Trace.TraceError("FK_YT shutdown {0}: {1}",operation,error.GetType().Name);
        }
        void Cleanup(string operation,Action action){try{action();}catch(Exception error){RecordFailure(operation,error);}}
        async Task CleanupAsync(string operation,Func<Task> action){try{await action();}catch(Exception error){RecordFailure(operation,error);}}
        var shutdownCookie=_playCookie;var shutdownCookieOwner=_player.CapturePlaybackOwner();
        void RetainShutdownCookie()
        {
            if(_playCookie is not {} cookie)return;
            var owner=ReferenceEquals(cookie,shutdownCookie)?shutdownCookieOwner:_player.CapturePlaybackOwner();
            _player.RetainPlaybackCookies(owner,cookie);_playCookie=null;
        }
        try
        {
            Cleanup("save timer",_saveTimer.Stop);
            Cleanup("playback cookie ownership",RetainShutdownCookie);
            Cleanup("player view controls",DisposePlayerViewModes);
            Cleanup("description popup",CloseDescriptionPopup);
            Cleanup("subtitle popup",CloseSubtitlePopup);
            Cleanup("authentication countdown",()=>CancelAuthenticationSkip(clearNotice:true));
            Cleanup("popout",ClosePopoutForShutdown);
            Cleanup("account cancellation",_accountCts.Cancel);Cleanup("history cancellation",_historyCts.Cancel);
            Cleanup("feed cancellation",()=>_feedCts?.Cancel());Cleanup("video cancellation",()=>_videoCts?.Cancel());
            Cleanup("comments cancellation",()=>_commentsCts?.Cancel());Cleanup("quality cancellation",()=>_qualityCts?.Cancel());
            Cleanup("position save",()=>SavePosition());
            await CleanupAsync("download drain",CancelDownloadAsync);
            await CleanupAsync("account drain",CancelAccountWorkAsync);
            // Mutations are separate from account reads: a change may itself
            // drain readers. Its rollback/export finally must finish before the
            // final settings save and the cookie cleanup snapshot.
            await CleanupAsync("account mutation drain",()=>Task.WhenAll(_accountMutationTasks.ToArray()));
            Cleanup("settings save",()=>_store.SaveSettings(_settings));
            await CleanupAsync("playback stop",StopPlaybackAsync);
            await CleanupAsync("feed disposal",()=>_feed.DisposeAsync().AsTask());
            await CleanupAsync("player disposal",()=>_player.DisposeAsync().AsTask());
            await CleanupAsync("owned process recovery drain",ProcessRunner.DrainRecoveryAsync);
            await CleanupAsync("download staging cleanup drain",DownloadService.DrainStageCleanupAsync);
        }
        finally
        {
            Cleanup("playback cookies",RetainShutdownCookie);
            await CleanupAsync("cookie deletion drain",_session.DrainCookieCleanupAsync);
            Cleanup("history token",_historyCts.Dispose);
            Cleanup("thumbnail cache",()=>ThumbnailImage.Cache?.Dispose());ThumbnailImage.Cache=null;
            _allowClose=true;_=Dispatcher.BeginInvoke(Close,DispatcherPriority.Normal);
        }
    }
    private void ShowDemo()
    {
        ClearDisplayedItems();_hasFeed=false;_hasMore=false;WelcomePanel.Visibility=Visibility.Collapsed;ShowPanel("browse");
        if(_pageTextFactory is null)SetPageUiText(()=>UiText.T("홈 추천","Home recommendations","ホームのおすすめ"));
        _feedNavigationMetadata=null;ResetBrowseMetadata("demo");
        var titles=new[]{"숲속의 아침, 창문 너머의 풍경","작게 시작하는 작업 공간 만들기","우주를 바라보는 새로운 방법","비 오는 날 듣는 피아노","좋아하는 것을 오래 즐기는 방법","도시를 떠나 걷는 하루","정리된 화면이 주는 여유","아름다운 건축을 찾아서"};
        for(var i=0;i<36;i++)AppendItem(new(){Id=$"demo{i}",Title=titles[i%titles.Length],Channel=new[]{"일상의 기록","Studio Notes","Science Today","Slow Archive"}[i%4],Duration=180+i*47,ViewCount=12400+i*3400,Url="https://www.youtube.com/watch?v=BaW_jenozKc"});
        SetFeedUiText(()=>UiText.T("데모 화면 · 네트워크 요청 없음","Demo screen · No network requests","デモ画面・ネットワークリクエストなし"));MoreButton.IsEnabled=false;SetStatus(()=>UiText.T("데모 화면입니다. 실제 계정·영상 재생 검증과 구분됩니다.","This is a demo screen. It does not verify real accounts or video playback.","これはデモ画面です。実際のアカウントや動画再生の検証とは別です。"));
    }
    private async Task CaptureSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();
        foreach(var scale in new[]{1d,1.25,1.5})
        {
            LayoutRoot.LayoutTransform=new ScaleTransform(scale,scale);Width=1380*scale;Height=900*scale;UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(this);
            using(var file=File.Create(Path.Combine(directory,$"browse-{scale:0.00}.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);}
            foreach(var button in new[]{HomeButton,SettingsButton})
            {
                var point=button.TranslatePoint(new Point(button.ActualWidth-5,button.ActualHeight/2),this);var hit=InputHitTest(point) as DependencyObject;var matches=false;
                while(hit!=null){if(ReferenceEquals(hit,button)){matches=true;break;}hit=VisualTreeHelper.GetParent(hit);}
                checks.Add(new{scale,control=button.Name,width=button.ActualWidth,height=button.ActualHeight,hit=matches});
            }
        }
        LayoutRoot.LayoutTransform=Transform.Identity;
        File.WriteAllText(Path.Combine(directory,"ui-smoke.json"),JsonSerializer.Serialize(new{type="WPF layout and hit-test simulation; not native DPI or mouse validation",checks},new JsonSerializerOptions{WriteIndented=true}));
    }
}
