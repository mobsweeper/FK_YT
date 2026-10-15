using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FkYouTube;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class LifecycleReviewRegression
{
    public static async Task RunAccountPersistenceOnlyAsync(string workspace)
    {
        await RunUiAsync(workspace,"account-persist-failure");
        await RunUiAsync(workspace,"account-explicit-recovery");
    }
    public static async Task RunUiAsync(string workspace,string? only=null)
    {
        var failures=new List<Exception>();
        foreach(var name in new[]{"settings-startup","resume-failure","history-status","history-drain","read-drain","zero-position","transition-position","pending-resume","pending-completed-resume","pending-coalesces","settings-controls","incognito-settings","channel-refresh","cookie-drain","feed-drain","feed-read-drain","feed-drain-failure","feed-read-finally-drain","account-read-timeout","close-during-mutation","startup-warning-priority","subtitle-warning-lifetime","quality-load-failure","previous-rejected","account-readonly","account-persist-failure","account-explicit-recovery"})
        {
            if(only is not null && name!=only)continue;
            var root=Path.Combine(workspace,"artifacts","lifecycle16-"+Guid.NewGuid().ToString("N"));
            var paths=new RuntimePaths(root);
            if(name is "account-persist-failure" or "account-explicit-recovery")
            {
                try{await AccountPersistenceAsync(paths,name=="account-explicit-recovery");Console.WriteLine("LIFECYCLE16 PASS "+name);}
                catch(Exception error){Console.WriteLine("LIFECYCLE16 FAIL "+name+": "+error);failures.Add(new InvalidOperationException(name,error));}
                finally
                {
                    SqliteConnection.ClearAllPools();
                    Assert(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(workspace,"artifacts"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"account persistence fixture escaped its owned root");
                    Directory.Delete(root,true);
                }
                continue;
            }
            if(name is "settings-startup" or "account-readonly")File.WriteAllText(Path.Combine(paths.Data,"settings.json"),"{ invalid fixture");
            if(name=="settings-startup")
            {
                var syntheticCookies=Path.Combine(root,"synthetic-cookies.txt");
                File.WriteAllText(syntheticCookies,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t2147483647\tSID\tsynthetic-not-a-real-session\n");
                new SessionService(paths).ImportFile(syntheticCookies);
            }
            if(name=="startup-warning-priority")File.WriteAllText(Path.Combine(paths.Data,"session-recovery-required"),"fixture");
            var window=new MainWindow(paths,["--demo"]);
            try
            {
                if(name is "resume-failure" or "pending-resume" or "pending-completed-resume" or "quality-load-failure")
                {window.ShowActivated=false;window.Left=-10000;window.Top=-10000;window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
                Get<DispatcherTimer>(window,"_saveTimer").Stop();
                var settings=Get<AppSettings>(window,"_settings");settings.Incognito=false;settings.AutoPlayNext=false;
                var store=Get<DataStore>(window,"_store");
                var video=new VideoItem{Id="AAAAAAAAAAA",Url="https://www.youtube.com/watch?v=AAAAAAAAAAA",Title="Synthetic A",Duration=120};
                var next=video with{Id="BBBBBBBBBBB",Url="https://www.youtube.com/watch?v=BBBBBBBBBBB",Title="Synthetic B"};
                Set(window,"_current",video);Set(window,"_lastValidPosition",25d);Set(window,"_lastValidDuration",120d);
                if(name is "resume-failure" or "history-status" or "transition-position")
                {using var db=new SqliteConnection($"Data Source={Path.Combine(paths.Data,"viewer.sqlite")}");db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE history";cmd.ExecuteNonQuery();}
                switch(name)
                {
                    case "settings-startup":
                        Set(window,"_demo",false);Invoke(window,"OnLoaded",window,new RoutedEventArgs());
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(Get<FrameworkElement>(window,"SettingsPanel").Visibility==Visibility.Visible && !Get<bool>(window,"_hasFeed"),"Unread settings started browsing with a fallback account/mode.");
                        Assert(Get<SessionService>(window,"_session").IsConnected,"Fixture did not preserve the existing encrypted session.");
                        Assert(!(bool)(await (Task<bool>)Invoke(window,"PlayVideoAsync",next,false,null,true)!),"Unread settings allowed a video transition with an existing session.");
                        Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),video),"Unread settings replaced the current video before the privacy barrier.");
                        settings.SyncHistory=true;settings.Incognito=false;Set(window,"_hasLoadedCurrent",true);
                        Invoke(window,"SavePosition");
                        await (Task)Invoke(window,"SendHistoryAsync",video,settings.AccountKey,CancellationToken.None)!;
                        Assert(store.GetHistory(settings.AccountKey).Count==0,"Unread settings persisted history using unsafe fallback preferences.");
                        Assert(!Directory.EnumerateFiles(Path.Combine(paths.Data,"temp"),"cookies-*.txt").Any(),"Unread settings acquired plaintext login cookies.");
                        Assert(File.ReadAllText(Path.Combine(paths.Data,"settings.json"))=="{ invalid fixture","Privacy barrier overwrote the broken original.");
                        break;
                    case "resume-failure":
                        await (Task)Invoke(window,"PlayVideoAsync",next,false,null,true)!;
                        Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),video),"Resume read failure replaced active video.");
                        Assert(Get<CancellationTokenSource?>(window,"_videoCts")==null,"Resume read failure retained a disposed CTS.");
                        _=new DataStore(paths);
                        await (Task)Invoke(window,"PlayVideoAsync",next,false,null,true)!;
                        Assert(Get<CancellationTokenSource?>(window,"_videoCts")==null,"Retry left a CTS registered.");
                        break;
                    case "history-status":
                        // No synthetic login: the remote service returns false without any network request.
                        await (Task)Invoke(window,"SendHistoryAsync",video,settings.AccountKey,CancellationToken.None)!;
                        break;
                    case "history-drain":
                        Get<HashSet<Task>>(window,"_historyTasks").Add(Task.FromException(new IOException("synthetic local completion error")));
                        await (Task)Invoke(window,"ResetAccountAsync")!;
                        Assert(!Get<CancellationTokenSource>(window,"_accountCts").IsCancellationRequested,"Failed old task prevented fresh account token.");
                        break;
                    case "read-drain":
                        var read=(IDisposable)Invoke(window,"BeginAccountRead")!;
                        var draining=(Task)Invoke(window,"ResetAccountAsync")!;
                        Assert(!draining.IsCompleted,"Account transition did not wait for an old query's lease disposal.");
                        read.Dispose();await draining;
                        Assert(Get<HashSet<Task>>(window,"_accountReadTasks").Count==0,"Completed query lifetime leaked.");
                        break;
                    case "zero-position":
                        store.SavePosition(settings.AccountKey,video,25,120);
                        Set(window,"_expectedMediaGeneration",4L);
                        var zero=new PlaybackSnapshot{MediaGeneration=4,VideoUrl=video.Url,Loaded=true,Paused=true,Position=0,Duration=120};
                        Invoke(window,"OnSnapshot",zero);Invoke(window,"SavePosition");
                        Assert(store.GetHistory(settings.AccountKey).Single().Position==25,"Initial zero erased resume position.");
                        Invoke(window,"OnSnapshot",zero with{Paused=false,Position=18});
                        Invoke(window,"OnSnapshot",zero);Invoke(window,"SavePosition");
                        Assert(store.GetHistory(settings.AccountKey).Single().Position==0,"Confirmed return to zero was not saved.");
                        break;
                    case "transition-position":
                        Set(window,"_hasLoadedCurrent",true);Invoke(window,"SavePosition");
                        Set(window,"_current",next);Set(window,"_lastValidPosition",40d);
                        _=new DataStore(paths);Invoke(window,"SavePosition");
                        var rows=store.GetHistory(settings.AccountKey);
                        Assert(rows.Any(row=>row.Video.Id==video.Id && row.Position==25) && rows.Any(row=>row.Video.Id==next.Id && row.Position==40),"Failed final snapshot was lost after video switch.");
                        break;
                    case "pending-resume":
                    case "pending-completed-resume":
                        store.SavePosition(settings.AccountKey,video,10,120);
                        SqliteConnection.ClearAllPools();
                        var dbPath=Path.Combine(paths.Data,"viewer.sqlite");
                        File.Move(dbPath,dbPath+".fixture");Directory.CreateDirectory(dbPath);
                        Set(window,"_hasLoadedCurrent",true);Set(window,"_lastValidPosition",name=="pending-resume"?50d:120d);Invoke(window,"SavePosition");
                        Set(window,"_current",next);Set(window,"_hasLoadedCurrent",false);
                        Directory.Delete(dbPath);File.Move(dbPath+".fixture",dbPath);
                        await (Task)Invoke(window,"PlayVideoAsync",video,false,null,true)!;
                        Assert(Get<double>(window,"_lastValidPosition")== (name=="pending-resume"?50d:0d),"Pending resume used stale DB position or ignored completion margin.");
                        break;
                    case "pending-coalesces":
                        Set(window,"_hasLoadedCurrent",true);Set(window,"_lastValidPosition",10d);Invoke(window,"SavePosition");
                        using(var db=new SqliteConnection($"Data Source={Path.Combine(paths.Data,"viewer.sqlite")}"))
                        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE history";cmd.ExecuteNonQuery();}
                        Set(window,"_lastValidPosition",25d);Invoke(window,"SavePosition");
                        _=new DataStore(paths);Set(window,"_lastValidPosition",10d);Invoke(window,"SavePosition");
                        Assert(store.GetHistory(settings.AccountKey).Single().Position==10,"Queued old position overwrote the most recent seek back to the last committed value.");
                        break;
                    case "settings-controls":
                        Directory.CreateDirectory(Path.Combine(paths.Data,"settings.json"));
                        var oldSpeed=settings.Speed;settings.MaxHeight=1080;var oldHeight=settings.MaxHeight;var errors=new List<Exception>();
                        DispatcherUnhandledExceptionEventHandler handler=(_,e)=>{errors.Add(e.Exception);e.Handled=true;};
                        Dispatcher.CurrentDispatcher.UnhandledException+=handler;
                        try
                        {
                            Get<ComboBox>(window,"SpeedCombo").SelectedItem=Get<ComboBox>(window,"SpeedCombo").Items.Cast<ComboBoxItem>().First(i=>i.Tag?.ToString()=="2");
                            Set(window,"_updatingQuality",true);Get<ComboBox>(window,"QualityCombo").ItemsSource=new[]{720,360};Get<ComboBox>(window,"QualityCombo").SelectedItem=720;Set(window,"_updatingQuality",false);
                            Get<ComboBox>(window,"QualityCombo").SelectedItem=360;
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert(errors.Count==0,"Settings persistence escaped an async UI handler.");
                            Assert(settings.Speed==oldSpeed && settings.MaxHeight==oldHeight,"Failed speed/quality persistence left runtime selection changed.");
                            Assert(Get<ComboBox>(window,"QualityCombo").SelectedItem is 720,"Quality rollback lost actual available selection below preferred maximum.");
                        }
                        finally{Dispatcher.CurrentDispatcher.UnhandledException-=handler;}
                        break;
                    case "incognito-settings":
                        Directory.CreateDirectory(Path.Combine(paths.Data,"settings.json"));
                        var unexpected=new List<Exception>();
                        DispatcherUnhandledExceptionEventHandler privacyHandler=(_,e)=>{unexpected.Add(e.Exception);e.Handled=true;};
                        Dispatcher.CurrentDispatcher.UnhandledException+=privacyHandler;
                        try
                        {
                            Get<CheckBox>(window,"IncognitoCheck").IsChecked=true;
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert(unexpected.Count==0,"Private-mode save failure escaped UI handler.");
                            Assert(settings.Incognito && Get<CheckBox>(window,"IncognitoCheck").IsChecked==true,"Private mode did not stay active after local persistence failure.");
                            Assert(Get<int>(window,"_accountBusyDepth")==0 && !Get<CancellationTokenSource>(window,"_accountCts").IsCancellationRequested,"Private mode left account transition blocked.");
                        }
                        finally{Dispatcher.CurrentDispatcher.UnhandledException-=privacyHandler;}
                        break;
                    case "channel-refresh":
                        Set(window,"_feedGeneration",7);Set(window,"_playingListGeneration",7);Set(window,"_hasMore",true);
                        Invoke(window,"ShowPanel","player");
                        // No runtime worker is installed in this isolated fixture; a synthetic cookie makes the refresh attempt fail locally.
                        var cookie=Path.Combine(root,"synthetic.txt");File.WriteAllText(cookie,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tSYNTHETIC_NOT_A_TOKEN\n");
                        Get<SessionService>(window,"_session").ImportFile(cookie);
                        Invoke(window,"ReloadChannels_Click",window,new RoutedEventArgs());
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(Get<FrameworkElement>(window,"PlayerPanel").Visibility==Visibility.Visible && ReferenceEquals(Get<VideoItem>(window,"_current"),video),"Failed channel refresh hid active playback.");
                        Assert(Get<TextBlock>(window,"StatusText").Text.Contains("채널 목록을 확인하지 못"),"Channel error was overwritten by feed navigation.");
                        var keptFeed=Get<int>(window,"_feedGeneration")==7 && Get<int>(window,"_playingListGeneration")==7 && Get<bool>(window,"_hasMore");
                        Set(window,"_expectedMediaGeneration",4L);
                        Invoke(window,"OnSnapshot",new PlaybackSnapshot{MediaGeneration=4,VideoUrl=video.Url,Loaded=true,Position=26,Duration=120});
                        Assert(keptFeed && Get<TextBlock>(window,"StatusText").Text.Contains("채널 목록을 확인하지 못"),"Channel refresh failure invalidated next-page lifetime or lost error on next playback snapshot.");
                        break;
                    case "cookie-drain":
                        Get<List<Task>>(Get<SessionService>(window,"_session"),"_cookieCleanups").Add(Task.FromException(new IOException("synthetic deletion failure")));
                        var rejected=false;
                        try{await (Task)Invoke(window,"ResetAccountAsync")!;}catch(IOException){rejected=true;}
                        Assert(rejected && !Get<CancellationTokenSource>(window,"_accountCts").IsCancellationRequested,"Failed cookie cleanup admitted transition or left old-account operations cancelled.");
                        break;
                    case "feed-drain":
                    case "feed-read-drain":
                    case "feed-drain-failure":
                    case "feed-read-finally-drain":
                        var feed=Get<FeedService>(window,"_feed");
                        var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var cleanupTasks=Get<HashSet<Task>>(feed,"_cleanupTasks");
                        var cleanupLock=Get<object>(feed,"_cleanupSync");
                        var feedGate=Get<SemaphoreSlim>(feed,"_gate");
                        var pendingRead=name=="feed-read-finally-drain"?(IDisposable)Invoke(window,"BeginAccountRead")!:null;
                        if(name=="feed-read-drain")await feedGate.WaitAsync();
                        else if(pendingRead is null)lock(cleanupLock)cleanupTasks.Add(cleanup.Task);
                        var reset=(Task)Invoke(window,"ResetAccountAsync")!;
                        try
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            Assert(!reset.IsCompleted,"Account transition did not await feed read/cleanup ownership.");
                            if(pendingRead is not null)
                            {
                                lock(cleanupLock)cleanupTasks.Add(cleanup.Task);
                                pendingRead.Dispose();pendingRead=null;
                                await Dispatcher.Yield(DispatcherPriority.Background);
                                Assert(!reset.IsCompleted,"Feed drain raced ahead of a canceled read's final lease transfer.");
                            }
                        }
                        finally
                        {
                            pendingRead?.Dispose();
                            if(name=="feed-read-drain")
                            {lock(cleanupLock)cleanupTasks.Add(cleanup.Task);feedGate.Release();}
                            if(name=="feed-drain-failure")cleanup.SetException(new AggregateException(new IOException("synthetic feed cleanup failure")));
                            else cleanup.SetResult();
                            try
                            {
                                try{await reset;Assert(name!="feed-drain-failure","Failed feed cleanup admitted transition.");}
                                catch(IOException){Assert(name=="feed-drain-failure","Successful feed drain unexpectedly failed.");}
                            }
                            finally
                            {
                                // Consume the deliberate fault even against the unfixed baseline.
                                try{await feed.DrainPendingAsync();}catch(AggregateException){}
                            }
                        }
                        Assert(!Get<CancellationTokenSource>(window,"_accountCts").IsCancellationRequested,"Feed cleanup failure stranded the old-account token.");
                        break;
                    case "account-read-timeout":
                        var heldRead=(IDisposable)Invoke(window,"BeginAccountRead")!;
                        var timeoutFeed=Get<FeedService>(window,"_feed");
                        Get<HashSet<Task>>(timeoutFeed,"_cleanupTasks").Add(Task.CompletedTask);
                        Get<List<Task>>(Get<SessionService>(window,"_session"),"_cookieCleanups").Add(Task.CompletedTask);
                        var timedReset=(Task)Invoke(window,"ResetAccountAsync")!;
                        try
                        {
                            var bounded=await Task.WhenAny(timedReset,Task.Delay(7500))==timedReset;
                            Assert(bounded,"Account read prevented the bounded feed cleanup from ever running.");
                            var aborted=false;
                            try{await timedReset;}catch(IOException){aborted=true;}
                            Assert(aborted,"Timed-out account read admitted a session change.");
                            Assert(Get<HashSet<Task>>(timeoutFeed,"_cleanupTasks").Count==0 &&
                                Get<List<Task>>(Get<SessionService>(window,"_session"),"_cookieCleanups").Count==0,
                                "Account read timeout skipped completed feed/cookie cleanup.");
                            Assert(!Get<CancellationTokenSource>(window,"_accountCts").IsCancellationRequested,
                                "Account read timeout left the old account permanently cancelled.");
                        }
                        finally{heldRead.Dispose();try{await timedReset;}catch(IOException){}}
                        break;
                    case "close-during-mutation":
                        var mutationSession=Get<SessionService>(window,"_session");
                        var synthetic=Path.Combine(root,"mutation-cookie.txt");
                        File.WriteAllText(synthetic,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tSYNTHETIC_BEFORE\n");
                        mutationSession.ImportFile(synthetic);store.SaveSettings(settings);
                        var savedSettings=File.ReadAllBytes(Path.Combine(paths.Data,"settings.json"));
                        var savedSession=File.ReadAllBytes(Path.Combine(paths.Data,"session.dpapi"));
                        var mutationRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var lateCleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Func<Task> change=async()=>
                        {
                            Get<AppSettings>(window,"_settings").AccountKey="synthetic-uncommitted";
                            File.WriteAllText(synthetic,File.ReadAllText(synthetic).Replace("SYNTHETIC_BEFORE","SYNTHETIC_AFTER"));
                            mutationSession.ImportFile(synthetic);
                            try{await mutationRelease.Task;}
                            finally{Get<List<Task>>(mutationSession,"_cookieCleanups").Add(lateCleanup.Task);}
                        };
                        var mutating=(Task)Invoke(window,"PersistSessionChangeAsync",change,false)!;
                        try
                        {
                            Invoke(window,"Window_Closing",window,new System.ComponentModel.CancelEventArgs());
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert(!Get<bool>(window,"_allowClose"),"Window closed before account mutation rollback/export finally.");
                            Assert(File.ReadAllBytes(Path.Combine(paths.Data,"settings.json")).SequenceEqual(savedSettings),
                                "Shutdown saved the tentative account settings before transaction rollback.");
                            mutationRelease.TrySetResult();
                            try{await mutating;}catch(OperationCanceledException){}
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert(!Get<bool>(window,"_allowClose"),"Shutdown missed cleanup enqueued by the mutation finally.");
                            Assert(File.ReadAllBytes(Path.Combine(paths.Data,"session.dpapi")).SequenceEqual(savedSession),
                                "Shutdown did not await encrypted session rollback.");
                        }
                        finally
                        {
                            mutationRelease.TrySetResult();lateCleanup.TrySetResult();
                            try{await mutating;}catch(OperationCanceledException){}
                            var closeDeadline=Environment.TickCount64+5000;
                            while(!Get<bool>(window,"_allowClose") && Environment.TickCount64<closeDeadline)await Task.Delay(10);
                            Assert(Get<bool>(window,"_allowClose"),"Shutdown did not finish after mutation and cleanup completed.");
                        }
                        break;
                    case "startup-warning-priority":
                        Set(window,"_demo",false);Set(window,"_profileListWarning","synthetic profile warning");
                        Invoke(window,"OnLoaded",window,new RoutedEventArgs());
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(Get<TextBlock>(window,"StatusText").Text.Contains("계정 변경"),"Optional Firefox profile warning hid session recovery notice.");
                        Assert(Get<TextBlock>(window,"StatusText").Text.EndsWith("synthetic profile warning") &&
                            Get<ComboBox>(window,"ProfileCombo").ToolTip?.ToString()=="synthetic profile warning",
                            "Optional profile warning was not retained separately from the primary startup result.");
                        break;
                    case "subtitle-warning-lifetime":
                        Set(window,"_expectedMediaGeneration",4L);
                        Set(Get<MpvPlayerService>(window,"_player"),"_snapshot",new PlaybackSnapshot{MediaGeneration=4,VideoUrl=video.Url,Ended=true,Loaded=false});
                        await (Task)Invoke(window,"LoadPreparedCaptionsAsync",Array.Empty<PreparedCaption>(),Get<int>(window,"_videoGeneration"))!;
                        var subtitleNotice=Get<TextBlock>(window,"StatusText").Text;
                        Assert(subtitleNotice.Contains("영상을 다시 재생"),"Subtitle setup fixture did not explain recovery after player termination.");
                        Invoke(window,"OnSnapshot",new PlaybackSnapshot{MediaGeneration=4,VideoUrl=video.Url,Loaded=true,Position=26,Duration=120});
                        Assert(Get<TextBlock>(window,"StatusText").Text==subtitleNotice,"Normal playback snapshot hid unresolved subtitle failure.");
                        Invoke(window,"ResetPlaybackExtras",false,false);
                        Assert(Get<string?>(window,"_subtitleLoadError")==null,"New playback retained old subtitle warning.");
                        break;
                    case "quality-load-failure":
                        Set(window,"_hasLoadedCurrent",true);
                        Get<Slider>(window,"SeekSlider").IsEnabled=true;
                        await (Task)Invoke(window,"PlayVideoAsync",video,true,25d,true)!;
                        Assert(!Get<bool>(window,"_hasLoadedCurrent") && !Get<Slider>(window,"SeekSlider").IsEnabled,
                            "Same-video quality load failure retained loaded state or enabled seeking.");
                        Assert(Get<TextBlock>(window,"StatusText").Text.Contains("재생을 시작하지 못"),"Quality failure fixture unexpectedly loaded a real player.");
                        break;
                    case "previous-rejected":
                        using(var db=new SqliteConnection($"Data Source={Path.Combine(paths.Data,"viewer.sqlite")}"))
                        {db.Open();using var command=db.CreateCommand();command.CommandText="DROP TABLE history";command.ExecuteNonQuery();}
                        Set(window,"_hasLoadedCurrent",true);
                        for(var i=0;i<32;i++){Set(window,"_current",video with{Id=$"P{i:0000000000}"});Invoke(window,"SavePosition");}
                        Set(window,"_current",video);var trail=Get<List<VideoItem>>(window,"_playbackTrail");trail.Add(next);
                        await (Task)Invoke(window,"PreviousVideoAsync")!;
                        Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),video) && trail.Count==1 && ReferenceEquals(trail[0],next),"Rejected previous navigation consumed its history entry.");
                        break;
                    case "account-readonly":
                        try{await (Task)Invoke(window,"ApplyChannelAsync",new YouTubeChannel{Id="fixture-account"})!;}catch(Exception){}
                        Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),video),"Known unreadable settings altered playback before rejecting account persistence.");
                        break;
                }
                Console.WriteLine("LIFECYCLE16 PASS "+name);
            }
            catch(Exception error){Console.WriteLine("LIFECYCLE16 FAIL "+name+": "+error);failures.Add(new InvalidOperationException(name,error));}
            finally
            {
                Set(window,"_closing",true);Set(window,"_allowClose",true);
                await Get<FeedService>(window,"_feed").DisposeAsync();await Get<MpvPlayerService>(window,"_player").DisposeAsync();
                Get<CancellationTokenSource>(window,"_accountCts").Dispose();Get<CancellationTokenSource>(window,"_historyCts").Dispose();
                ThumbnailImage.Cache?.Dispose();ThumbnailImage.Cache=null;window.Close();SqliteConnection.ClearAllPools();
                Directory.Delete(root,true);
            }
        }
        if(failures.Count>0)throw new AggregateException(failures);
    }
    private static async Task AccountPersistenceAsync(RuntimePaths paths,bool recovery)
    {
        const string cookies="# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tSYNTHETIC_OLD_COOKIE\n";
        var session=new SessionService(paths);var store=new DataStore(paths);
        var source=Path.Combine(paths.Root,"synthetic-cookies.txt");File.WriteAllText(source,cookies);session.ImportFile(source);
        var channel=new YouTubeChannel{Id="synthetic-old-channel",DataSyncId="synthetic_user||",AuthUser=1};
        var channelPath=Path.Combine(paths.Data,"channels",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel.Id)))+".dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(channelPath)!);
        File.WriteAllBytes(channelPath,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new{channel,cookies},JsonDefaults.Options),null,DataProtectionScope.CurrentUser));
        session.RestoreSelectedChannel(channel.Id);var oldSelected=session.SelectedChannel;
        var original=new AppSettings{AccountKey="synthetic-old-identity",AuthMode="synthetic old connection",SelectedChannelId=channel.Id,FirefoxProfile="synthetic-unused-profile",ChannelHistoryKeys=new(){{channel.Id,"synthetic-old-identity"}},Incognito=false};
        store.SaveSettings(original);
        var sessionFile=Path.Combine(paths.Data,"session.dpapi");var settingsFile=Path.Combine(paths.Data,"settings.json");
        var marker=Path.Combine(paths.Data,"session-recovery-required");
        var oldSession=File.ReadAllBytes(sessionFile);var oldChannel=File.ReadAllBytes(channelPath);var oldSettings=File.ReadAllBytes(settingsFile);
        if(recovery){File.WriteAllText(marker,"synthetic interrupted change");session=new SessionService(paths);}
        // This helper only uses four managed fields. Skipping the Window constructor
        // prevents Firefox discovery, native hosts, timers, worker starts and all UI I/O.
        var window=(MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Set(window,"_store",store);Set(window,"_settings",original);Set(window,"_session",session);Set(window,"_closing",false);
        Set(window,"_accountMutationTasks",new HashSet<Task>());
        var freshIdentity="synthetic-fresh-"+Guid.NewGuid().ToString("N");var sawSeparateClone=false;
        Func<Task> change=()=>
        {
            var pending=Get<AppSettings>(window,"_settings");
            sawSeparateClone=!ReferenceEquals(pending,original) && !ReferenceEquals(pending.ChannelHistoryKeys,original.ChannelHistoryKeys);
            File.WriteAllText(source,cookies.Replace("SYNTHETIC_OLD_COOKIE","SYNTHETIC_FRESH_COOKIE"));session.ImportFile(source);session.ForgetSelectedChannel();
            pending.AccountKey=freshIdentity;pending.AuthMode="synthetic fresh connection";pending.SelectedChannelId="";pending.ChannelHistoryKeys.Clear();
            if(recovery)
            {
                Assert(session.HasSessionRecoveryFailure && !session.IsConnected,"Recovery became connected before settings publication.");
                var blocked=false;try{using var ignored=session.AcquireBase();}catch(IOException){blocked=true;}
                Assert(blocked,"Recovery consumed a new cookie before its settings commit.");
            }
            return Task.CompletedTask;
        };
        try
        {
            if(!recovery)
            {
                Exception? failure=null;
                using(var held=new FileStream(settingsFile,FileMode.Open,FileAccess.Read,FileShare.Read))
                    try{await (Task)Invoke(window,"PersistSessionChangeAsync",change,false)!;}catch(Exception error){failure=error;}
                Assert(failure is IOException or UnauthorizedAccessException,"Locked settings publication did not fail as a storage error: "+(failure?.GetType().Name??"none"));
                Assert(sawSeparateClone && ReferenceEquals(Get<AppSettings>(window,"_settings"),original),"Persistence failure did not restore the previous settings object.");
                Assert(original.AccountKey=="synthetic-old-identity" && original.AuthMode=="synthetic old connection" && original.SelectedChannelId==channel.Id &&
                    original.ChannelHistoryKeys.Count==1 && original.ChannelHistoryKeys[channel.Id]=="synthetic-old-identity","Failed cloned identity mutation changed original settings or channel keys.");
                Assert(File.ReadAllBytes(sessionFile).SequenceEqual(oldSession) && File.ReadAllBytes(settingsFile).SequenceEqual(oldSettings) && File.ReadAllBytes(channelPath).SequenceEqual(oldChannel),
                    "UI settings publication failure changed its previous encrypted session/settings pair or unrelated channel file.");
                Assert(ReferenceEquals(session.SelectedChannel,oldSelected) && Get<string>(session,"_selectedChannelId")==channel.Id && session.IsConnected && !session.HasSessionRecoveryFailure && !File.Exists(marker),
                    "UI persistence rollback did not restore selected state or clear its completed recovery marker.");
                using var lease=session.Acquire();Assert(File.ReadAllText(lease.Path!).Contains("SYNTHETIC_OLD_COOKIE"),"Restored UI selection acquired a different synthetic session.");
            }
            else
            {
                Assert(session.HasSessionRecoveryFailure && !session.IsConnected,"Synthetic interrupted session was usable before explicit recovery.");
                await (Task)Invoke(window,"PersistSessionChangeAsync",change,true)!;
                var current=Get<AppSettings>(window,"_settings");var persisted=store.LoadSettings();
                Assert(sawSeparateClone && !ReferenceEquals(current,original) && current.AccountKey==freshIdentity && persisted.AccountKey==freshIdentity && current.AccountKey!=original.AccountKey,
                    "Explicit recovery did not keep and persist the fresh settings identity.");
                Assert(current.SelectedChannelId.Length==0 && persisted.SelectedChannelId.Length==0 && current.ChannelHistoryKeys.Count==0 && persisted.ChannelHistoryKeys.Count==0 &&
                    original.ChannelHistoryKeys.Count==1 && original.SelectedChannelId==channel.Id,"Recovery retained old selected identity or changed its original settings object.");
                Assert(session.IsConnected && !session.HasSessionRecoveryFailure && session.SelectedChannel is null && !File.Exists(marker),"Committed fresh session did not clear the durable recovery barrier.");
                Assert(File.ReadAllBytes(channelPath).SequenceEqual(oldChannel),"Fresh base recovery changed an unrelated old channel file.");
                using var lease=session.AcquireBase();Assert(File.ReadAllText(lease.Path!).Contains("SYNTHETIC_FRESH_COOKIE"),"Committed UI recovery acquired the wrong synthetic base session.");
            }
        }
        finally{await session.DrainCookieCleanupAsync();}
    }
    static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!.Invoke(target,args);
    static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static void Set(object target,string name,object? value)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
