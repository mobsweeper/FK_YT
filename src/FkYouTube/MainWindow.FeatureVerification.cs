using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private async Task RunChannelProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();string? failure=null;
        _settings.SyncHistory=false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var channels=await _session.ListChannelsAsync(deadline.Token);var keys=new HashSet<string>();
            foreach(var channel in channels.Take(2))
            {
                await ApplyChannelAsync(channel);keys.Add(_settings.AccountKey);
                _session.RestoreSelectedChannel(channel.Id);
                using var lease=AcquirePlaybackSession();
                checks.Add(new{name="selected-session-DPAPI-restored",passed=_session.SelectedChannel?.Id==channel.Id && lease.Path!=null && File.Exists(lease.Path)});
            }
            checks.Add(new{name="two-channels-have-separate-local-history-keys",passed=channels.Count>=2 && keys.Count==2});
            _settings.Incognito=true;using(var lease=AcquirePlaybackSession())checks.Add(new{name="incognito-does-not-reuse-selected-channel-cookie",passed=lease.Path==null});
            checks.Add(new{name="no-playback-history-created",passed=_store.GetHistory(_settings.AccountKey).Count==0});
        }
        catch(Exception ex){failure=ex.GetType().Name;}
        File.WriteAllText(Path.Combine(directory,"channel-smoke.json"),JsonSerializer.Serialize(new{failure,checks,scope="C# channel list/switch/encrypted restore and account isolation; no video or history/feedback writes."},new JsonSerializerOptions{WriteIndented=true}));
    }
    private async Task RunFeatureProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();
        _settings.SyncHistory=false;_settings.AccountKey="feature-fixture";
        var video=new VideoItem{Id="fixture1",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ",Title="Fixture"};
        _settings.Incognito=true;_current=video;_hasLoadedCurrent=true;_lastValidPosition=36;_lastValidDuration=120;
        SavePosition();using(var lease=AcquirePlaybackSession())checks.Add(new{name="incognito-no-cookie-or-local-history",passed=lease.Path==null && _store.GetHistory(_settings.AccountKey).Count==0});
        _settings.Incognito=false;SavePosition();checks.Add(new{name="normal-mode-still-saves-resume",passed=Math.Abs(_store.GetResumePosition(_settings.AccountKey,video.Id)-36)<.01});
        _current=null;_settings.MaxHeight=720;_resolutions=[144,360,720,1080,2160];var generation=_videoGeneration;
        DisplayResolutions();checks.Add(new{name="quality-list-from-provided-resolutions-no-reload",passed=QualityCombo.Items.Cast<int>().SequenceEqual(_resolutions.Prepend(0)) && QualityCombo.SelectedItem is int selected && selected==720 && generation==_videoGeneration});
        _current=video;_expectedMediaGeneration=51;_hasLoadedCurrent=true;_settings.AutoPlayNext=true;
        OnSnapshot(new(){VideoUrl=video.Url,MediaGeneration=51,Ended=true,EndReason="error"});
        checks.Add(new{name="error-end-does-not-autoplay",passed=ReferenceEquals(_current,video) && _autoplayHandledGeneration!=_videoGeneration});
        await _videoStartGate.WaitAsync();
        Task reload;
        try
        {
            reload=PlayVideoAsync(video,true,55);
            OnSnapshot(new(){VideoUrl=video.Url,MediaGeneration=51,Loaded=true,Position=5,Duration=120});
            checks.Add(new{name="old-same-video-event-cannot-change-pending-reload",passed=_lastValidPosition==55 && !_hasLoadedCurrent});
            _videoCts?.Cancel();
        }
        finally{_videoStartGate.Release();}
        await reload;
        _items.Clear();_items.Add(new(){Kind="playlist",Id="fixture-list"});
        _items.Add(video with{Id="fixture-member",MembersOnly=true});_items.Add(video);
        _settings.HideMembersOnly=true;_settings.Incognito=true;
        await _videoStartGate.WaitAsync();Task playList;
        try
        {
            playList=PlayListAsync();
            checks.Add(new{name="playlist-play-button-starts-first-eligible-video",passed=ReferenceEquals(_current,video) && _playingIndex==2 && _lastValidPosition==0 && _videoCts!=null});
            _videoCts?.Cancel();
        }
        finally{_videoStartGate.Release();}
        await playList;
        _current=video;_items.Clear();_items.Add(video);_playingIndex=0;_playingListGeneration=_feedGeneration;
        _hasFeed=true;_hasMore=false;_feedExhausted=false;
        _relatedItems.Add(new(){Id="fixture2",Url="https://www.youtube.com/watch?v=5EQSpS5WvdI",Title="Related fixture"});
        UpdateUpNext();checks.Add(new{name="failed-playlist-page-does-not-jump-to-recommendations",passed=_upNext.Count==0});
        _feedExhausted=true;UpdateUpNext();checks.Add(new{name="confirmed-playlist-end-allows-related-next",passed=_upNext.Count==1});
        _current=null;ShowDemo();
        _dismissedChannels.Add("hide-fixture");var before=_items.Count;AppendItem(new(){Id="hidden",ChannelId="hide-fixture"});
        checks.Add(new{name="dismissed-channel-does-not-return-in-next-batch",passed=_items.Count==before});
        // Pending history work must be cancelled by the setting before entering the sender.
        _settings.Incognito=false;HistorySyncCheck.IsChecked=true;
        await _historyQueue.WaitAsync();Task queuedHistory;
        try
        {
            queuedHistory=SendHistoryAsync(video,_settings.AccountKey,_accountCts.Token);TrackHistory(queuedHistory);
            HistorySyncCheck.IsChecked=false;
        }
        finally{_historyQueue.Release();}
        await queuedHistory;
        checks.Add(new{name="history-off-cancels-queued-send",passed=!_settings.SyncHistory && _store.GetHistory(_settings.AccountKey).Single(v=>v.Video.Id==video.Id).SyncStatus=="local"});
        // A loading feed with an available next candidate must not block playback.
        var second=video with{Id="fixture-second"};
        _settings.Incognito=true;_items.Clear();_items.Add(video);_items.Add(second);
        _current=video;_playingIndex=0;_playingListGeneration=_feedGeneration;_hasFeed=true;_hasMore=true;
        ShowPanel("player");
        var pendingFeed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading=true;_feedLoadTask=pendingFeed.Task;
        await _videoStartGate.WaitAsync();Task nextTask;
        try
        {
            nextTask=AutoPlayNextAsync(_videoGeneration,_autoplayEpoch,true);
            checks.Add(new{name="loaded-next-does-not-wait-for-feed-page",passed=ReferenceEquals(_current,second) && _videoCts!=null && !pendingFeed.Task.IsCompleted});
            checks.Add(new{name="ordinary-playback-quality-lookup-is-lazy",passed=_qualityCts==null && QualityLoadButton.Visibility==Visibility.Visible && QualityCombo.Items.Count==0});
            _videoCts?.Cancel();_autoplayEpoch++;pendingFeed.SetResult();_loading=false;
        }
        finally{_videoStartGate.Release();}
        await nextTask;
        pendingFeed=new(TaskCreationOptions.RunContinuationsAsynchronously);_feedLoadTask=pendingFeed.Task;_loading=true;
        await _videoStartGate.WaitAsync();Task listTask;
        try
        {
            listTask=PlayListAsync();
            checks.Add(new{name="playlist-first-video-does-not-wait-for-full-page",passed=ReferenceEquals(_current,video) && _videoCts!=null && !pendingFeed.Task.IsCompleted});
            _videoCts?.Cancel();_feedGeneration++;pendingFeed.SetResult();_loading=false;
        }
        finally{_videoStartGate.Release();}
        await listTask;
        var recommendation=video with{Id="fixture-related"};
        _playbackTrail.Clear();_playbackTrail.Add(video);_playbackTrail.Add(recommendation);
        _current=second;_playingIndex=1;_playingListGeneration=_feedGeneration;
        await _videoStartGate.WaitAsync();Task previousTask;bool previousPending;
        try
        {
            previousTask=PreviousVideoAsync();
            previousPending=ReferenceEquals(_current,recommendation) && _playbackTrail.Count==2 && !previousTask.IsCompleted;
            _videoCts?.Cancel();
        }
        finally{_videoStartGate.Release();}
        await previousTask;
        checks.Add(new{name="cancelled-previous-retains-mixed-list-history",passed=previousPending && ReferenceEquals(PreviousVideo(),recommendation) && _playbackTrail.Count==2});
        await RunBrowseUiProbeAsync(checks,directory);
        await RunRatingsUiProbeAsync(checks,directory);
        await RunDescriptionUiProbeAsync(checks,directory);
        RunAuthenticationNoticeUiProbe(checks);
        await RunAuthenticationSkipProbeAsync(checks);
        _current=null;_hasFeed=false;_hasMore=false;ShowDemo();
        await CaptureSmokeAsync(directory);
        File.WriteAllText(Path.Combine(directory,"feature-smoke.json"),JsonSerializer.Serialize(new{checks,scope="Offline WPF state and rendering; no account/network feedback writes."},new JsonSerializerOptions{WriteIndented=true}));
    }
    private async Task RunRatingsUiProbeAsync(List<object> checks,string directory)
    {
        if(!_demo || _playerStarted)throw new InvalidOperationException("Ratings UI probe requires an offline demo window.");
        var originalPrivate=_settings.Incognito;var originalAccount=_settings.AccountKey;
        var originalWidth=Width;var originalHeight=Height;var originalTransform=LayoutRoot.LayoutTransform;
        var originalChannelName=PlayingChannelName.Text;
        var item=new VideoItem{Id="aqz-KE-bpKQ",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ",Title="평가 기능 검사 · 합성 수치"};
        try
        {
            _settings.Incognito=true;_current=item;_videoMetadataGeneration++;
            ResetVideoRatings();ShowPanel("player");PlayingTitle.Text=item.Title;
            PlayingChannelName.Text="배치 확인을 위한 아주 긴 합성 채널 이름 · 긴 이름은 말줄임으로 표시";
            checks.Add(new{name="ratings-incognito-no-auto-query-or-vote",passed=!LikeButton.IsEnabled && !DislikeButton.IsEnabled && !RatingRefreshButton.IsEnabled && DislikeStatsButton.IsEnabled && _ratingReadCts==null && _dislikeStatsCts==null});
            var account=RatingAccountStamp;var generation=_videoMetadataGeneration;var revision=_ratingRevision;
            ApplyVideoRating(new(item.Id,"like",true),item,generation,account,revision);
            checks.Add(new{name="rating-state-selected-like",passed=_videoRating?.Value=="like" && LikeButton.Content.ToString()!.StartsWith("✓ 👍") && !DislikeButton.Content.ToString()!.StartsWith("✓") && !LikeButton.IsEnabled});
            checks.Add(new{name="rating-toggle-cancel-and-switch",passed=RatingToggleTarget("like","like")=="none" && RatingToggleTarget("dislike","dislike")=="none" && RatingToggleTarget("like","dislike")=="dislike" && RatingToggleTarget("none","like")=="like"});
            ApplyVideoRating(new(item.Id,"dislike",true),item,generation,account,revision-1);
            checks.Add(new{name="rating-stale-revision-ignored",passed=_videoRating?.Value=="like"});
            _ratingBusy=true;var duringWriteRevision=RelatedRatingRevision;_ratingBusy=false;
            ApplyVideoRating(new(item.Id,"none",true),item,generation,account,duringWriteRevision);
            checks.Add(new{name="rating-related-read-during-write-cannot-overwrite-result",passed=_videoRating?.Value=="like"});
            ApplyVideoRating(new(item.Id,"dislike",true),item,generation-1,account,revision);
            checks.Add(new{name="rating-old-video-generation-ignored",passed=_videoRating?.Value=="like"});
            ApplyVideoRating(new(item.Id,"dislike",true),item,generation,"different-account",revision);
            checks.Add(new{name="rating-other-account-result-ignored",passed=_videoRating?.Value=="like"});
            ApplyVideoRating(new("BaW_jenozKc","dislike",true),item,generation,account,revision);
            checks.Add(new{name="rating-other-video-response-ignored",passed=_videoRating?.Value=="like"});
            ApplyVideoRating(new(item.Id,"dislike",true),item,generation,account,revision);
            checks.Add(new{name="rating-state-selected-dislike",passed=_videoRating?.Value=="dislike" && DislikeButton.Content.ToString()!.StartsWith("✓ 👎") && !LikeButton.Content.ToString()!.StartsWith("✓")});
            _dislikeStats=new(item.Id,123456,789);UpdateRatingControls();
            checks.Add(new{name="ryd-count-is-labelled-estimate",passed=DislikeButton.Content.ToString()!.Contains("👎 ≈") && LikeButton.Content.ToString()!.StartsWith("👍 ") && LikeButton.Content.ToString()!.Contains(123456.ToString("N0")) && RydAttributionButton.Content.ToString()!.Contains("Return YouTube Dislike")});
            ResetPlaybackExtras(preserveMetadata:true);
            checks.Add(new{name="quality-change-retains-rating-and-counts",passed=_videoRating?.Value=="dislike" && _dislikeStats?.Dislikes==789});
            using(var pending=new CancellationTokenSource())
            {
                _ratingReadCts=pending;SetAccountBusy(true);
                checks.Add(new{name="rating-account-transition-cancels-and-disables",passed=pending.IsCancellationRequested && !LikeButton.IsEnabled && !RatingRefreshButton.IsEnabled && !DislikeStatsButton.IsEnabled});
                ApplyVideoRating(new(item.Id,"like",true),item,generation,RatingAccountStamp,_ratingRevision);
                checks.Add(new{name="rating-transition-result-ignored",passed=_videoRating?.Value=="dislike"});
                _ratingReadCts=null;SetAccountBusy(false);
            }
            _settings.Incognito=false;UpdateRatingControls();
            checks.Add(new{name="ratings-guest-cannot-vote",passed=!_session.IsConnected && !LikeButton.IsEnabled && !DislikeButton.IsEnabled && !RatingRefreshButton.IsEnabled && DislikeStatsButton.IsEnabled});
            _videoRating=new(item.Id,"unknown",false);UpdateRatingControls();
            checks.Add(new{name="unknown-rating-does-not-enable-vote",passed=!LikeButton.IsEnabled && !DislikeButton.IsEnabled});
            DislikeStatsState.Text="싫어요 ≈ RYD 추정치 · 합성 수치";
            var subtitleMenu=new ContextMenu{Style=(Style)FindResource("SubtitleMenuStyle")};
            PopulateSubtitleMenu(subtitleMenu,new SubtitleSelection(10,[new(3,"한국어 (ko)","ko",true),new(7,"English · 자동 생성","en",false)],false),_videoGeneration);
            var subtitleOptions=subtitleMenu.Items.OfType<MenuItem>().ToArray();
            checks.Add(new{name="subtitle-menu-languages-and-current-selection",passed=subtitleOptions.Length==3 && subtitleOptions[0].Header.ToString()!.Contains("자막 끄기") && (long)subtitleOptions[1].Tag==7 && subtitleOptions[2].Header.ToString()!.StartsWith("✓") && (long)subtitleOptions[2].Tag==3});
            PopulateSubtitleMenu(subtitleMenu,new SubtitleSelection(10,[],true),_videoGeneration);
            checks.Add(new{name="subtitle-menu-off-and-empty-state",passed=((MenuItem)subtitleMenu.Items[0]).Header.ToString()!.StartsWith("✓") && !((MenuItem)subtitleMenu.Items[1]).IsEnabled});
            AvailableSubtitle[] allLanguages=[new("ja","日本語","https://www.youtube.com/api/timedtext?v=aqz-KE-bpKQ&lang=ja",false),
                new("es","Español","https://www.youtube.com/api/timedtext?v=aqz-KE-bpKQ&lang=es",true),
                new("fr","Français","https://www.youtube.com/api/timedtext?v=aqz-KE-bpKQ&lang=fr",false)];
            AppendSubtitleCatalog(subtitleMenu,allLanguages,10,_videoGeneration);
            var languages=subtitleMenu.Items.OfType<MenuItem>().Where(option=>option.Tag is string).ToArray();
            checks.Add(new{name="subtitle-catalog-includes-non-ko-en-and-auto-languages",passed=languages.Select(option=>(string)option.Tag).Order().SequenceEqual(new[]{"es","fr","ja"}) && subtitleMenu.Items.OfType<MenuItem>().Any(option=>option.Header.ToString()=="기계번역 · 자동 생성") && _subtitleTasks.Count==0});
            var orderedMenu=new ContextMenu{Style=(Style)FindResource("SubtitleMenuStyle")};
            PopulateSubtitleMenu(orderedMenu,new SubtitleSelection(10,[new(9,"현재 일본어","ja",true)],false),_videoGeneration);
            AvailableSubtitle Caption(string language,bool automatic)=>new(language,language,"https://www.youtube.com/api/timedtext?v=aqz-KE-bpKQ&lang="+language,automatic);
            AppendSubtitleCatalog(orderedMenu,[Caption("fr",false),Caption("ja",false),Caption("ko-orig",false),Caption("en-US",false),
                Caption("de",true),Caption("ja",true),Caption("ko",true),Caption("en",true)],10,_videoGeneration);
            var orderedOptions=orderedMenu.Items.OfType<MenuItem>().ToArray();
            var loadedFallback=orderedOptions.Single(option=>option.Header.ToString()=="불러온 자막");
            checks.Add(new{name="subtitle-catalog-official-then-machine-en-ko-ja-rest",passed=orderedOptions[0].Header.ToString()!.Contains("자막 끄기") && orderedOptions[1].Header.ToString()=="공식 자막 · 제작자 제공" && orderedOptions[6].Header.ToString()=="기계번역 · 자동 생성" &&
                orderedOptions.Where(option=>option.Tag is string).Select(option=>(string)option.Tag).SequenceEqual(new[]{"en-US","ko-orig","ja","fr","en","ko","ja","de"}) &&
                loadedFallback.Items.OfType<MenuItem>().Single().Header.ToString()!.StartsWith("✓") && (long)((MenuItem)loadedFallback.Items[0]).Tag==9 &&
                SubtitleLanguagePriority("ENG")==0 && SubtitleLanguagePriority("kor")==1 && SubtitleLanguagePriority("jpn")==2 && SubtitleLanguagePriority(null)==3});
            _subtitleCatalog=allLanguages;_subtitleCatalogKey="fixture";_subtitleCatalogExpires=DateTimeOffset.UtcNow.AddMinutes(5);
            ResetPlaybackExtras(preserveMetadata:true);
            checks.Add(new{name="subtitle-catalog-survives-quality-only-change",passed=_subtitleCatalog.Count==3});
            ResetSubtitleCatalog();
            checks.Add(new{name="subtitle-catalog-reset-drops-video-urls",passed=_subtitleCatalog.Count==0 && _subtitleCatalogKey==null});
            foreach(var scale in new[]{1d,1.25,1.5})
            {
                LayoutRoot.LayoutTransform=new ScaleTransform(scale,scale);Width=1380*scale;Height=900*scale;UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                checks.Add(new{name="rating-controls-scaled-layout",scale,passed=RatingPanel.IsVisible && LikeButton.ActualWidth>0 && DislikeButton.ActualWidth>0 && BrowseProbeButtonHit(DislikeStatsButton) && BrowseProbeButtonHit(SubtitleButton),scope="WPF scale and hit target; not native display DPI"});
                var channelCenter=PlayingChannelPanel.TranslatePoint(new Point(0,PlayingChannelPanel.ActualHeight/2),VideoColumn);
                var ratingCenter=RatingPanel.TranslatePoint(new Point(0,RatingPanel.ActualHeight/2),VideoColumn);
                checks.Add(new{name="compact-ratings-share-channel-row",scale,passed=PlayingChannelPanel.Parent is WrapPanel && ReferenceEquals(PlayingChannelPanel.Parent,RatingPanel.Parent) && Math.Abs(channelCenter.Y-ratingCenter.Y)<1 && ratingCenter.X>=channelCenter.X+PlayingChannelPanel.ActualWidth && PlayingChannelButton.ActualWidth<=220});
                checks.Add(new{name="compact-rating-status-and-credit-in-tooltips",scale,passed=RatingState.Visibility==Visibility.Collapsed && DislikeStatsState.Visibility==Visibility.Collapsed && RydAttributionButton.Visibility==Visibility.Collapsed && LikeButton.ToolTip?.ToString()?.Contains(RatingState.Text)==true && RatingRefreshButton.ToolTip?.ToString()?.Contains(RatingState.Text)==true && DislikeButton.ToolTip?.ToString()?.Contains(DislikeStatsState.Text)==true && DislikeStatsButton.ToolTip?.ToString()?.Contains(DislikeStatsState.Text)==true && DislikeStatsButton.ToolTip?.ToString()?.Contains("Return YouTube Dislike")==true && DislikeStatsButton.ContextMenu?.Items.OfType<MenuItem>().Any(option=>option.Header.ToString()!.Contains("Return YouTube Dislike"))==true && ToolTipService.GetShowOnDisabled(LikeButton)});
                checks.Add(new{name="compact-playback-controls",scale,passed=PreviousVideoButton.Padding==new Thickness(8,5,8,5) && PreviousVideoButton.ActualHeight<=32 && FullscreenButton.ActualWidth==30 && FullscreenButton.ActualHeight==30 && SpeedCombo.MinWidth==70 && SpeedCombo.ActualHeight<=32 && LikeButton.Padding==new Thickness(8,5,8,5) && LikeButton.ActualHeight<=32});
                var dpi=VisualTreeHelper.GetDpi(this);
                var image=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(this);
                using var file=File.Create(Path.Combine(directory,"ratings-"+scale.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+".png"));
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);
            }
            LayoutRoot.LayoutTransform=originalTransform;Width=MinWidth;Height=originalHeight;UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var channelBottom=PlayingChannelPanel.TranslatePoint(new Point(PlayingChannelPanel.ActualWidth,PlayingChannelPanel.ActualHeight),VideoColumn);
            var ratingTop=RatingPanel.TranslatePoint(new Point(),VideoColumn);
            var ratingRight=RatingPanel.TranslatePoint(new Point(RatingPanel.ActualWidth,0),VideoColumn);
            checks.Add(new{name="compact-ratings-wrap-at-minimum-window-width",passed=RatingPanel.IsVisible && PlayingChannelButton.ActualWidth>0 && PlayingChannelButton.ActualWidth<=220 && PlayingChannelName.TextTrimming==TextTrimming.CharacterEllipsis && ratingTop.Y>=channelBottom.Y && ratingRight.X<=VideoColumn.ActualWidth+1 && BrowseProbeButtonHit(DislikeStatsButton),channelButtonWidth=PlayingChannelButton.ActualWidth,channelNameWidth=PlayingChannelName.ActualWidth,channelBottom,ratingTop,ratingRight,columnWidth=VideoColumn.ActualWidth,statsHit=BrowseProbeButtonHit(DislikeStatsButton),scope="Offline WPF minimum window width with a long synthetic channel name"});
            var narrowDpi=VisualTreeHelper.GetDpi(this);
            var narrowImage=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*narrowDpi.DpiScaleX),(int)Math.Ceiling(ActualHeight*narrowDpi.DpiScaleY),96*narrowDpi.DpiScaleX,96*narrowDpi.DpiScaleY,PixelFormats.Pbgra32);narrowImage.Render(this);
            using(var narrowFile=File.Create(Path.Combine(directory,"ratings-narrow.png")))
            {var narrowEncoder=new PngBitmapEncoder();narrowEncoder.Frames.Add(BitmapFrame.Create(narrowImage));narrowEncoder.Save(narrowFile);}
            // The scale simulation deliberately grows the window beyond a monitor.
            // Restore its normal bounds before checking a native popup's placement.
            LayoutRoot.LayoutTransform=originalTransform;Width=originalWidth;Height=originalHeight;UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            subtitleMenu.PlacementTarget=SubtitleButton;subtitleMenu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;
            PopulateSubtitleMenu(subtitleMenu,new SubtitleSelection(10,Enumerable.Range(1,30).Select(i=>new SubtitleTrack(i,"한국어 · 합성 트랙 "+i,"ko",i==1)).ToArray(),false),_videoGeneration);
            try
            {
                subtitleMenu.IsOpen=true;subtitleMenu.UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var scroll=BrowseProbeDescendants<ScrollViewer>(subtitleMenu).FirstOrDefault();
                checks.Add(new{name="subtitle-dropdown-scrolls-long-track-list",passed=subtitleMenu.IsVisible && scroll?.ScrollableHeight>0 && subtitleMenu.Items.OfType<MenuItem>().Count()==31});
                var anchorTop=SubtitleButton.PointToScreen(new Point());
                var anchorBottom=SubtitleButton.PointToScreen(new Point(SubtitleButton.ActualWidth,SubtitleButton.ActualHeight));
                var menuTop=subtitleMenu.PointToScreen(new Point());
                var menuBottom=subtitleMenu.PointToScreen(new Point(subtitleMenu.ActualWidth,subtitleMenu.ActualHeight));
                var adjacent=Math.Min(Math.Abs(menuBottom.Y-anchorTop.Y),Math.Abs(menuTop.Y-anchorBottom.Y))<=20;
                checks.Add(new{name="subtitle-dropdown-attaches-to-button",passed=subtitleMenu.IsVisible && adjacent && menuTop.X<=anchorBottom.X && menuBottom.X>=anchorTop.X,
                    anchorTop,anchorBottom,menuTop,menuBottom,scope="Native WPF popup screen coordinates at normal window size; no physical click"});
                if(subtitleMenu.ActualWidth>0 && subtitleMenu.ActualHeight>0)
                {
                    var dpi=VisualTreeHelper.GetDpi(subtitleMenu);
                    var picture=new RenderTargetBitmap((int)Math.Ceiling(subtitleMenu.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(subtitleMenu.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);picture.Render(subtitleMenu);
                    using var imageFile=File.Create(Path.Combine(directory,"subtitle-dropdown.png"));
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(picture));png.Save(imageFile);
                }
            }
            finally{subtitleMenu.IsOpen=false;}
            PopulateSubtitleMenu(subtitleMenu,new SubtitleSelection(10,[new(1,"한국어","ko",true),new(2,"영어","en",false)],false),_videoGeneration);
            AppendSubtitleCatalog(subtitleMenu,allLanguages,10,_videoGeneration);
            try
            {
                subtitleMenu.IsOpen=true;subtitleMenu.UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var dpi=VisualTreeHelper.GetDpi(subtitleMenu);
                var picture=new RenderTargetBitmap((int)Math.Ceiling(subtitleMenu.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(subtitleMenu.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);picture.Render(subtitleMenu);
                using var imageFile=File.Create(Path.Combine(directory,"subtitle-all-languages.png"));
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(picture));png.Save(imageFile);
            }
            finally{subtitleMenu.IsOpen=false;}
            using(var read=new CancellationTokenSource())using(var send=new CancellationTokenSource())using(var counts=new CancellationTokenSource())
            {
                _ratingReadCts=read;_ratingSendCts=send;_dislikeStatsCts=counts;
                ResetVideoRatings();
                checks.Add(new{name="video-change-clears-and-cancels-ratings",passed=read.IsCancellationRequested && send.IsCancellationRequested && counts.IsCancellationRequested && _videoRating==null && _dislikeStats==null});
                _ratingReadCts=null;_ratingSendCts=null;_dislikeStatsCts=null;
            }
        }
        finally
        {
            _settings.Incognito=originalPrivate;_settings.AccountKey=originalAccount;_current=null;
            PlayingChannelName.Text=originalChannelName;
            LayoutRoot.LayoutTransform=originalTransform;Width=originalWidth;Height=originalHeight;
            ResetVideoRatings();UpdateLayout();
        }
    }
    private async Task RunBrowseUiProbeAsync(List<object> checks,string directory)
    {
        // The feature flag supplies demo feeds. Keep avatar fixtures offline as well.
        var thumbnailCache=ThumbnailImage.Cache;ThumbnailImage.Cache=null;
        try
        {
            _settings.Incognito=true;_current=null;_hasLoadedCurrent=false;_hasMore=false;
            ShowPanel("browse");
            var channelMetadata=new FeedNavigationMetadata
            {
                Title="Fixture channel videos",FilterKind="channel",
                Channel=new(){Id="UC1234567890123456789012",Title="Fixture channel",Url="https://www.youtube.com/@FixtureCreator/videos",Thumbnail="https://images.fixture.invalid/channel.png",Description="Fixture channel description"},
                Filters=[new(){Id="server-latest",Label="서버 최신순",Selected=true},new(){Id="server-popular",Label="서버 인기순"},new(){Id="server-oldest",Label="서버 오래된순"}]
            };
            ApplyBrowseMetadata(channelMetadata);UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks.Add(new{name="channel-header-displays-server-metadata",passed=ChannelHeader.Visibility==Visibility.Visible && ChannelHeaderTitle.Text==channelMetadata.Channel.Title && ChannelHeaderDescription.Text==channelMetadata.Channel.Description && ChannelHeaderAvatar.Url==channelMetadata.Channel.Thumbnail && PageTitle.Text==channelMetadata.Title});
            var filterButtons=BrowseProbeDescendants<Button>(BrowseFilterItems).ToArray();
            var selectedFilter=filterButtons.SingleOrDefault(button=>button.Tag is FeedFilter{Selected:true});
            var unselectedFilter=filterButtons.FirstOrDefault(button=>button.Tag is FeedFilter{Selected:false});
            checks.Add(new{name="channel-filters-preserve-server-labels-and-selected-state",passed=BrowseFiltersScroll.Visibility==Visibility.Visible && filterButtons.Select(button=>button.Content?.ToString()).SequenceEqual(channelMetadata.Filters.Select(filter=>filter.Label)) && selectedFilter!=null && unselectedFilter!=null && selectedFilter.FontWeight==FontWeights.SemiBold && !Equals(selectedFilter.Background,unselectedFilter.Background)});
            var homeMetadata=new FeedNavigationMetadata{Title="Fixture home",FilterKind="home",Filters=[new(){Id="opaque-home-music",Label="서버 음악",Selected=true},new(){Id="opaque-home-mix",Label="서버 믹스"}]};
            ApplyBrowseMetadata(homeMetadata);
            checks.Add(new{name="home-filters-replace-channel-header-with-server-chips",passed=ChannelHeader.Visibility==Visibility.Collapsed && ChannelHeaderAvatar.Url==null && ChannelHeaderTitle.Text=="" && BrowseFiltersScroll.Visibility==Visibility.Visible && BrowseFilterItems.Items.Cast<FeedFilter>().SequenceEqual(homeMetadata.Filters)});
            ResetBrowseMetadata("search:fixture");
            checks.Add(new{name="browse-navigation-reset-removes-stale-channel-and-filters",passed=ChannelHeader.Visibility==Visibility.Collapsed && ChannelHeaderAvatar.Url==null && ChannelHeaderDescription.Text=="" && BrowseFiltersScroll.Visibility==Visibility.Collapsed && BrowseFilterItems.Items.Count==0});

            var video=new VideoItem{Id="browse-fixture",Title="Browse fixture",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ"};
            _current=video;_items.Clear();_items.Add(video);_playingIndex=0;
            UpdatePlayingChannel(video);
            checks.Add(new{name="playing-channel-awaits-owner-with-disabled-neutral-row",passed=PlayingChannelPanel.Visibility==Visibility.Visible && !PlayingChannelButton.IsEnabled && PlayingChannelName.Text=="채널 정보 확인 중" && PlayingChannelAvatar.Url==null && ReferenceEquals(PlayingChannelButton.Tag,video)});
            var ownerVideo=video with{Channel="Fixture owner",ChannelId=channelMetadata.Channel.Id,ChannelUrl=channelMetadata.Channel.Url,ChannelThumbnail=channelMetadata.Channel.Thumbnail};
            UpdatePlayingChannel(ownerVideo);
            checks.Add(new{name="playing-channel-displays-owner-and-exact-click-target",passed=PlayingChannelButton.IsEnabled && PlayingChannelName.Text==ownerVideo.Channel && PlayingChannelAvatar.Url==ownerVideo.ChannelThumbnail && ReferenceEquals(PlayingChannelButton.Tag,ownerVideo) && YouTubeService.TryChannelUrl(((VideoItem)PlayingChannelButton.Tag).ChannelUrl,out var ownerUrl) && ownerUrl==channelMetadata.Channel.Url});
            checks.Add(new{name="owner-enrichment-keeps-current-video-and-list-references",passed=ReferenceEquals(_current,video) && ReferenceEquals(_items[0],video) && _playingIndex==0});

            _current=null;ClearDisplayedItems();AppendItem(ownerVideo);
            for(var index=1;index<8;index++)AppendItem(ownerVideo with{Id="browse-fixture-"+index,Title="Fixture channel video "+(index+1),Duration=120+index*41,ViewCount=1200+index*751});
            ApplyBrowseMetadata(channelMetadata);ShowPanel("browse");
            VideoList.ScrollIntoView(_rows[0]);UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            await CaptureBrowseFixtureAsync(directory,channelMetadata,ownerVideo,checks);
            await RunCardShareUiProbeAsync(ownerVideo,checks);
            var channelStyle=FindResource("ChannelLinkButtonStyle");
            var cardButtons=BrowseProbeDescendants<Button>(VideoList).Where(button=>ReferenceEquals(button.Tag,ownerVideo)).ToArray();
            var mainChannel=cardButtons.SingleOrDefault(button=>ReferenceEquals(button.Style,channelStyle));
            checks.Add(new{name="main-card-video-and-channel-actions-are-siblings",passed=cardButtons.Length==2 && mainChannel!=null && !BrowseProbeHasButtonAncestor(mainChannel)});
            await BrowseProbeChannelClickAsync(mainChannel,ownerVideo,"main-card-channel-click",checks);

            foreach(var side in new[]{"next","related"})
            {
                _current=null;_upNext.Clear();_relatedItems.Clear();_upNext.Add(ownerVideo);_relatedItems.Add(ownerVideo);
                ShowPanel("player");SelectPlayerSide(side);UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var sideList=side=="next"?NextList:RelatedList;
                var sideButtons=BrowseProbeDescendants<Button>(sideList).Where(button=>ReferenceEquals(button.Tag,ownerVideo)).ToArray();
                var sideChannel=sideButtons.SingleOrDefault(button=>ReferenceEquals(button.Style,channelStyle));
                checks.Add(new{name=side+"-card-video-and-channel-actions-are-siblings",passed=sideButtons.Length==2 && sideChannel!=null && !BrowseProbeHasButtonAncestor(sideChannel)});
                await BrowseProbeChannelClickAsync(sideChannel,ownerVideo,side+"-card-channel-click",checks);
            }
            _current=video;UpdatePlayingChannel(ownerVideo);ShowPanel("player");UpdateLayout();
            await BrowseProbeChannelClickAsync(PlayingChannelButton,ownerVideo,"playing-channel-click",checks);
        }
        finally{ThumbnailImage.Cache=thumbnailCache;ResetBrowseMetadata("home");}
    }
    private async Task RunCardShareUiProbeAsync(VideoItem originalCard,List<object> checks)
    {
        var border=BrowseProbeDescendants<Border>(VideoList).SingleOrDefault(item=>ReferenceEquals(item.Tag,originalCard) && item.ContextMenu!=null);
        if(border is null || border.ContextMenu is not {} menu || !_demo || _playerStarted)
        {checks.Add(new{name="card-share-realized-menu",passed=false});return;}
        var copied=new List<string>();var originalSink=_copyShareText;var originalCurrent=_current;
        var current=new VideoItem{Id="BaW_jenozKc",Url="https://www.youtube.com/watch?v=BaW_jenozKc",Title="Other current fixture"};
        var generation=_videoGeneration;var feedGeneration=_feedGeneration;var viewGeneration=_viewGeneration;
        var source=_source;var account=_settings.AccountKey;var videoCts=_videoCts;
        var ratingTaskCount=_ratingTasks.Count;var historyTaskCount=_historyTasks.Count;var itemCount=_items.Count;
        async Task SelectCardAsync(VideoItem card)
        {
            border.SetCurrentValue(FrameworkElement.TagProperty,card);
            menu.PlacementTarget=border;
            menu.GetBindingExpression(FrameworkElement.DataContextProperty)?.UpdateTarget();
            menu.UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        }
        try
        {
            _copyShareText=copied.Add;_current=current;
            await SelectCardAsync(originalCard with{Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ&si=fixture&list=PLfixture&t=42"});
            menu.IsOpen=true;menu.UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var share=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="영상 링크 복사");
            var notInterested=menu.Items.OfType<MenuItem>().Single(item=>item.Tag as string=="not_interested");
            var dontRecommend=menu.Items.OfType<MenuItem>().Single(item=>item.Tag as string=="dont_recommend_channel");
            share.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,share));
            checks.Add(new{name="card-share-uses-clicked-video-and-clean-url",passed=menu.IsVisible && share.IsEnabled && copied.SequenceEqual(new[]{"https://www.youtube.com/watch?v=aqz-KE-bpKQ"}) && (share.DataContext as VideoItem)?.Url.Contains("si=fixture")==true});
            checks.Add(new{name="card-share-keeps-feedback-disabled-without-server-actions",passed=!notInterested.IsEnabled && !dontRecommend.IsEnabled});
            foreach(var kind in new[]{"playlist","channel"})
            {
                // A watch URL on a non-video card must not turn it into a video share.
                await SelectCardAsync(originalCard with{Kind=kind});
                var before=copied.Count;share.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,share));
                checks.Add(new{name="card-share-rejects-"+kind,passed=!share.IsEnabled && copied.Count==before});
            }
            await SelectCardAsync(originalCard with{Url="https://example.invalid/watch?v=aqz-KE-bpKQ"});
            var invalidBefore=copied.Count;share.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,share));
            checks.Add(new{name="card-share-rejects-invalid-video-url",passed=copied.Count==invalidBefore});
            await SelectCardAsync(originalCard with{RecommendationActions=new(){["not_interested"]="fixture-action",["dont_recommend_channel"]="fixture-channel-action"}});
            checks.Add(new{name="card-share-keeps-existing-server-feedback-enabled",passed=share.IsEnabled && notInterested.IsEnabled && dontRecommend.IsEnabled});
            _copyShareText=_=>throw new System.Runtime.InteropServices.ExternalException("Fixture clipboard busy");
            share.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,share));
            checks.Add(new{name="card-share-handles-busy-clipboard",passed=copied.Count==invalidBefore});
            checks.Add(new{name="card-share-does-not-play-navigate-or-write-account",passed=ReferenceEquals(_current,current) && _videoGeneration==generation && _feedGeneration==feedGeneration && _viewGeneration==viewGeneration && _source==source && _settings.AccountKey==account && ReferenceEquals(_videoCts,videoCts) && _ratingTasks.Count==ratingTaskCount && _historyTasks.Count==historyTaskCount && _items.Count==itemCount && !_playerStarted});
        }
        finally
        {
            menu.IsOpen=false;border.SetCurrentValue(FrameworkElement.TagProperty,originalCard);
            _copyShareText=originalSink;_current=originalCurrent;
        }
    }
    private async Task RunDescriptionUiProbeAsync(List<object> checks,string directory)
    {
        if(!_demo || _playerStarted)throw new InvalidOperationException("Description UI probe requires an offline demo window.");
        var originalCurrent=_current;var originalDescription=_currentDescription;var originalFailed=_descriptionFailed;
        try
        {
            _current=new VideoItem{Id="aqz-KE-bpKQ",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ",Title="Description fixture"};
            PlayingTitle.Text=_current.Title;ShowPanel("player");ResetVideoDescription();UpdateLayout();
            PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks.Add(new{name="description-title-opens-separate-popup-with-pending-state",passed=DescriptionPopup.IsOpen && DescriptionPopup.Child is Border && DescriptionText.Text=="설명을 불러오는 중입니다…" && PlayingTitleButton.IsVisible});
            const string text="Fixture description\n<b>plain text</b>\nhttps://example.invalid/fixture";
            SetVideoDescription(text);DescriptionText.Select(0,7);
            checks.Add(new{name="description-is-readonly-selectable-plain-text",passed=DescriptionText.IsReadOnly && DescriptionText.Text==text && DescriptionText.SelectedText=="Fixture"});
            SetVideoDescription("");
            checks.Add(new{name="description-empty-video-is-distinct-from-failure",passed=DescriptionText.Text=="이 영상에는 설명이 없습니다."});
            SetVideoDescription(null,true);
            checks.Add(new{name="description-failed-read-displays-retry-state",passed=DescriptionText.Text.StartsWith("영상 설명을 불러오지 못했습니다.")});
            SetVideoDescription(string.Join("\n",Enumerable.Range(1,80).Select(i=>"Fixture description line "+i)));
            DescriptionPopup.Child?.UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks.Add(new{name="description-long-text-scrolls-within-bounded-bubble",passed=DescriptionScroll.ScrollableHeight>0 && DescriptionPopup.Child is FrameworkElement bubble && bubble.ActualWidth<=620 && bubble.ActualHeight<=420});
            if(DescriptionPopup.Child is FrameworkElement child && child.ActualWidth>0 && child.ActualHeight>0)
            {
                var dpi=VisualTreeHelper.GetDpi(child);
                var image=new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(child.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(child);
                using var file=File.Create(Path.Combine(directory,"description-bubble.png"));
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));png.Save(file);
            }
            PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            checks.Add(new{name="description-title-second-click-closes-popup",passed=!DescriptionPopup.IsOpen});
            PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            DescriptionCloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,DescriptionCloseButton));
            checks.Add(new{name="description-close-button-closes-popup",passed=!DescriptionPopup.IsOpen});
            SetVideoDescription(text);PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            ResetPlaybackExtras(preserveMetadata:true);
            checks.Add(new{name="description-quality-change-closes-popup-and-keeps-text",passed=!DescriptionPopup.IsOpen && _currentDescription==text});
            PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            ResetPlaybackExtras();
            checks.Add(new{name="description-video-change-closes-and-clears-text",passed=!DescriptionPopup.IsOpen && _currentDescription==null && !_descriptionFailed});
            SetVideoDescription(text);PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            SetAccountBusy(true);
            checks.Add(new{name="description-account-transition-closes-and-clears-text",passed=!DescriptionPopup.IsOpen && _currentDescription==null});
            SetAccountBusy(false);
            SetVideoDescription(text);PlayingTitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,PlayingTitleButton));
            ShowPanel("browse");
            checks.Add(new{name="description-leaving-player-closes-popup",passed=!DescriptionPopup.IsOpen});
        }
        finally
        {
            CloseDescriptionPopup();SetVideoDescription(originalDescription,originalFailed);_current=originalCurrent;
        }
    }
    private void RunAuthenticationNoticeUiProbe(List<object> checks)
    {
        if(!_demo || _playerStarted)throw new InvalidOperationException("Authentication notice probe requires an offline demo window.");
        try
        {
            ShowPanel("player");
            const string message="인증이 완료된 쿠키가 필요합니다. 7초 후 다음 영상을 재생합니다.";
            ShowAuthenticationNotice(message,true);UpdateLayout();
            var belowVideo=AuthenticationNoticePanel.TranslatePoint(new Point(),this).Y>=VideoSurface.TranslatePoint(new Point(0,VideoSurface.ActualHeight),this).Y;
            checks.Add(new{name="authentication-notice-readable-and-outside-video",passed=AuthenticationNoticePanel.IsVisible && AuthenticationNoticeText.Text==message && AuthenticationSkipCancelButton.IsEnabled && belowVideo && BrowseProbeButtonHit(AuthenticationSkipCancelButton)});
            ShowAuthenticationNotice("인증이 완료된 쿠키가 필요합니다.",false);
            checks.Add(new{name="authentication-notice-without-pending-skip-disables-cancel",passed=AuthenticationNoticePanel.IsVisible && !AuthenticationSkipCancelButton.IsEnabled});
            HideAuthenticationNotice();UpdateLayout();
            var collapsedRow=AuthenticationNoticePanel.Parent is Grid grid && grid.RowDefinitions[Grid.GetRow(AuthenticationNoticePanel)].ActualHeight==0;
            checks.Add(new{name="authentication-notice-hide-removes-row-and-clears-state",passed=AuthenticationNoticePanel.Visibility==Visibility.Collapsed && collapsedRow && AuthenticationNoticeText.Text=="" && !AuthenticationSkipCancelButton.IsEnabled});
        }
        finally{HideAuthenticationNotice();}
    }
    private async Task RunAuthenticationSkipProbeAsync(List<object> checks)
    {
        if(!_demo || _playerStarted)throw new InvalidOperationException("Authentication skip probe requires an offline demo window.");
        var originalCurrent=_current;var originalAuto=_settings.AutoPlayNext;var originalReady=_ready;
        var originalCheck=AutoPlayCheck.IsChecked;
        var video=new VideoItem{Id="aqz-KE-bpKQ",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ",Title="Authentication fixture"};
        void Prepare()
        {
            CancelAuthenticationSkip(clearNotice:true);_current=video;_videoGeneration++;
            _playingListGeneration=_feedGeneration;_playingIndex=-1;_hasFeed=false;_hasMore=false;_loading=false;
            _relatedTask=Task.CompletedTask;_relatedItems.Clear();_upNext.Clear();_snapshot=null;
            ShowPanel("player");
        }
        try
        {
            Prepare();_settings.AutoPlayNext=false;var previousTask=_authenticationSkipTask;HandleAuthenticationRequired();
            checks.Add(new{name="authentication-autoplay-off-only-notifies",passed=AuthenticationNoticePanel.IsVisible && !AuthenticationSkipCancelButton.IsEnabled && ReferenceEquals(previousTask,_authenticationSkipTask)});

            _settings.AutoPlayNext=true;StartAuthenticationSkip();var cancelTask=_authenticationSkipTask;
            AuthenticationSkipCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,AuthenticationSkipCancelButton));
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(2));
            HandleAuthenticationRequired();
            checks.Add(new{name="authentication-cancel-button-stops-and-does-not-rearm",passed=_authenticationSkipCts is null && ReferenceEquals(cancelTask,_authenticationSkipTask) && AuthenticationNoticePanel.IsVisible && !AuthenticationSkipCancelButton.IsEnabled});

            Prepare();HandleAuthenticationRequired();var changeTask=_authenticationSkipTask;
            ResetPlaybackExtras();await changeTask.WaitAsync(TimeSpan.FromSeconds(2));
            checks.Add(new{name="authentication-video-change-cancels-delay",passed=_authenticationSkipCts is null && AuthenticationNoticePanel.Visibility==Visibility.Collapsed});

            Prepare();HandleAuthenticationRequired();var accountTask=_authenticationSkipTask;
            SetAccountBusy(true);await accountTask.WaitAsync(TimeSpan.FromSeconds(2));SetAccountBusy(false);
            checks.Add(new{name="authentication-account-transition-cancels-delay",passed=_authenticationSkipCts is null && AuthenticationNoticePanel.Visibility==Visibility.Collapsed});

            Prepare();HandleAuthenticationRequired();var offTask=_authenticationSkipTask;
            _ready=false;AutoPlayCheck.IsChecked=false;_ready=true;AutoPlay_Changed(AutoPlayCheck,new RoutedEventArgs());
            await offTask.WaitAsync(TimeSpan.FromSeconds(2));
            checks.Add(new{name="authentication-autoplay-checkbox-cancels-delay",passed=!_settings.AutoPlayNext && _authenticationSkipCts is null && !AuthenticationSkipCancelButton.IsEnabled});

            Prepare();_settings.AutoPlayNext=true;HandleAuthenticationRequired();var leaveTask=_authenticationSkipTask;
            ShowPanel("browse");await leaveTask.WaitAsync(TimeSpan.FromSeconds(2));
            checks.Add(new{name="authentication-leaving-player-cancels-delay",passed=_authenticationSkipCts is null && AuthenticationNoticePanel.Visibility==Visibility.Collapsed});

            Prepare();HandleAuthenticationRequired();var manualTask=_authenticationSkipTask;
            NextVideo_Click(NextVideoButton,new RoutedEventArgs());await manualTask.WaitAsync(TimeSpan.FromSeconds(2));
            checks.Add(new{name="authentication-manual-next-cancels-delay",passed=_authenticationSkipCts is null && AuthenticationNoticePanel.Visibility==Visibility.Collapsed});

            Prepare();var timing=Stopwatch.StartNew();HandleAuthenticationRequired();
            await _authenticationSkipTask.WaitAsync(TimeSpan.FromSeconds(12));timing.Stop();
            checks.Add(new{name="authentication-waits-seven-seconds-and-handles-empty-queue",passed=timing.Elapsed.TotalSeconds>=6.9 && timing.Elapsed.TotalSeconds<12 && ReferenceEquals(_current,video) && _authenticationSkipCts is null && !AuthenticationSkipCancelButton.IsEnabled && AuthenticationNoticeText.Text.Contains("다음 재생할 영상이 없거나"),seconds=timing.Elapsed.TotalSeconds,scope="Real timer and existing next-video path with an empty offline queue; no network or player"});
        }
        finally
        {
            CancelAuthenticationSkip(clearNotice:true);await _authenticationSkipTask;
            _current=originalCurrent;_settings.AutoPlayNext=originalAuto;_ready=false;AutoPlayCheck.IsChecked=originalCheck;_ready=originalReady;
        }
    }
    private async Task CaptureBrowseFixtureAsync(string directory,FeedNavigationMetadata metadata,VideoItem ownerVideo,List<object> checks)
    {
        var width=Width;var height=Height;var transform=LayoutRoot.LayoutTransform;
        try
        {
            foreach(var scale in new[]{1d,1.25,1.5})
            {
                LayoutRoot.LayoutTransform=new ScaleTransform(scale,scale);Width=1380*scale;Height=900*scale;
                VideoList.ScrollIntoView(_rows[0]);UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var filterButtons=BrowseProbeDescendants<Button>(BrowseFilterItems).ToArray();
                checks.Add(new{name="channel-fixture-scale-layout",scale,passed=ChannelHeader.IsVisible && filterButtons.Length==metadata.Filters.Count && _items.Count==8,scope="WPF scaled rendering; not native display DPI"});
                foreach(var filterButton in filterButtons)
                    checks.Add(new{name="channel-filter-scale-hit",scale,filter=(filterButton.Tag as FeedFilter)?.Id,width=filterButton.ActualWidth,height=filterButton.ActualHeight,passed=BrowseProbeButtonHit(filterButton) && filterButton.IsEnabled});
                var channelButton=BrowseProbeDescendants<Button>(VideoList).SingleOrDefault(button=>ReferenceEquals(button.Tag,ownerVideo) && ReferenceEquals(button.Style,FindResource("ChannelLinkButtonStyle")));
                checks.Add(new{name="main-card-channel-scale-hit",scale,passed=channelButton!=null && BrowseProbeButtonHit(channelButton) && channelButton.IsEnabled});
                var dpi=VisualTreeHelper.GetDpi(this);
                var image=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(this);
                var suffix=scale.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture);
                using var file=File.Create(Path.Combine(directory,"channel-browse-"+suffix+".png"));
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);
            }
        }
        finally
        {
            LayoutRoot.LayoutTransform=transform;Width=width;Height=height;UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        }
    }
    private bool BrowseProbeButtonHit(Button button)
    {
        if(button.ActualWidth<=0 || button.ActualHeight<=0 || !button.IsVisible)return false;
        var point=button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),this);
        for(var hit=InputHitTest(point) as DependencyObject;hit!=null;hit=VisualTreeHelper.GetParent(hit))
            if(ReferenceEquals(hit,button))return true;
        return false;
    }
    private async Task BrowseProbeChannelClickAsync(Button? button,VideoItem video,string name,List<object> checks)
    {
        if(button==null || !_demo || _playerStarted)
        {checks.Add(new{name,passed=false,reason="offline-channel-control-unavailable"});return;}
        var point=button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),this);
        var hit=InputHitTest(point) as DependencyObject;var hitButton=false;
        while(hit!=null){if(ReferenceEquals(hit,button)){hitButton=true;break;}hit=VisualTreeHelper.GetParent(hit);}
        var generation=_videoGeneration;
        await _videoStartGate.WaitAsync();
        bool stoppedWithoutVideoStart;
        try
        {
            // Hold the start gate so even a regressed ancestor video handler cannot launch mpv.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            stoppedWithoutVideoStart=_videoGeneration==generation+1 && _current==null && !_playerStarted;
            _videoCts?.Cancel();
        }
        finally{_videoStartGate.Release();}
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var expected=YouTubeService.TryChannelUrl(video.ChannelUrl,out var channelUrl)?channelUrl:"";
        checks.Add(new{name,passed=hitButton && button.IsEnabled && stoppedWithoutVideoStart && _videoGeneration==generation+1 && _current==null && !_playerStarted && BrowsePanel.Visibility==Visibility.Visible && _source==expected,scope="Realized WPF button hit target and routed Click; demo feed; no native mouse or playback"});
    }
    private static IEnumerable<T> BrowseProbeDescendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);
            if(child is T match)yield return match;
            foreach(var descendant in BrowseProbeDescendants<T>(child))yield return descendant;
        }
    }
    private static bool BrowseProbeHasButtonAncestor(DependencyObject child)
    {
        for(var parent=VisualTreeHelper.GetParent(child);parent!=null;parent=VisualTreeHelper.GetParent(parent))
            if(parent is Button)return true;
        return false;
    }
    private async Task RunNativeFeatureProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();string? failure=null;
        _settings.Incognito=true;_settings.SyncHistory=false;_settings.Volume=0;_settings.MaxHeight=360;_settings.AutoPlayNext=true;
        Header.IsEnabled=Sidebar.IsEnabled=false;
        const string id="aqz-KE-bpKQ";
        var first=new VideoItem{Id=id,Title="자동재생 검사 1 · 기록 없음",Url="https://www.youtube.com/watch?v="+id};
        var next=first with{Title="자동재생 검사 2 · 기록 없음"};
        ClearDisplayedItems();AppendItem(first);AppendItem(next);_hasFeed=false;_hasMore=false;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await PlayVideoAsync(first,explicitStart:0);
            while(_snapshot?.Loaded!=true){timeout.Token.ThrowIfCancellationRequested();await Task.Delay(100,timeout.Token);}
            await LoadResolutionsAsync(first,_videoMetadataGeneration);
            checks.Add(new{name="real-video-resolutions",passed=_resolutions.Count>0,heights=_resolutions.ToArray()});
            await _player.CommandAsync("seek",Math.Max(0,_snapshot.Duration-1.5),"absolute+exact");
            while(!ReferenceEquals(_current,next) || _snapshot?.Loaded!=true || _snapshot.Position<.2 || _videoCts!=null){timeout.Token.ThrowIfCancellationRequested();await Task.Delay(100,timeout.Token);}
            checks.Add(new{name="natural-eof-advances-to-next-list-position",passed=ReferenceEquals(_current,next) && _playingIndex==1});
            checks.Add(new{name="autoplay-starts-next-at-zero",passed=_snapshot.Position<10});
            checks.Add(new{name="incognito-public-playback-no-local-history",passed=_store.GetHistory(_settings.AccountKey).Count==0 && _playCookie?.Path==null});
            await _player.CommandAsync("set_property","pause",true);
            await _player.CommandAsync("seek",40,"absolute+exact");
            await Task.Delay(700,timeout.Token);
            var actual=await _player.GetPropertyAsync<double>("time-pos",timeout.Token);
            checks.Add(new{name="native-seek-after-autoplay",passed=Math.Abs(actual-40)<.5});
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{await StopPlaybackAsync();}
        File.WriteAllText(Path.Combine(directory,"native-feature-smoke.json"),JsonSerializer.Serialize(new{failure,checks,scope="Guest native mpv playback/resolutions/natural EOF/seek, no account history or recommendation writes."},new JsonSerializerOptions{WriteIndented=true}));
    }
}
