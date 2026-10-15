using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FkYouTube;
using FkYouTube.Controls;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class AsyncLifecycleRegression
{
    public static Task RunAsync(string workspace,string? suite=null,Func<string,Func<Task>,Task>? report=null)
    {
        const int groupDeadlineSeconds=120;
        const int timeoutDrainSeconds=20;
        var groups=new List<(string Key,string Name,Func<CancellationToken,Task> Run)>
        {
            ("privacy-recovery","Unread settings block authenticated playback and history",_=>LifecycleReviewRegression.RunUiAsync(workspace,"settings-startup")),
            ("review16","Lifecycle review 16 models",_=>LifecycleReviewRegression.RunUiAsync(workspace)),
            ("history-partial","Partial remote history preserves confirmed status",_=>PartialRemoteHistoryKeepsConfirmedStatus(workspace)),
            ("save-failure","Playback survives local save failure and retries",_=>PlaybackSaveFailureKeepsPlaybackAndRetries(workspace)),
            ("account","Account transitions and shutdown",async token=>
            {
                var failures=new List<Exception>();
                foreach(var test in new Func<string,Task>[]{AccountResetCannotRenewWorkAfterShutdown,DisconnectCannotExposeOldHistory,
                    AccountTransitionBlocksPlayerNavigation,PendingNavigationCannotResumeAcrossAccountTransition,ShutdownSaveFailureStillCleansOwnedResources})
                {token.ThrowIfCancellationRequested();try{await test(workspace);}catch(Exception error){failures.Add(error);}}
                if(failures.Count>0)throw new AggregateException(failures);
            }),
            ("state","Playback metadata and autoplay queue interleavings",token=>PlaybackStateRegression.RunUiAsync(workspace,token)),
            ("pause-intent","Latest pause intent across reload and IPC acknowledgement",token=>PlaybackStateRegression.RunPauseIntentAsync(workspace,token)),
            ("quality-ui","Automatic and fixed-cap quality selection",token=>PlaybackStateRegression.RunUiAsync(workspace,token,qualityOnly:true)),
            ("selected-account-errors","Selected-account response failures and authentication skip",token=>PlaybackStateRegression.RunSelectedAccountFailureReviewAsync(workspace,token)),
            ("controls","Main and popout native controls and view modes",token=>PlayerInteractionRegression.RunUiAsync(workspace,"controls",token)),
            ("prepared-subtitles","Prepared subtitles request budget cancellation and prefetch",token=>PlayerInteractionRegression.RunUiAsync(workspace,"prepared-subtitles",token)),
            ("subtitle-selection","Subtitle selection catalog failure and popup ownership",token=>PlayerInteractionRegression.RunUiAsync(workspace,"subtitle-selection",token)),
            ("ui-language","App UI language persistence live refresh isolation and rollback",_=>UiLanguageRegression.RunUiAsync(workspace))
        }.Where(group=>suite is null?group.Key is not ("privacy-recovery" or "quality-ui" or "selected-account-errors"):group.Key==suite ||
            suite=="interactions" && (group.Key is "controls" or "prepared-subtitles" or "subtitle-selection")).ToArray();
        if(groups.Length==0)throw new ArgumentException("Unknown UI regression suite.",nameof(suite));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var failures = new List<Exception>();
                        foreach(var group in groups)
                        {
                            using var stop=new CancellationTokenSource();
                            var abort=false;
                            async Task RunGroupAsync()
                            {
                                var watch=System.Diagnostics.Stopwatch.StartNew();
                                Console.WriteLine($"UI-GROUP started={group.Key} deadlineSeconds={groupDeadlineSeconds}");
                                var running=group.Run(stop.Token);
                                try{await running.WaitAsync(TimeSpan.FromSeconds(groupDeadlineSeconds));}
                                catch(TimeoutException error)
                                {
                                    // No following fixture may start while timed-out UI
                                    // work still owns this dispatcher. Stop case admission,
                                    // allow the case's existing bounded finally to drain,
                                    // and abort the remaining groups before App shutdown.
                                    abort=true;stop.Cancel();
                                    try{await running.WaitAsync(TimeSpan.FromSeconds(timeoutDrainSeconds));}
                                    catch(Exception drainError){Console.WriteLine("UI-GROUP timeout-drain="+drainError.GetType().Name);}
                                    throw new TimeoutException($"UI group {group.Key} exceeded its {groupDeadlineSeconds}-second deadline; remaining UI groups were aborted.",error);
                                }
                                finally{Console.WriteLine($"UI-GROUP finished={group.Key} seconds={watch.Elapsed.TotalSeconds:F2} aborted={abort}");}
                            }
                            if(report is not null)await report(group.Name,RunGroupAsync);
                            else try{await RunGroupAsync();}catch(Exception error){failures.Add(error);}
                            if(abort)break;
                        }
                        if (failures.Count > 0) throw new AggregateException(failures);
                        completion.TrySetResult();
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally { app.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
                });
                Dispatcher.Run();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "FK_YT isolated async lifecycle regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // One Application/STA for all selected groups. The outer watchdog is
        // derived from their individual budgets plus one abort/teardown allowance.
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(groups.Length*groupDeadlineSeconds+timeoutDrainSeconds+5));
    }

    private static async Task AccountResetCannotRenewWorkAfterShutdown(string workspace)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "artifacts", "async-lifecycle-" + Guid.NewGuid().ToString("N")));
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]);
        var heldHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountCancellation = Get<CancellationTokenSource>(window, "_accountCts");
        var history = Get<HashSet<Task>>(window, "_historyTasks");
        history.Add(heldHistory.Task);
        try
        {
            // No player or worker is started. Hold only the existing history-drain
            // boundary, then apply the state and cancellation used by Closing.
            var resetting = InvokeTask(window, "ResetAccountAsync");
            Assert(!resetting.IsCompleted, "The fixture did not hold the account reset at its history drain.");
            Set(window, "_closing", true);
            accountCancellation.Cancel();
            heldHistory.SetResult();
            var cancelled = false;
            try { await resetting; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Account reset returned successfully after shutdown and could continue connecting an account.");
            Assert(ReferenceEquals(Get<CancellationTokenSource>(window, "_accountCts"), accountCancellation) &&
                   accountCancellation.IsCancellationRequested,
                "Account reset replaced shutdown's cancelled token with a live account token.");
        }
        finally
        {
            heldHistory.TrySetResult();
            history.Clear();
            Get<DispatcherTimer>(window, "_saveTimer").Stop();
            Get<CancellationTokenSource>(window, "_accountCts").Dispose();
            Get<CancellationTokenSource>(window, "_historyCts").Dispose();
            ThumbnailImage.Cache?.Dispose();
            ThumbnailImage.Cache = null;
            Set(window, "_allowClose", true);
            window.Close();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DisconnectCannotExposeOldHistory(string workspace)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "artifacts", "async-lifecycle-" + Guid.NewGuid().ToString("N")));
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]);
        var heldHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = Get<HashSet<Task>>(window, "_historyTasks");
        var settings = Get<AppSettings>(window, "_settings");
        settings.AccountKey = "fixture-old-account";
        Get<DataStore>(window, "_store").SavePosition(settings.AccountKey,
            new VideoItem { Id = "fixture0001", Url = "https://www.youtube.com/watch?v=fixture0001", Title = "Fixture history" }, 12, 120);
        history.Add(heldHistory.Task);
        try
        {
            typeof(MainWindow).GetMethod("Disconnect_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            // An enabled sidebar admits a real local-history click while disconnect
            // waits for the old account's already-running history task to finish.
            if (Get<FrameworkElement>(window, "Sidebar").IsEnabled)
                await InvokeTask(window, "ShowLocalHistoryAsync");
            heldHistory.SetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Session transactions publish a new settings snapshot only after commit.
            while (Get<AppSettings>(window,"_settings").AccountKey != "guest") await Task.Delay(10, deadline.Token);
            Assert(settings.AccountKey=="fixture-old-account","Disconnect mutated its previous settings snapshot before commit.");
            Assert(Get<List<VideoItem>>(window, "_items").Count == 0,
                "Disconnect changed to guest while the previous account's history rows remained visible.");
        }
        finally
        {
            heldHistory.TrySetResult();
            history.Clear();
            Get<DispatcherTimer>(window, "_saveTimer").Stop();
            Get<CancellationTokenSource>(window, "_accountCts").Dispose();
            Get<CancellationTokenSource>(window, "_historyCts").Dispose();
            ThumbnailImage.Cache?.Dispose();
            ThumbnailImage.Cache = null;
            Set(window, "_allowClose", true);
            window.Close();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AccountTransitionBlocksPlayerNavigation(string workspace)
    {
        var root = PrepareFeedFixture(workspace);
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]) { ShowActivated = false };
        var heldHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = Get<HashSet<Task>>(window, "_historyTasks");
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Set(window, "_demo", false);
            ImportSyntheticSession(window, root);
            var video = new VideoItem { Id = "fixture0001", Title = "Fixture", Url = "https://www.youtube.com/watch?v=fixture0001",
                Channel = "Fixture owner", ChannelId = "UCabcdefghijklmnopqrstuv", ChannelUrl = "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv/videos" };
            Invoke(window, "ShowPanel", "player"); Invoke(window, "UpdatePlayingChannel", video);
            history.Add(heldHistory.Task);
            Get<CheckBox>(window, "IncognitoCheck").IsChecked = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            var channel = Get<Button>(window, "PlayingChannelButton");
            Assert(!channel.IsEnabled && !Get<FrameworkElement>(window, "PlayerPanel").IsEnabled,
                "Player channel navigation remained reachable during an account transition.");
            var view = Get<int>(window, "_viewGeneration");
            channel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, channel));
            await InvokeTask(window, "OpenFeedAsync", "home", "Blocked home");
            await InvokeTask(window, "PlayVideoAsync", video, false, null, true);
            await InvokeTask(window, "ShowLocalHistoryAsync");
            Invoke(window, "Disconnect_Click", window, new RoutedEventArgs());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(Get<int>(window, "_viewGeneration") == view && !File.Exists(Path.Combine(root, "feed-request.json")),
                "A common navigation/account entry point admitted work with the previous session while switching.");
            heldHistory.SetResult();
            await Until(() => Get<AppSettings>(window, "_settings").Incognito && Get<FrameworkElement>(window, "Sidebar").IsEnabled);
            Assert(Get<List<VideoItem>>(window, "_items").Count == 0, "Previous account results survived the private landing screen.");
            Get<CheckBox>(window, "IncognitoCheck").IsChecked = false;
            await Until(() => File.Exists(Path.Combine(root, "feed-request.json")) && !Get<bool>(window, "_loading"));
            Assert(!Get<AppSettings>(window, "_settings").Incognito && Get<List<VideoItem>>(window, "_items").Count == 1,
                "The completed account transition failed to start its new home feed.");
            Set(window, "_loading", true); Set(window, "_hasMore", true);
            Set(window, "_feedNavigationMetadata", new FeedNavigationMetadata());
            Invoke(window, "SetAccountBusy", true); Invoke(window, "SetAccountBusy", true); Invoke(window, "SetAccountBusy", false);
            Assert(!Get<FrameworkElement>(window, "Sidebar").IsEnabled && Get<bool>(window, "_ratingsAccountBusy"),
                "Nested channel reload prematurely released the outer account/rating barrier.");
            Invoke(window, "SetAccountBusy", false);
            Assert(Get<FrameworkElement>(window, "Sidebar").IsEnabled && !Get<bool>(window, "_ratingsAccountBusy"),
                "The completed outer transition left the account/rating barrier enabled.");
            Assert(!Get<bool>(window, "_loading") && !Get<bool>(window, "_hasMore") &&
                   Get<FeedNavigationMetadata?>(window, "_feedNavigationMetadata") is null && Get<bool>(window, "_hasFeed"),
                "A stopped feed retained its loading/cursor state, preventing a fresh refresh after a failed channel reload.");
        }
        finally { heldHistory.TrySetResult(); history.Clear(); await DisposeFixture(window, root); }
    }

    private static async Task PendingNavigationCannotResumeAcrossAccountTransition(string workspace)
    {
        var root = PrepareFeedFixture(workspace);
        var window = new MainWindow(new RuntimePaths(root), ["--demo"]) { ShowActivated = false };
        var gate = Get<SemaphoreSlim>(window, "_videoStartGate");
        var heldHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = Get<HashSet<Task>>(window, "_historyTasks");
        var gateHeld = false;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Set(window, "_demo", false);
            ImportSyntheticSession(window, root);
            await gate.WaitAsync(); gateHeld = true;
            var channel = new Button { Tag = new VideoItem { Channel = "Old owner", ChannelUrl = "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv/videos" } };
            Invoke(window, "Channel_Click", channel, new RoutedEventArgs());
            history.Add(heldHistory.Task);
            Get<CheckBox>(window, "IncognitoCheck").IsChecked = true;
            gate.Release(); gateHeld = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(!File.Exists(Path.Combine(root, "feed-request.json")),
                "Navigation waiting for the old player's stop resumed across an account transition.");
            heldHistory.SetResult();
            await Until(() => Get<AppSettings>(window, "_settings").Incognito && Get<FrameworkElement>(window, "Sidebar").IsEnabled);
            Assert(Get<List<VideoItem>>(window, "_items").Count == 0, "Stale navigation populated the new account screen.");
        }
        finally { if(gateHeld)gate.Release(); heldHistory.TrySetResult(); history.Clear(); await DisposeFixture(window, root); }
    }

    private static async Task ShutdownSaveFailureStillCleansOwnedResources(string workspace)
    {
        var root = PrepareFeedFixture(workspace);
        var paths = new RuntimePaths(root);
        var window = new MainWindow(paths, ["--demo"]) { ShowActivated = false };
        var settingsPath = Path.Combine(paths.Data, "settings.json");
        var unhandled = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, e) => { unhandled.Add(e.Exception); e.Handled = true; };
        CancellationTokenRegistration throwingCallback = default;
        System.Diagnostics.Process? observedProcess = null;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Set(window, "_demo", false);
            ImportSyntheticSession(window, root);
            await InvokeTask(window, "OpenFeedAsync", "home", "Synthetic home");
            var feed = Get<FeedService>(window, "_feed");
            var process = Get<FkYouTube.ProcessSupport.OwnedProcess>(feed, "_process");
            observedProcess = System.Diagnostics.Process.GetProcessById(process.Id);
            var cookiePath = Get<CookieLease>(feed, "_lease").Path!;
            Assert(File.Exists(cookiePath), "Fixture did not hold a synthetic plaintext cookie lease.");
            var playbackLease = Get<SessionService>(window, "_session").Acquire();
            var playbackCookiePath = playbackLease.Path!;
            Set(window, "_playCookie", playbackLease);
            Set(window, "_current", new VideoItem { Id = "fixture0001", Url = "https://www.youtube.com/watch?v=fixture0001", Title = "Synthetic active playback" });
            Get<DataStore>(window, "_store").SaveSettings(Get<AppSettings>(window, "_settings"));
            File.SetAttributes(settingsPath, FileAttributes.ReadOnly);
            Set(window, "_downloadTask", Task.FromException(new IOException("Synthetic download drain failure")));
            Get<HashSet<Task>>(window, "_historyTasks").Add(Task.FromException(new IOException("Synthetic account drain failure")));
            var accountCancellation = Get<CancellationTokenSource>(window, "_accountCts");
            throwingCallback = accountCancellation.Token.Register(() => throw new IOException("Synthetic cancellation callback failure"));
            window.Dispatcher.UnhandledException += capture;
            window.Close();
            await Until(() => Get<bool>(window, "_allowClose") || unhandled.Count > 0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(Get<bool>(window, "_allowClose") && !window.IsVisible && unhandled.Count == 0,
                "Settings/cancellation/drain failure prevented final close or escaped the shutdown handler.");
            Assert(!Get<DispatcherTimer>(window, "_saveTimer").IsEnabled && accountCancellation.IsCancellationRequested,
                "Shutdown did not stop the save timer and cancel the account after a save failure.");
            Assert(observedProcess.HasExited && !File.Exists(cookiePath) && Get<object?>(feed, "_process") is null,
                "An earlier shutdown failure skipped the feed worker or synthetic cookie lease cleanup.");
            Assert(Get<object?>(window, "_playCookie") is null && !File.Exists(playbackCookiePath) && Get<VideoItem?>(window, "_current") is null,
                "Shutdown did not finish playback state cleanup after an earlier failure.");
            var failures = Get<List<string>>(window, "_shutdownErrors");
            Assert(failures.Any(value => value.StartsWith("settings save: ", StringComparison.Ordinal)) &&
                   failures.Any(value => value.StartsWith("account cancellation: ", StringComparison.Ordinal)) &&
                   failures.Any(value => value.StartsWith("download drain: ", StringComparison.Ordinal)) &&
                   failures.Any(value => value.StartsWith("account drain: ", StringComparison.Ordinal)),
                "Shutdown did not collect the fixture's independent save, callback and drain failures.");
        }
        finally
        {
            window.Dispatcher.UnhandledException -= capture; throwingCallback.Dispose(); observedProcess?.Dispose();
            if(File.Exists(settingsPath))File.SetAttributes(settingsPath, FileAttributes.Normal);
            Get<HashSet<Task>>(window, "_historyTasks").Clear();
            await DisposeFixture(window, root);
        }
    }

    private static async Task PartialRemoteHistoryKeepsConfirmedStatus(string workspace)
    {
        var root=PrepareFeedFixture(workspace);var window=new MainWindow(new RuntimePaths(root),["--demo"]);
        try
        {
            Get<DispatcherTimer>(window,"_saveTimer").Stop();Set(window,"_demo",false);ImportSyntheticSession(window,root);
            var settings=Get<AppSettings>(window,"_settings");settings.Incognito=false;settings.AccountKey="synthetic-history-account";
            var video=new VideoItem{Id="fixture0001",Title="Previously sent",Url="https://www.youtube.com/watch?v=fixture0001"};
            var store=Get<DataStore>(window,"_store");store.SavePosition(settings.AccountKey,video,20,120);store.SetSyncStatus(settings.AccountKey,video.Id,"sent");
            File.WriteAllText(Path.Combine(root,"worker","feed_worker.py"),"""
import json,sys
request=json.loads(sys.stdin.readline())
base={'requestId':request['requestId'],'generation':request['generation']}
print(json.dumps(dict(base,type='item',item={'id':'fixture0001','title':'Confirmed before failure','url':'https://www.youtube.com/watch?v=fixture0001','kind':'video'})),flush=True)
print(json.dumps(dict(base,type='error',message='Synthetic partial history failure')),flush=True)
""");
            await InvokeTask(window,"OpenFeedAsync","history","Synthetic remote history");
            Assert(Get<List<VideoItem>>(window,"_items").Count==1,"The failed history batch discarded an already received item.");
            Assert(store.GetHistory(settings.AccountKey).Single().SyncStatus=="confirmed","A received remote-history item lost its confirmed status when the remaining batch failed.");
            Assert(Get<TextBlock>(window,"FeedState").Text.Contains("불러오지 못"),"Partial history persistence hid the worker failure.");
        }
        finally{await DisposeFixture(window,root);}
    }

    private static async Task PlaybackSaveFailureKeepsPlaybackAndRetries(string workspace)
    {
        var root=Path.GetFullPath(Path.Combine(workspace,"artifacts","async-save-"+Guid.NewGuid().ToString("N")));
        var paths=new RuntimePaths(root);var window=new MainWindow(paths,["--demo"]);
        try
        {
            Get<DispatcherTimer>(window,"_saveTimer").Stop();
            var settings=Get<AppSettings>(window,"_settings");settings.Incognito=false;settings.AccountKey="synthetic-save-account";
            var video=new VideoItem{Id="fixture0001",Url="https://www.youtube.com/watch?v=fixture0001",Title="Synthetic position"};
            Set(window,"_current",video);Set(window,"_hasLoadedCurrent",true);Set(window,"_lastValidPosition",42d);Set(window,"_lastValidDuration",120d);
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(paths.Data,"viewer.sqlite")}.ToString()))
            {db.Open();using var command=db.CreateCommand();command.CommandText="DROP TABLE history";command.ExecuteNonQuery();}
            try{Invoke(window,"SavePosition");}
            catch(TargetInvocationException error)when(error.InnerException is SqliteException sql)
            {throw new InvalidOperationException($"Active playback save exposed SQLite error {sql.SqliteErrorCode} instead of retaining the position for retry.");}
            Assert(ReferenceEquals(Get<VideoItem>(window,"_current"),video) && Get<bool>(window,"_hasLoadedCurrent"),"A local save failure disturbed active playback state.");
            Assert(Get<double>(window,"_lastSavedPosition")!=42d,"A failed save was marked as committed and cannot retry.");
            _=new DataStore(paths);Invoke(window,"SavePosition");
            Assert(Get<DataStore>(window,"_store").GetHistory(settings.AccountKey).Single().Position==42 && Get<double>(window,"_lastSavedPosition")==42d,
                "A later successful save did not persist the last valid position after storage recovered.");
        }
        finally{await DisposeFixture(window,root);}
    }

    private static string PrepareFeedFixture(string workspace)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "artifacts", "async-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, "runtime", "python")); Directory.CreateDirectory(Path.Combine(root, "worker"));
        foreach(var file in Directory.EnumerateFiles(Path.Combine(workspace, "runtime", "python")))
            File.Copy(file, Path.Combine(root, "runtime", "python", Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(root, "worker", "feed_worker.py"), """
import json,sys,pathlib
for line in sys.stdin:
    request=json.loads(line)
    pathlib.Path('feed-request.json').write_text(json.dumps({'hasCookiePath':bool(request.get('cookiePath')),'source':request.get('source')}))
    base={'requestId':request['requestId'],'generation':request['generation']}
    print(json.dumps(dict(base,type='item',item={'id':'fixture0001','title':'Synthetic new feed','url':'https://www.youtube.com/watch?v=fixture0001','kind':'video'})),flush=True)
    print(json.dumps(dict(base,type='done',hasMore=False)),flush=True)
""");
        return root;
    }
    private static void ImportSyntheticSession(MainWindow window, string root)
    {
        var cookie = Path.Combine(root, "synthetic-cookie.txt");
        File.WriteAllText(cookie, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tSYNTHETIC_NOT_A_TOKEN\n");
        Get<SessionService>(window, "_session").ImportFile(cookie);
    }
    private static async Task DisposeFixture(MainWindow window, string root)
    {
        Get<DispatcherTimer>(window, "_saveTimer").Stop(); Set(window, "_closing", true); Set(window, "_allowClose", true);
        await Get<FeedService>(window, "_feed").DisposeAsync(); await Get<FkYouTube.Playback.MpvPlayerService>(window, "_player").DisposeAsync();
        Get<CookieLease?>(window, "_playCookie")?.Dispose();
        Get<CancellationTokenSource>(window, "_accountCts").Dispose(); Get<CancellationTokenSource>(window, "_historyCts").Dispose();
        ThumbnailImage.Cache?.Dispose(); ThumbnailImage.Cache = null; window.Close(); SqliteConnection.ClearAllPools();
        if(Directory.Exists(root))Directory.Delete(root, recursive: true);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!condition())await Task.Delay(10, deadline.Token);
    }
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task InvokeTask(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
