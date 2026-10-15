using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text;
using System.IO.Pipes;
using System.Diagnostics;
using FkYouTube;
using FkYouTube.Playback;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;

internal static class PlayerRootRegression
{
    public static Task RunAsync(string workspace)
    {
        var fixtureRoot = Path.Combine(Path.GetFullPath(workspace), "artifacts", "player-root-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var canonical = new RuntimePaths(fixtureRoot);
            var slash = new RuntimePaths(fixtureRoot + Path.DirectorySeparatorChar);
            var dot = new RuntimePaths(Path.Combine(fixtureRoot, "."));
            Assert(canonical.Root == slash.Root && canonical.Root == dot.Root, "Equivalent roots were not normalized.");
            Assert(canonical.Data == slash.Data && canonical.Data == dot.Data, "Equivalent roots selected different data directories.");
            Assert(canonical.InstanceMutexName == slash.InstanceMutexName && canonical.InstanceMutexName == dot.InstanceMutexName,
                "Equivalent roots selected different instance mutexes.");
            using var first = new Mutex(false, canonical.InstanceMutexName, out var created);
            using var second = new Mutex(false, slash.InstanceMutexName, out var createdAgain);
            Assert(created && !createdAgain, "A trailing separator bypassed the fake-folder instance guard.");
            var volumeRoot = Path.GetPathRoot(fixtureRoot)!;
            Assert(RuntimePaths.NormalizeRoot(volumeRoot) == volumeRoot, "Normalization stripped a volume root separator.");

            var choose = typeof(MpvPlayerService).GetMethod("FindPlaylistHistoryRemoval", BindingFlags.NonPublic | BindingFlags.Static)!;
            using var playlist = JsonDocument.Parse("[{\"id\":5},{\"id\":17},{\"id\":23,\"current\":true},{\"id\":41}]");
            var selected = ((long Entry, int Index)?)choose.Invoke(null, [playlist.RootElement, 23L, 41L, 23L]);
            Assert(selected == (5L, 0), "History cleanup failed to select an old entry by ID.");
            using var shifted = JsonDocument.Parse("[{\"id\":17},{\"id\":23,\"current\":true},{\"id\":41}]");
            selected = ((long Entry, int Index)?)choose.Invoke(null, [shifted.RootElement, 23L, 41L, 23L]);
            Assert(selected == (17L, 0), "History cleanup reused an index after the playlist shifted.");
            using var nativeAdvance = JsonDocument.Parse("[{\"id\":23},{\"id\":41,\"current\":true}]");
            Assert(choose.Invoke(null, [nativeAdvance.RootElement, 23L, 41L, 23L]) is null,
                "History cleanup selected the current or native-advanced prepared entry.");
            using var pendingEvent = JsonDocument.Parse("[{\"id\":17,\"playing\":true},{\"id\":23},{\"id\":41}]");
            Assert(choose.Invoke(null, [pendingEvent.RootElement, 23L, 41L, 17L]) is null,
                "History cleanup selected a native entry whose start event had not caught up.");
            using var unknownFuture = JsonDocument.Parse("[{\"id\":23,\"current\":true},{\"id\":57}]");
            Assert(choose.Invoke(null, [unknownFuture.RootElement, 23L, null, 23L]) is null,
                "History cleanup selected an untracked future entry that could become current.");

            var confirmVod = typeof(MpvPlayerService).GetMethod("IsConfirmedVodResult", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var sample in new[]
            {
                ("{\"is_live\":false,\"live_status\":\"not_live\"}", true),
                ("{\"is_live\":false,\"live_status\":\"was_live\"}", true),
                ("{\"is_live\":false}", true),
                ("{\"is_live\":true,\"live_status\":\"is_live\"}", false),
                ("{\"is_live\":false,\"live_status\":\"is_upcoming\"}", false),
                ("{\"is_live\":false,\"live_status\":\"post_live\"}", false),
                ("{}", false), ("not-json", false)
            })
            {
                using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new { status = 0, stdout = sample.Item1 }));
                Assert((bool)confirmVod.Invoke(null, [metadata.RootElement])! == sample.Item2,
                    "Cookie release accepted ambiguous/live metadata or rejected a confirmed VOD.");
            }
            using var failedExtractor = JsonDocument.Parse("{\"status\":1,\"stdout\":\"{\\\"is_live\\\":false}\"}");
            Assert(!(bool)confirmVod.Invoke(null, [failedExtractor.RootElement])!, "Cookie release accepted failed extraction metadata.");
            var directSource = typeof(MpvPlayerService).GetMethod("DirectMediaSource", BindingFlags.NonPublic | BindingFlags.Static)!;
            const string signedUrl = "https://media.fixture.invalid/가짜?signature=one,two;three";
            var edl = (string)directSource.Invoke(null, [signedUrl])!;
            Assert(edl == "edl://%" + System.Text.Encoding.UTF8.GetByteCount(signedUrl) + "%" + signedUrl,
                "Single direct stream did not bypass the hook with UTF-8-safe EDL framing.");
        }
        finally
        {
            if (Directory.Exists(Path.Combine(fixtureRoot, "data"))) Directory.Delete(Path.Combine(fixtureRoot, "data"));
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot);
        }
        return Task.CompletedTask;
    }

    public static async Task RunHttpChunkOptionsAsync(string workspace)
    {
        const int chunkBytes=5242880;
        const string youtube="https://www.youtube.com/watch?v=fixture0002";
        using var metadata=JsonDocument.Parse("""
            {"requested_formats":[
              {"url":"https://media.fixture.invalid/video?sig=one,two;three","protocol":"https","http_headers":{"User-Agent":"Chunk fixture","Referer":"https://www.youtube.com/","X-Untrusted":"ignored"},"downloader_options":{"http_chunk_size":10485760}},
              {"url":"https://media.fixture.invalid/audio","protocol":"https","http_headers":{"User-Agent":"Chunk fixture","Referer":"https://www.youtube.com/","X-Untrusted":"ignored"},"downloader_options":{"http_chunk_size":5242880}}]}
            """);
        var prepared=YouTubeService.ParsePreparedVideo(metadata.RootElement);
        var account=new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"};
        var failures=new List<string>();
        foreach(var mode in new[]{"prepared","selected-resolver","invalid-prevalidation"})
        {
            try
            {
                await using var fixture=await SubtitleFixture.CreateAsync(workspace);
                if(mode=="invalid-prevalidation")
                {
                    var property=typeof(PreparedVideo).GetProperty("HttpChunkSize");
                    var parameter=typeof(MpvPlayerService).GetMethod("PrepareNextAsync")!.GetParameters().SingleOrDefault(item=>item.Name=="httpChunkSize");
                    Assert(property?.PropertyType==typeof(int?) && parameter?.ParameterType==typeof(int?),
                        "The typed HTTP chunk option was not exposed for validated direct and prepared callers.");
                    foreach(var invalid in new[]{-1,0,67108865,int.MaxValue})
                    {
                        var unsafePrepared=new PreparedVideo(prepared.MediaUrl,prepared.Headers,[],[]);property!.SetValue(unsafePrepared,invalid);
                        var before=fixture.Commands;
                        await ExpectAsync<ArgumentOutOfRangeException>(()=>fixture.Player.LoadAsync(youtube,null,720,accountContext:account,resolvedStream:unsafePrepared),
                            "Invalid resolved HTTP chunk bytes were accepted.");
                        await ExpectAsync<ArgumentOutOfRangeException>(()=>PrepareChunkAsync(fixture.Player,youtube,prepared,invalid),
                            "Invalid prepared HTTP chunk bytes were accepted.");
                        Assert(fixture.Commands==before && fixture.Player.Snapshot.MediaGeneration==7 && fixture.Player.Snapshot.Loaded && fixture.NativePlaying,
                            "Invalid HTTP chunk bytes changed the previous playback generation or reached IPC.");
                    }
                    continue;
                }
                if(mode=="prepared")
                    Assert(await PrepareChunkAsync(fixture.Player,youtube,prepared,chunkBytes),"Valid chunked media could not be prepared.");
                else
                    await fixture.Player.LoadAsync(youtube,null,720,accountContext:account,resolveStream:_=>Task.FromResult(prepared));
                AssertChunkOptions(fixture.LastLoadOptions,true);
                Assert(fixture.LastLoadOptions is{ } withHeaders && withHeaders.GetProperty("user-agent").GetString()=="Chunk fixture" &&
                    withHeaders.GetProperty("http-header-fields").GetString()!.Contains("Referer: https://www.youtube.com/",StringComparison.Ordinal) &&
                    !withHeaders.GetProperty("http-header-fields").GetString()!.Contains("X-Untrusted",StringComparison.Ordinal) &&
                    !fixture.RecordedCommands.Any(command=>command[0].GetString()=="set_property" && command[1].GetString() is "curl-max-request-size" or "stream-lavf-o"),
                    "HTTP chunk propagation altered header filtering or changed global player options.");
                var withoutChunk=new PreparedVideo(prepared.MediaUrl,prepared.Headers,[],[]);
                if(mode=="prepared")
                {
                    await fixture.Player.ClearPreparedNextAsync();
                    Assert(await PrepareChunkAsync(fixture.Player,youtube,withoutChunk,null),"Chunk-free media could not be prepared after clearing the previous entry.");
                }
                else
                    await fixture.Player.LoadAsync(youtube,null,720,accountContext:account,resolveStream:_=>Task.FromResult(withoutChunk));
                AssertChunkOptions(fixture.LastLoadOptions,false);
            }
            catch(Exception error){failures.Add(mode+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));

        static async Task<bool> PrepareChunkAsync(MpvPlayerService player,string targetUrl,PreparedVideo media,int? chunk)
        {
            // Keep the pre-fix RED build runnable; the optional argument is verified
            // separately above and is supplied once the production signature exists.
            var method=typeof(MpvPlayerService).GetMethod("PrepareNextAsync")!;
            var arguments=new List<object?>{targetUrl,media.MediaUrl,7L,720,media.Headers,CancellationToken.None};
            if(method.GetParameters().Length==7)arguments.Add(chunk);
            return await (Task<bool>)method.Invoke(player,arguments.ToArray())!;
        }
        static void AssertChunkOptions(JsonElement? options,bool expected)
        {
            Assert(options is{ },"The fake native load did not record per-file options.");
            var actual=options!.Value;
            if(expected)
                Assert(actual.TryGetProperty("curl-max-request-size",out var curl) && curl.GetString()=="5242880" &&
                    actual.TryGetProperty("stream-lavf-o",out var lavf) && lavf.GetString()=="request_size=5242880",
                    "Direct EDL media lost the selected HTTP chunk bytes for curl or libavformat.");
            else
                Assert(!actual.TryGetProperty("curl-max-request-size",out _) && !actual.TryGetProperty("stream-lavf-o",out _),
                    "A later chunk-free load inherited a previous entry's HTTP request limit.");
        }
    }

    public static async Task RunDirectValidationReview6Async(string workspace)
    {
        const string youtube="https://www.youtube.com/watch?v=fixture0002";
        var account=new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"};
        var failures=new List<string>();
        foreach(var source in new string?[]{"http://media.fixture.invalid/video","file:///fixture/video","https://media.fixture.invalid/video\r\nfixture",null})
        {
            try
            {
                await using var fixture=await SubtitleFixture.CreateAsync(workspace);
                var before=fixture.Commands;
                await ExpectAsync<ArgumentException>(()=>fixture.Player.LoadAsync(youtube,null,720,accountContext:account,
                    resolvedStream:new PreparedVideo(source!,new Dictionary<string,string>(),[],[])),
                    "An invalid directly supplied media URL was accepted.");
                Assert(fixture.Commands==before && fixture.Player.Snapshot.MediaGeneration==7 && fixture.Player.Snapshot.Loaded &&
                    fixture.NativePlaying && !fixture.NativeProcessExited,
                    "Direct media URL validation changed existing playback or reached native IPC.");
            }
            catch(Exception error){failures.Add("direct "+(source is null?"null":source.Split(':')[0])+": "+error.Message);}
        }
        foreach(var mode in new[]{"null","url","chunk"})
        {
            try
            {
                await using var fixture=await SubtitleFixture.CreateAsync(workspace);
                PreparedVideo? resolved=mode=="null"?null:new(mode=="url"?"http://media.fixture.invalid/video":"https://media.fixture.invalid/video",
                    new Dictionary<string,string>(),[],[],mode=="chunk"?0:null);
                Exception? failure=null;
                try{await fixture.Player.LoadAsync(youtube,null,720,accountContext:account,resolveStream:_=>Task.FromResult(resolved!));}
                catch(Exception error){failure=error;}
                Assert(failure is not null,"An invalid selected-account resolver result fell through to playback: "+mode);
                Assert(fixture.Player.Snapshot.MediaGeneration==8 && fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded &&
                    fixture.Player.Snapshot.ErrorMessage is not null && fixture.NativeProcessExited,
                    "A late resolver failure escaped the existing owned-load failure cleanup: "+mode);
                Assert(!fixture.RecordedCommands.Any(command=>command[0].GetString()=="loadfile" ||
                    command[0].GetString()=="set_property" && command[1].GetString()=="ytdl-raw-options"),
                    "Invalid selected-account metadata configured the default extractor or reached loadfile: "+mode);
            }
            catch(Exception error){failures.Add("resolver "+mode+": "+error.Message);}
        }
        try
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var reports=0;fixture.Player.PlaybackError+=_=>Interlocked.Increment(ref reports);
            await ExpectAsync<InvalidOperationException>(()=>fixture.Player.LoadAsync(youtube,null,720,accountContext:account),
                "A selected account without a resolver was accepted.");
            Assert(reports==0 && fixture.Commands==0 && fixture.Player.Snapshot.MediaGeneration==7 && fixture.Player.Snapshot.Loaded,
                "A pre-ownership failure was reported as an error of the previously playing video's generation.");
        }
        catch(Exception error){failures.Add("pre-ownership error identity: "+error.Message);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    public static async Task RunDisconnectReview6Async(string workspace)
    {
        var failures=new List<string>();
        try
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var reports=0;PlaybackFailure? report=null;
            fixture.Player.PlaybackError+=failure=>{report=failure;Interlocked.Increment(ref reports);};
            await fixture.DisconnectIpcAsync();
            var stopped=fixture.Player.Snapshot;
            Assert(stopped.MediaGeneration==7 && stopped.Ended && !stopped.Loaded && !stopped.Buffering && !stopped.Seeking &&
                stopped.EndReason=="error" && !string.IsNullOrWhiteSpace(stopped.ErrorMessage) && reports==1 &&
                report?.MediaGeneration==7 && report.VideoUrl==stopped.VideoUrl && report.Message==stopped.ErrorMessage,
                "An IPC disconnect without a completed process watcher left playable state or omitted its one scoped error.");
        }
        catch(Exception error){failures.Add("reader disconnect: "+error);}
        try
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var reports=0;fixture.Player.PlaybackError+=_=>Interlocked.Increment(ref reports);
            fixture.DeliverDelayedEvent("shutdown","{}");
            await fixture.WaitForReaderAsync();
            Assert(fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded && fixture.Player.Snapshot.EndReason=="error" && reports==1,
                "An unexpected shutdown canceled the reader before committing its one failure snapshot.");
        }
        catch(Exception error){failures.Add("native shutdown: "+error.Message);}
        try
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            await using var successor=await SubtitleFixture.CreateAsync(workspace);
            var published=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports=0;fixture.Player.PlaybackError+=_=>Interlocked.Increment(ref reports);
            fixture.Player.SnapshotChanged+=snapshot=>
            {
                if(snapshot.MediaGeneration!=7 || !snapshot.Ended)return;
                published.TrySetResult();release.Task.GetAwaiter().GetResult();
            };
            var watching=fixture.StartExitWatcher();
            try
            {
                fixture.KillNativeProcess();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                successor.SetMedia(99,true);fixture.ReplaceSession(successor);fixture.SetMedia(99,true);
                release.TrySetResult();await watching.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(fixture.Player.Snapshot.MediaGeneration==99 && fixture.Player.Snapshot.Loaded && !fixture.Player.Snapshot.Ended &&
                    fixture.Player.Snapshot.ErrorMessage is null && reports==0,
                    "An older process watcher published an unscoped error after a new playback owner was installed.");
            }
            finally{release.TrySetResult();fixture.RestoreSession();}
        }
        catch(Exception error){failures.Add("stale watcher: "+error.Message);}
        try
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            await using var successor=await SubtitleFixture.CreateAsync(workspace);
            successor.SetMedia(99,true);fixture.ReplaceSession(successor);fixture.SetMedia(99,true);
            try
            {
                // Simulate packets which passed the reader's old-owner check immediately
                // before replacement, then reached their mutation after replacement.
                fixture.DeliverDelayedEvent("property-change","{\"name\":\"pause\",\"data\":true}");
                fixture.DeliverDelayedEvent("end-file","{\"playlist_entry_id\":42,\"reason\":\"error\"}");
                fixture.DeliverDelayedEvent("start-file","{\"playlist_entry_id\":999}");
                fixture.DeliverDelayedEvent("file-loaded","{}");
                var current=fixture.Player.Snapshot;
                Assert(current.MediaGeneration==99 && current.Loaded && !current.Ended && !current.Paused &&
                    current.ErrorMessage is null && fixture.ActiveEntry==42,
                    "A delayed packet from an old native owner changed successor media state.");
            }
            finally{fixture.RestoreSession();}
        }
        catch(Exception error){failures.Add("stale native event: "+error.Message);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    public static async Task RunSubtitlesAsync(string workspace)
    {
        try{await RunSubtitleContractsAsync(workspace);await RunExternalSubtitleContractsAsync(workspace);await RunExternalSubtitleRejectionAsync(workspace);await RunPreparedSubtitleUrlBoundaryAsync(workspace);await RunHighestQualityAsync(workspace);await RunPreparedSubtitleCompletionAsync(workspace);await RunLoadFailureContractsAsync(workspace);await RunCatalogLabelSelectionAsync(workspace);}
        catch(Exception ex){Console.WriteLine("SUBTITLE TRACE "+ex);throw;}
    }

    public static async Task RunOwnershipReviewAsync(string workspace)
    {
        await RunReview16R2Async(workspace);
        Assert(!typeof(IOException).IsAssignableFrom(typeof(InvalidDataException)),
            "The external-caption regression assumes InvalidDataException is not caught by IOException.");
        var failures=new List<string>();
        foreach(var mode in new[]{"external-malformed", "append-error", "append-no-reply", "append-readback-error", "append-ambiguous",
            "append-current-same-source", "append-stale", "append-canceled", "append-success-no-id"})
        {
            try
            {
                await using var fixture=await SubtitleFixture.CreateAsync(workspace);
                if(mode=="external-malformed")
                {
                    fixture.BlockSubAdd();
                    var addition=fixture.Player.SelectExternalSubtitleAsync(new("fr","French","https://www.youtube.com/api/timedtext?lang=fr",false),7);
                    await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));fixture.MalformedTracks=true;fixture.ReleaseSubAdd();
                    await ExpectAsync<IOException>(()=>addition,"Malformed post-add metadata escaped the unconfirmed external-caption cleanup.");
                    Assert(fixture.Player.OwnedProcessIds.Count==0 && fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded,
                        "Malformed post-add metadata left an untracked subtitle in a live session.");
                    continue;
                }
                fixture.LoadError=mode is "append-error" or "append-current-same-source" or "append-stale";fixture.SuppressLoadReply=mode=="append-no-reply";
                fixture.OmitAppendEntryId=mode is "append-readback-error" or "append-ambiguous" or "append-success-no-id";
                fixture.FailPlaylistAfterAppend=mode=="append-readback-error";fixture.DuplicateAppend=mode=="append-ambiguous";
                const string media="https://media.fixture.invalid/next";
                if(mode=="append-current-same-source")
                    fixture.SetCurrentSource((string)typeof(MpvPlayerService).GetMethod("DirectMediaSource",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[media])!);
                if(mode is "append-stale" or "append-canceled")fixture.BlockAppend();
                using var cancellation=new CancellationTokenSource();
                Exception? error=null;bool? result=null;
                try
                {
                    var preparing=fixture.Player.PrepareNextAsync("https://www.youtube.com/watch?v=fixture0002",media,7,720,cancellationToken:cancellation.Token);
                    if(mode is "append-stale" or "append-canceled")
                    {
                        await fixture.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        if(mode=="append-stale")fixture.SetMedia(99,true);else cancellation.Cancel();
                        fixture.ReleaseAppend();
                    }
                    result=await preparing;
                }
                catch(Exception exception){error=exception;}
                if(mode=="append-success-no-id")
                {
                    Assert(result==true && fixture.PlaylistIds.Length==2,"A unique ID-less append could not be confirmed by source and new ID.");
                    await fixture.Player.ClearPreparedNextAsync();
                    Assert(fixture.PlaylistIds.SequenceEqual([42L]) && fixture.NativePlaying,"Confirmed ID-less preparation could not be cleared safely.");
                    continue;
                }
                Assert(result!=true,"Unconfirmed append was accepted as a prepared video: "+mode);
                if(mode=="append-stale")
                {
                    Assert(error is not null && fixture.Player.Snapshot.MediaGeneration==99 && fixture.Player.Snapshot.Loaded &&
                        fixture.Player.OwnedProcessIds.Count>0 && !fixture.RecordedCommands.Any(command=>command[0].GetString()=="quit"),
                        "An older append failure closed or changed a newer generation.");
                    continue;
                }
                if(mode is "append-error" or "append-no-reply" or "append-current-same-source" or "append-canceled")
                    Assert(error is not null && fixture.PlaylistIds.SequenceEqual([42L]) && fixture.NativePlaying &&
                        fixture.Player.OwnedProcessIds.Count>0 && fixture.Player.Snapshot.Loaded,
                        "A uniquely identified uncertain append was not removed while preserving current playback: "+mode);
                else
                    Assert(fixture.Player.OwnedProcessIds.Count==0 && fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded &&
                        !string.IsNullOrWhiteSpace(fixture.Player.Snapshot.ErrorMessage),
                        "An unidentifiable uncertain append left an owned native session or an apparently playing snapshot: "+mode);
            }
            catch(Exception error){failures.Add(mode+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    public static async Task RunReview16R2Async(string workspace)
    {
        var failures=new List<string>();
        foreach(var test in new (string Name,Func<string,Task> Run)[]{("D5 prepared cancellation",RunPreparedCancellationReviewAsync),
            ("D8 prepared prevalidation",RunPreparedPrevalidationReviewAsync),("S2 cookie consumer receipt",RunCookieConsumerReceiptReviewAsync),
            ("R2-D1 load initialization failure",RunLoadInitializationFailureReviewAsync),
            ("U-D1 captured playback shutdown",RunCapturedPlaybackShutdownReviewAsync),
            ("U-D1 unconfirmed owner recovery",RunUnconfirmedPlaybackOwnerReviewAsync),
            ("U-D1 retained-cookie bound",RunRetainedCookieBoundReviewAsync)})
        {
            try{await test.Run(workspace);}
            catch(Exception error){failures.Add(test.Name+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    private static async Task RunCapturedPlaybackShutdownReviewAsync(string workspace)
    {
        var capture=typeof(MpvPlayerService).GetMethod("CapturePlaybackOwner");
        var stop=typeof(MpvPlayerService).GetMethod("StopOwnedPlaybackAsync");
        var retain=typeof(MpvPlayerService).GetMethod("RetainPlaybackCookies");
        Assert(capture is not null && stop is not null && retain is not null,
            "Caller preflight cleanup cannot capture and stop only its exact native owner while retaining that owner's cookies.");
        foreach(var mode in new[]{"owned","stale-generation","stale-session"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            await using var replacement=mode=="stale-session"?await SubtitleFixture.CreateAsync(workspace):null;
            var cookie=fixture.CreateSyntheticCookie();using var lease=new CookieLease(cookie);
            var owner=capture!.Invoke(fixture.Player,null)!;
            retain!.Invoke(fixture.Player,[owner,lease]);
            Assert(File.Exists(cookie),"Retaining a cookie deleted it while its native owner was live.");
            if(mode=="stale-generation")fixture.SetMedia(99,true);
            if(replacement is not null)fixture.ReplaceSession(replacement);
            try
            {
                var stopped=await (Task<bool>)stop!.Invoke(fixture.Player,[owner])!;
                if(mode=="owned")
                    Assert(stopped && fixture.Player.OwnedProcessIds.Count==0 && fixture.Player.Snapshot.Ended &&
                        !fixture.Player.Snapshot.Loaded && !File.Exists(cookie),
                        "Exact captured shutdown did not stop native playback and release its retained lease after process proof.");
                else
                    Assert(!stopped && fixture.Player.Snapshot.Loaded && fixture.Player.OwnedProcessIds.Count>0 && File.Exists(cookie) &&
                        !fixture.RecordedCommands.Any(command=>command[0].GetString()=="quit") &&
                        (replacement is null || replacement.Commands==0),
                        "Stale captured shutdown changed a newer generation/session or released the old consumer's cookie.");
            }
            finally
            {
                if(replacement is not null)fixture.RestoreSession();
                await fixture.Player.DisposeAsync();
                Assert(!File.Exists(cookie),"Session disposal did not release a retained lease after owned process termination.");
            }
        }
    }

    public static async Task RunOwnershipR4Async(string workspace)
    {
        await RunCapturedPlaybackShutdownReviewAsync(workspace);
        await RunUnconfirmedPlaybackOwnerReviewAsync(workspace);
        await RunRetainedCookieBoundReviewAsync(workspace);
    }

    private static async Task RunUnconfirmedPlaybackOwnerReviewAsync(string workspace)
    {
        foreach(var mode in new[]{"stop","dispose"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var cookie=fixture.CreateSyntheticCookie();using var lease=new CookieLease(cookie);
            var owner=fixture.Player.CapturePlaybackOwner();fixture.Player.RetainPlaybackCookies(owner,lease);
            using var repair=fixture.InvalidateNativeWaitHandle();
            try
            {
                if(mode=="dispose")
                    await ExpectAsync<IOException>(()=>fixture.Player.DisposeAsync().AsTask(),"Unconfirmed disposal silently reported success.");
                else
                {
                    Assert(!await fixture.Player.StopOwnedPlaybackAsync(owner),"A lost native wait handle granted a termination receipt from an empty job query.");
                    await ExpectAsync<IOException>(()=>fixture.Player.StartAsync((nint)1),"An unresolved detached owner allowed a replacement native session.");
                }
                Assert(fixture.NativeJobHandleRetained && File.Exists(cookie),
                    "Unconfirmed shutdown disposed the verifiable job owner or its possibly-used cookie.");
            }
            finally{fixture.RestoreNativeWaitHandle(repair);}
            if(mode=="dispose")await fixture.Player.DisposeAsync();else await fixture.Player.StopAsync();
            Assert(!File.Exists(cookie) && fixture.NativeOwnerDisposed && fixture.NativeProcessExited,
                "A later Stop/Dispose did not retry the detached native owner and release its cookie after actual process proof.");
        }
    }

    private static async Task RunRetainedCookieBoundReviewAsync(string workspace)
    {
        await using var fixture=await SubtitleFixture.CreateAsync(workspace);
        var owner=fixture.Player.CapturePlaybackOwner();var original=fixture.CreateSyntheticCookie();
        var cookies=new List<string>();
        for(var index=0;index<12;index++)
        {
            var path=original+"."+index;File.Copy(original,path);cookies.Add(path);
            fixture.Player.RetainPlaybackCookies(owner,new CookieLease(path));
        }
        const string video="https://www.youtube.com/watch?v=fixture0002";
        await ExpectAsync<InvalidOperationException>(()=>fixture.Player.LoadAsync(video,null,720,
            accountContext:new YouTubeChannel{Id="fixture",AuthUser=1,DataSyncId="123456||fixture",PageId="123456"}),
            "A missing selected-account resolver did not retain its prevalidation failure at the cookie bound.");
        Assert(fixture.NativePlaying && !fixture.NativeProcessExited && cookies.All(File.Exists),
            "The retention bound closed a valid previous player before selected-account prevalidation completed.");
        // The fake session deliberately has no mpv executable. Once its bounded owner
        // is drained, replacement startup fails locally before any media/network use.
        await ExpectAsync<FileNotFoundException>(()=>fixture.Player.LoadAsync(video,null,720,
            resolvedStream:new("https://media.fixture.invalid/bounded",new Dictionary<string,string>(),[720],[])),
            "A valid load did not drain the twelve retained prior-cookie consumers before replacement startup.");
        Assert(fixture.NativeProcessExited && fixture.NativeOwnerDisposed && cookies.All(path=>!File.Exists(path)),
            "The retained-cookie bound failed to stop its actual native owner and release all twelve leases.");
    }

    private static async Task RunLoadInitializationFailureReviewAsync(string workspace)
    {
        var failures=new List<string>();
        foreach(var mode in new[]{"slang-error","sid-error","visibility-error","slang-no-reply","prepared-error","stale-generation","stale-session","account-prevalidation"})
        {
            try
            {
                await using var fixture=await SubtitleFixture.CreateAsync(workspace);
                await using var replacement=mode=="stale-session"?await SubtitleFixture.CreateAsync(workspace):null;
                var cookie=fixture.CreateSyntheticCookie();using var lease=new CookieLease(cookie);
                const string youtube="https://www.youtube.com/watch?v=fixture0002";
                if(mode=="prepared-error")
                    Assert(await fixture.Player.PrepareNextAsync(youtube,"https://media.fixture.invalid/prepared",7,720),
                        "Initialization fixture did not prepare a native next entry.");
                fixture.SubtitleDefaultError=mode=="sid-error"?"sid":mode=="visibility-error"?"sub-visibility":"slang";
                fixture.SuppressSubtitleDefaultReply=mode=="slang-no-reply"?"slang":null;
                if(mode is "stale-generation" or "stale-session")fixture.BlockSubtitleDefaults();
                var reports=0;fixture.Player.PlaybackError+=_=>Interlocked.Increment(ref reports);
                var offset=fixture.Commands;
                var loading=fixture.Player.LoadAsync(youtube,cookie,720,
                    accountContext:mode=="account-prevalidation"?new YouTubeChannel{Id="fixture-channel",Name="Fixture",DataSyncId="fixture||",AuthUser=1}:null,
                    resolvedStream:mode=="account-prevalidation"?null:new("https://media.fixture.invalid/next",new Dictionary<string,string>(),[720],[]));
                PlaybackSnapshot? retainedSnapshot=mode=="account-prevalidation"?fixture.Player.Snapshot:null;
                if(mode is "stale-generation" or "stale-session")
                {
                    await fixture.SubtitleDefaultStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if(mode=="stale-generation")fixture.SetMedia(99,true);else fixture.ReplaceSession(replacement!);
                    retainedSnapshot=fixture.Player.Snapshot;
                    fixture.ReleaseSubtitleDefaults();
                }
                try
                {
                    if(mode=="slang-no-reply")await ExpectAsync<IOException>(()=>loading,"Missing initial subtitle reply was reported successful.");
                    else await ExpectAsync<InvalidOperationException>(()=>loading,"Initial subtitle failure was reported successful: "+mode);
                    var commands=fixture.RecordedCommands.Skip(offset).ToArray();
                    Assert(!commands.Any(command=>command[0].GetString() is "stop" or "loadfile" or "playlist-play-index"),
                        "Initialization fixture did not fail before native replacement/adoption: "+mode);
                    Assert(File.Exists(cookie),"Initialization failure disposed a caller-owned cookie lease: "+mode);
                    if(mode is "stale-generation" or "stale-session" or "account-prevalidation")
                    {
                        Assert(fixture.Player.Snapshot==retainedSnapshot && !fixture.Player.Snapshot.Ended &&
                            fixture.Player.OwnedProcessIds.Count>0 && fixture.NativePlaying && !commands.Any(command=>command[0].GetString()=="quit") &&
                            !CanReleaseCookies(fixture.Player,8) && !CanReleaseCookies(fixture.Player,99),
                            "Initialization failure changed an unowned generation/session or granted cookie release: "+mode);
                        Assert(reports==0,"An unowned initialization failure reported over the current media: "+mode);
                        if(replacement is not null)Assert(replacement.Player.OwnedProcessIds.Count>0 && replacement.NativePlaying && replacement.Commands==0,
                            "Stale initialization cleanup touched a replacement native session.");
                    }
                    else
                    {
                        var snapshot=fixture.Player.Snapshot;
                        Assert(snapshot.MediaGeneration==8 && snapshot.Ended && !snapshot.Loaded && !snapshot.Buffering && !snapshot.Seeking &&
                            !string.IsNullOrWhiteSpace(snapshot.ErrorMessage) && reports==1 && fixture.Player.OwnedProcessIds.Count==0 && !fixture.NativePlaying,
                            "Initial subtitle failure abandoned previous native playback or a prepared entry: "+mode);
                        Assert(CanReleaseCookies(fixture.Player,8) && !CanReleaseCookies(fixture.Player,7) && !CanReleaseCookies(fixture.Player,99),
                            "Initialization shutdown did not grant cookie release to exactly its failed generation: "+mode);
                        lease.Dispose();Assert(!File.Exists(cookie),"Caller could not release its cookie after initialization shutdown.");
                    }
                }
                finally{if(mode=="stale-session")fixture.RestoreSession();}
            }
            catch(Exception error){failures.Add(mode+": "+error.Message);}
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" | ",failures));
    }

    public static async Task RunPreparedCancellationReviewAsync(string workspace)
    {
        foreach(var mode in new[]{"exact-id","query-error","query-no-reply","malformed-playlist","remove-error","remove-no-reply","remove-unconfirmed","already-removed","native-current","stale-generation","stale-session"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            await using var replacement=mode=="stale-session"?await SubtitleFixture.CreateAsync(workspace):null;
            const string media="https://media.fixture.invalid/same-source";
            fixture.SetCurrentSource((string)typeof(MpvPlayerService).GetMethod("DirectMediaSource",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[media])!);
            Assert(await fixture.Player.PrepareNextAsync("https://www.youtube.com/watch?v=fixture0002",media,7,720),"Cancellation fixture did not prepare a native entry.");
            fixture.PlaylistError=mode=="query-error";fixture.MalformedPlaylist=mode=="malformed-playlist";
            fixture.SuppressPlaylistReply=mode=="query-no-reply";fixture.SuppressRemoveReply=mode=="remove-no-reply";
            fixture.RemoveError=mode=="remove-error";fixture.IgnorePlaylistRemove=mode=="remove-unconfirmed";
            if(mode=="already-removed")fixture.DropPreparedEntry();
            if(mode=="native-current")fixture.AdvancePreparedEntry();
            if(mode is "stale-generation" or "stale-session")fixture.BlockPlaylistRead();
            var clearing=fixture.Player.ClearPreparedNextAsync();
            if(mode is "stale-generation" or "stale-session")
            {
                await fixture.PlaylistReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if(mode=="stale-generation")fixture.SetMedia(99,true);
                else fixture.ReplaceSession(replacement!);
                fixture.ReleasePlaylistRead();
                try{await clearing;}
                finally{if(mode=="stale-session")fixture.RestoreSession();}
                Assert(fixture.PlaylistIds.SequenceEqual([42L,43L]) && fixture.NativePlaying && fixture.Player.OwnedProcessIds.Count>0 &&
                    !fixture.RecordedCommands.Any(command=>command[0].GetString() is "quit" or "playlist-remove"),"Stale prepared cancellation changed another generation/session: "+mode);
                if(replacement is not null)Assert(replacement.Player.OwnedProcessIds.Count>0 && replacement.NativePlaying && replacement.Commands==0,
                    "Stale prepared cancellation touched the replacement native session.");
                continue;
            }
            await clearing;
            if(mode is "exact-id" or "already-removed")
                Assert(fixture.PlaylistIds.SequenceEqual([42L]) && fixture.NativePlaying && fixture.Player.OwnedProcessIds.Count>0,
                    "Prepared cancellation removed a duplicate-source current video or failed exact cleanup: "+mode);
            else
                Assert(fixture.Player.OwnedProcessIds.Count==0 && !fixture.NativePlaying && fixture.Player.Snapshot.Ended &&
                    !fixture.Player.Snapshot.Loaded && !string.IsNullOrWhiteSpace(fixture.Player.Snapshot.ErrorMessage),
                    "Unconfirmed prepared cancellation abandoned its live native owner: "+mode);
            await fixture.Player.ClearPreparedNextAsync();
        }
    }

    public static async Task RunPreparedPrevalidationReviewAsync(string workspace)
    {
        foreach(var mode in new[]{"tracks","sid","visibility"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            fixture.MalformedTracks=mode=="tracks";
            using var bad=JsonDocument.Parse("{}");
            if(mode=="sid")fixture.SidReadback=bad.RootElement.Clone();
            if(mode=="visibility")fixture.VisibilityReadback=bad.RootElement.Clone();
            var offset=fixture.Commands;
            await ExpectAsync<InvalidDataException>(()=>fixture.Player.AddPreparedSubtitleAsync(new("fr","https://www.youtube.com/api/timedtext?fixture=prevalidation.vtt"),7),
                "Malformed prepared-caption prevalidation was not observable: "+mode);
            Assert(fixture.MutationsSince(offset)==0 && fixture.NativePlaying && fixture.Player.OwnedProcessIds.Count>0 && fixture.Player.Snapshot.Loaded &&
                !fixture.RecordedCommands.Any(command=>command[0].GetString()=="quit"),"Prepared-caption prevalidation closed or changed a valid native session: "+mode);
        }
    }

    public static async Task RunCookieConsumerReceiptReviewAsync(string workspace)
    {
        foreach(var stale in new[]{false,true})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var cookie=fixture.CreateSyntheticCookie();using var lease=new CookieLease(cookie);
            fixture.LoadError=!stale;fixture.PlaylistError=stale;
            if(stale)fixture.BlockPlaylistRead();
            var loading=fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",cookie,720,
                resolvedStream:new("https://media.fixture.invalid/failed",new Dictionary<string,string>(),[720],[]));
            if(stale)
            {await fixture.PlaylistReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));fixture.SetMedia(99,true);fixture.ReleasePlaylistRead();}
            await ExpectAsync<InvalidOperationException>(()=>loading,"Synthetic cookie consumer failure was not rejected.");
            Assert(File.Exists(cookie),"Player disposed a cookie lease owned by its caller.");
            if(stale)
                Assert(!CanReleaseCookies(fixture.Player,8) && !CanReleaseCookies(fixture.Player,99) && fixture.Player.OwnedProcessIds.Count>0,
                    "A stale failure granted cookie release while a newer native consumer remained alive.");
            else
            {
                Assert(CanReleaseCookies(fixture.Player,8) && !CanReleaseCookies(fixture.Player,7) && !CanReleaseCookies(fixture.Player,99),
                    "Confirmed native shutdown did not grant cookie release to exactly its failed generation.");
                lease.Dispose();Assert(!File.Exists(cookie),"Caller could not release its synthetic cookie after confirmed native shutdown.");
            }
        }
    }

    private static bool CanReleaseCookies(MpvPlayerService player,long generation)
    {
        var receipt=typeof(MpvPlayerService).GetMethod("CanReleasePlaybackCookies",BindingFlags.Public|BindingFlags.Instance);
        Assert(receipt is not null,"Player provides no exact-generation consumer shutdown receipt for its caller's cookie lease.");
        return (bool)receipt!.Invoke(player,[generation])!;
    }

    private static async Task RunCatalogLabelSelectionAsync(string workspace)
    {
        await using var fixture=await SubtitleFixture.CreateAsync(workspace);
        foreach(var length in new[]{140,141,160,256})
        {
            var sourceUrl="https://www.youtube.com/api/timedtext?v=fixture0001&length="+length;
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{id="fixture0001",subtitles=new Dictionary<string,object>{["zh-Hans"]=new[]{new{ext="vtt",name=new string('x',length),url=sourceUrl}}}}));
            var caption=YouTubeService.ParseSubtitleCatalog(doc.RootElement,"fixture0001").Single();
            Assert(caption.Label.Length<=160 && caption.Label.EndsWith(" (zh-Hans) · 수동 자막",StringComparison.Ordinal) && caption.Url==sourceUrl && caption.Language=="zh-Hans",
                "A valid catalog name exceeded the final selection label contract or lost its identity/suffix.");
            await fixture.Player.SelectExternalSubtitleAsync(caption,7);
            Assert(fixture.Player.PreferredSubtitleLanguage=="zh-Hans","A bounded catalog label could not be selected.");
        }
        await ExpectAsync<InvalidDataException>(()=>fixture.Player.SelectExternalSubtitleAsync(new("en",new string('x',161),"https://www.youtube.com/api/timedtext?v=fixture0001",false),7),
            "An externally supplied oversized label bypassed the selection contract.");
    }

    private static async Task RunPreparedSubtitleCompletionAsync(string workspace)
    {
        foreach(var mode in new[]{"rejected","rejected-off","rejected-empty","missing-track","missing-track-off"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            await fixture.Player.SelectSubtitleAsync(2,7);
            if(mode.EndsWith("-off",StringComparison.Ordinal))await fixture.Player.SelectSubtitleAsync(null,7);
            if(mode=="rejected-empty")fixture.ClearNativeSubtitles();
            var before=await fixture.Player.GetSubtitlesAsync();
            var beforeSid=fixture.Sid;var beforeVisible=fixture.Visible;
            var beforeOwners=fixture.Player.OwnedProcessIds.ToHashSet();
            var beforeIdentity=fixture.NativeIdentity;
            var beforeProcesses=fixture.DescribeOwnedProcesses(beforeOwners);
            Assert(beforeOwners.Contains(beforeIdentity.Pid) && fixture.RetainsNativeIdentity(beforeIdentity),
                "The prepared-caption fixture had no live exact native owner before rejection.");
            fixture.RejectSubAdd=mode.StartsWith("rejected",StringComparison.Ordinal);fixture.SkipSubAdd=!fixture.RejectSubAdd;
            Exception? failure=null;
            try{await fixture.Player.AddPreparedSubtitleAsync(new("fr","https://www.youtube.com/api/timedtext?fixture=rejected.vtt"),7);}
            catch(Exception error){failure=error;}
            var snapshot=fixture.Player.Snapshot;
            var failureTypes=new List<string>();for(var error=failure;error is not null;error=error.InnerException)failureTypes.Add(error.GetType().Name);
            var stops=fixture.RecordedCommands.Count(command=>command[0].GetString() is "stop" or "quit");
            var afterOwners=fixture.Player.OwnedProcessIds.ToHashSet();var sameNativeOwner=fixture.RetainsNativeIdentity(beforeIdentity);
            Console.WriteLine($"PREPARED TERMINAL mode={mode} failure={string.Join('/',failureTypes)} generation={snapshot.MediaGeneration} loaded={snapshot.Loaded} ended={snapshot.Ended} native={fixture.NativePlaying} sameNativeOwner={sameNativeOwner} beforeOwners={beforeProcesses} afterOwners={fixture.DescribeOwnedProcesses(afterOwners)} stop={stops}");
            Assert(snapshot.MediaGeneration==7 && snapshot.Loaded && !snapshot.Ended && fixture.NativePlaying && sameNativeOwner &&
                fixture.Player.HasActiveMedia(7) && afterOwners.Contains(beforeIdentity.Pid) &&
                !fixture.RecordedCommands.Any(command=>command[0].GetString() is "stop" or "quit"),
                "A terminal rejected optional prepared caption stopped valid current-generation playback: "+mode);
            Assert(failure is InvalidOperationException,"A definite prepared-caption rejection did not report an actionable optional-subtitle failure.");
            Assert((failure?.GetType().Name=="SubtitleCommandRejectedException")==fixture.RejectSubAdd,
                "Prepared retry classification conflated a native rejection with a successful no-track response.");
            var after=await fixture.Player.GetSubtitlesAsync();
            Assert(before.Off==after.Off && before.Tracks.SequenceEqual(after.Tracks) && fixture.Player.PreferredSubtitleLanguage=="en" &&
                Equals(beforeSid,fixture.Sid) && beforeVisible==fixture.Visible,
                "A rejected prepared caption changed existing tracks, selection, or retained language.");
        }
        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            fixture.RejectSubAdd=true;fixture.BlockSubAdd();
            var addition=fixture.Player.AddPreparedSubtitleAsync(new("fr","https://www.youtube.com/api/timedtext?fixture=rejected-delayed-readback.vtt"),7);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));fixture.BlockTrackRead();fixture.ReleaseSubAdd();
            await fixture.TrackReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var next=fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/following",new Dictionary<string,string>(),[720],[]));
            try
            {
                await Task.Delay(50);
                Assert(!addition.IsCompleted && !next.IsCompleted,"A rejected prepared caption released the load gate before validating its terminal no-op.");
            }
            finally{fixture.ReleaseTrackRead();await ExpectAsync<InvalidOperationException>(()=>addition,"A terminal rejected optional caption became success.");await next;}
            Assert(fixture.Player.Snapshot.Loaded && !fixture.Player.Snapshot.Ended && fixture.NativePlaying,
                "A validated rejected optional caption prevented the following video from loading.");
        }
        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            await fixture.Player.SelectSubtitleAsync(2,7);
            fixture.BlockSubAdd();using var cancellation=new CancellationTokenSource();
            var addition=fixture.Player.AddPreparedSubtitleAsync(new("fr","https://www.youtube.com/api/timedtext?fixture=canceled.vtt"),7,cancellation.Token);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));cancellation.Cancel();
            var next=fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/following",new Dictionary<string,string>(),[720],[]));
            try
            {
                await Task.Delay(50);
                Assert(!addition.IsCompleted && !next.IsCompleted && fixture.SubAdds==1,"Canceled prepared sub-add released the load gate before native completion.");
            }
            finally{fixture.ReleaseSubAdd();await ExpectAsync<OperationCanceledException>(()=>addition,"Canceled prepared addition was reported successful.");await next;}
            var tracks=await fixture.Player.GetSubtitlesAsync();
            Assert(tracks.Tracks.All(track=>track.Language!="fr") && fixture.Player.PreferredSubtitleLanguage=="en",
                "A late prepared caption contaminated the following file or replaced the retained language.");
        }
        foreach(var mode in new[]{"malformed-readback","missing-reply","rejection-readback-failure","rejection-readback-timeout",
            "rejection-hidden-sid-change","rejection-track-change","rejection-native-change","missing-status"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            fixture.RejectSubAdd=mode.StartsWith("rejection-",StringComparison.Ordinal);
            fixture.SuppressSubAddReply=mode=="missing-reply";fixture.OmitSubAddReplyStatus=mode=="missing-status";
            fixture.SkipSubAdd=mode=="missing-status";fixture.AddTrackOnRejectedSubAdd=mode=="rejection-track-change";
            if(mode=="rejection-hidden-sid-change")await fixture.Player.SelectSubtitleAsync(null,7);
            var blocked=mode=="malformed-readback" || fixture.RejectSubAdd;
            if(blocked)fixture.BlockSubAdd();
            var addition=fixture.Player.AddPreparedSubtitleAsync(new("fr","https://www.youtube.com/api/timedtext?fixture=missing.vtt"),7);
            if(blocked)
            {
                await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if(mode is "malformed-readback" or "rejection-readback-failure")fixture.MalformedTracks=true;
                if(mode=="rejection-readback-timeout")fixture.SuppressTrackReply=true;
                if(mode=="rejection-hidden-sid-change")fixture.SidReadback=JsonSerializer.SerializeToElement(1L);
                if(mode=="rejection-native-change")fixture.SetMedia(8,true);
                fixture.ReleaseSubAdd();
            }
            await ExpectAsync<IOException>(()=>addition,"An unconfirmed native prepared-caption result was accepted: "+mode);
            Assert(fixture.Player.OwnedProcessIds.Count==0 && !fixture.NativePlaying &&
                (mode=="rejection-native-change"?fixture.Player.Snapshot.MediaGeneration==8:fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded),
                "An unconfirmed prepared addition left a live session behind: "+mode);
        }
    }

    private static async Task RunLoadFailureContractsAsync(string workspace)
    {
        foreach(var mode in new[]{"playlist-error","playlist-missing","load-error","load-no-reply","playlist-stale","playlist-canceled"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            fixture.PlaylistError=mode is "playlist-error" or "playlist-stale";
            fixture.HidePlaylist=mode=="playlist-missing";fixture.LoadError=mode=="load-error";
            fixture.SuppressLoadReply=mode=="load-no-reply";
            using var cancellation=new CancellationTokenSource();
            if(mode is "playlist-stale" or "playlist-canceled")fixture.BlockPlaylistRead();
            var reports=0;fixture.Player.PlaybackError+=_=>Interlocked.Increment(ref reports);
            var loading=fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,cancellationToken:cancellation.Token,
                resolvedStream:new("https://media.fixture.invalid/failed",new Dictionary<string,string>(),[720],[]));
            if(mode is "playlist-stale" or "playlist-canceled")
            {
                await fixture.PlaylistReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if(mode=="playlist-stale")fixture.SetMedia(99,true);else cancellation.Cancel();
                fixture.ReleasePlaylistRead();
            }
            if(mode=="playlist-canceled")await ExpectAsync<OperationCanceledException>(()=>loading,"Canceled submitted load was reported successful.");
            else if(mode=="load-no-reply")await ExpectAsync<IOException>(()=>loading,"A submitted load with no response was reported successful.");
            else await ExpectAsync<InvalidOperationException>(()=>loading,"A failed submitted load was reported successful.");
            var snapshot=fixture.Player.Snapshot;
            if(mode=="playlist-stale")
                Assert(snapshot.MediaGeneration==99 && snapshot.Loaded && !snapshot.Ended && fixture.Player.OwnedProcessIds.Count>0 && reports==0 &&
                    !fixture.RecordedCommands.Any(command=>command[0].GetString()=="quit"),"Old load failure stopped or reported an error over a newer generation.");
            else
                Assert(snapshot.Ended && !snapshot.Loaded && !snapshot.Buffering && fixture.Player.OwnedProcessIds.Count==0 && !fixture.NativePlaying,
                    "A submitted load failure ended the snapshot but left native playback/session alive: "+mode);
        }
    }

    public static async Task RunAccessErrorsAsync(string workspace)
    {
        ServiceRegression.AccessErrorContracts();
        await using var fixture=await SubtitleFixture.CreateAsync(workspace);
        var reports=new List<string>();fixture.Player.PlaybackError+=failure=>{lock(reports)reports.Add(failure.Message);};
        var generation=7L;
        foreach(var (stderr,code,status) in new[]{
            ("Sign in to confirm your age. private signed URL token", "signin_age",1),
            ("Sign in to confirm you're not a bot","bot",1),("Members-only video. Sign in","member",1),
            ("Private video. Sign in","private",1),("HTTP 429","rate_limit",1),("Connection timed out","network",1),
            ("Video unavailable","unavailable",1),("DRM-protected. Sign in","generic",1),
            ("Sign in to confirm your age","generic",0)})
        {
            fixture.SetMedia(generation++,true);
            using var metadata=JsonDocument.Parse(JsonSerializer.Serialize(new{status,stderr}));fixture.ExtractionFailure=metadata.RootElement.Clone();
            int prior;lock(reports)prior=reports.Count;
            await fixture.EndErrorAsync();
            await UntilAsync(()=>{lock(reports)return reports.Count>prior;});
            var snapshot=fixture.Player.Snapshot;
            Assert(snapshot.Ended && snapshot.EndReason=="error" && snapshot.AuthenticationRequired==(code=="signin_age"),"Native extractor error lost its authentication/media distinction.");
            Assert(snapshot.ErrorMessage!=null && !snapshot.ErrorMessage.Contains("token") && !snapshot.ErrorMessage.Contains("mpv-untrusted"),"Playback snapshot exposed raw diagnostics.");
            if(code!="generic")Assert(snapshot.ErrorMessage==ProcessRunner.ErrorMessage(code),"The classified native error had the wrong fixed message.");
            Assert(fixture.RecordedCommands.Any(command=>command[0].GetString()=="get_property" && command[1].GetString()=="user-data/mpv/ytdl/json-subprocess-result/status"),
                "End-file extraction diagnosis did not use the bounded existing IPC channel.");
        }
        fixture.SetMedia(generation++,true);fixture.ExtractionFailure=null;
        int before;lock(reports)before=reports.Count;
        await fixture.EndErrorAsync();await UntilAsync(()=>{lock(reports)return reports.Count>before;});
        Assert(!fixture.Player.Snapshot.AuthenticationRequired && fixture.Player.Snapshot.ErrorMessage!=null,"Missing extractor metadata invented an authentication requirement.");

        fixture.SetMedia(generation++,true);fixture.BlockExtractionRead();
        using var stale=JsonDocument.Parse("{\"status\":1,\"stderr\":\"Sign in to confirm your age\"}");fixture.ExtractionFailure=stale.RootElement.Clone();
        await fixture.EndErrorAsync();await fixture.ExtractionReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.SetMedia(generation++,true);fixture.ReleaseExtractionRead();
        // A round-trip after release establishes that the pending response and its
        // asynchronous continuation had a chance to run without blocking the reader.
        await fixture.Player.GetSubtitlesAsync();
        Assert(fixture.Player.Snapshot.Loaded && !fixture.Player.Snapshot.Ended && fixture.Player.Snapshot.ErrorMessage is null && !fixture.Player.Snapshot.AuthenticationRequired,
            "Delayed authentication diagnosis contaminated the next media generation.");

        foreach(var stderrFirst in new[]{false,true})
        {
            fixture.SetMedia(generation++,true);fixture.ExtractionFailure=null;
            if(stderrFirst){await fixture.ExtractionPropertyAsync("stderr","Sign in to confirm your age signed-secret");await fixture.ExtractionPropertyAsync("status",1);}
            else{await fixture.ExtractionPropertyAsync("status",1);await fixture.ExtractionPropertyAsync("stderr","Sign in to confirm your age signed-secret");}
            await fixture.ExtractionPropertyAsync("status",null);await fixture.ExtractionPropertyAsync("stderr",null);
            lock(reports)before=reports.Count;
            await fixture.EndErrorAsync();await UntilAsync(()=>{lock(reports)return reports.Count>before;});
            Assert(fixture.Player.Snapshot.AuthenticationRequired,"on_after_end_file deletion lost the observed, paired authentication failure.");
            // Deletion after end-file must also leave the final safe classification intact.
            await fixture.ExtractionPropertyAsync("stderr",null);await fixture.ExtractionPropertyAsync("status",null);
            Assert(fixture.Player.Snapshot.AuthenticationRequired,"A post-end null property erased the confirmed authentication failure.");
        }
        async Task ExpectCleared(string reason)
        {
            lock(reports)before=reports.Count;
            await fixture.EndErrorAsync();await UntilAsync(()=>{lock(reports)return reports.Count>before;});
            Assert(!fixture.Player.Snapshot.AuthenticationRequired,reason);
        }
        fixture.SetMedia(generation++,true);await fixture.ExtractionPropertyAsync("status",1);await fixture.ExtractionPropertyAsync("stderr","Sign in to confirm your age");
        await fixture.FileLoadedAsync();await ExpectCleared("A successful file-loaded retained a previous extraction failure for a later media error.");
        fixture.SetMedia(generation++,true);await fixture.ExtractionPropertyAsync("status",1);await fixture.ExtractionPropertyAsync("stderr","Sign in");
        await fixture.StartFileAsync();await ExpectCleared("A new start-file reused the previous extraction failure.");
        fixture.SetMedia(generation++,true);await fixture.ExtractionPropertyAsync("stderr","Sign in");await fixture.ExtractionPropertyAsync("status",0);
        await ExpectCleared("Successful status 0 plus stderr invented an authentication failure.");
        fixture.SetMedia(generation++,true);await fixture.ExtractionPropertyAsync("status",1);await fixture.ExtractionPropertyAsync("stderr","Sign in");
        await UntilAsync(()=>fixture.LatestExtractionGeneration==fixture.Player.Snapshot.MediaGeneration);
        fixture.SetMedia(generation++,true);await ExpectCleared("A cached error from a different media generation was reused.");
        for(var index=0;index<80;index++)
        {
            fixture.SetMedia(100+index,true,100+index);await fixture.ExtractionPropertyAsync("status",1);await fixture.ExtractionPropertyAsync("stderr","Sign in");
            await UntilAsync(()=>fixture.LatestExtractionGeneration==100+index);
        }
        await UntilAsync(()=>fixture.LatestExtractionGeneration==179);
        Assert(fixture.ExtractionFailureCount==64 && fixture.ExtractionMetadataCount==64,"The sanitized native metadata cache exceeded its 64-entry limit.");
        await fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/new-media",new Dictionary<string,string>(),[720],[]));
        Assert(fixture.ExtractionFailureCount==0 && fixture.ExtractionMetadataCount==0 && fixture.Player.Snapshot.ErrorMessage is null && !fixture.Player.Snapshot.AuthenticationRequired,
            "A fresh load carried cached authentication failures into successful playback.");
    }

    private static async Task RunExternalSubtitleContractsAsync(string workspace)
    {
        AvailableSubtitle Caption(string language,string fixture="chosen",bool automatic=false)=>
            new(language,"Synthetic "+language,"https://www.youtube.com/api/timedtext?lang="+language+"&fixture="+fixture,automatic);
        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            var player=fixture.Player;
            await player.AddPreparedSubtitleAsync(new("ko","https://www.youtube.com/api/timedtext?fixture=prepared.vtt"),7);
            var protectedIds=(await player.GetSubtitlesAsync()).Tracks.Select(track=>track.Id).ToHashSet();
            var french=Caption("fr");var adds=fixture.SubAdds;
            await player.SelectExternalSubtitleAsync(french,7);
            var chosen=(await player.GetSubtitlesAsync()).Tracks.Single(track=>track.Selected);
            Assert(chosen.Language=="fr" && fixture.SubAdds==adds+1 && player.PreferredSubtitleLanguage=="fr",
                "Selected catalog language was not added once, confirmed, and retained as preference.");
            var lastAdd=fixture.RecordedCommands.Last(command=>command[0].GetString()=="sub-add");
            Assert(lastAdd[1].GetString()==french.Url && lastAdd[2].GetString()=="select" && lastAdd[3].GetString()==french.Label && lastAdd[4].GetString()==french.Language,
                "Catalog selection forwarded unrelated URLs/metadata or failed to select the chosen language only.");
            await player.SelectExternalSubtitleAsync(french,7);
            Assert(fixture.SubAdds==adds+1 && (await player.GetSubtitlesAsync()).Tracks.Single(track=>track.Selected).Id==chosen.Id,
                "The same video/URL downloaded a duplicate track instead of reusing its confirmed ID.");
            await player.SelectSubtitleAsync(null,7);await player.SelectExternalSubtitleAsync(french,7);
            Assert(!(await player.GetSubtitlesAsync()).Off && fixture.SubAdds==adds+1,
                "Re-selecting a cached language after off redownloaded it or left visibility disabled.");

            for(var index=0;index<25;index++)await player.SelectExternalSubtitleAsync(Caption("es","retention-"+index,index%2==0),7);
            var retained=await player.GetSubtitlesAsync();
            Assert(retained.Tracks.Count<=protectedIds.Count+12 && protectedIds.All(id=>retained.Tracks.Any(track=>track.Id==id)) &&
                fixture.RemovedIds.All(id=>!protectedIds.Contains(id)),"External subtitle retention grew without bound or removed a default/prepared track.");

            var regional=Caption("zh-Hans","region");await player.SelectExternalSubtitleAsync(regional,7);
            Assert(player.PreferredSubtitleLanguage=="zh-Hans","Catalog language key case was lost in the getter.");
            await player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/new-media",new Dictionary<string,string>(),[720],[]));
            var next=await player.GetSubtitlesAsync();adds=fixture.SubAdds;
            Assert(fixture.LastRawOptions is{ } raw && raw.GetProperty("sub-langs").GetString()=="(?i:zh-Hans(?:-orig)?),ko,en,ko-orig,en-orig",
                "A new file did not request the preferred regional language and its original variant while retaining ko/en defaults.");
            Assert(fixture.LastRawOptions is{ } subtitleOptions && subtitleOptions.TryGetProperty("write-subs",out _) && subtitleOptions.TryGetProperty("write-auto-subs",out _),
                "Ordinary mpv extraction omitted either manual or automatic captions.");
            await player.SelectExternalSubtitleAsync(regional,next.MediaGeneration);
            Assert(fixture.SubAdds==adds+1 && (await player.GetSubtitlesAsync()).Tracks.Single(track=>track.Selected).Id>32,
                "A new media generation reused the previous file's URL-to-track mapping.");

            var invalid=regional with{Language="live_chat"};var commands=fixture.Commands;
            foreach(var language in new[]{"live_chat","en,all","(.*)","ja\n","x",new string('a',65)})
                await ExpectAsync<InvalidDataException>(()=>player.SelectExternalSubtitleAsync(invalid with{Language=language},next.MediaGeneration),"Malformed catalog language reached IPC.");
            foreach(var url in new[]{"http://www.youtube.com/api/timedtext?lang=fr","https://evil.fixture.invalid/api/timedtext?lang=fr",
                "https://www.youtube.com/watch?v=fixture0001","https://user@www.youtube.com/api/timedtext","https://www.youtube.com:444/api/timedtext",
                "https://www.youtube.com/api/timedtext#fragment","https://www.youtube.com/api/timedtext?lang=fr\n","https://www.youtube.com\\api\\timedtext"})
                await ExpectAsync<InvalidDataException>(()=>player.SelectExternalSubtitleAsync(regional with{Url=url},next.MediaGeneration),"An arbitrary caption endpoint reached IPC.");
            await ExpectAsync<InvalidDataException>(()=>player.SelectExternalSubtitleAsync(regional with{Label="secret\nlabel"},next.MediaGeneration),"Caption label controls reached IPC.");
            await ExpectAsync<InvalidOperationException>(()=>player.SelectExternalSubtitleAsync(regional,next.MediaGeneration-1),"A stale catalog selection reached IPC.");
            using(var canceled=new CancellationTokenSource())
            {canceled.Cancel();await ExpectAsync<OperationCanceledException>(()=>player.SelectExternalSubtitleAsync(regional,next.MediaGeneration,canceled.Token),"A canceled external selection reached IPC.");}
            Assert(fixture.Commands==commands,"Rejected metadata/stale/canceled external selection changed the player.");

            var priorLanguage=player.PreferredSubtitleLanguage;fixture.ForceWrongSid=true;
            var failed=Caption("de","unverified");adds=fixture.SubAdds;
            await ExpectAsync<InvalidOperationException>(()=>player.SelectExternalSubtitleAsync(failed,next.MediaGeneration),"An unconfirmed external choice became success.");
            Assert(player.PreferredSubtitleLanguage==priorLanguage,"An unconfirmed readback overwrote the preferred language.");
            fixture.ForceWrongSid=false;await player.SelectExternalSubtitleAsync(failed,next.MediaGeneration);
            Assert(fixture.SubAdds==adds+1 && player.PreferredSubtitleLanguage=="de","Retrying a known added track duplicated its download after failed confirmation.");
        }

        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            var player=fixture.Player;await player.SelectSubtitleAsync(2,7);
            fixture.BlockSubAdd();using var canceled=new CancellationTokenSource();
            var older=player.SelectExternalSubtitleAsync(Caption("fr","canceled-download"),7,canceled.Token);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));canceled.Cancel();
            var newer=player.SelectExternalSubtitleAsync(Caption("es","following-choice"),7);
            await Task.Delay(50);
            Assert(!older.IsCompleted && !newer.IsCompleted && fixture.SubAdds==1,"Canceled submitted sub-add released the load gate before terminal native completion.");
            fixture.ReleaseSubAdd();await ExpectAsync<OperationCanceledException>(()=>older,"The canceled download was reported successful.");await newer;
            var result=await player.GetSubtitlesAsync();
            Assert(player.PreferredSubtitleLanguage=="es" && result.Tracks.Single(track=>track.Selected).Language=="es" &&
                result.Tracks.All(track=>track.Language!="fr") && result.Tracks.Any(track=>track.Id==1) && result.Tracks.Any(track=>track.Id==2),
                "Late canceled sub-add contaminated a following choice or removed baseline tracks.");
        }

        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            fixture.BlockSubAdd();using var canceled=new CancellationTokenSource();
            var older=fixture.Player.SelectExternalSubtitleAsync(Caption("fr","previous-file"),7,canceled.Token);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));canceled.Cancel();fixture.SetMedia(8,true);fixture.ReleaseSubAdd();
            await ExpectAsync<OperationCanceledException>(()=>older,"Canceled download changing files became success.");
            Assert(fixture.Player.OwnedProcessIds.Count==0 && fixture.Player.PreferredSubtitleLanguage is null,
                "An unconfirmed old-file addition retained a live session or wrote a stale preference.");
        }

        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            fixture.SkipSubAdd=true;
            await ExpectAsync<IOException>(()=>fixture.Player.SelectExternalSubtitleAsync(Caption("fr","missing-native-track"),7),
                "A native success envelope with no added track became confirmed selection.");
            Assert(fixture.Player.OwnedProcessIds.Count==0 && !fixture.Player.Snapshot.Loaded && fixture.Player.Snapshot.Ended,
                "An unconfirmed native addition kept a live player/snapshot instead of requesting replay.");
        }
    }

    private static async Task RunExternalSubtitleRejectionAsync(string workspace)
    {
        AvailableSubtitle Caption(string fixture)=>new("fr","Synthetic French","https://www.youtube.com/api/timedtext?lang=fr&fixture="+fixture,false);
        foreach(var mode in new[]{"selected","off","empty","cache-full","http-429"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            var player=fixture.Player;
            if(mode=="cache-full")
                for(var index=0;index<12;index++)await player.SelectExternalSubtitleAsync(Caption("cache-"+index),7);
            await player.SelectSubtitleAsync(2,7);
            if(mode=="off")await player.SelectSubtitleAsync(null,7);
            if(mode=="empty")fixture.ClearNativeSubtitles();
            var before=await player.GetSubtitlesAsync();var identity=fixture.NativeIdentity;
            var beforeSid=fixture.Sid;var beforeVisible=fixture.Visible;var priorLanguage=player.PreferredSubtitleLanguage;
            var removedBefore=fixture.RemovedIds.Length;var commands=fixture.Commands;
            fixture.RejectSubAdd=true;fixture.SubAddError=mode=="http-429"?"HTTP error 429":"loading failed";
            Exception? failure=null;
            try{await player.SelectExternalSubtitleAsync(Caption("rejected-"+mode),7);}
            catch(Exception error){failure=error;}
            Assert(failure?.GetType().Name=="SubtitleCommandRejectedException",
                "An explicit unchanged manual rejection did not report the narrow recoverable category: "+mode);
            var after=await player.GetSubtitlesAsync();var removed=fixture.RemovedIds.Skip(removedBefore).ToHashSet();
            Assert(after.Tracks.SequenceEqual(before.Tracks.Where(track=>!removed.Contains(track.Id))) && after.Off==before.Off &&
                Equals(beforeSid,fixture.Sid) && beforeVisible==fixture.Visible && player.PreferredSubtitleLanguage==priorLanguage &&
                player.HasActiveMedia(7) && fixture.NativePlaying && fixture.RetainsNativeIdentity(identity) &&
                !fixture.RecordedCommands.Skip(commands).Any(command=>command[0].GetString() is "stop" or "quit"),
                "A confirmed manual rejection changed the retained selection, owner, language, or valid post-eviction baseline: "+mode);
            var adds=fixture.SubAdds;fixture.RejectSubAdd=false;
            await player.SelectExternalSubtitleAsync(Caption("rejected-"+mode),7);
            Assert(fixture.SubAdds==adds+1 && player.PreferredSubtitleLanguage=="fr" && (await player.GetSubtitlesAsync()).Tracks.Count<=15,
                "A failed candidate entered the success cache or exceeded bounded retention: "+mode);
        }
        foreach(var mode in new[]{"track-change","source-change","default-change","selected-change","visibility-change","hidden-sid-change",
            "readback-failure","readback-timeout","native-change","missing-reply","missing-status"})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            fixture.RejectSubAdd=mode is not("missing-reply" or "missing-status");
            fixture.AddTrackOnRejectedSubAdd=mode=="track-change";
            fixture.SuppressSubAddReply=mode=="missing-reply";fixture.OmitSubAddReplyStatus=mode=="missing-status";
            if(mode=="hidden-sid-change")await fixture.Player.SelectSubtitleAsync(null,7);
            fixture.BlockSubAdd();
            var addition=fixture.Player.SelectExternalSubtitleAsync(Caption("unsafe-"+mode),7);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(mode=="source-change")fixture.NativeTrackDetail=new("external-filename","https://other.fixture.invalid/changed.vtt");
            if(mode=="default-change")fixture.NativeTrackDetail=new("default",true);
            if(mode=="selected-change")fixture.NativeTrackDetail=new("selected",true);
            if(mode=="visibility-change")fixture.VisibilityReadback=JsonSerializer.SerializeToElement(false);
            if(mode=="hidden-sid-change")fixture.SidReadback=JsonSerializer.SerializeToElement(1L);
            if(mode=="readback-failure")fixture.MalformedTracks=true;
            if(mode=="readback-timeout")fixture.SuppressTrackReply=true;
            if(mode=="native-change")fixture.SetMedia(8,true);
            fixture.ReleaseSubAdd();
            await ExpectAsync<IOException>(()=>addition,"An unconfirmed/changed manual rejection escaped fail-closed handling: "+mode);
            Assert(fixture.Player.OwnedProcessIds.Count==0 && !fixture.NativePlaying &&
                (mode=="native-change"?fixture.Player.Snapshot.MediaGeneration==8:fixture.Player.Snapshot.Ended && !fixture.Player.Snapshot.Loaded),
                "An unconfirmed manual addition left its native session live: "+mode);
        }
        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            fixture.RejectSubAdd=true;fixture.BlockSubAdd();using var canceled=new CancellationTokenSource();
            var addition=fixture.Player.SelectExternalSubtitleAsync(Caption("canceled-rejection"),7,canceled.Token);
            await fixture.SubAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));canceled.Cancel();fixture.BlockTrackRead();fixture.ReleaseSubAdd();
            await fixture.TrackReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var next=fixture.Player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/following",new Dictionary<string,string>(),[720],[]));
            try{await Task.Delay(50);Assert(!addition.IsCompleted && !next.IsCompleted,"Canceled rejected manual addition released its gate before strong readback.");}
            finally{fixture.ReleaseTrackRead();await ExpectAsync<OperationCanceledException>(()=>addition,"Canceled rejected manual addition became success.");await next;}
            Assert(fixture.Player.HasActiveMedia(fixture.Player.Snapshot.MediaGeneration) && fixture.NativePlaying,
                "A fully confirmed canceled rejection prevented the following video.");
        }
    }

    private static async Task RunPreparedSubtitleUrlBoundaryAsync(string workspace)
    {
        await using var fixture=await SubtitleFixture.CreateAsync(workspace);
        var identity=fixture.NativeIdentity;var commands=fixture.Commands;
        foreach(var url in new[]{"https://other.fixture.invalid/private","http://www.youtube.com/api/timedtext","https://www.youtube.com/watch?v=fixture0001",
            "https://user@www.youtube.com/api/timedtext","https://www.youtube.com:8443/api/timedtext","https://www.youtube.com/api/timedtext#fragment",
            "https://www.youtube.com/api/timedtext\n","https://www.youtube.com\\api/timedtext"})
            await ExpectAsync<InvalidDataException>(()=>fixture.Player.AddPreparedSubtitleAsync(new("fr",url),7),"Invalid optional prepared URL reached native IPC.");
        foreach(var language in new[]{"en,all","live_chat","en\n","x",new string('a',65)})
            await ExpectAsync<InvalidDataException>(()=>fixture.Player.AddPreparedSubtitleAsync(new(language,"https://www.youtube.com/api/timedtext?lang=fr"),7),"Invalid prepared language reached native IPC.");
        Assert(fixture.Commands==commands && fixture.Player.HasActiveMedia(7) && fixture.RetainsNativeIdentity(identity),
            "Invalid optional caption validation mutated or closed valid media.");
        const string signedUrl="https://www.youtube.com/api/timedtext?v=fixture0001&sig=A%2fb%2BC%3D&lang=fr";
        await fixture.Player.AddPreparedSubtitleAsync(new("fr",signedUrl),7);
        Assert(fixture.RecordedCommands.Last(command=>command[0].GetString()=="sub-add")[1].GetString()==signedUrl,
            "Prepared subtitle validation changed the extractor's signed query.");
    }

    public static async Task RunHighestQualityAsync(string workspace)
    {
        foreach(var height in new[]{0,1440})
        foreach(var selected in new[]{false,true})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            const string url="https://www.youtube.com/watch?v=fixture0002";
            var channel=selected?new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=0}:null;
            await fixture.Player.LoadAsync(url,null,height,accountContext:channel);
            var format=fixture.RecordedCommands.Last(command=>command[0].GetString()=="set_property" && command[1].GetString()=="ytdl-format")[2].GetString();
            Assert(format==(height==0?"bv*+ba/b":"bv*[height<=1440]+ba/b[height<=1440]") &&
                fixture.RecordedCommands.Last(command=>command[0].GetString()=="loadfile")[1].GetString()==url,
                "Regular playback lost the unlimited/explicit height policy or performed eager direct extraction.");
            var raw=fixture.RecordedCommands.Last(command=>command[0].GetString()=="set_property" && command[1].GetString()=="ytdl-raw-options")[2];
            Assert(selected
                    ? raw.GetProperty("extractor-args").GetString()==YouTubeAccountOptions.ExtractorArguments(channel)
                    : !raw.TryGetProperty("extractor-args",out _),
                "Native playback did not preserve the selected-only HLS policy or contaminated guest extraction.");
        }
        await using(var fixture=await SubtitleFixture.CreateAsync(workspace))
        {
            const string url="https://www.youtube.com/watch?v=fixture0002";
            const string hls="https://manifest.googlevideo.com/api/manifest/hls_playlist/fixture/file/index.m3u8";
            var channel=new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2};
            await fixture.Player.LoadAsync(url,null,0,accountContext:channel,
                resolvedStream:new(hls,new Dictionary<string,string>{{"User-Agent","fixture-agent"}},[1080],[]));
            var load=fixture.RecordedCommands.Last(command=>command[0].GetString()=="loadfile");
            Assert(load[1].GetString() is { } source && source.StartsWith("edl://",StringComparison.Ordinal) && source.Contains(hls,StringComparison.Ordinal) &&
                load[4].GetProperty("user-agent").GetString()=="fixture-agent",
                "Verified HLS lost direct-source isolation or its media User-Agent for a nonzero account slot.");
        }
        foreach(var requestedHeight in new[]{0,1})
        {
            await using var fixture=await SubtitleFixture.CreateAsync(workspace);
            const string url="https://www.youtube.com/watch?v=fixture0002";
            await fixture.Player.PrepareNextAsync(url,"https://media.fixture.invalid/prepared-unlimited",7,0);
            await fixture.Player.LoadAsync(url,null,requestedHeight);
            Assert(fixture.Adoptions==(requestedHeight==0?1:0),
                "Prepared media confused an unlimited request with an explicit one-pixel cap.");
        }
    }
    private static async Task RunSubtitleContractsAsync(string workspace)
    {
        SubtitleParserContracts();
        await using var fixture=await SubtitleFixture.CreateAsync(workspace);
        var player=fixture.Player;
        var initial=await player.GetSubtitlesAsync();
        Assert(initial.MediaGeneration==7 && initial.Tracks.Count==3 && initial.Tracks.Count(track=>track.Language=="en")==2 && initial.Tracks.Select(track=>track.Label).Distinct().Count()==3,
            "Subtitle menu collapsed duplicate-language tracks or lost their distinct IDs/labels.");
        await player.SelectSubtitleAsync(3,initial.MediaGeneration);
        var selected=await player.GetSubtitlesAsync();
        Assert(!selected.Off && selected.Tracks.Single(track=>track.Selected).Id==3,"Selecting a duplicate-language track chose another ID.");
        Assert(fixture.Languages.First()=="en","The chosen subtitle language did not become the next-file preference.");
        await player.AddPreparedSubtitleAsync(new("en","https://www.youtube.com/api/timedtext?fixture=caption.vtt"),7);
        Assert((await player.GetSubtitlesAsync()).Tracks.Single(track=>track.Selected).Id==3,"Adding another subtitle replaced the explicitly selected duplicate-language ID.");
        await player.SelectSubtitleAsync(null,7);
        Assert((await player.GetSubtitlesAsync()).Off && fixture.Sid is string no && no=="no" && !fixture.Visible,"Subtitle off did not disable selection and visibility.");
        await player.AddPreparedSubtitleAsync(new("ko","https://www.youtube.com/api/timedtext?fixture=korean.vtt"),7);
        Assert((await player.GetSubtitlesAsync()).Off,"Adding an external subtitle ignored the user's off choice.");
        var commands=fixture.Commands;
        await ExpectAsync<InvalidOperationException>(()=>player.SelectSubtitleAsync(99,7),"An absent subtitle ID was sent to mpv.");
        Assert(fixture.MutationsSince(commands)==0,"An absent subtitle ID mutated mpv properties.");
        commands=fixture.Commands;
        await ExpectAsync<InvalidOperationException>(()=>player.SelectSubtitleAsync(1,6),"A stale subtitle menu was accepted.");
        Assert(fixture.Commands==commands,"A stale subtitle selection reached IPC.");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(()=>player.GetSubtitlesAsync(canceled.Token),"Canceled subtitle listing reached IPC.");
            await ExpectAsync<OperationCanceledException>(()=>player.SelectSubtitleAsync(1,7,canceled.Token),"Canceled subtitle selection reached IPC.");
            Assert(fixture.Commands==commands,"Canceled subtitle operations reached IPC.");
        }
        fixture.SetMedia(8,false);
        var addition=player.AddPreparedSubtitleAsync(new("en","https://www.youtube.com/api/timedtext?fixture=late.vtt"),8);
        await UntilAsync(()=>fixture.ReadyCount==1);
        Assert(!addition.IsCompleted && fixture.MutationsSince(commands)==0,"External captions were added before file-loaded.");
        await fixture.FileLoadedAsync();await addition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert((await player.GetSubtitlesAsync()).Off,"File-loaded waiting lost the retained off preference.");
        await player.SelectSubtitleAsync(3,8);
        fixture.NextTracks=[new(31,"한국어","ko"),new(32,"English","en"),new(33,"English auto","en")];
        await player.LoadAsync("https://www.youtube.com/watch?v=fixture0002",null,720,resolvedStream:new("https://media.fixture.invalid/stream",new Dictionary<string,string>(),[720],[]));
        var next=await player.GetSubtitlesAsync();
        Assert(!next.Off && next.Tracks.Single(track=>track.Selected).Language=="en" && fixture.LastLoadOptions is{ } normal && normal.GetProperty("sid").GetString()=="auto",
            "Direct next-file loading reused a stale subtitle ID or lost the selected language.");
        await player.PrepareNextAsync("https://www.youtube.com/watch?v=fixture0003","https://media.fixture.invalid/prepared",next.MediaGeneration,720);
        await player.SelectSubtitleAsync(null,next.MediaGeneration);
        await player.LoadAsync("https://www.youtube.com/watch?v=fixture0003",null,720);
        var adopted=await player.GetSubtitlesAsync();
        Assert(adopted.MediaGeneration>next.MediaGeneration && adopted.Off && fixture.Adoptions==1,
            "Prepared adoption restored old per-file subtitle options over the user's off choice.");
        fixture.BlockTrackRead();
        var reading=player.GetSubtitlesAsync();
        await fixture.TrackReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.SetMedia(adopted.MediaGeneration+1,true);fixture.ReleaseTrackRead();
        await ExpectAsync<InvalidOperationException>(()=>reading,"A subtitle query returned track data after the media generation changed.");
        fixture.MalformedTracks=true;
        commands=fixture.Commands;
        await ExpectAsync<InvalidDataException>(()=>player.SelectSubtitleAsync(31,adopted.MediaGeneration+1),"Malformed track-list permitted a subtitle write.");
        Assert(fixture.MutationsSince(commands)==0,"Malformed track-list caused subtitle mutations.");
    }

    private static void SubtitleParserContracts()
    {
        const string tracks="[{\"type\":\"audio\",\"id\":1},{\"type\":\"sub\",\"id\":1,\"lang\":\"ko\",\"title\":\"한국어\"},{\"type\":\"sub\",\"id\":2,\"lang\":\"en\",\"title\":\"English\"},{\"type\":\"sub\",\"id\":3,\"lang\":\"en\",\"title\":\"English auto\"},{\"type\":\"sub\",\"id\":4},{\"type\":\"sub\",\"id\":5,\"lang\":\"xx-unknown\",\"title\":\"unknown title\\nline\"}]";
        SubtitleSelection Parse(string list,string sid="2",string visible="true")
        {
            using var data=JsonDocument.Parse(list);using var selected=JsonDocument.Parse(sid);using var visibility=JsonDocument.Parse(visible);
            return MpvPlayerService.ParseSubtitleSelection(data.RootElement,selected.RootElement,visibility.RootElement,7);
        }
        var parsed=Parse(tracks);
        Assert(parsed.Tracks.Count==5 && parsed.Tracks.Single(track=>track.Selected).Id==2 && parsed.Tracks[3].Language==null && parsed.Tracks[3].Label.Length>0 && parsed.Tracks[4].Language=="xx-unknown" && !parsed.Tracks[4].Label.Contains('\n'),
            "Unknown/missing subtitle title/language or an audio ID collision corrupted the menu.");
        Assert(Parse(tracks,"false").Off && Parse(tracks,"\"no\"").Off && Parse(tracks,"2","false").Off,"Native off/hidden subtitle states were ignored.");
        Assert(!Parse("[{\"type\":\"sub\",\"id\":9,\"selected\":true,\"main-selection\":0}]","\"auto\"").Off,"Native auto selection lost its primary selected track.");
        foreach(var bad in new[]{"null","{}","[null]","[{\"id\":1}]","[{\"type\":1}]","[{\"type\":\"sub\",\"id\":\"1\"}]","[{\"type\":\"sub\",\"id\":0}]",
            "[{\"type\":\"sub\",\"id\":1},{\"type\":\"sub\",\"id\":1}]","[{\"type\":\"sub\",\"id\":1,\"lang\":1}]","[{\"type\":\"sub\",\"id\":1,\"title\":{}}]","[{\"type\":\"sub\",\"id\":1,\"selected\":\"yes\"}]"})
            ExpectInvalid(()=>Parse(bad,"false"),"Malformed subtitle track-list was accepted: "+bad);
        foreach(var badSid in new[]{"true","null","{}","0","-1","1.5","\"other\"","99"})ExpectInvalid(()=>Parse(tracks,badSid),"Malformed or unavailable subtitle sid was accepted.");
        foreach(var badVisibility in new[]{"null","0","\"yes\"","{}"})ExpectInvalid(()=>Parse(tracks,"2",badVisibility),"Malformed subtitle visibility was accepted.");
        ExpectInvalid(()=>Parse("[{\"type\":\"sub\",\"id\":1,\"selected\":true},{\"type\":\"sub\",\"id\":2,\"selected\":true}]","\"auto\""),"An ambiguous primary auto subtitle was accepted.");
        var excessive=JsonSerializer.Serialize(Enumerable.Range(1,257).Select(id=>new{id,type="sub"}));
        ExpectInvalid(()=>Parse(excessive,"false"),"Subtitle track-list exceeded its bounded count.");
    }

    private sealed record FixtureSubtitle(long Id,string? Title,string? Language);
    private sealed class SubtitleFixture:IAsyncDisposable
    {
        private readonly string _root;
        private readonly NamedPipeServerStream _server;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _stop=new();
        private readonly SemaphoreSlim _writeGate=new(1,1);
        private readonly Task _serverTask;
        private bool _ipcDeliberatelyClosed;
        private readonly Process _processExit;
        private readonly object _session;
        private readonly Type _sessionType;
        private readonly List<JsonElement> _commands=[];
        private readonly object _fixtureLock=new();
        private readonly List<(long Id,string Source,JsonElement? Options)> _playlist=[(42,"https://media.fixture.invalid/current",null)];
        private List<FixtureSubtitle> _tracks=[new(1,"한국어","ko"),new(2,"English","en"),new(3,"English auto","en")];
        private TaskCompletionSource? _trackRelease;
        private TaskCompletionSource? _subAddRelease;
        private TaskCompletionSource? _extractionRelease;
        private TaskCompletionSource? _playlistRelease;
        private TaskCompletionSource? _appendRelease;
        private TaskCompletionSource? _subtitleDefaultRelease;
        private long _entry=42;
        public MpvPlayerService Player {get;}
        public object Sid {get;private set;}=1L;
        public bool Visible {get;private set;}=true;
        public string[] Languages {get;private set;}=["ko","kor","en","eng"];
        public FixtureSubtitle[] NextTracks {get;set;}=[new(31,"한국어","ko"),new(32,"English","en")];
        public JsonElement? LastLoadOptions {get;private set;}
        public JsonElement? LastRawOptions {get;private set;}
        public int Adoptions {get;private set;}
        public bool MalformedTracks {get;set;}
        public bool SuppressTrackReply {get;set;}
        public bool ForceWrongSid {get;set;}
        public bool SkipSubAdd {get;set;}
        public bool RejectSubAdd {get;set;}
        public HashSet<string> RejectedSubAddLanguages {get;}=[];
        public string SubAddError {get;set;}="loading failed";
        public KeyValuePair<string,object?>? NativeTrackDetail {get;set;}
        public bool AddTrackOnRejectedSubAdd {get;set;}
        public bool OmitSubAddReplyStatus {get;set;}
        public bool SuppressSubAddReply {get;set;}
        public bool PlaylistError {get;set;}
        public bool MalformedPlaylist {get;set;}
        public bool RemoveError {get;set;}
        public bool IgnorePlaylistRemove {get;set;}
        public bool SuppressPlaylistReply {get;set;}
        public bool SuppressRemoveReply {get;set;}
        public string? SubtitleDefaultError {get;set;}
        public string? SuppressSubtitleDefaultReply {get;set;}
        public JsonElement? SidReadback {get;set;}
        public JsonElement? VisibilityReadback {get;set;}
        public bool HidePlaylist {get;set;}
        public bool LoadError {get;set;}
        public bool SuppressLoadReply {get;set;}
        public bool OmitAppendEntryId {get;set;}
        public bool FailPlaylistAfterAppend {get;set;}
        public bool DuplicateAppend {get;set;}
        private bool _appendSubmitted;
        public long[] PlaylistIds=>_playlist.Select(item=>item.Id).ToArray();
        public bool NativePlaying {get;private set;}=true;
        public JsonElement? ExtractionFailure {get;set;}
        public TaskCompletionSource ExtractionReadStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TrackReadStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubAddStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlaylistReadStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AppendStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubtitleDefaultStarted {get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Commands {get{lock(_fixtureLock)return _commands.Count;}}
        private OwnedProcess NativeProcess=>(OwnedProcess)_sessionType.GetProperty("Process")!.GetValue(_session)!;
        public bool NativeProcessExited=>_processExit.HasExited;
        public bool NativeOwnerDisposed=>(int)typeof(OwnedProcess).GetField("_disposed",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(NativeProcess)! != 0;
        public bool NativeJobHandleRetained=>!NativeOwnerDisposed && (nint)typeof(OwnedProcess).GetField("_job",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(NativeProcess)! != 0;
        public (object Session,OwnedProcess Owner,int Pid,nint Job) NativeIdentity=>
            (_session,NativeProcess,NativeProcess.Id,(nint)typeof(OwnedProcess).GetField("_job",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(NativeProcess)!);
        public bool RetainsNativeIdentity((object Session,OwnedProcess Owner,int Pid,nint Job) expected)
        {
            var current=typeof(MpvPlayerService).GetField("_session",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player);
            var actual=NativeIdentity;
            return ReferenceEquals(current,expected.Session) && ReferenceEquals(actual.Session,expected.Session) &&
                ReferenceEquals(actual.Owner,expected.Owner) && actual.Pid==expected.Pid && expected.Job!=0 && actual.Job==expected.Job &&
                !NativeOwnerDisposed && !NativeProcessExited;
        }
        public string DescribeOwnedProcesses(IEnumerable<int> ids)=>string.Join(',',ids.Order().Select(id=>
        {
            try{using var process=Process.GetProcessById(id);return id+":"+process.ProcessName;}
            catch(ArgumentException){return id+":exited";}
            catch(InvalidOperationException){return id+":exited";}
            catch(System.ComponentModel.Win32Exception){return id+":unavailable";}
        }));
        public Process InvalidateNativeWaitHandle()
        {
            var repair=Process.GetProcessById(NativeProcess.Id);_=repair.SafeHandle;
            NativeProcess.Process.Dispose();return repair;
        }
        public void RestoreNativeWaitHandle(Process repair)=>typeof(OwnedProcess).GetField("<Process>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(NativeProcess,repair);
        public JsonElement[] RecordedCommands {get{lock(_fixtureLock)return _commands.Select(command=>command.Clone()).ToArray();}}
        public int SubAdds=>RecordedCommands.Count(command=>command[0].GetString()=="sub-add");
        public long[] RemovedIds=>RecordedCommands.Where(command=>command[0].GetString()=="sub-remove").Select(command=>command[1].GetInt64()).ToArray();
        public int ReadyCount
        {
            get
            {
                var ready=_sessionType.GetProperty("FileReady")!.GetValue(_session)!;
                return (int)ready.GetType().GetProperty("Count")!.GetValue(ready)!;
            }
        }
        public int ExtractionFailureCount=>MetadataCount("ExtractionFailures");
        public int ExtractionMetadataCount=>MetadataCount("ExtractionMetadata");
        public long LatestExtractionGeneration
        {
            get
            {
                var stateLock=typeof(MpvPlayerService).GetField("_stateLock",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player)!;
                lock(stateLock)
                {
                    var dictionary=(System.Collections.IDictionary)_sessionType.GetProperty("ExtractionFailures")!.GetValue(_session)!;
                    return dictionary.Values.Cast<object>().Select(item=>(long)item.GetType().GetProperty("Generation")!.GetValue(item)!).DefaultIfEmpty(-1).Max();
                }
            }
        }
        private int MetadataCount(string property)
        {
            var stateLock=typeof(MpvPlayerService).GetField("_stateLock",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player)!;
            lock(stateLock)return ((System.Collections.IDictionary)_sessionType.GetProperty(property)!.GetValue(_session)!).Count;
        }
        private SubtitleFixture(string root,RuntimePaths paths,NamedPipeServerStream server,NamedPipeClientStream client,OwnedProcess process)
        {
            _root=root;_server=server;_reader=new(server,new UTF8Encoding(false),false,4096,true);_writer=new(server,new UTF8Encoding(false),4096,true){AutoFlush=true};
            _processExit=Process.GetProcessById(process.Id);_= _processExit.SafeHandle;
            Player=new(paths);_sessionType=typeof(MpvPlayerService).GetNestedType("Session",BindingFlags.NonPublic)!;
            _session=Activator.CreateInstance(_sessionType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[process,client],null)!;
            _sessionType.GetField("Reader")!.SetValue(_session,new StreamReader(client,new UTF8Encoding(false),false,4096,true));
            _sessionType.GetField("Writer")!.SetValue(_session,new StreamWriter(client,new UTF8Encoding(false),4096,true){AutoFlush=true});
            Set("_session",_session);Set("_host",(nint)1);SetMedia(7,true);
            _sessionType.GetField("ReadTask")!.SetValue(_session,(Task)typeof(MpvPlayerService).GetMethod("ReadMessagesAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(Player,[_session])!);
            _serverTask=Task.Run(ServeAsync);
        }
        public static async Task<SubtitleFixture> CreateAsync(string workspace)
        {
            var root=Path.GetFullPath(Path.Combine(workspace,"artifacts","subtitle-service-fixture-"+Guid.NewGuid().ToString("N")));
            var paths=new RuntimePaths(root);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.YtDlp)!);File.WriteAllText(paths.YtDlp,"must never execute");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Deno)!);File.WriteAllText(paths.Deno,"must never execute");
            var name="fkyt-subtitle-"+Guid.NewGuid().ToString("N");
            var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            var connecting=client.ConnectAsync(5000);await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));await connecting;
            var process=OwnedProcess.Start(Path.Combine(workspace,"runtime","python","python.exe"),["-B","-c","import time;time.sleep(120)"],root);
            return new(root,paths,server,client,process);
        }
        private void Set(string field,object value)=>typeof(MpvPlayerService).GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(Player,value);
        public void SetMedia(long generation,bool loaded,long? entry=null)
        {
            if(entry.HasValue)_entry=entry.Value;
            var stateLock=typeof(MpvPlayerService).GetField("_stateLock",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player)!;
            lock(stateLock)
            {
                var current=typeof(MpvPlayerService).GetField("_session",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player);
                if(current is not null)_sessionType.GetField("MediaGeneration")!.SetValue(current,generation);
                Set("_mediaGeneration",generation);Set("_snapshot",new PlaybackSnapshot{MediaGeneration=generation,Loaded=loaded,VideoUrl="https://www.youtube.com/watch?v=fixture0001"});Set("_activeEntry",(long?)_entry);
                var entries=(Dictionary<long,long>)typeof(MpvPlayerService).GetField("_entryGenerations",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player)!;entries[_entry]=generation;
                var loadedEntries=(HashSet<long>)typeof(MpvPlayerService).GetField("_loadedEntries",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Player)!;
                if(loaded)loadedEntries.Add(_entry);else loadedEntries.Remove(_entry);
            }
        }
        public int MutationsSince(int offset)
        {lock(_fixtureLock)return _commands.Skip(offset).Count(command=>command[0].GetString() is "set_property" or "sub-add" or "sub-remove");}
        public void BlockTrackRead(){TrackReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_trackRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleaseTrackRead()=>_trackRelease?.TrySetResult();
        public void BlockSubAdd(){SubAddStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_subAddRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleaseSubAdd()=>_subAddRelease?.TrySetResult();
        public void ClearNativeSubtitles(){_tracks.Clear();Sid="no";}
        public void BlockPlaylistRead(){PlaylistReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_playlistRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleasePlaylistRead()=>_playlistRelease?.TrySetResult();
        public void SetCurrentSource(string source)=>_playlist[0]=(_playlist[0].Id,source,_playlist[0].Options);
        public void DropPreparedEntry()=>_playlist.RemoveAll(item=>item.Id!=42);
        public void AdvancePreparedEntry(){_entry=_playlist[1].Id;SetMedia(7,true,_entry);}
        public void ReplaceSession(SubtitleFixture replacement)=>Set("_session",replacement._session);
        public void RestoreSession()=>Set("_session",_session);
        public async Task DisconnectIpcAsync()
        {
            _stop.Cancel();await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));_writer.Dispose();_reader.Dispose();_server.Dispose();
            _ipcDeliberatelyClosed=true;
            await ((Task)_sessionType.GetField("ReadTask")!.GetValue(_session)!).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public Task WaitForReaderAsync()=>((Task)_sessionType.GetField("ReadTask")!.GetValue(_session)!).WaitAsync(TimeSpan.FromSeconds(5));
        public Task StartExitWatcher()
        {
            var watching=(Task)typeof(MpvPlayerService).GetMethod("WatchExitAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Player,[_session])!;
            _sessionType.GetField("ExitTask")!.SetValue(_session,watching);return watching;
        }
        public void KillNativeProcess()=>NativeProcess.Kill();
        public long? ActiveEntry=>(long?)typeof(MpvPlayerService).GetField("_activeEntry",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Player);
        public void DeliverDelayedEvent(string name,string json)
        {
            using var message=JsonDocument.Parse(json);
            typeof(MpvPlayerService).GetMethod("HandleEvent",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Player,[_session,name,message.RootElement]);
        }
        public string CreateSyntheticCookie()
        {var path=Path.Combine(_root,"synthetic-cookie.txt");File.WriteAllText(path,"# Netscape HTTP Cookie File\n.fixture.invalid\tTRUE\t/\tTRUE\t0\tfixture\tnon-secret\n");return path;}
        public void BlockAppend(){AppendStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_appendRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleaseAppend()=>_appendRelease?.TrySetResult();
        public void BlockSubtitleDefaults(){SubtitleDefaultStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_subtitleDefaultRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleaseSubtitleDefaults()=>_subtitleDefaultRelease?.TrySetResult();
        public void BlockExtractionRead(){ExtractionReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_extractionRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public void ReleaseExtractionRead()=>_extractionRelease?.TrySetResult();
        public Task EndErrorAsync()=>WriteAsync(new{@event="end-file",playlist_entry_id=_entry,reason="error",error="mpv-untrusted raw URL token"});
        public Task StartFileAsync()=>WriteAsync(new{@event="start-file",playlist_entry_id=_entry});
        public Task ExtractionPropertyAsync(string leaf,object? data)=>WriteAsync(new{@event="property-change",name="user-data/mpv/ytdl/json-subprocess-result/"+leaf,data});
        public Task FileLoadedAsync()=>WriteAsync(new{ @event="file-loaded" });
        private void AutoSelect()
        {
            FixtureSubtitle? preferred=null;
            foreach(var language in Languages)
            {
                preferred=_tracks.FirstOrDefault(track=>track.Language==language || track.Language?.StartsWith(language+"-",StringComparison.Ordinal)==true);
                if(preferred!=null)break;
            }
            preferred??=_tracks.FirstOrDefault();Sid=preferred is not null?(object)preferred.Id:"no";
        }
        private async Task ServeAsync()
        {
            try
            {
                while(await _reader.ReadLineAsync(_stop.Token) is{ } line)
                {
                    using var message=JsonDocument.Parse(line);var command=message.RootElement.GetProperty("command");
                    lock(_fixtureLock)_commands.Add(command.Clone());
                    var name=command[0].GetString();object? data=null;var loaded=false;var error="success";
                    if(name=="get_property")
                    {
                        switch(command[1].GetString())
                        {
                            case "track-list":
                                if(_trackRelease is{ } release){TrackReadStarted.TrySetResult();await release.Task.WaitAsync(_stop.Token);_trackRelease=null;}
                                data=MalformedTracks?(object)new{malformed=true}:_tracks.Select(track=>
                                {
                                    var row=new Dictionary<string,object?>{{"id",track.Id},{"type","sub"},{"title",track.Title},{"lang",track.Language},{"selected",Sid is long actual && actual==track.Id}};
                                    if(NativeTrackDetail is{ } detail)row[detail.Key]=detail.Value;
                                    return row;
                                }).ToArray();break;
                            case "sid":data=SidReadback is{} sidReadback?sidReadback:Sid;break;
                            case "sub-visibility":data=VisibilityReadback is{} visibilityReadback?visibilityReadback:Visible;break;
                            case "playlist":
                                if(_playlistRelease is{ } playlistRelease){PlaylistReadStarted.TrySetResult();await playlistRelease.Task.WaitAsync(_stop.Token);_playlistRelease=null;}
                                data=MalformedPlaylist?(object)new{malformed=true}:HidePlaylist?Array.Empty<object>():_playlist.Select(item=>(object)new{id=item.Id,filename=item.Source,current=item.Id==_entry}).ToArray();if(PlaylistError || FailPlaylistAfterAppend && _appendSubmitted)error="fixture playlist failure";break;
                            case "user-data/mpv/ytdl/json-subprocess-result/status":
                                if(_extractionRelease is{ } extraction){ExtractionReadStarted.TrySetResult();await extraction.Task.WaitAsync(_stop.Token);_extractionRelease=null;}
                                data=ExtractionFailure?.GetProperty("status");break;
                            case "user-data/mpv/ytdl/json-subprocess-result/stderr":data=ExtractionFailure?.GetProperty("stderr");break;
                        }
                    }
                    if(name=="set_property")
                    {
                        if(command[1].GetString() is "slang" or "sid" or "sub-visibility" && _subtitleDefaultRelease is{ } subtitleDefaultRelease)
                        {SubtitleDefaultStarted.TrySetResult();await subtitleDefaultRelease.Task.WaitAsync(_stop.Token);_subtitleDefaultRelease=null;}
                        switch(command[1].GetString())
                        {
                            case "sid":if(command[2].ValueKind==JsonValueKind.Number)Sid=ForceWrongSid?_tracks[0].Id:command[2].GetInt64();else if(command[2].GetString()=="auto")AutoSelect();else Sid="no";break;
                            case "sub-visibility":Visible=command[2].GetBoolean();break;
                            case "slang":Languages=command[2].EnumerateArray().Select(item=>item.GetString()!).ToArray();break;
                            case "ytdl-raw-options":LastRawOptions=command[2].Clone();break;
                        }
                        if(command[1].GetString()==SubtitleDefaultError)error="fixture initial subtitle failure";
                    }
                    if(name=="sub-add")
                    {
                        if(_subAddRelease is{ } release){SubAddStarted.TrySetResult();await release.Task.WaitAsync(_stop.Token);_subAddRelease=null;}
                        var rejected=RejectSubAdd || RejectedSubAddLanguages.Contains(command[4].GetString()??"");
                        if(rejected)error=SubAddError;
                        if(!SkipSubAdd && (!rejected || AddTrackOnRejectedSubAdd))
                        {var id=_tracks.Select(track=>track.Id).DefaultIfEmpty(0L).Max()+1;_tracks.Add(new(id,command[3].GetString(),command[4].GetString()));if(command[2].GetString()=="select")Sid=id;}
                    }
                    if(name=="sub-remove")
                    {var id=command[1].GetInt64();_tracks.RemoveAll(track=>track.Id==id);if(Sid is long selected && selected==id)Sid="no";}
                    if(name=="loadfile")
                    {
                        NativePlaying=true;if(LoadError)error="fixture load reply failure";
                        var options=command[4].Clone();LastLoadOptions=options;
                        if(command[2].GetString()=="append")
                        {
                            if(_appendRelease is{ } appendRelease){AppendStarted.TrySetResult();await appendRelease.Task.WaitAsync(_stop.Token);_appendRelease=null;}
                            var nextId=_playlist.Max(item=>item.Id)+1;_playlist.Add((nextId,command[1].GetString()!,options));if(DuplicateAppend)_playlist.Add((nextId+1,command[1].GetString()!,options));_appendSubmitted=true;data=OmitAppendEntryId?null:new{playlist_entry_id=nextId};
                        }
                        else
                        {_entry++;_playlist.Clear();_playlist.Add((_entry,command[1].GetString()!,options));_tracks=NextTracks.ToList();ApplyOptions(options);loaded=true;}
                    }
                    if(name=="playlist-play-index")
                    {
                        var entry=_playlist[command[1].GetInt32()];_entry=entry.Id;_tracks=NextTracks.ToList();if(entry.Options is{ } options)ApplyOptions(options);Adoptions++;loaded=true;
                    }
                    if(name=="playlist-remove"){if(RemoveError)error="fixture remove failure";else if(!IgnorePlaylistRemove)_playlist.RemoveAt(command[1].GetInt32());}
                    if(name is "stop" or "quit")NativePlaying=false;
                    if(loaded)
                    {await WriteAsync(new{@event="start-file",playlist_entry_id=_entry});await FileLoadedAsync();}
                    if(name=="sub-add" && SuppressSubAddReply || name=="loadfile" && SuppressLoadReply)continue;
                    if(name=="set_property" && command[1].GetString()==SuppressSubtitleDefaultReply)continue;
                    if(name=="get_property" && command[1].GetString()=="playlist" && SuppressPlaylistReply || name=="playlist-remove" && SuppressRemoveReply)continue;
                    if(name=="get_property" && command[1].GetString()=="track-list" && SuppressTrackReply)continue;
                    if(name=="sub-add" && OmitSubAddReplyStatus)
                    {await WriteAsync(new{request_id=message.RootElement.GetProperty("request_id").GetInt64(),data});continue;}
                    await WriteAsync(new{request_id=message.RootElement.GetProperty("request_id").GetInt64(),error,data});
                    if(name=="quit")return;
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
            catch(IOException)when(_stop.IsCancellationRequested){}
        }
        private void ApplyOptions(JsonElement options)
        {
            if(options.TryGetProperty("slang",out var slang))Languages=slang.GetString()!.Split(',');
            if(options.TryGetProperty("sid",out var sid) && sid.GetString()=="no")Sid="no";else AutoSelect();
            if(options.TryGetProperty("sub-visibility",out var visibility))Visible=visibility.GetString()=="yes";
        }
        private async Task WriteAsync(object message)
        {await _writeGate.WaitAsync();try{await _writer.WriteLineAsync(JsonSerializer.Serialize(message));}finally{_writeGate.Release();}}
        public async ValueTask DisposeAsync()
        {
            ReleaseTrackRead();ReleaseSubAdd();ReleaseExtractionRead();ReleasePlaylistRead();ReleaseAppend();ReleaseSubtitleDefaults();await Player.DisposeAsync();_stop.Cancel();await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            await _processExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));_processExit.Dispose();_reader.Dispose();
            try{_writer.Dispose();}
            catch(Exception error)when(_ipcDeliberatelyClosed && error is IOException or ObjectDisposedException)
            { /* The EOF fixture already joined its server and closed this pipe deliberately. */ }
            _server.Dispose();_stop.Dispose();_writeGate.Dispose();
            Assert(Path.GetFileName(_root).StartsWith("subtitle-service-fixture-",StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(_root))=="artifacts","Subtitle fixture cleanup escaped its owned directory.");
            await PlaybackStateRegression.DeleteReleasedFixtureAsync(_root,"subtitle-service-fixture-");
        }
    }
    private static async Task UntilAsync(Func<bool> condition)
    {var until=Stopwatch.StartNew();while(!condition()){if(until.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("Subtitle fixture did not reach its controlled IPC boundary.");await Task.Delay(10);}}
    private static async Task ExpectAsync<T>(Func<Task> action,string message)where T:Exception
    {var rejected=false;try{await action();}catch(T){rejected=true;}Assert(rejected,message);}
    private static void ExpectInvalid(Action action,string message)
    {var rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Assert(rejected,message);}

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
