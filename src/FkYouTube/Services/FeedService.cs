using System.Text.Json;
using System.Windows.Threading;
using FkYouTube.ProcessSupport;
using System.ComponentModel;

namespace FkYouTube.Services;

public sealed record FeedBatch(bool HasMore,int Count,string? Warning=null);
public sealed class FeedService : IAsyncDisposable
{
    private const int BatchSize=90;
    private readonly RuntimePaths _paths;
    private OwnedProcess? _process;
    private CookieLease? _lease;
    private readonly SemaphoreSlim _gate=new(1,1);
    private Task? _drain;
    private BoundedLineReader? _output;
    private readonly object _cleanupSync=new();
    private readonly HashSet<Task> _cleanupTasks=[];
    public FeedService(RuntimePaths paths)=>_paths=paths;
    public async Task<FeedBatch> ReadAsync(bool open,string source,int generation,CookieLease? lease,Action<VideoItem> onItem,CancellationToken ct,bool hideMembersOnly=true,YouTubeChannel? accountContext=null,
        Action<FeedNavigationMetadata>? onMetadata=null,string? filterId=null,int? previousGeneration=null)
    {
        var entered=false;
        try
        {
            await _gate.WaitAsync(ct);entered=true;
            if((!open || filterId!=null) && (_process==null || _process.HasExited))throw new IOException(UiText.T("목록 연결이 종료되었습니다. 새로고침으로 다시 조회하세요.","The list connection has ended. Refresh to load it again.","一覧の接続が終了しました。更新してもう一度取得してください。"));
            if(open && filterId==null)
            {
                Stop();
                lock(_cleanupSync)
                {
                    _lease=lease;lease=null;
                    _process=OwnedProcess.Start(_paths.Python,_paths.BuildPythonArguments(_paths.FeedWorker,unbuffered:true),_paths.Root);
                    _drain=DrainAsync(_process.StandardError);
                }
                open=true;
            }
            var process=_process!;var id=Guid.NewGuid().ToString("N");
            var output=_output??=new BoundedLineReader(process.StandardOutput,BoundedTextReader.FeedLineLimit);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var cancel=timeout.Token.Register(()=>{try{process.Kill();}catch{}});
            var request=new {command=filterId!=null?"feed.select":open?"feed.open":"feed.next",requestId=id,generation,source,batchSize=BatchSize,cookiePath=_lease?.Path,hideMembersOnly,accountContext,filterId,previousGeneration};
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,JsonDefaults.Options).AsMemory(),timeout.Token);await process.StandardInput.FlushAsync(timeout.Token);
            var count=0;
            while(true)
            {
                var line=await output.ReadLineAsync(timeout.Token);
                if(line==null)throw new IOException(UiText.T("목록 작업자가 종료되었습니다. 새로고침으로 다시 연결하세요.","The list worker has exited. Refresh to reconnect.","一覧のワーカーが終了しました。更新して再接続してください。"));
                using var doc=ParseMessage(line);var r=doc.RootElement;
                if(RequiredString(r,"requestId")!=id)continue;
                if(r.TryGetProperty("generation",out var gen))
                {if(gen.ValueKind!=JsonValueKind.Number || !gen.TryGetInt32(out var value))throw new JsonException("Invalid feed generation.");if(value!=generation)continue;}
                switch(RequiredString(r,"type"))
                {
                    case "metadata":
                        if(r.TryGetProperty("metadata",out var metadata))
                        {if(metadata.ValueKind!=JsonValueKind.Object)throw new JsonException("Invalid feed metadata.");if(metadata.Deserialize<FeedNavigationMetadata>(JsonDefaults.Options) is {} navigation)onMetadata?.Invoke(navigation);}
                        break;
                    case "item":
                        if(count>=BatchSize)throw new IOException(UiText.T("목록 작업자가 요청한 항목 수를 초과했습니다. 새로고침으로 다시 연결하세요.","The list worker returned more items than requested. Refresh to reconnect.","一覧のワーカーが要求数を超える項目を返しました。更新して再接続してください。"));
                        if(!r.TryGetProperty("item",out var data) || data.ValueKind!=JsonValueKind.Object)throw new JsonException("Invalid feed item.");
                        var item=data.Deserialize<VideoItem>(JsonDefaults.Options);if(item!=null){onItem(item);count++;if(count%12==0){if(SynchronizationContext.Current is DispatcherSynchronizationContext)await Dispatcher.Yield(DispatcherPriority.Background);else await Task.Yield();timeout.Token.ThrowIfCancellationRequested();}}break;
                    case "done":
                        var warning=r.TryGetProperty("warning",out var w) && w.ValueKind==JsonValueKind.Object && w.TryGetProperty("message",out var wm) && wm.ValueKind==JsonValueKind.String?LocalizeWorkerMessage(wm.GetString()!):null;
                        if(r.TryGetProperty("hasMore",out var more) && more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new JsonException("Invalid feed completion.");
                        return new(more.ValueKind==JsonValueKind.True,count,warning);
                    case "error":throw new IOException(OptionalMessage(r,UiText.T("목록을 불러오지 못했습니다.","Could not load the list.","一覧を読み込めませんでした。")));
                }
            }
        }
        catch(JsonException ex){if(entered)Stop();throw new IOException(UiText.T("목록 작업자 응답이 올바르지 않습니다. 새로고침으로 다시 연결하세요.","The list worker response is invalid. Refresh to reconnect.","一覧のワーカーの応答が正しくありません。更新して再接続してください。"),ex);}
        catch {if(entered)Stop();throw;}
        finally
        {
            if(lease!=null)lock(_cleanupSync)QueueCleanupLocked(null,lease,null,false);
            if(entered)_gate.Release();
        }
    }
    private static async Task DrainAsync(StreamReader reader){try{await BoundedTextReader.DrainAsync(reader,CancellationToken.None);}catch(IOException){}catch(ObjectDisposedException){}}
    public async Task RecommendAsync(int generation,VideoItem video,string action,string handle,YouTubeChannel? accountContext,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var process=_process;if(process==null || process.HasExited)throw new IOException(UiText.T("목록 연결이 끝났습니다. 새로고침 후 다시 시도하세요.","The list connection has ended. Refresh and try again.","一覧の接続が終了しました。更新してから、もう一度お試しください。"));
            var output=_output??=new BoundedLineReader(process.StandardOutput,BoundedTextReader.FeedLineLimit);
            var id=Guid.NewGuid().ToString("N");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var cancellation=timeout.Token.Register(()=>{try{process.Kill();}catch{}});
            var request=new{command="recommendation.action",requestId=id,generation,action,actionHandle=handle,videoId=video.Id,accountContext};
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,JsonDefaults.Options).AsMemory(),timeout.Token);await process.StandardInput.FlushAsync(timeout.Token);
            while(true)
            {
                var line=await output.ReadLineAsync(timeout.Token);if(line==null)throw new IOException(UiText.T("추천 설정 연결이 종료되었습니다. 새로고침 후 다시 시도하세요.","The recommendation settings connection has ended. Refresh and try again.","おすすめ設定の接続が終了しました。更新してから、もう一度お試しください。"));
                using var doc=ParseMessage(line);var root=doc.RootElement;
                if(RequiredString(root,"requestId")!=id)continue;
                if(RequiredString(root,"type")=="feedback.result" && root.TryGetProperty("success",out var success) && success.ValueKind==JsonValueKind.True)return;
                throw new IOException(OptionalMessage(root,UiText.T("추천 설정을 적용하지 못했습니다.","Could not apply the recommendation settings.","おすすめ設定を適用できませんでした。")));
            }
        }
        catch(JsonException ex){Stop();throw new IOException(UiText.T("추천 설정 작업자 응답이 올바르지 않습니다. 새로고침 후 다시 시도하세요.","The recommendation settings worker response is invalid. Refresh and try again.","おすすめ設定のワーカーの応答が正しくありません。更新してから、もう一度お試しください。"),ex);}
        catch{Stop();throw;}
        finally{_gate.Release();}
    }
    private static JsonDocument ParseMessage(string line)
    {
        var document=JsonDocument.Parse(line);
        if(document.RootElement.ValueKind==JsonValueKind.Object)return document;
        document.Dispose();throw new JsonException("A feed message must be an object.");
    }
    private static string RequiredString(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var value) || value.ValueKind!=JsonValueKind.String)throw new JsonException("Invalid feed message field: "+name);
        return value.GetString()!;
    }
    private static string OptionalMessage(JsonElement root,string fallback)=>root.TryGetProperty("message",out _)?LocalizeWorkerMessage(RequiredString(root,"message")):fallback;
    private static string LocalizeWorkerMessage(string message)
    {
        // Translate only the bundled worker's fixed app notices at the display
        // boundary. Request keys, account context and server content stay intact.
        const string retainedSuffix=" 받은 항목은 유지했습니다. 새로고침으로 다시 조회하세요.";
        var retained=message.EndsWith(retainedSuffix,StringComparison.Ordinal);
        var original=retained?message[..^retainedSuffix.Length]:message;
        var localized=original switch
        {
            "로그인 연결 갱신 필요: Firefox 또는 쿠키 파일을 다시 연결하세요."=>UiText.T("로그인 연결 갱신 필요: Firefox 또는 쿠키 파일을 다시 연결하세요.","Sign-in connection needs refreshing: reconnect Firefox or the cookie file.","ログイン接続の更新が必要です。Firefox または Cookie ファイルを再接続してください。"),
            "목록을 불러오는 중 연결에 실패했습니다. 다시 시도해 주세요."=>UiText.T("목록을 불러오는 중 연결에 실패했습니다. 다시 시도해 주세요.","The connection failed while loading the list. Try again.","一覧の読み込み中に接続に失敗しました。もう一度お試しください。"),
            "이 주소 또는 목록 형식은 지원하지 않습니다."=>UiText.T("이 주소 또는 목록 형식은 지원하지 않습니다.","This URL or list format is not supported.","この URL または一覧の形式はサポートされていません。"),
            "목록을 사용할 수 없습니다. 새로고침해 주세요."=>UiText.T("목록을 사용할 수 없습니다. 새로고침해 주세요.","The list is unavailable. Refresh it.","一覧を利用できません。更新してください。"),
            "목록 작업자를 사용할 수 없습니다. 다시 시작해 주세요."=>UiText.T("목록 작업자를 사용할 수 없습니다. 다시 시작해 주세요.","The list worker is unavailable. Restart the app.","一覧のワーカーを利用できません。アプリを再起動してください。"),
            "YouTube 추천 설정을 적용하지 못했습니다. 목록을 새로고침한 뒤 다시 시도하세요."=>UiText.T("YouTube 추천 설정을 적용하지 못했습니다. 목록을 새로고침한 뒤 다시 시도하세요.","Could not apply the YouTube recommendation settings. Refresh the list and try again.","YouTube のおすすめ設定を適用できませんでした。一覧を更新してから、もう一度お試しください。"),
            _=>null
        };
        return localized is null?message:localized+(retained?UiText.T(retainedSuffix," Received items were kept. Refresh to load the list again."," 取得済みの項目は保持しました。更新してもう一度取得してください。"):"");
    }
    public void Stop()
    {
        lock(_cleanupSync)
        {
            var process=_process;var lease=_lease;var drain=_drain;
            _process=null;_lease=null;_output=null;_drain=null;
            if(process==null && lease==null && drain==null)return;
            var killFailed=false;
            try{process?.Kill();}
            catch(Exception ex)when(ex is Win32Exception or InvalidOperationException or ObjectDisposedException){killFailed=true;}
            // Each cleanup owns the detached generation only. Stop stays cheap
            // for synchronous UI call sites, while shutdown can await all work.
            QueueCleanupLocked(process,lease,drain,killFailed);
        }
    }
    private void QueueCleanupLocked(OwnedProcess? process,CookieLease? lease,Task? drain,bool killFailed)
    {
        var cleanup=Task.Run(()=>CleanupAsync(process,lease,drain,killFailed));
        _cleanupTasks.Add(cleanup);
        _=cleanup.ContinueWith(completed=>
        {
            _=completed.Exception; // Observe now; retain failures until the explicit drain.
            lock(_cleanupSync)if(completed.IsCompletedSuccessfully)_cleanupTasks.Remove(completed);
        },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    private static async Task CleanupAsync(OwnedProcess? process,CookieLease? lease,Task? drain,bool killFailed,Func<Task>? stopAndDrain=null)
    {
        var errors=new List<Exception>();
        ProcessStopReceipt? receipt=null;
        if(killFailed)errors.Add(new IOException(UiText.T("목록 작업자 종료 요청에 실패했습니다.","Could not request that the list worker stop.","一覧のワーカーの終了を要求できませんでした。")));
        if(process!=null)
        {
            receipt=new ProcessStopReceipt();lease?.AttachConsumer(receipt);receipt.MarkStarted();
            try
            {
                // The detached feed keeps its existing two-second stop bound.
                // On failure, transfer the still-open Job and its lease receipt
                // to the shared observer instead of disposing on an assumption.
                await ProcessRunner.CompleteOwnedProcessAsync(process,[],receipt,
                    stopAndDrain??(()=>StopFeedProcessAsync(process))).ConfigureAwait(false);
            }
            catch(IOException error){errors.Add(error);}
            catch(Exception ex)when(ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
            {errors.Add(new IOException(UiText.T("목록 작업자 종료를 확인하지 못했습니다.","Could not verify that the list worker stopped.","一覧のワーカーの終了を確認できませんでした。")));}
        }
        if(drain!=null)
        {
            try{await drain.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);}
            catch(TimeoutException){errors.Add(new IOException(UiText.T("목록 작업자 출력 정리 시간이 초과되었습니다.","Cleaning up the list worker output timed out.","一覧のワーカーの出力のクリーンアップがタイムアウトしました。")));}
            catch(Exception)
            {errors.Add(new IOException(UiText.T("목록 작업자 출력을 정리하지 못했습니다.","Could not clean up the list worker output.","一覧のワーカーの出力をクリーンアップできませんでした。")));}
        }
        if(lease!=null)
        {
            try
            {
                if(receipt is {ConsumersStopped:false})lease.Dispose();
                else await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch(IOException error){errors.Add(error);}
        }
        if(errors.Count>0)throw new AggregateException(errors);
    }
    private static async Task StopFeedProcessAsync(OwnedProcess process)
    {
        process.Kill();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            while(process.GetProcessIds().Count>0)await Task.Delay(20,timeout.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException)
        {throw new IOException(UiText.T("목록 작업자가 제한 시간 안에 종료되지 않았습니다.","The list worker did not stop within the time limit.","一覧のワーカーが制限時間内に終了しませんでした。"));}
    }
    public async Task DrainPendingAsync()
    {
        var errors=new List<Exception>();
        while(true)
        {
            Task[] pending;lock(_cleanupSync)pending=_cleanupTasks.ToArray();
            if(pending.Length==0)break;
            try{await Task.WhenAll(pending).ConfigureAwait(false);}
            catch
            {
                foreach(var task in pending)if(task.Exception is {} error)errors.AddRange(error.Flatten().InnerExceptions);
            }
            finally{lock(_cleanupSync)foreach(var task in pending)_cleanupTasks.Remove(task);}
        }
        if(errors.Count>0)throw new AggregateException(UiText.T("목록 연결 정리 중 문제가 발생했습니다.","A problem occurred while cleaning up the list connection.","一覧の接続のクリーンアップ中に問題が発生しました。"),errors);
    }
    public async Task StopAndDrainAsync()
    {
        // Account transitions cancel/disable new reads before this barrier. An
        // in-flight read may still transfer its lease in finally; join its gate
        // before taking the cleanup snapshot, then wait without holding the gate.
        Stop();
        if(!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            throw new IOException(UiText.T("이전 목록 조회가 종료되지 않아 계정 전환을 중단했습니다. 잠시 후 다시 시도하세요.","Account switching was stopped because the previous list request has not ended. Try again later.","前の一覧取得が終了していないため、アカウントの切り替えを中止しました。しばらくしてからお試しください。"));
        try{Stop();}finally{_gate.Release();}
        try{await DrainPendingAsync().ConfigureAwait(false);}
        catch(AggregateException error)
        {throw new IOException(UiText.T("이전 목록 연결을 정리하지 못해 계정 전환을 중단했습니다. 잠시 후 다시 시도하세요.","Account switching was stopped because the previous list connection could not be cleaned up. Try again later.","前の一覧の接続をクリーンアップできなかったため、アカウントの切り替えを中止しました。しばらくしてからお試しください。"),error);}
    }
    public async ValueTask DisposeAsync(){Stop();await DrainPendingAsync().ConfigureAwait(false);}
}
