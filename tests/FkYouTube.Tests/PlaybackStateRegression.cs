using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FkYouTube;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class PlaybackStateRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string VideoUrl = "https://www.youtube.com/watch?v=fixture0001";
    private static VideoItem Video() => new() { Id = "fixture0001", Url = VideoUrl, Title = "Fixture" };

    // Called by the existing STA fixture, so the test process creates only one WPF Application.
    public static async Task RunUiAsync(string workspace,CancellationToken stopToken=default,bool qualityOnly=false)
    {
        var failures = new List<string>();
        foreach (var test in new Func<string, Task>[] { QualityOptionsRespectStoredCapAsync, ExplicitQualitySelectionRaisesCapAsync, AutomaticQualityRoundTripAsync, AutomaticQualityLabelsAndSaveRollbackAsync, QualityReloadPauseIntentAsync, QualityReloadKeepsMetadataAsync, RelatedOwnerPersistsAsync, CancelledQualityRequestDoesNotBlockAsync, InitialQualityChangeStartsRelatedOnceAsync, SelectedAccountFailureStopsPreviousVideoAsync, SelectedAccountResponseErrorsAsync, AuthenticationFailureContinuesNextAsync, DeclinedPrefetchCompletesStatusAsync, PreflightFailureOwnsPreviousPlaybackAsync, PostLoadCommandFailurePreservesStatusAsync, AutoplayQueueInterleavingsAsync })
        {
            // The expanded owned-process pause cases have their own unchanged
            // 120-second full-suite deadline; the focused quality run includes them.
            if(!qualityOnly && test.Method.Name==nameof(QualityReloadPauseIntentAsync))continue;
            if(qualityOnly && test.Method.Name is not (nameof(QualityOptionsRespectStoredCapAsync) or nameof(ExplicitQualitySelectionRaisesCapAsync) or nameof(AutomaticQualityRoundTripAsync) or nameof(AutomaticQualityLabelsAndSaveRollbackAsync) or nameof(QualityReloadPauseIntentAsync)))continue;
            stopToken.ThrowIfCancellationRequested();
            try { await test(workspace);stopToken.ThrowIfCancellationRequested(); }
            catch(OperationCanceledException)when(stopToken.IsCancellationRequested){throw;}
            catch (Exception ex) { failures.Add(test.Method.Name + ": " + ex.Message); }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static Task QualityOptionsRespectStoredCapAsync(string workspace)
    {
        var root=FixtureRoot(workspace);var window=new MainWindow(new RuntimePaths(root),["--demo"]);
        try
        {
            var settings=Get<AppSettings>(window,"_settings");var store=Get<DataStore>(window,"_store");
            var combo=Get<ComboBox>(window,"QualityCombo");var state=Get<TextBlock>(window,"QualityState");
            foreach(var scenario in new (int Cap,int[] Available,int? Selected)[]
                {(144,[720],null),(144,[1080,720],null),(900,[1080,360,720],720),(720,[360,720,1080],720),(1080,[360,720],720),(2160,[1080,720],1080),(0,[720,4320,1080],0)})
            {
                settings.MaxHeight=scenario.Cap;store.SaveSettings(settings);
                Set(window,"_resolutions",(IReadOnlyList<int>)scenario.Available);
                Invoke(window,"DisplayResolutions");
                Assert(Equals(combo.SelectedItem,scenario.Selected),
                    $"Quality options selected {combo.SelectedItem ?? "nothing"} for stored cap {scenario.Cap}; an unavailable lower cap must remain unselected.");
                Assert(settings.MaxHeight==scenario.Cap && store.LoadSettings().MaxHeight==scenario.Cap &&
                    Get<CancellationTokenSource?>(window,"_videoCts") is null,
                    "Loading quality options changed the stored preference or started a playback reload.");
                Assert(state.Text==(scenario.Selected is null?$"최대 {scenario.Cap}p":"") && combo.IsEnabled &&
                    combo.Visibility==Visibility.Visible && Get<Button>(window,"QualityLoadButton").Visibility==Visibility.Collapsed &&
                    combo.Items.Cast<int>().SequenceEqual(scenario.Available.Prepend(0)) &&
                    Get<IReadOnlyList<int>>(window,"_resolutions").SequenceEqual(scenario.Available),
                    "Quality options hid the stored cap, changed offered heights, or disabled explicit selection.");
            }
            foreach(var cap in new[]{0,144})
            {
                settings.MaxHeight=cap;store.SaveSettings(settings);
                Set(window,"_resolutions",(IReadOnlyList<int>)Array.Empty<int>());Invoke(window,"DisplayResolutions");
                Assert(combo.SelectedItem is null && combo.Items.Count==0 && !combo.IsEnabled && state.Text=="제공되는 영상 화질 정보 없음" &&
                    settings.MaxHeight==cap && store.LoadSettings().MaxHeight==cap && Get<CancellationTokenSource?>(window,"_videoCts") is null,
                    "An empty quality list changed the preference, retained a selected format or lost its unavailable message.");
            }
        }
        finally{CleanupWindow(window,root);}
        return Task.CompletedTask;
    }

    private static async Task AutomaticQualityRoundTripAsync(string workspace)
    {
        var root=FixtureRoot(workspace);var paths=new RuntimePaths(root);var window=new MainWindow(paths,["--demo"]);
        var gate=Get<SemaphoreSlim>(window,"_videoStartGate");await gate.WaitAsync();
        try
        {
            var settings=Get<AppSettings>(window,"_settings");var store=Get<DataStore>(window,"_store");
            var combo=Get<ComboBox>(window,"QualityCombo");var video=Video();
            settings.MaxHeight=0;store.SaveSettings(settings);
            Set(window,"_current",video);Set(window,"_videoGeneration",4);Set(window,"_videoMetadataGeneration",7);
            Set(window,"_lastValidPosition",25d);Set(window,"_resolutions",(IReadOnlyList<int>)new[]{360,1080});
            Invoke(window,"DisplayResolutions");
            Assert(combo.SelectedItem is 0 && combo.Items.Cast<int>().SequenceEqual(new[]{0,360,1080}),
                "The quality menu has no distinct unlimited automatic option.");
            foreach(var choice in new[]{1080,0})
            {
                var generation=Get<int>(window,"_videoGeneration");combo.SelectedItem=choice;
                Assert(settings.MaxHeight==choice && store.LoadSettings().MaxHeight==choice,
                    "The highest numeric cap and automatic preference did not persist independently.");
                Assert(Get<int>(window,"_videoGeneration")==generation+1 && Get<int>(window,"_videoMetadataGeneration")==7 &&
                    Get<double>(window,"_lastValidPosition")==25d && ReferenceEquals(Get<VideoItem>(window,"_current"),video),
                    "Automatic or numeric quality changes lost the same-video position or metadata ownership.");
                await CancelQualityReloadAsync(window);
            }
            Set(window,"_resolutions",(IReadOnlyList<int>)new[]{720,2160});Invoke(window,"DisplayResolutions");
            Assert(combo.SelectedItem is 0 && settings.MaxHeight==0 && combo.Items.Cast<int>().SequenceEqual(new[]{0,720,2160}) &&
                Get<CancellationTokenSource?>(window,"_videoCts") is null,
                "A later video with higher formats changed automatic quality into the previous video's numeric cap.");
            Assert(new DataStore(paths).LoadSettings().MaxHeight==0,"Automatic quality was lost when settings were reloaded.");
        }
        finally
        {
            await CancelQualityReloadAsync(window);gate.Release();CleanupWindow(window,root);
        }
    }

    private static async Task AutomaticQualityLabelsAndSaveRollbackAsync(string workspace)
    {
        var root=FixtureRoot(workspace);var paths=new RuntimePaths(root);var window=new MainWindow(paths,["--demo"]);
        var language=UiText.Language;
        try
        {
            var settings=Get<AppSettings>(window,"_settings");var store=Get<DataStore>(window,"_store");
            var combo=Get<ComboBox>(window,"QualityCombo");
            Set(window,"_resolutions",(IReadOnlyList<int>)new[]{360,720});
            var label=(TextBlock)combo.ItemTemplate.LoadContent();
            foreach(var row in new[]{("ko","최고 화질 (자동)","최대 720p"),("en","Highest quality (Auto)","Up to 720p"),("ja","最高画質（自動）","最大 720p")})
            {
                UiText.SetLanguage(row.Item1);label.DataContext=0;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(label.Text==row.Item2,"The automatic quality option was not localized: "+row.Item1);
                label.DataContext=720;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(label.Text==row.Item3,"Numeric quality options lost their localized fixed-cap label: "+row.Item1);
            }
            foreach(var row in new (int Cap,int? Selected,int Attempt)[]{(0,0,720),(1080,720,0),(144,null,0)})
            {
                settings.MaxHeight=row.Cap;store.SaveSettings(settings);Invoke(window,"DisplayResolutions");
                var file=Path.Combine(paths.Data,"settings.json");var saved=file+".fixture";
                File.Move(file,saved);Directory.CreateDirectory(file);
                try
                {
                    combo.SelectedItem=row.Attempt;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(settings.MaxHeight==row.Cap && Equals(combo.SelectedItem,row.Selected) &&
                        Get<CancellationTokenSource?>(window,"_videoCts") is null,
                        "Failed quality persistence did not restore the automatic, numeric or unavailable selection.");
                }
                finally{Directory.Delete(file);File.Move(saved,file);}
                Assert(store.LoadSettings().MaxHeight==row.Cap,"Failed quality persistence overwrote the saved preference.");
            }
            combo.SelectedItem=0;
            Assert(settings.MaxHeight==0 && store.LoadSettings().MaxHeight==0 && combo.SelectedItem is 0,
                "A stored cap below every available format could not be explicitly recovered through automatic quality.");
        }
        finally{UiText.SetLanguage(language);CleanupWindow(window,root);}
    }

    private static async Task CancelQualityReloadAsync(MainWindow window)
    {
        Get<CancellationTokenSource?>(window,"_videoCts")?.Cancel();var deadline=Stopwatch.StartNew();
        while(Get<CancellationTokenSource?>(window,"_videoCts") is not null && deadline.Elapsed<TimeSpan.FromSeconds(5))
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert(Get<CancellationTokenSource?>(window,"_videoCts") is null,"The cancelled quality reload did not drain.");
    }

    private static async Task ExplicitQualitySelectionRaisesCapAsync(string workspace)
    {
        var root=FixtureRoot(workspace);var window=new MainWindow(new RuntimePaths(root),["--demo"]);
        var gate=Get<SemaphoreSlim>(window,"_videoStartGate");await gate.WaitAsync();
        try
        {
            var settings=Get<AppSettings>(window,"_settings");settings.MaxHeight=144;
            var store=Get<DataStore>(window,"_store");store.SaveSettings(settings);
            Set(window,"_current",Video());Set(window,"_videoGeneration",4);Set(window,"_videoMetadataGeneration",7);
            Set(window,"_lastValidPosition",25d);Set(window,"_resolutions",(IReadOnlyList<int>)new[]{720});
            Invoke(window,"DisplayResolutions");
            var combo=Get<ComboBox>(window,"QualityCombo");
            Assert(combo.SelectedItem is null && settings.MaxHeight==144,
                "The only available higher quality was preselected before the user could explicitly raise the cap.");
            combo.SelectedItem=720;
            Assert(settings.MaxHeight==720 && store.LoadSettings().MaxHeight==720 &&
                Get<TextBlock>(window,"QualityState").Text=="",
                "An explicit higher quality selection failed to persist its cap or retained the previous cap label.");
            Assert(Get<int>(window,"_videoGeneration")==5 && Get<int>(window,"_videoMetadataGeneration")==7 &&
                Get<double>(window,"_lastValidPosition")==25d && Get<CancellationTokenSource?>(window,"_videoCts") is {IsCancellationRequested:false},
                "An explicit quality selection did not enter the normal same-video reload with its saved position.");
        }
        finally
        {
            // The controlled gate keeps this real SelectionChanged route before any
            // native player/extraction work. Cancel that wait and let its handler drain.
            Get<CancellationTokenSource?>(window,"_videoCts")?.Cancel();
            var deadline=Stopwatch.StartNew();
            while(Get<CancellationTokenSource?>(window,"_videoCts") is not null && deadline.Elapsed<TimeSpan.FromSeconds(5))
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            gate.Release();
            Assert(Get<CancellationTokenSource?>(window,"_videoCts") is null,"The cancelled quality reload did not drain before fixture cleanup.");
            CleanupWindow(window,root);
        }
    }

    private static async Task DeclinedPrefetchCompletesStatusAsync(string workspace)
    {
        var root=FixtureRoot(workspace);CopyPython(workspace,root);Directory.CreateDirectory(Path.Combine(root,"worker"));
        File.WriteAllText(Path.Combine(root,"worker","selected_ytdlp.py"),
            "import json,sys\nsys.stdin.readline()\nwith open('prefetch-calls.txt','a') as f:f.write('call\\n')\nprint(json.dumps({'url':'https://media.fixture.invalid/prefetch','is_live':False}))\n");
        var window=new MainWindow(new RuntimePaths(root),["--demo"]);
        try
        {
            Set(Get<SessionService>(window,"_session"),"<SelectedChannel>k__BackingField",new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"});
            Get<AppSettings>(window,"_settings").AutoPlayNext=true;
            Set(window,"_snapshot",new PlaybackSnapshot{Loaded=true,Position=50,Duration=100,MediaGeneration=7});
            Set(window,"_expectedMediaGeneration",7L);Set(window,"_videoGeneration",1);Set(window,"_current",Video());
            var candidate=Video() with{Id="fixture0002",Url="https://www.youtube.com/watch?v=fixture0002"};
            Get<ObservableCollection<VideoItem>>(window,"_upNext").Add(candidate);
            // Resolve succeeds, but the player has no live session and declines preparation.
            await (Task)Invoke(window,"EnsurePrefetchAsync")!;
            Assert(Get<TextBlock>(window,"PrefetchState").Text=="다음 영상은 일반 방식으로 이어 재생합니다." &&
                Get<VideoItem?>(window,"_preparedItem") is null && ReferenceEquals(Get<VideoItem>(window,"_prefetchAttempt"),candidate),
                "A declined prepare left the UI in preparing state or reset the bounded attempt guard.");
            await (Task)Invoke(window,"EnsurePrefetchAsync")!;
            Assert(File.ReadAllLines(Path.Combine(root,"prefetch-calls.txt")).Length==1,
                "A declined prepare repeatedly re-extracted the same candidate on later snapshots.");
        }
        finally{CleanupWindow(window,root);}
    }

    private static async Task QualityReloadKeepsMetadataAsync(string workspace)
    {
        var root = FixtureRoot(workspace);
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]);
        var gate = Get<SemaphoreSlim>(window, "_videoStartGate");
        var related = new CancellationTokenSource(); var comments = new CancellationTokenSource(); var quality = new CancellationTokenSource();
        Task? reload = null;
        await gate.WaitAsync();
        try
        {
            var item = Video(); var candidate = item with { Id = "fixture0002", Url = "https://www.youtube.com/watch?v=fixture0002" };
            Set(window, "_current", item); Set(window, "_videoGeneration", 4);
            SetOptional(window, "_videoMetadataGeneration", 4);
            Set(window, "_commentsRequested", true); Set(window, "_relatedCts", related); Set(window, "_commentsCts", comments); Set(window, "_qualityCts", quality);
            Get<ObservableCollection<VideoItem>>(window, "_relatedItems").Add(candidate);
            Get<ObservableCollection<VideoItem>>(window, "_upNext").Add(candidate);
            var commentRows = new[] { new CommentItem("fixture", "keep this comment", 0, null) };
            Get<ItemsControl>(window, "CommentsList").ItemsSource = commentRows;
            Invoke(window, "SelectPlayerSide", "comments");
            Get<Button>(window, "QualityLoadButton").IsEnabled = false;
            Get<TextBlock>(window, "QualityState").Text = "화질 조회 중…";
            var relatedTask = new TaskCompletionSource(); Set(window, "_relatedTask", relatedTask.Task);
            reload = (Task)Invoke(window, "PlayVideoAsync", item, true, (double?)12, true)!;
            Assert(!reload.IsCompleted, "The reload did not wait at the controlled player gate.");
            Assert(!related.IsCancellationRequested && !comments.IsCancellationRequested && !quality.IsCancellationRequested,
                "A quality-only reload cancelled the same video's metadata requests.");
            Assert(Get<ObservableCollection<VideoItem>>(window, "_relatedItems").Count == 1 &&
                   Get<ObservableCollection<VideoItem>>(window, "_upNext").Count == 1, "Quality-only reload cleared recommendations or next videos.");
            Assert(ReferenceEquals(Get<ItemsControl>(window, "CommentsList").ItemsSource, commentRows) && Get<bool>(window, "_commentsRequested") &&
                   Get<FrameworkElement>(window, "CommentsBody").Visibility == Visibility.Visible, "Quality-only reload cleared the open comments tab.");
            Assert(!Get<Button>(window, "QualityLoadButton").IsEnabled && Get<TextBlock>(window, "QualityState").Text == "화질 조회 중…",
                "Quality-only reload reset an in-progress format request.");
            Assert(ReferenceEquals(Get<Task>(window, "_relatedTask"), relatedTask.Task), "Quality-only reload replaced its related request.");
            Assert(Get<int>(window, "_videoMetadataGeneration") == 4, "Quality-only reload invalidated same-video metadata responses.");
        }
        finally
        {
            Get<CancellationTokenSource?>(window, "_videoCts")?.Cancel();
            gate.Release(); if (reload != null) await reload;
            Set(window, "_relatedCts", null); Set(window, "_commentsCts", null); Set(window, "_qualityCts", null);
            related.Dispose(); comments.Dispose(); quality.Dispose();
            CleanupWindow(window, root);
        }
    }

    private static async Task RelatedOwnerPersistsAsync(string workspace)
    {
        var root = FixtureRoot(workspace); CopyPython(workspace, root);
        Directory.CreateDirectory(Path.Combine(root, "worker"));
        File.WriteAllText(Path.Combine(root, "worker", "related_feed.py"), "import json,sys,urllib.parse\nr=json.loads(sys.stdin.readline())\nvideo=urllib.parse.parse_qs(urllib.parse.urlsplit(r['url']).query)['v'][0]\nprint(json.dumps({'type':'related','sourceVideoId':video,'items':[], 'channel':{'id':'UCabcdefghijklmnopqrstuv','title':'Fixture Owner','url':'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv','thumbnail':'https://yt3.ggpht.com/fixture-avatar'}}))\n");
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]);
        try
        {
            var item = Video(); Set(window, "_current", item); Set(window, "_videoGeneration", 1); SetOptional(window, "_videoMetadataGeneration", 1);
            Get<List<VideoItem>>(window, "_items").Add(item);
            var row = new VideoRow(); row.Items.Add(item); Get<ObservableCollection<VideoRow>>(window, "_rows").Add(row);
            Set(window, "_playingIndex", 0); Set(window, "_hasLoadedCurrent", true); Set(window, "_lastValidPosition", 15d); Set(window, "_lastValidDuration", 100d);
            await (Task)Invoke(window, "LoadRelatedAsync", item, 1)!;
            var current = Get<VideoItem>(window, "_current");
            Assert(current.Channel == "Fixture Owner" && current.ChannelThumbnail != null && current.CanOpenChannel,
                "Related metadata changed only the channel button, leaving the current record empty.");
            Assert(ReferenceEquals(Get<List<VideoItem>>(window, "_items")[0], current) && ReferenceEquals(row.Items[0], current),
                "Metadata enrichment broke the current playlist occurrence's reference identity.");
            Invoke(window, "SavePosition");
            var stored = Get<DataStore>(window, "_store").GetHistory(Get<AppSettings>(window, "_settings").AccountKey).Single().Video;
            Assert(stored.Channel == current.Channel && stored.ChannelUrl == current.ChannelUrl, "The history row lost enriched channel metadata.");
        }
        finally { CleanupWindow(window, root); }
    }

    private static Task CancelledQualityRequestDoesNotBlockAsync(string workspace)
    {
        var root = FixtureRoot(workspace); var window = new MainWindow(new RuntimePaths(root), ["--demo"]);
        using var old = new CancellationTokenSource(); old.Cancel();
        try
        {
            var item = Video(); Set(window, "_current", item); Set(window, "_qualityCts", old);
            var settings = Get<AppSettings>(window, "_settings"); Set(window, "_qualityKey", $"{settings.AccountKey}:{settings.Incognito}:{item.Id}");
            Set(window, "_resolutions", (IReadOnlyList<int>)new[] { 360, 720 });
            Invoke(window, "QualityLoad_Click", window, new RoutedEventArgs());
            Assert(Get<ComboBox>(window, "QualityCombo").IsEnabled, "A cancelled previous-video quality request blocked the new video's quality button.");
        }
        finally { Set(window, "_qualityCts", null); CleanupWindow(window, root); }
        return Task.CompletedTask;
    }

    private static Task InitialQualityChangeStartsRelatedOnceAsync(string workspace) => PlaybackBoundaryAsync(workspace, false);

    private static async Task AutoplayQueueInterleavingsAsync(string workspace)
    {
        foreach(var mode in new[]{"direct-unrelated-feed","related-replacement","early-related-start","partial-feed","stale-intent"})
        {
            var root=FixtureRoot(workspace);var window=new MainWindow(new RuntimePaths(root),["--demo"]);
            var gate=Get<SemaphoreSlim>(window,"_videoStartGate");await gate.WaitAsync();
            var oldRelated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var latestRelated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var feed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var oldCts=new CancellationTokenSource();using var latestCts=new CancellationTokenSource();
            using var startup=new CancellationTokenSource();Task? autoplay=null;
            try
            {
                var current=Video();var candidate=mode=="partial-feed"?Video() with{Title="Next exact occurrence"}:
                    Video() with{Id="fixture0002",Url="https://www.youtube.com/watch?v=fixture0002",Title="Next related video"};
                Set(window,"_current",current);Set(window,"_videoGeneration",7);
                Set(window,"_playingListGeneration",Get<int>(window,"_feedGeneration"));Set(window,"_playingIndex",-1);
                Get<AppSettings>(window,"_settings").AutoPlayNext=true;
                Get<Grid>(window,"PlayerPanel").Visibility=Visibility.Visible;
                Set(window,"_hasFeed",true);Set(window,"_hasMore",true);Set(window,"_loading",true);
                Set(window,"_feedLoadTask",feed.Task);Set(window,"_relatedRequested",true);
                Set(window,"_relatedTask",oldRelated.Task);Set(window,"_relatedCts",oldCts);
                if(mode=="partial-feed")
                {Get<List<VideoItem>>(window,"_items").Add(current);Set(window,"_playingIndex",0);}
                if(mode=="early-related-start")
                {Set(window,"_relatedRequested",false);Set(window,"_relatedTask",Task.CompletedTask);Set(window,"_relatedCts",null);Set(window,"_videoCts",startup);}
                autoplay=(Task)Invoke(window,"AutoPlayNextAsync",7,Get<int>(window,"_autoplayEpoch"),false)!;
                Assert(!autoplay.IsCompleted && ReferenceEquals(Get<VideoItem>(window,"_current"),current),
                    "Autoplay lost its wait before the candidate arrived: "+mode);
                if(mode=="related-replacement")
                {
                    Set(window,"_relatedTask",latestRelated.Task);Set(window,"_relatedCts",latestCts);
                    Invoke(window,"NotifyPlaybackQueueChanged");oldCts.Cancel();oldRelated.TrySetResult();
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert(!autoplay.IsCompleted && ReferenceEquals(Get<VideoItem>(window,"_current"),current),
                        "Canceled related work consumed EOF intent instead of following its replacement.");
                }
                if(mode=="early-related-start")
                {
                    Set(window,"_relatedTask",latestRelated.Task);Set(window,"_relatedRequested",true);
                    Set(window,"_relatedCts",latestCts);Set(window,"_videoCts",null);Invoke(window,"NotifyPlaybackQueueChanged");
                }
                if(mode=="stale-intent")
                {Set(window,"_videoGeneration",8);Invoke(window,"NotifyPlaybackQueueChanged");}
                if(mode=="partial-feed")Get<List<VideoItem>>(window,"_items").Add(candidate);
                else Get<ObservableCollection<VideoItem>>(window,"_relatedItems").Add(candidate);
                Set(window,"_relatedCts",null);oldRelated.TrySetResult();latestRelated.TrySetResult();
                Invoke(window,"NotifyPlaybackQueueChanged");
                for(var attempt=0;attempt<100 && !autoplay.IsCompleted && ReferenceEquals(Get<VideoItem>(window,"_current"),current);attempt++)
                    await Task.Delay(10);
                if(mode=="stale-intent")
                    Assert(autoplay.IsCompleted && ReferenceEquals(Get<VideoItem>(window,"_current"),current),
                        "An obsolete EOF transitioned after its video generation changed.");
                else
                    Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),candidate) && !feed.Task.IsCompleted,
                        "Autoplay did not consume the current exact candidate before an unrelated/full batch wait: "+mode);
                Console.WriteLine("QUEUE-INTERLEAVING mode="+mode+" scope=offline-WPF-controlled-tasks-no-native-media-no-network");
            }
            finally
            {
                Get<CancellationTokenSource?>(window,"_videoCts")?.Cancel();
                Set(window,"_relatedCts",null);Set(window,"_videoCts",null);
                oldRelated.TrySetResult();latestRelated.TrySetResult();feed.TrySetResult();Invoke(window,"NotifyPlaybackQueueChanged");
                gate.Release();if(autoplay is not null)await autoplay.WaitAsync(TimeSpan.FromSeconds(5));
                CleanupWindow(window,root);
            }
        }
    }
    private static Task SelectedAccountFailureStopsPreviousVideoAsync(string workspace) => PlaybackBoundaryAsync(workspace, true);
    private static Task AuthenticationFailureContinuesNextAsync(string workspace) => PlaybackBoundaryAsync(workspace, true, true);

    private static Task QualityReloadPauseIntentAsync(string workspace)=>RunPauseIntentAsync(workspace);
    public static async Task RunPauseIntentAsync(string workspace,CancellationToken stopToken=default)
    {
        var failures=new List<string>();
        foreach(var mode in new[]{"paused","playing","paused-rapid","playing-rapid","new-video","autoplay",
            "paused-before","playing-before","paused-gate","playing-gate","paused-resolve","playing-resolve",
            "paused-submit","playing-submit","paused-rejected","paused-newer-rejection","paused-double-gate","paused-canceled-next","paused-gate-rejected",
            "paused-write-gap","playing-write-gap","playing-write-observation","playing-delayed-observation","paused-ambiguous-reply"})
        {
            stopToken.ThrowIfCancellationRequested();
            try{await PlaybackBoundaryAsync(workspace,false,pauseMode:mode);}
            catch(OperationCanceledException)when(stopToken.IsCancellationRequested){throw;}
            catch(Exception error){failures.Add(mode+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    private static async Task PreflightFailureOwnsPreviousPlaybackAsync(string workspace)
    {
        var failures=new List<string>();
        foreach(var mode in new[]{"missing-ytdlp","missing-deno","invalid-account","previous-deno","stop-unconfirmed"})
        {
            try{await PlaybackBoundaryAsync(workspace,true,preflightFailure:mode);}
            catch(Exception error){failures.Add(mode+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    private static async Task PostLoadCommandFailurePreservesStatusAsync(string workspace)
    {
        var failures=new List<string>();
        foreach(var property in new[]{"volume","speed"})
        {
            try{await PlaybackBoundaryAsync(workspace,false,postLoadFailure:property);}
            catch(Exception error){failures.Add(property+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    public static async Task RunSelectedAccountFailureReviewAsync(string workspace,CancellationToken stopToken=default)
    {
        stopToken.ThrowIfCancellationRequested();await SelectedAccountResponseErrorsAsync(workspace);
        stopToken.ThrowIfCancellationRequested();await AuthenticationFailureContinuesNextAsync(workspace);
    }

    private static async Task SelectedAccountResponseErrorsAsync(string workspace)
    {
        var failures=new List<string>();
        foreach(var mode in new[]{"malformed-json","drm","url","headers","hls","live","split"})
            try{await PlaybackBoundaryAsync(workspace,true,selectedResponse:mode);}
            catch(Exception error){failures.Add(mode+": "+error.GetType().Name+" "+error.Message);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    private static async Task PlaybackBoundaryAsync(string workspace, bool failSelectedAccount, bool authenticationSkip = false, string? preflightFailure = null, string? postLoadFailure = null,string? selectedResponse=null,string? pauseMode=null)
    {
        var root = FixtureRoot(workspace); CopyPython(workspace, root);
        var paths = new RuntimePaths(root);
        Directory.CreateDirectory(Path.Combine(root, "worker"));
        File.WriteAllText(Path.Combine(root, "worker", "related_feed.py"), "import json,sys,urllib.parse\nr=json.loads(sys.stdin.readline())\nvideo=urllib.parse.parse_qs(urllib.parse.urlsplit(r['url']).query)['v'][0]\nwith open('related-calls.txt','a') as f:f.write('call\\n')\nprint(json.dumps({'type':'related','sourceVideoId':video,'items':[]}))\n");
        File.WriteAllText(Path.Combine(root, "worker", "selected_ytdlp.py"), "import sys\nsys.stdin.readline()\nprint('FK_YT_ERROR:network',file=sys.stderr)\nsys.exit(1)\n");
        if(pauseMode?.EndsWith("-resolve",StringComparison.Ordinal)==true)
            File.WriteAllText(Path.Combine(root,"worker","selected_ytdlp.py"),
                "import json,pathlib,sys,time\nsys.stdin.readline()\npathlib.Path('resolve-started').write_text('ready')\nwhile not pathlib.Path('resolve-release').exists(): time.sleep(0.01)\nprint(json.dumps({'url':'https://media.fixture.invalid/stream','is_live':False}))\n");
        if (authenticationSkip)
            File.WriteAllText(Path.Combine(root, "worker", "selected_ytdlp.py"), "import sys,json\nsys.stdin.readline()\nif 'fixture0002' in sys.argv[-1]:\n print('FK_YT_ERROR:signin_age',file=sys.stderr)\n sys.exit(1)\nprint(json.dumps({'url':'https://media.fixture.invalid/stream','is_live':False}))\n");
        if(selectedResponse is not null)
        {
            const string secret="private-upstream-secret";
            var response=selectedResponse switch
            {
                "malformed-json"=>"{\"https://private.fixture.invalid/?token="+secret+"\":",
                "drm"=>JsonSerializer.Serialize(new{url="https://media.fixture.invalid/"+secret,has_drm=true,is_live=false}),
                "url"=>JsonSerializer.Serialize(new{url="http://media.fixture.invalid/"+secret,is_live=false}),
                "headers"=>JsonSerializer.Serialize(new{is_live=false,requested_formats=new[]{
                    new{url="https://media.fixture.invalid/video",http_headers=new{Cookie=secret+"-a"}},
                    new{url="https://media.fixture.invalid/audio",http_headers=new{Cookie=secret+"-b"}}}}),
                "hls"=>JsonSerializer.Serialize(new{url="https://untrusted.fixture.invalid/"+secret,manifest_url="https://untrusted.fixture.invalid/master",protocol="m3u8_native",is_live=false,live_status="not_live",vcodec="avc1",acodec="mp4a"}),
                "live"=>"{\"is_live\":true}",
                "split"=>"{\"requested_formats\":[{},{},{}]}",
                _=>throw new ArgumentException("Unknown selected-response fixture.")
            };
            File.WriteAllText(Path.Combine(root,"worker","selected_ytdlp.py"),
                "import sys,json\nr=json.loads(sys.stdin.readline())\nassert r['accountContext']['authUser']==2\nprint("+JsonSerializer.Serialize(response)+")\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(paths.YtDlp)!); File.WriteAllText(paths.YtDlp, "must never execute");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Deno)!); File.WriteAllText(paths.Deno, "must never execute");
        var window = new MainWindow(paths, ["--demo"]);
        var pipeName = "fkyt-initial-quality-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connecting = client.ConnectAsync(5000); await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)); await connecting;
        var process = OwnedProcess.Start(paths.Python, ["-c", "import time;time.sleep(30)"], root);
        using var processExit = Process.GetProcessById(process.Id);
        _ = processExit.SafeHandle;
        using var stopServer = new CancellationTokenSource();
        string? loaded = null; var stops = 0;var replacementLoads=0;var postLoadFailures=0;var appliedVolume=0;
        var loadOptions=new List<JsonElement>();var pauseCommands=0;var nativePaused=false;
        var loadSubmitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPauseWritten=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPause=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var formatWritten=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFormat=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCookiePath=Path.Combine(root,"previous-playback-cookie.txt");var oldCookieExistedAtQuit=false;
        Task ServeAsync(NamedPipeServerStream connection,bool replacement=false)=>Task.Run(async () =>
        {
            using var reader = new StreamReader(connection, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(connection, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(stopServer.Token) is { } line)
                {
                    using var request = JsonDocument.Parse(line); var command = request.RootElement.GetProperty("command"); var name = command[0].GetString();
                    object? data = null;
                    if (name == "loadfile")
                    {
                        loaded = command[1].GetString();if(replacement)Interlocked.Increment(ref replacementLoads);
                        lock(loadOptions)loadOptions.Add(command[4].Clone());
                        nativePaused=command[4].GetProperty("pause").GetString()=="yes";
                        loadSubmitted.TrySetResult();
                        if(pauseMode?.EndsWith("-submit",StringComparison.Ordinal)==true)await releaseLoad.Task.WaitAsync(stopServer.Token);
                    }
                    var pauseCommand=(name is "cycle" or "set_property") && command[1].GetString()=="pause";
                    if(pauseCommand)
                    {
                        var pauseCount=Interlocked.Increment(ref pauseCommands);
                        firstPauseWritten.TrySetResult();
                        if(pauseMode=="paused-newer-rejection" && pauseCount==1)await releaseFirstPause.Task.WaitAsync(stopServer.Token);
                        if(pauseMode is not ("paused-rejected" or "paused-gate-rejected") && !(pauseMode=="paused-newer-rejection" && pauseCount==1))nativePaused=name=="cycle"?!nativePaused:command[2].GetBoolean();
                    }
                    if (name == "stop") Interlocked.Increment(ref stops);
                    if (name == "quit") oldCookieExistedAtQuit=File.Exists(oldCookiePath);
                    if(name=="set_property" && command[1].GetString()=="ytdl-format" && (pauseMode?.EndsWith("-write-gap",StringComparison.Ordinal)==true || pauseMode=="playing-write-observation"))
                    {formatWritten.TrySetResult();await releaseFormat.Task.WaitAsync(stopServer.Token);}
                    if (name == "get_property" && command[1].GetString() == "playlist") data = loaded is null ? Array.Empty<object>() : new object[] { new { id = 42, filename = loaded } };
                    if(name=="get_property" && command[1].GetString()=="pause")data=nativePaused;
                    var error="success";
                    if(pauseCommand && (pauseMode is "paused-rejected" or "paused-gate-rejected" || pauseMode=="paused-newer-rejection" && Volatile.Read(ref pauseCommands)==1))error="fixture-pause-rejected";
                    if(postLoadFailure is not null && name=="set_property" && command[1].GetString()==postLoadFailure)
                    {Interlocked.Increment(ref postLoadFailures);error="fixture-post-load-"+postLoadFailure;}
                    else if(name=="set_property" && command[1].GetString()=="volume")Interlocked.Increment(ref appliedVolume);
                    if(pauseCommand && pauseMode=="paused-ambiguous-reply" && Volatile.Read(ref pauseCommands)==1)
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new{request_id=request.RootElement.GetProperty("request_id").GetInt64(),data}));
                    else await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = request.RootElement.GetProperty("request_id").GetInt64(), error, data }));
                    if (name == "loadfile" && (authenticationSkip || postLoadFailure is not null))
                    {
                        await writer.WriteLineAsync("{\"event\":\"start-file\",\"playlist_entry_id\":42}");
                        await writer.WriteLineAsync("{\"event\":\"file-loaded\"}");
                    }
                    if (name == "quit") return;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (stopServer.IsCancellationRequested) { }
        });
        var serverTask=ServeAsync(server);
        NamedPipeServerStream? replacementServer=null;NamedPipeClientStream? replacementClient=null;
        OwnedProcess? replacementProcess=null;Process? replacementExit=null;Task replacementServerTask=Task.CompletedTask;
        var player = Get<MpvPlayerService>(window, "_player");
        try
        {
            var sessionType = typeof(MpvPlayerService).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [process, client], null)!;
            sessionType.GetField("Reader")!.SetValue(session, new StreamReader(client, new UTF8Encoding(false), false, 4096, true));
            sessionType.GetField("Writer")!.SetValue(session, new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true });
            Set(player, "_session", session); Set(player, "_host", (nint)1);
            sessionType.GetField("ReadTask")!.SetValue(session, (Task)Invoke(player, "ReadMessagesAsync", session)!);
            Set(window, "_playerStarted", true); Set(window, "_current", Video()); Set(window, "_videoGeneration", 1); SetOptional(window, "_videoMetadataGeneration", 1);
            if(pauseMode is not null)
            {
                var isQuality=pauseMode is not ("new-video" or "autoplay");
                var paused=!pauseMode.StartsWith("playing",StringComparison.Ordinal);
                nativePaused=paused;
                var prior=Get<VideoItem>(window,"_current");var next=prior with{Id="fixture0002",Url="https://www.youtube.com/watch?v=fixture0002"};
                var snapshot=new PlaybackSnapshot{MediaGeneration=1,VideoUrl=prior.Url,Loaded=true,Paused=paused,Position=25,Duration=100};
                Set(player,"_mediaGeneration",1L);Set(player,"_snapshot",snapshot);Set(window,"_snapshot",snapshot);
                Set(window,"_expectedMediaGeneration",1L);Set(window,"_hasLoadedCurrent",true);Set(window,"_lastValidPosition",25d);Set(window,"_lastValidDuration",100d);
                var settings=Get<AppSettings>(window,"_settings");settings.AutoPlayNext=pauseMode=="autoplay";
                async Task WaitUntilAsync(Func<bool> condition,string message)
                {
                    var elapsed=Stopwatch.StartNew();
                    while(!condition() && elapsed.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(10);
                    Assert(condition(),message);
                }
                void TogglePause()=>Invoke(window,"Pause_Click",window,new RoutedEventArgs());
                var hasInput=pauseMode.Contains('-') && pauseMode is not ("paused-rapid" or "playing-rapid" or "new-video" or "playing-write-observation" or "playing-delayed-observation");
                if(pauseMode.EndsWith("-before",StringComparison.Ordinal) || pauseMode=="paused-rejected")
                {
                    TogglePause();await WaitUntilAsync(()=>Volatile.Read(ref pauseCommands)==1,"The pre-reload pause command was not submitted.");
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Assert(Get<PlaybackSnapshot>(window,"_snapshot").Paused==paused,"The fixture unexpectedly delivered a pause snapshot before the quality selection.");
                }
                if(pauseMode=="paused-newer-rejection")
                {
                    TogglePause();await firstPauseWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    TogglePause();TogglePause();releaseFirstPause.TrySetResult();
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                }
                if(pauseMode=="paused-ambiguous-reply")
                {
                    await (Task)Invoke(window,"TogglePlaybackPauseAsync")!;
                    Assert(!nativePaused && Volatile.Read(ref pauseCommands)==1,"The ambiguous reply fixture did not apply native pause before omitting acknowledgement status.");
                    await (Task)Invoke(window,"TogglePlaybackPauseAsync")!;
                    Assert(nativePaused && Volatile.Read(ref pauseCommands)==2,
                        "A pause command with an ambiguous acknowledgement let the next toggle skip restoring the native state.");
                }
                if(isQuality)
                {
                    settings.MaxHeight=360;Get<DataStore>(window,"_store").SaveSettings(settings);
                    Set(window,"_resolutions",(IReadOnlyList<int>)new[]{360,720,1080});Invoke(window,"DisplayResolutions");
                    var gate=Get<SemaphoreSlim>(window,"_videoStartGate");await gate.WaitAsync();
                    Task<bool>? superseding=null;Task? pendingPause=null;
                    try
                    {
                        var combo=Get<ComboBox>(window,"QualityCombo");combo.SelectedItem=720;
                        Assert(Get<PlaybackSnapshot?>(window,"_snapshot") is null && Get<CancellationTokenSource?>(window,"_videoCts") is not null,
                            "The quality selection did not reach the controlled reload boundary.");
                        if(pauseMode.EndsWith("-rapid",StringComparison.Ordinal))combo.SelectedItem=1080;
                        if(pauseMode.EndsWith("-gate",StringComparison.Ordinal) || pauseMode is "paused-canceled-next" or "paused-gate-rejected")
                        {
                            if(pauseMode=="paused-gate-rejected")pendingPause=(Task)Invoke(window,"TogglePlaybackPauseAsync")!;else TogglePause();
                            if(pauseMode=="paused-double-gate")TogglePause();
                            if(pauseMode=="paused-canceled-next")superseding=(Task<bool>)Invoke(window,"PlayVideoAsync",next,false,(double?)0,true)!;
                        }
                        if(pauseMode.EndsWith("-resolve",StringComparison.Ordinal))
                            Set(Get<SessionService>(window,"_session"),"<SelectedChannel>k__BackingField",new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"});
                    }
                    finally{gate.Release();}
                    if(pauseMode.EndsWith("-resolve",StringComparison.Ordinal))
                    {
                        await WaitUntilAsync(()=>File.Exists(Path.Combine(root,"resolve-started")),"The selected-account resolver did not reach its controlled wait.");
                        TogglePause();File.WriteAllText(Path.Combine(root,"resolve-release"),"go");
                    }
                    if(pauseMode.EndsWith("-submit",StringComparison.Ordinal))
                    {
                        await loadSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));TogglePause();releaseLoad.TrySetResult();
                    }
                    if(pauseMode.EndsWith("-write-gap",StringComparison.Ordinal) || pauseMode=="playing-write-observation")
                    {
                        await formatWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        var nativeWriteGate=(SemaphoreSlim)sessionType.GetProperty("WriteGate")!.GetValue(session)!;
                        await nativeWriteGate.WaitAsync();
                        try
                        {
                            var previousRequest=(long)sessionType.GetField("NextRequest")!.GetValue(session)!;
                            releaseFormat.TrySetResult();
                            await WaitUntilAsync(()=>(long)sessionType.GetField("NextRequest")!.GetValue(session)!>previousRequest,
                                "The loadfile request did not reach the held native IPC write gate.");
                            if(pauseMode=="playing-write-observation")
                                Invoke(window,"OnSnapshot",new PlaybackSnapshot{MediaGeneration=player.Snapshot.MediaGeneration,VideoUrl=prior.Url,Paused=true,Buffering=true});
                            else TogglePause();
                        }
                        finally{nativeWriteGate.Release();}
                    }
                    if(superseding is not null)Assert(await superseding,"The replacement video did not load after canceling the old quality/input generation.");
                    var deadline=Stopwatch.StartNew();
                    while(Get<CancellationTokenSource?>(window,"_videoCts") is not null && deadline.Elapsed<TimeSpan.FromSeconds(10))
                        await Task.Delay(10);
                    Assert(Get<CancellationTokenSource?>(window,"_videoCts") is null,"The quality reload did not drain.");
                    if(pendingPause is not null)await pendingPause.WaitAsync(TimeSpan.FromSeconds(5));
                    if(pauseMode=="paused-gate-rejected")
                        Assert(Volatile.Read(ref pauseCommands)==0 && ((System.Collections.IDictionary)sessionType.GetProperty("Pending")!.GetValue(session)!).Count==0,
                            "An intent already acknowledged by loadfile submitted a redundant pause command or leaked its pending request.");
                    Assert(pauseMode=="paused-canceled-next" || ReferenceEquals(Get<VideoItem>(window,"_current"),prior) && Get<int>(window,"_videoMetadataGeneration")==1 &&
                        Get<double>(window,"_lastValidPosition")==25 && Get<DataStore>(window,"_store").LoadSettings().MaxHeight==
                        (pauseMode.EndsWith("-rapid",StringComparison.Ordinal)?1080:720),
                        "The quality reload lost its video, metadata generation, position or saved quality.");
                }
                else if(pauseMode=="autoplay")
                {
                    Get<List<VideoItem>>(window,"_items").AddRange([prior,next]);Set(window,"_playingIndex",0);
                    Set(window,"_playingListGeneration",Get<int>(window,"_feedGeneration"));Invoke(window,"ShowPanel","player");
                    await (Task)Invoke(window,"AutoPlayNextAsync",1,Get<int>(window,"_autoplayEpoch"),false)!;
                }
                else Assert(await (Task<bool>)Invoke(window,"PlayVideoAsync",next,false,(double?)0,true)!,"The new video did not load.");
                await Get<Task>(window,"_relatedTask");
                if(pauseMode=="playing-delayed-observation")
                {
                    Assert(Get<CancellationTokenSource?>(window,"_videoCts") is null,"The late snapshot fixture did not wait for the load to finish.");
                    Invoke(window,"OnSnapshot",player.Snapshot with{Paused=true,Buffering=true});
                    Assert(Get<Button>(window,"PlayPauseButton").Content?.ToString()=="일시정지",
                        "A same-generation native pause observation delivered after the load changed its confirmed requested playback intent.");
                }
                JsonElement[] submitted;lock(loadOptions)submitted=loadOptions.ToArray();
                var expectedPaused=isQuality && paused;
                if(hasInput && pauseMode is not ("paused-rejected" or "paused-double-gate" or "paused-ambiguous-reply") && !pauseMode.EndsWith("-submit",StringComparison.Ordinal))expectedPaused=!paused;
                if(pauseMode=="paused-canceled-next")expectedPaused=false;
                var expectedPause=expectedPaused?"yes":"no";
                Assert(submitted.Length==1 && submitted[0].GetProperty("pause").GetString()==expectedPause,
                    $"The FIRST loadfile must carry pause={expectedPause} for {pauseMode}; actual="+
                    string.Join(",",submitted.Select(options=>options.GetProperty("pause").GetString())));
                Assert(submitted[0].GetProperty("start").GetString()==(isQuality && pauseMode!="paused-canceled-next"?"25":"0") && (hasInput || Volatile.Read(ref pauseCommands)==0),
                    "Playback restored pause after loading or changed the requested resume position.");
                Assert(pauseMode.EndsWith("-resolve",StringComparison.Ordinal) || loaded==(isQuality && pauseMode!="paused-canceled-next"?prior.Url:next.Url),"Playback selected the wrong media after the pause-intent transition.");
                if(hasInput)
                {
                    var expectedFinal=pauseMode.EndsWith("-submit",StringComparison.Ordinal)?!paused:expectedPaused;
                    await WaitUntilAsync(()=>nativePaused==expectedFinal,"The most recent pause input did not survive the reload submission boundary.");
                    Assert(Get<Button>(window,"PlayPauseButton").Content?.ToString()==(expectedFinal?"재생":"일시정지"),"The playback button did not show the latest accepted pause intent.");
                }
                Console.WriteLine($"QUALITY-PAUSE mode={pauseMode} firstLoadPause={expectedPause} loadCount={submitted.Length} scope=offline-WPF-selection-and-owned-IPC-no-real-mpv");
                return;
            }
            if(postLoadFailure is not null)
            {
                Set(player,"_mediaGeneration",1L);Set(player,"_snapshot",new PlaybackSnapshot{MediaGeneration=1,VideoUrl=VideoUrl,Loaded=true});
                Set(window,"_expectedMediaGeneration",1L);Get<AppSettings>(window,"_settings").AutoPlayNext=false;
                var trail=Get<List<VideoItem>>(window,"_playbackTrail");var priorTrail=Video() with{Id="trail000001"};trail.Add(priorTrail);
                var next=Video() with{Id="fixture0002",Url="https://www.youtube.com/watch?v=fixture0002"};
                var accepted=await (Task<bool>)Invoke(window,"PlayVideoAsync",next,false,(double?)0,true)!;
                Assert(!accepted && loaded==next.Url && Volatile.Read(ref postLoadFailures)==1 &&
                    (postLoadFailure!="speed" || Volatile.Read(ref appliedVolume)==1),
                    "The post-load property failure did not follow a successful LoadAsync or was accepted as successful playback.");
                Assert(processExit.HasExited && player.OwnedProcessIds.Count==0 && Get<object?>(player,"_session") is null &&
                    (bool)sessionType.GetField("ConnectionClosed")!.GetValue(session)! && (bool)sessionType.GetField("CookieConsumersStopped")!.GetValue(session)! &&
                    player.Snapshot.MediaGeneration==2 && player.Snapshot.Ended && !player.Snapshot.Loaded && player.Snapshot.VideoUrl==next.Url &&
                    Get<CookieLease?>(window,"_playCookie") is null,
                    "A post-load property failure did not finish cleanup of its exact native session and media generation.");
                Assert(trail.Count==1 && ReferenceEquals(trail[0],priorTrail) && ReferenceEquals(Get<VideoItem>(window,"_current"),next),
                    "A failed post-load property command consumed or appended playback history.");
                await serverTask;
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var expected="재생을 시작하지 못했습니다: mpv: fixture-post-load-"+postLoadFailure;
                Assert(Get<TextBlock>(window,"StatusText").Text==expected,
                    "The post-load property failure status was overwritten after the completed operation's queued snapshots drained.");
                Console.WriteLine($"POST-LOAD-FAIL property={postLoadFailure} injected={Volatile.Read(ref postLoadFailures)} cleanup=verified status=preserved scope=offline-WPF-fake-native-IPC");
                return;
            }
            if (failSelectedAccount)
            {
                // Synthetic session context only: no real cookies or network requests.
                var sessionService = Get<SessionService>(window, "_session");
                Set(sessionService, "<SelectedChannel>k__BackingField", new YouTubeChannel { Id = "fixture", AuthUser = 2, DataSyncId = "123456||fixture", PageId = "123456" });
                Set(player, "_mediaGeneration", 1L);
                Set(player, "_snapshot", new PlaybackSnapshot { MediaGeneration = 1, VideoUrl = VideoUrl, Loaded = true });
                Set(window,"_expectedMediaGeneration",1L);
                if(selectedResponse is not null)
                {
                    File.WriteAllText(oldCookiePath,"# Netscape HTTP Cookie File\n.fixture.invalid\tTRUE\t/\tTRUE\t0\tfixture\tnon-secret\n");
                    Set(window,"_playCookie",new CookieLease(oldCookiePath));
                    sessionType.GetField("CookieConsumerGeneration")!.SetValue(session,1L);
                    sessionType.GetField("HasPlaybackCookies")!.SetValue(session,true);
                    if(selectedResponse=="headers")Get<AppSettings>(window,"_settings").MaxHeight=1080;
                }
                var next = Video() with { Id = "fixture0002", Url = "https://www.youtube.com/watch?v=fixture0002" };
                var following = Video() with { Id = "fixture0003", Url = "https://www.youtube.com/watch?v=fixture0003" };
                if(preflightFailure is not null)
                {
                    File.WriteAllText(oldCookiePath,"# Netscape HTTP Cookie File\n.fixture.invalid\tTRUE\t/\tTRUE\t0\tfixture\tnon-secret\n");
                    Set(window,"_playCookie",new CookieLease(oldCookiePath));
                    sessionType.GetField("CookieConsumerGeneration")!.SetValue(session,1L);
                    sessionType.GetField("HasPlaybackCookies")!.SetValue(session,true);
                    if(preflightFailure=="stop-unconfirmed")
                    {
                        using var repair=Process.GetProcessById(process.Id);_=repair.SafeHandle;
                        process.Process.Dispose();
                        Exception? boundaryError=null;
                        try
                        {
                            var stopped=await (Task<bool>)Invoke(window,"StopPlaybackAsync")!;
                            Assert(!stopped && Get<bool>(window,"_playerStarted") && File.Exists(oldCookiePath) && oldCookieExistedAtQuit,
                                "A normal stop accepted unconfirmed termination or deleted its current consumer's cookie.");
                            var selected=sessionService.SelectedChannel;var account=Get<AppSettings>(window,"_settings").AccountKey;
                            var rejected=false;
                            try{await (Task)Invoke(window,"ResetAccountAsync")!;}catch(IOException){rejected=true;}
                            Assert(rejected && ReferenceEquals(sessionService.SelectedChannel,selected) && Get<AppSettings>(window,"_settings").AccountKey==account && File.Exists(oldCookiePath),
                                "An unconfirmed normal stop allowed account reset or released its still-owned cookie.");
                        }
                        catch(Exception error){boundaryError=error;throw;}
                        finally
                        {
                            typeof(OwnedProcess).GetField("<Process>k__BackingField",Private)!.SetValue(process,repair);
                            try{await player.StopAsync();}catch when(boundaryError is not null){}
                        }
                        Assert(await (Task<bool>)Invoke(window,"StopPlaybackAsync")! && !File.Exists(oldCookiePath) && processExit.HasExited,
                            "A recovered normal stop did not finish its actual process/cookie cleanup.");
                        return;
                    }
                    if(preflightFailure=="missing-ytdlp")File.Delete(paths.YtDlp);
                    if(preflightFailure is "missing-deno" or "previous-deno")File.Delete(paths.Deno);
                    if(preflightFailure=="invalid-account")
                        Set(sessionService,"<SelectedChannel>k__BackingField",new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="invalid"});
                    var failures=new List<string>();
                    if(preflightFailure=="previous-deno")
                    {
                        var trail=Get<List<VideoItem>>(window,"_playbackTrail");trail.Add(next);
                        await (Task)Invoke(window,"PreviousVideoAsync")!;
                        if(trail.Count!=1 || !ReferenceEquals(trail[0],next))failures.Add("Failed previous navigation consumed its playback trail.");
                    }
                    else if(await (Task<bool>)Invoke(window,"PlayVideoAsync",next,false,(double?)0,true)!)
                        failures.Add("A failed preflight was accepted as successful playback.");
                    if(!processExit.HasExited || player.OwnedProcessIds.Count!=0 || !player.Snapshot.Ended || player.Snapshot.Loaded)
                        failures.Add("Preflight failure retained previous native playback under the replacement UI.");
                    if(!oldCookieExistedAtQuit)failures.Add("The previous native consumer lost its cookie before owned shutdown.");
                    if(File.Exists(oldCookiePath))failures.Add("A verified stopped previous consumer retained its cookie lease.");
                    if(failures.Count>0)throw new InvalidOperationException(string.Join(" ",failures));
                    return;
                }
                if (authenticationSkip)
                {
                    Get<AppSettings>(window, "_settings").AutoPlayNext = true;
                    Get<List<VideoItem>>(window, "_items").AddRange([next, following]);
                }
                var accepted=await (Task<bool>)Invoke(window, "PlayVideoAsync", next, false, (double?)0, true)!;
                Assert(!accepted,"A selected-account resolution failure was accepted as successful playback.");
                Assert(Volatile.Read(ref stops) > 0 && loaded is null,
                    "A selected-account resolution failure left the previous native video playing under the new title.");
                Assert(player.Snapshot.Ended && !player.Snapshot.Loaded && player.Snapshot.VideoUrl == next.Url,
                    "A selected-account resolution failure did not terminate the new media generation.");
                if(selectedResponse is not null)
                {
                    var status=Get<TextBlock>(window,"StatusText").Text;
                    Assert(status=="재생을 시작하지 못했습니다: 선택한 계정의 영상 스트림을 읽지 못했습니다. 다른 영상을 재생하거나 잠시 후 다시 시도해 주세요." &&
                        !status.Contains("Firefox",StringComparison.OrdinalIgnoreCase) &&
                        !status.Contains("private-upstream-secret") && !status.Contains("private.fixture.invalid"),
                        "An unconfirmed metadata failure advertised profile replacement or exposed upstream details.");
                    Assert(!player.Snapshot.AuthenticationRequired && Get<CancellationTokenSource?>(window,"_authenticationSkipCts") is null &&
                        sessionService.SelectedChannel is {AuthUser:2,Id:"fixture"} && Get<AppSettings>(window,"_settings").MaxHeight==(selectedResponse=="headers"?1080:0) &&
                        Get<CancellationTokenSource?>(window,"_videoCts") is null && Get<CookieLease?>(window,"_playCookie") is null &&
                        oldCookieExistedAtQuit && !File.Exists(oldCookiePath) && processExit.HasExited && player.OwnedProcessIds.Count==0 &&
                        !Directory.EnumerateFiles(Path.Combine(root,"data","temp")).Any(),
                        "A malformed selected-account response changed authentication, account/quality, request lifetime or lease cleanup.");
                    Console.WriteLine($"SELECTED-RESPONSE mode={selectedResponse} accepted=False authentication=False cleanup=verified scope=offline-worker-and-owned-IPC");
                }
                if (authenticationSkip)
                {
                    var notice = Get<TextBlock>(window, "AuthenticationNoticeText");
                    Assert(notice.Text.Contains("7초") && player.Snapshot.AuthenticationRequired,
                        "The classified selected-account failure did not start the seven-second notice.");
                    var elapsed = Stopwatch.StartNew();
                    // A failed load now closes its exact native session, including
                    // failures before loadfile. Prove the old job is gone before
                    // supplying the next independent fake process/IPC connection.
                    // Real playback starts this replacement in StartAsync; this
                    // fixture continues to substitute only the native boundary.
                    await processExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(player.OwnedProcessIds.Count==0 &&
                        typeof(MpvPlayerService).GetField("_session",Private)!.GetValue(player) is null,
                        "The authentication failure did not fully stop its original native session before countdown recovery.");
                    var replacementPipe="fkyt-authentication-next-"+Guid.NewGuid().ToString("N");
                    replacementServer=new NamedPipeServerStream(replacementPipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    replacementClient=new NamedPipeClientStream(".",replacementPipe,PipeDirection.InOut,PipeOptions.Asynchronous);
                    var replacementConnecting=replacementClient.ConnectAsync(5000);
                    await replacementServer.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));await replacementConnecting;
                    replacementProcess=OwnedProcess.Start(paths.Python,["-c","import time;time.sleep(30)"],root);
                    replacementExit=Process.GetProcessById(replacementProcess.Id);_=replacementExit.SafeHandle;
                    var replacement=Activator.CreateInstance(sessionType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[replacementProcess,replacementClient],null)!;
                    sessionType.GetField("Reader")!.SetValue(replacement,new StreamReader(replacementClient,new UTF8Encoding(false),false,4096,true));
                    sessionType.GetField("Writer")!.SetValue(replacement,new StreamWriter(replacementClient,new UTF8Encoding(false),4096,true){AutoFlush=true});
                    Set(player,"_session",replacement);replacementServerTask=ServeAsync(replacementServer,true);
                    sessionType.GetField("ReadTask")!.SetValue(replacement,(Task)Invoke(player,"ReadMessagesAsync",replacement)!);
                    await Get<Task>(window, "_authenticationSkipTask").WaitAsync(TimeSpan.FromSeconds(12));
                    await Get<Task>(window, "_relatedTask");
                    Assert(elapsed.Elapsed.TotalSeconds >= 6.5 && Get<VideoItem>(window, "_current").Id == following.Id &&
                        player.Snapshot.VideoUrl == following.Url && player.Snapshot.Loaded && !player.Snapshot.AuthenticationRequired,
                        "The authentication countdown did not load the next playlist video through the normal playback path.");
                    const string expectedMedia="https://media.fixture.invalid/stream";
                    Assert(Volatile.Read(ref replacementLoads)==1 && loaded=="edl://%"+Encoding.UTF8.GetByteCount(expectedMedia)+"%"+expectedMedia,
                        "The authentication transition did not submit exactly one resolved load to its replacement native session.");
                    Console.WriteLine($"AUTH-SKIP oldProcessExited={processExit.HasExited} replacementLoads={Volatile.Read(ref replacementLoads)} next={Get<VideoItem>(window,"_current").Id} loaded={player.Snapshot.Loaded} countdownSeconds={elapsed.Elapsed.TotalSeconds:F2} scope=offline-WPF-fake-native-IPC");
                    Assert(Get<HashSet<Task>>(window, "_historyTasks").Count == 0 &&
                        !Directory.EnumerateFiles(Path.Combine(root, "data", "temp")).Any(),
                        "The synthetic authentication transition left a history write or plaintext lease.");
                }
                return;
            }
            // Simulate a quality click after selection but before the first load
            // reached its related-request boundary. The player IPC is fully fake.
            await (Task)Invoke(window, "PlayVideoAsync", Get<VideoItem>(window, "_current"), true, (double?)0, true)!;
            await Get<Task>(window, "_relatedTask");
            var marker = Path.Combine(root, "related-calls.txt");
            Assert(File.Exists(marker) && File.ReadAllLines(marker).Length == 1,
                "A quality change during initial loading permanently skipped the first related request.");
            await (Task)Invoke(window, "PlayVideoAsync", Get<VideoItem>(window, "_current"), true, (double?)0, true)!;
            await Get<Task>(window, "_relatedTask");
            Assert(File.ReadAllLines(marker).Length == 1, "A later quality-only reload repeated the completed related request.");
        }
        finally
        {
            Invoke(window, "CancelAuthenticationSkip", true);
            releaseLoad.TrySetResult();File.WriteAllText(Path.Combine(root,"resolve-release"),"cleanup");
            releaseFirstPause.TrySetResult();
            releaseFormat.TrySetResult();
            await Get<Task>(window, "_authenticationSkipTask");
            await player.DisposeAsync(); stopServer.Cancel(); await Task.WhenAll(serverTask,replacementServerTask);
            await processExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            processExit.Dispose();
            if(replacementExit is not null){await replacementExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));replacementExit.Dispose();}
            replacementProcess?.Dispose();replacementClient?.Dispose();replacementServer?.Dispose();
            Set(window, "_playerStarted", false); CleanupWindow(window, root);
        }
    }

    public static async Task RunPlayerAsync(string workspace)
    {
        var failures = new List<string>();
        foreach (var mode in new[] { "lost-prepared", "missing-prepared", "cancel-resolve", "no-resolver" })
        {
            try { await AccountFallbackAsync(workspace, mode); }
            catch (Exception ex) { failures.Add(mode + ": " + ex); }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static async Task AccountFallbackAsync(string workspace, string mode)
    {
        var root = FixtureRoot(workspace); var paths = new RuntimePaths(root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.YtDlp)!); File.WriteAllText(paths.YtDlp, "must never execute");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Deno)!); File.WriteAllText(paths.Deno, "must never execute");
        var pipeName = "fkyt-fallback-fixture-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connecting = client.ConnectAsync(5000); await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)); await connecting;
        await using var player = new MpvPlayerService(paths);
        var process = OwnedProcess.Start(Path.Combine(workspace, "runtime", "python", "python.exe"), ["-c", "import time;time.sleep(30)"], root);
        using var processExit = Process.GetProcessById(process.Id);
        _ = processExit.SafeHandle;
        using var stopServer = new CancellationTokenSource();
        var calls = 0; string? loaded = null; string? extractor = null;
        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(stopServer.Token) is { } line)
                {
                    using var request = JsonDocument.Parse(line); var command = request.RootElement.GetProperty("command"); var name = command[0].GetString();
                    object? data = null;
                    if (name == "loadfile") loaded = command[1].GetString();
                    if (name == "set_property" && command[1].GetString() == "ytdl-raw-options") extractor = command[2].GetProperty("extractor-args").GetString();
                    if (name == "get_property" && command[1].GetString() == "playlist") data = loaded is null ? Array.Empty<object>() : new object[] { new { id = 42, filename = loaded } };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = request.RootElement.GetProperty("request_id").GetInt64(), error = "success", data }));
                    if (name == "quit") return;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (stopServer.IsCancellationRequested) { }
        });
        try
        {
            var sessionType = typeof(MpvPlayerService).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [process, client], null)!;
            sessionType.GetField("Reader")!.SetValue(session, new StreamReader(client, new UTF8Encoding(false), false, 4096, true));
            sessionType.GetField("Writer")!.SetValue(session, new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true });
            Set(player, "_session", session); Set(player, "_host", (nint)1);
            Set(player, "_mediaGeneration", 1L); Set(player, "_snapshot", new PlaybackSnapshot { MediaGeneration = 1 });
            sessionType.GetField("ReadTask")!.SetValue(session, (Task)Invoke(player, "ReadMessagesAsync", session)!);
            if (mode == "lost-prepared")
            {
                var type = typeof(MpvPlayerService).GetNestedType("PreparedNext", BindingFlags.NonPublic)!;
                Set(player, "_preparedNext", Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [session, 41L, 1L, VideoUrl, 720], null));
            }
            using var cancellation = new CancellationTokenSource();
            Func<CancellationToken, Task<PreparedVideo>> resolver = token =>
            {
                calls++;
                if (mode == "cancel-resolve") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(new PreparedVideo("https://media.fixture.invalid/stream", new Dictionary<string, string>(), [720], []));
            };
            var method = typeof(MpvPlayerService).GetMethod("LoadAsync")!;
            var args = new List<object?> { VideoUrl, null, 720, 0d, cancellation.Token, new YouTubeChannel { Id = "fixture", AuthUser = 2, DataSyncId = "123456||fixture", PageId = "123456" }, null };
            if (method.GetParameters().Length >= 8) args.Add(mode == "no-resolver" ? null : resolver);
            if (method.GetParameters().Length >= 9) args.Add(false);
            if (method.GetParameters().Length >= 10) args.Add(null);
            Exception? error = null;
            try { await ((Task)method.Invoke(player, args.ToArray())!).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { error = ex; }
            if (mode == "no-resolver") Assert(error is InvalidOperationException && loaded == null, "An account without an authoritative resolver used ordinary extraction.");
            else if (mode == "cancel-resolve") Assert(error is OperationCanceledException && calls == 1 && loaded == null, "Cancelled account fallback loaded a stream or did not propagate cancellation.");
            else Assert(error == null && calls == 1 && loaded?.Contains("media.fixture.invalid", StringComparison.Ordinal) == true && extractor?.Contains("123456||fixture", StringComparison.Ordinal) == true,
                "Prepared adoption loss did not resolve exactly once under the selected account: " + error?.Message);
        }
        finally
        {
            await player.DisposeAsync(); stopServer.Cancel(); await serverTask;
            // This injected Session has no real StartAsync exit/drain tasks, and
            // its sleeping surrogate intentionally ignores mpv's quit command.
            // Observe termination, then release our independent process handle
            // before deleting the fixture's working directory.
            await processExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            processExit.Dispose();
            await DeleteReleasedFixtureAsync(root, "playback-state-");
        }
    }

    internal static async Task DeleteReleasedFixtureAsync(string root, string prefix)
    {
        root = Path.GetFullPath(root);
        Assert(Path.GetFileName(root).StartsWith(prefix, StringComparison.Ordinal) &&
            Path.GetFileName(Path.GetDirectoryName(root)) == "artifacts", "Fixture cleanup escaped its owned directory.");
        // The owned process has already exited and its handles were disposed.
        // Retry only the observed transient Windows sharing/lock violations, not other failures.
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            Assert((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Fixture root became a link.");
            try { Directory.Delete(root, true); return; }
            catch (IOException error) when (attempt < 10 && (error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(50).ConfigureAwait(false); }
        }
    }

    private static string FixtureRoot(string workspace) => Path.GetFullPath(Path.Combine(workspace, "artifacts", "playback-state-" + Guid.NewGuid().ToString("N")));
    private static void CopyPython(string workspace, string root)
    {
        var destination = Path.Combine(root, "runtime", "python"); Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(Path.Combine(workspace, "runtime", "python"))) File.Copy(source, Path.Combine(destination, Path.GetFileName(source)));
    }
    private static void CleanupWindow(MainWindow window, string root)
    {
        Get<DispatcherTimer>(window, "_saveTimer").Stop(); Set(window, "_closing", true); Set(window, "_allowClose", true);
        Get<CancellationTokenSource>(window, "_accountCts").Dispose(); Get<CancellationTokenSource>(window, "_historyCts").Dispose();
        ThumbnailImage.Cache?.Dispose(); ThumbnailImage.Cache = null; window.Close(); SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static void SetOptional(object instance, string name, object value) => instance.GetType().GetField(name, Private)?.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
