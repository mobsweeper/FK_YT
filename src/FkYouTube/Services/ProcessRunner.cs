using FkYouTube.ProcessSupport;
using System.ComponentModel;

namespace FkYouTube.Services;

public sealed record CommandResult(int ExitCode,string Output,string Error);
// Use one receipt per invocation. No started process is also safe to clean up.
public sealed class ProcessStopReceipt
{
    private readonly TaskCompletionSource _stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state; // 0 = no child started, 1 = owned consumer, 2 = stop proven
    public bool ConsumersStopped=>Volatile.Read(ref _state)!=1;
    internal Task Completion=>Volatile.Read(ref _state)==0?Task.CompletedTask:_stopped.Task;
    internal void MarkStarted()
    {
        if(Interlocked.CompareExchange(ref _state,1,0)!=0)throw new InvalidOperationException("A stop receipt belongs to one invocation.");
    }
    internal void ConfirmStopped(){Volatile.Write(ref _state,2);_stopped.TrySetResult();}
}
public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(string executable,IReadOnlyList<string> args,string root,TimeSpan timeout,CancellationToken cancellationToken=default,string? standardInput=null,ProcessStopReceipt? stopReceipt=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline=new CancellationTokenSource(timeout);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
        stopReceipt??=new ProcessStopReceipt();stopReceipt.MarkStarted();
        OwnedProcess process;
        try{process=OwnedProcess.Start(executable,args,root);}
        catch{stopReceipt.ConfirmStopped();throw;} // Start disposes its suspended child/job before throwing.
        using var registration=linked.Token.Register(()=> { try{process.Kill();}catch{} });
        var output=BoundedTextReader.ReadToEndAsync(process.StandardOutput,BoundedTextReader.CommandOutputLimit,linked.Token);
        var error=BoundedTextReader.ReadToEndAsync(process.StandardError,BoundedTextReader.CommandErrorLimit,linked.Token);
        try
        {
            if(standardInput!=null){await process.StandardInput.WriteLineAsync(standardInput.AsMemory(),linked.Token);await process.StandardInput.FlushAsync(linked.Token);process.StandardInput.Close();}
            var active=new List<Task>{output,error,process.WaitForExitAsync(linked.Token)};
            // Observe an output-limit failure while the child is still alive, so
            // finally terminates its job rather than waiting for the time limit.
            while(active.Count>0){var completed=await Task.WhenAny(active);await completed;active.Remove(completed);}
            var result=new CommandResult(process.ExitCode,await output,await error);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested)
        {throw new OperationCanceledException(cancellationToken);}
        catch(OperationCanceledException)when(deadline.IsCancellationRequested)
        {throw new IOException(UiText.T("요청 시간이 초과되었습니다. 네트워크 연결을 확인하고 다시 시도하세요.","The request timed out. Check your network connection and try again.","リクエストがタイムアウトしました。ネットワーク接続を確認してから、もう一度お試しください。"),new TimeoutException("The owned request exceeded its time limit."));}
        finally
        {
            // DisposeAsync also waits for a callback that already entered Kill.
            // No cancellation callback may retain this owner past its final close.
            await registration.DisposeAsync().ConfigureAwait(false);
            // A canceled wait is not proof that Windows released inherited pipe
            // and cookie handles. Finish this owned job before its lease is disposed.
            try
            {
                await CompleteOwnedProcessAsync(process,[output,error],stopReceipt);
            }
            catch(Exception ex)when(cancellationToken.IsCancellationRequested && IsStopFailure(ex)){}
        }
    }
    private static bool IsStopFailure(Exception error)=>error is IOException or Win32Exception or InvalidOperationException or ObjectDisposedException;
    private static readonly PendingCleanupTracker Recoveries=new();
    // Call after request producers have stopped. A timeout leaves each real
    // process/job and receipt with its observer for the next drain attempt.
    public static Task DrainRecoveryAsync()=>DrainRecoveryWithinAsync(TimeSpan.FromSeconds(5));
    internal static Task DrainRecoveryWithinAsync(TimeSpan timeout)=>Recoveries.DrainAsync(timeout,
        "Some owned request processes are still waiting for confirmed exit.");
    internal static async Task CompleteOwnedProcessAsync(OwnedProcess process,IReadOnlyList<Task> readers,ProcessStopReceipt receipt,Func<Task>? stopAndDrain=null)
    {
        try
        {
            await (stopAndDrain?.Invoke()??StopAndDrainAsync(process,readers)).ConfigureAwait(false);
            if(!process.HasConfirmedTreeExit())throw new IOException("The owned process has no confirmed stop receipt.");
            receipt.ConfirmStopped();process.Dispose();
        }
        catch(Exception ex)when(IsStopFailure(ex))
        {
            // Transfer the actual process and still-open Job handle. Closing a
            // kill-on-close job is a termination request, not proof of exit.
            // This task owns late confirmation even after the caller unwinds.
            Recoveries.Track(RecoverOwnedProcessAsync(process,readers,receipt,stopAndDrain));
            throw;
        }
    }
    private static async Task RecoverOwnedProcessAsync(OwnedProcess process,IReadOnlyList<Task> readers,ProcessStopReceipt receipt,Func<Task>? stopAndDrain)
    {
        while(true)
        {
            try
            {
                if(stopAndDrain is not null)await stopAndDrain().ConfigureAwait(false);
                else
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                if(process.HasConfirmedTreeExit())
                {
                    receipt.ConfirmStopped();
                    // Observe any reader failures without waiting indefinitely:
                    // root exit plus an empty owned job is the cookie boundary.
                    foreach(var reader in readers)ObserveReader(reader);
                    process.Dispose();return;
                }
            }
            catch(Exception ex)when(IsStopFailure(ex) || ex is TimeoutException or OperationCanceledException){}
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
    private static void ObserveReader(Task reader)=>
        _=reader.ContinueWith(completed=>{_=completed.Exception;},CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    internal static async Task StopAndDrainAsync(OwnedProcess process,IReadOnlyList<Task> readers)
    {
        process.Kill();
        try{await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));}
        catch(TimeoutException ex){throw new IOException(UiText.T("요청 프로세스를 정리하지 못했습니다. 잠시 후 다시 시도해 주세요.","Could not clean up the request process. Try again later.","リクエストプロセスをクリーンアップできませんでした。しばらくしてからお試しください。"),ex);}
        var deadline=Environment.TickCount64+5000;
        while(process.GetProcessIds().Count>0)
        {
            process.Kill();
            if(Environment.TickCount64>=deadline)throw new IOException(UiText.T("요청 프로세스를 정리하지 못했습니다. 잠시 후 다시 시도해 주세요.","Could not clean up the request process. Try again later.","リクエストプロセスをクリーンアップできませんでした。しばらくしてからお試しください。"));
            await Task.Delay(50);
        }
        try{await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));}
        catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}
        catch(TimeoutException ex){throw new IOException(UiText.T("요청 출력을 정리하지 못했습니다. 잠시 후 다시 시도해 주세요.","Could not clean up the request output. Try again later.","リクエストの出力をクリーンアップできませんでした。しばらくしてからお試しください。"),ex);}
    }
    public static bool IsErrorCode(string code)=>code is "signin_age" or "bot" or "member" or "private" or "unavailable" or "rate_limit" or "network" or "session" or "generic";
    public static string ClassifyError(string error)
    {
        // The selected adapter emits only this allowlist. Native yt-dlp stderr is
        // examined in memory; none of its URLs, identities or cookie text is returned.
        var markers=error.Split('\n').Select(line=>line.Trim()).Where(line=>line.StartsWith("FK_YT_ERROR:",StringComparison.Ordinal)).Select(line=>line[12..]).Distinct().ToArray();
        if(markers.Length>0)return markers.Length==1 && IsErrorCode(markers[0])?markers[0]:"generic";
        bool Has(params string[] terms)=>terms.Any(term=>error.Contains(term,StringComparison.OrdinalIgnoreCase));
        if(Has("not a bot","not-a-bot","bot detection","captcha challenge","confirm you're human","confirm you’re human"))return "bot";
        if(Has("429","too many requests","rate limit","rate-limit"))return "rate_limit";
        if(Has("members-only","members only","join this channel","available to channel members","membership required"))return "member";
        if(Has("private video","video is private","this video is private"))return "private";
        if(Has("drm protected","drm-protected"))return "generic";
        if(Has("login_required","sign in","log in","login required","age-restricted","age restricted","confirm your age","age verification"))return "signin_age";
        if(Has("cookies are no longer valid","cookies have expired","cookie file is missing","invalid account request"))return "session";
        if(Has("timed out","timeout","unable to resolve","name resolution","getaddrinfo","network","connection reset","connection refused","certificate verify failed"))return "network";
        if(Has("video unavailable","video is unavailable","video has been removed","video has been deleted","not available in your country","copyright"))return "unavailable";
        return "generic";
    }
    public static string ErrorMessage(string code,bool authenticated=false)=>code switch
    {
        "signin_age"=>authenticated?UiText.T("YouTube 로그인·나이 확인이 필요한 영상입니다. 연결한 프로필에서 인증을 완료한 뒤 세션을 다시 연결하세요.","This video requires YouTube sign-in or age verification. Complete verification in the connected profile, then reconnect the session.","この動画には YouTube へのログインまたは年齢確認が必要です。接続したプロファイルで認証を完了してから、セッションを再接続してください。"):UiText.T("YouTube 로그인·나이 확인이 필요한 영상입니다. 일반 모드에서 인증된 계정을 연결하세요.","This video requires YouTube sign-in or age verification. Connect a verified account in normal mode.","この動画には YouTube へのログインまたは年齢確認が必要です。通常モードで認証済みのアカウントを接続してください。"),
        "bot"=>UiText.T("YouTube에서 자동 요청 확인을 요구합니다. 연결한 프로필에서 확인을 완료한 뒤 세션을 다시 연결하세요.","YouTube requires an automated request check. Complete the check in the connected profile, then reconnect the session.","YouTube が自動リクエストの確認を求めています。接続したプロファイルで確認を完了してから、セッションを再接続してください。"),
        "member"=>UiText.T("채널 멤버십이 필요한 영상입니다. 선택한 계정의 이용 권한을 확인하세요.","This video requires a channel membership. Check access permissions for the selected account.","この動画にはチャンネルメンバーシップが必要です。選択したアカウントの利用権限を確認してください。"),
        "private"=>UiText.T("비공개 영상입니다. 선택한 계정의 이용 권한을 확인하세요.","This video is private. Check access permissions for the selected account.","この動画は非公開です。選択したアカウントの利用権限を確認してください。"),
        "unavailable"=>UiText.T("영상이 삭제되었거나 현재 사용할 수 없습니다.","The video has been deleted or is currently unavailable.","動画が削除されたか、現在利用できません。"),
        "rate_limit"=>UiText.T("YouTube 요청 제한입니다. 잠시 후 다시 시도하세요.","YouTube requests are limited. Try again later.","YouTube のリクエストが制限されています。しばらくしてからお試しください。"),
        "network"=>UiText.T("네트워크 연결을 확인하고 다시 시도하세요.","Check your network connection and try again.","ネットワーク接続を確認してから、もう一度お試しください。"),
        "session"=>UiText.T("로그인 연결 갱신이 필요합니다. 설정에서 세션을 다시 연결하세요.","Your sign-in connection needs refreshing. Reconnect the session in settings.","ログイン接続の更新が必要です。設定からセッションを再接続してください。"),
        _=>UiText.T("YouTube에서 정보를 가져오지 못했습니다. 연결 상태와 도구 버전을 확인하세요.","Could not retrieve information from YouTube. Check your connection and tool versions.","YouTube から情報を取得できませんでした。接続状態とツールのバージョンを確認してください。")
    };
    public static string SafeError(string error,bool authenticated=false)=>ErrorMessage(ClassifyError(error),authenticated);
}

// Own cleanup tasks independently of their original caller. Successes retire
// themselves; failures are observed immediately and reported by the next drain.
// A bounded drain never cancels or detaches a pending cleanup.
internal sealed class PendingCleanupTracker
{
    private readonly object _sync=new();
    private readonly HashSet<Task> _pending=[];
    internal void Track(Task cleanup)
    {
        lock(_sync)_pending.Add(cleanup);
        _=cleanup.ContinueWith(completed=>
        {
            _=completed.Exception;
            if(completed.IsCompletedSuccessfully)lock(_sync)_pending.Remove(completed);
        },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    internal async Task DrainAsync(TimeSpan timeout,string message)
    {
        if(timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline=Environment.TickCount64+checked((long)timeout.TotalMilliseconds);
        while(true)
        {
            Task[] snapshot;lock(_sync)snapshot=_pending.ToArray();
            if(snapshot.Length==0)return;
            var combined=Task.WhenAll(snapshot);
            // WaitAsync may time out before WhenAll faults. Observe that later
            // aggregate too; the original tasks remain in this tracker's set.
            _=combined.ContinueWith(completed=>{_=completed.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            try
            {
                var remaining=deadline-Environment.TickCount64;
                if(remaining<=0 && !combined.IsCompleted)throw new TimeoutException();
                await combined.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,remaining))).ConfigureAwait(false);
            }
            catch(TimeoutException error){throw new IOException(message,error);}
            catch
            {
                var failures=snapshot.Where(task=>task.IsFaulted).SelectMany(task=>task.Exception!.Flatten().InnerExceptions).ToArray();
                lock(_sync)foreach(var completed in snapshot.Where(task=>task.IsCompleted))_pending.Remove(completed);
                throw new IOException(message,failures.Length==0?null:new AggregateException(failures));
            }
            lock(_sync)foreach(var completed in snapshot)_pending.Remove(completed);
        }
    }
}
