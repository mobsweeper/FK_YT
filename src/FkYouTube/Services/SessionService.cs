using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FkYouTube.Services;

public sealed record FirefoxProfile(string Name,string Path) { public override string ToString()=>Name; }
public sealed class CookieLease : IDisposable, IAsyncDisposable
{
    private readonly object _cleanupSync=new();
    private readonly Action<Task>? _trackCleanup;
    private Task? _cleanup;
    private readonly List<ProcessStopReceipt> _consumers=[];
    public string? Path { get; }
    public CookieLease(string? path)=>Path=path;
    internal CookieLease(string? path,Action<Task> trackCleanup){Path=path;_trackCleanup=trackCleanup;}
    public void AttachConsumer(ProcessStopReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock(_cleanupSync)
        {
            if(_cleanup is not null)throw new ObjectDisposedException(nameof(CookieLease));
            if(!_consumers.Contains(receipt))_consumers.Add(receipt);
        }
    }
    public void Dispose()
    {
        Task? pending=null;
        lock(_cleanupSync)
        {
            if(_cleanup is not null)return;
            if(_consumers.Any(receipt=>!receipt.ConsumersStopped))
                pending=_cleanup=DeleteAfterConsumersAsync();
            else try { if(Path is not null)DeleteOwnedFile();_cleanup=Task.CompletedTask; }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {
                // Production leases transfer retry ownership to SessionService.
                // The original operation never waits synchronously or loses its
                // own exception to a temporary cleanup failure.
                if(_trackCleanup is not null)pending=_cleanup=DeleteWithRetryAsync();
            }
        }
        if(pending is not null)_trackCleanup?.Invoke(pending);
    }
    public ValueTask DisposeAsync()
    {
        Task cleanup;bool track;
        lock(_cleanupSync)
        {
            track=_cleanup is null;
            cleanup=_cleanup??=DeleteAfterConsumersAsync();
        }
        if(track)_trackCleanup?.Invoke(cleanup);
        return new(cleanup);
    }
    private async Task DeleteAfterConsumersAsync()
    {
        await Task.WhenAll(_consumers.Select(receipt=>receipt.Completion)).ConfigureAwait(false);
        await DeleteWithRetryAsync().ConfigureAwait(false);
    }
    private void DeleteOwnedFile()
    {
        FileBoundary.EnsureRegularPath(System.IO.Path.GetDirectoryName(Path!)!);
        File.Delete(Path!);
    }
    private async Task DeleteWithRetryAsync()
    {
        if(Path==null)return;
        int[] delays=[50,100,200];
        for(var attempt=0;;attempt++)
        {
            try{DeleteOwnedFile();return;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {
                if(attempt==delays.Length)throw new IOException(UiText.T("임시 로그인 파일을 정리하지 못했습니다.","Could not remove a temporary sign-in file.","一時ログインファイルを削除できませんでした。"));
            }
            await Task.Delay(delays[attempt]).ConfigureAwait(false);
        }
    }
}
public sealed partial class SessionService
{
    private const int MaximumEncryptedSessionBytes=16*1024*1024;
    private readonly RuntimePaths _paths;
    private SessionMutation? _activeMutation;
    private string RecoveryMarkerPath=>Path.Combine(_paths.Data,"session-recovery-required");
    public bool HasSessionRecoveryFailure {get;private set;}
    private static string SessionRecoveryMessage=>UiText.T("이전 로그인 변경을 복구하지 못해 세션 사용을 중지했습니다. Firefox 연결 또는 쿠키 가져오기로 새 로그인 연결을 확정하세요.","Session access was stopped because the previous sign-in change could not be recovered. Confirm a new sign-in connection through Firefox or cookie import.","前回のログイン変更を復元できなかったため、セッションの使用を停止しました。Firefox 接続または Cookie のインポートで、新しいログイン接続を確定してください。");
    private void EnsureSessionUsable(bool recoveryWrite=false)
    {
        if(HasSessionRecoveryFailure && !(recoveryWrite && _activeMutation?.AllowsRecovery==true))
            throw new IOException(SessionRecoveryMessage);
    }
    private void MarkSessionRecoveryFailure(){HasSessionRecoveryFailure=true;ForgetSelectedChannel();}
    public SessionMutation BeginMutation(bool allowRecovery=false)
    {
        if(_activeMutation is not null)throw new InvalidOperationException(UiText.T("로그인 변경이 이미 진행 중입니다.","A sign-in change is already in progress.","ログインの変更がすでに進行中です。"));
        FileBoundary.EnsureRegularPath(RecoveryMarkerPath);
        if(File.Exists(RecoveryMarkerPath))MarkSessionRecoveryFailure();
        if(HasSessionRecoveryFailure && !allowRecovery)throw new IOException(SessionRecoveryMessage);
        // Publish the barrier before changing any encrypted session. An unfinished
        // operation must remain blocked even if the process exits before rollback.
        if(!File.Exists(RecoveryMarkerPath))
        {
            try
            {
                using var marker=new FileStream(RecoveryMarkerPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                marker.Write("FKYT session change in progress\n"u8);marker.Flush(true);
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {
                if(File.Exists(RecoveryMarkerPath))MarkSessionRecoveryFailure();
                throw;
            }
        }
        var mutation=new SessionMutation(this,HasSessionRecoveryFailure);
        _activeMutation=mutation;
        if(mutation.AllowsRecovery)ForgetSelectedChannel();
        return mutation;
    }
    public sealed class SessionMutation : IDisposable
    {
        private readonly SessionService _owner;
        private readonly Dictionary<string,byte[]?> _originals=new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _changed=new(StringComparer.OrdinalIgnoreCase);
        private readonly string _selectedId;
        private readonly YouTubeChannel? _selected;
        private bool _finished;
        internal bool AllowsRecovery {get;}
        public bool IsCommitted {get;private set;}
        internal SessionMutation(SessionService owner,bool recovery)
        {_owner=owner;AllowsRecovery=recovery;_selectedId=owner._selectedChannelId;_selected=owner.SelectedChannel;}
        internal void Capture(string path)
        {
            if(_originals.ContainsKey(path))return;
            FileBoundary.EnsureRegularPath(path);
            _originals.Add(path,File.Exists(path)?FileBoundary.ReadBoundedFile(path,MaximumEncryptedSessionBytes):null);
        }
        internal void Changed(string path)=>_changed.Add(path);
        public void Commit()
        {
            if(_finished){if(IsCommitted)return;throw new ObjectDisposedException(nameof(SessionMutation));}
            if(AllowsRecovery && (!_changed.Contains(_owner.SecretPath) || !File.Exists(_owner.SecretPath)))
                throw new IOException(UiText.T("새 로그인 세션이 준비되지 않아 복구 상태를 유지했습니다.","Recovery is still required because the new sign-in session is not ready.","新しいログインセッションが準備できていないため、復旧が必要な状態を維持しました。"));
            // The caller has already persisted settings. Marker cleanup failure
            // cannot roll that commit back to a different session/settings pair.
            IsCommitted=true;
            try
            {
                DeleteSessionFile(_owner.RecoveryMarkerPath);
                _owner.HasSessionRecoveryFailure=false;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {_owner.MarkSessionRecoveryFailure();throw new IOException(SessionRecoveryMessage);}
            finally{Finish();}
        }
        public void Dispose()
        {
            if(_finished)return;
            var failed=false;
            try
            {
                foreach(var path in _changed.Reverse())
                {
                    try
                    {
                        if(_originals[path] is {} encrypted)WriteOwnedEncryptedFile(path,encrypted);
                        else DeleteSessionFile(path);
                    }
                    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failed=true;}
                }
                if(!failed && !AllowsRecovery)
                {
                    try{DeleteSessionFile(_owner.RecoveryMarkerPath);}
                    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failed=true;}
                }
                if(failed || AllowsRecovery)_owner.MarkSessionRecoveryFailure();
                else{_owner._selectedChannelId=_selectedId;_owner.SelectedChannel=_selected;}
            }
            finally{Finish();}
            if(failed)throw new IOException(SessionRecoveryMessage);
        }
        private void Finish()
        {
            _finished=true;
            if(ReferenceEquals(_owner._activeMutation,this))_owner._activeMutation=null;
            foreach(var bytes in _originals.Values)if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);
            _originals.Clear();_changed.Clear();
        }
    }
    private readonly object _cookieCleanupSync=new();
    private readonly List<Task> _cookieCleanups=[];
    private CookieLease CreateCookieLease(string? path)=>new(path,TrackCookieCleanup);
    private CookieLease CreateCookieFile(Action<Stream> write)
    {
        var directory=Path.Combine(_paths.Data,"temp");
        FileBoundary.EnsureRegularPath(directory);
        var path=Path.Combine(directory,"cookies-"+Guid.NewGuid().ToString("N")+".txt");
        var lease=CreateCookieLease(path);var created=false;
        try
        {
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {created=true;write(stream);}
            return lease;
        }
        catch
        {
            // The using scope has closed the writer before cleanup/retry starts.
            // A failed CreateNew never transfers ownership of an existing file.
            if(created)lease.Dispose();
            throw;
        }
    }
    private void StoreEncryptedFile(string path,byte[] encrypted)
    {
        EnsureSessionUsable(recoveryWrite:true);
        _activeMutation?.Capture(path);
        WriteOwnedEncryptedFile(path,encrypted);
        _activeMutation?.Changed(path);
    }
    private static void WriteOwnedEncryptedFile(string path,byte[] encrypted)
    {
        FileBoundary.EnsureRegularPath(path);
        var directory=Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        FileBoundary.EnsureRegularPath(path);
        var temporary=Path.Combine(directory,Path.GetFileNameWithoutExtension(path)+"-"+Guid.NewGuid().ToString("N")+".new");
        var created=false;
        try
        {
            FileBoundary.EnsureRegularPath(temporary);
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {created=true;stream.Write(encrypted);stream.Flush(true);}
            RetrySessionStorage(()=>
            {
                FileBoundary.EnsureRegularPath(path);FileBoundary.EnsureRegularPath(temporary);
                File.Move(temporary,path,true);
            });created=false;
        }
        finally
        {
            if(created)try{FileBoundary.EnsureRegularPath(temporary);File.Delete(temporary);}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
    }
    private static void DeleteSessionFile(string path)=>RetrySessionStorage(()=>{FileBoundary.EnsureRegularPath(path);File.Delete(path);});
    private static void RetrySessionStorage(Action operation)
    {
        int[] delays=[25,50,100];
        for(var attempt=0;;attempt++)
        {
            try{operation();return;}
            // Windows overwrite/rename can surface a sharing lock as access
            // denied. Retry only these fixed file operations; permissions and
            // reparse guards are rechecked each time and failures remain closed.
            catch(Exception ex)when(attempt<delays.Length && IsRetryableSessionStorageFailure(ex))
            {Thread.Sleep(delays[attempt]);}
        }
    }
    private static bool IsRetryableSessionStorageFailure(Exception error)=>
        (error is IOException && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) ||
        (error is UnauthorizedAccessException && error.HResult==unchecked((int)0x80070005));
    private void TrackCookieCleanup(Task cleanup)
    {
        lock(_cookieCleanupSync)
        {
            _cookieCleanups.RemoveAll(task=>task.IsCompletedSuccessfully);
            _cookieCleanups.Add(cleanup);
        }
    }
    // Call after consumers/processes have finished disposing their leases.
    public async Task DrainCookieCleanupAsync()
    {
        var failed=0;
        var deadline=Environment.TickCount64+5000;
        while(true)
        {
            Task[] pending;
            lock(_cookieCleanupSync){pending=_cookieCleanups.ToArray();_cookieCleanups.Clear();}
            if(pending.Length==0)break;
            foreach(var task in pending)
                try { await task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,deadline-Environment.TickCount64))).ConfigureAwait(false); }
                catch(TimeoutException)
                {
                    // Retain cleanup ownership for a later drain; the process
                    // observer can still confirm termination and delete safely.
                    lock(_cookieCleanupSync)_cookieCleanups.AddRange(pending.Where(item=>!item.IsCompletedSuccessfully));
                    throw new IOException(UiText.T("일부 요청의 종료 확인을 기다려 임시 로그인 파일 정리를 보류했습니다. 잠시 후 다시 시도하세요.","Temporary sign-in cleanup is waiting for some requests to stop. Try again shortly.","一部のリクエストの終了確認を待っているため、一時ログインファイルの削除を保留しました。しばらくしてから再試行してください。"));
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failed++;}
        }
        if(failed>0)throw new IOException(UiText.T("일부 임시 로그인 파일을 정리하지 못했습니다. 사용 중인 파일을 해제한 뒤 앱을 다시 실행하세요.","Could not remove some temporary sign-in files. Close programs using them, then restart the app.","一部の一時ログインファイルを削除できませんでした。使用中のファイルを解放してから、アプリを再起動してください。"));
    }
    private string SecretPath=>Path.Combine(_paths.Data,"session.dpapi");
    public bool IsConnected=>!HasSessionRecoveryFailure && File.Exists(SecretPath);
    public SessionService(RuntimePaths paths)
    {
        _paths=paths;
        FileBoundary.EnsureRegularPath(RecoveryMarkerPath);
        HasSessionRecoveryFailure=File.Exists(RecoveryMarkerPath);
        var temporary=Path.Combine(paths.Data,"temp");
        FileBoundary.EnsureRegularPath(temporary);
        var temp=Directory.CreateDirectory(temporary);
        FileBoundary.EnsureRegularPath(temporary);
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        var user=WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(UiText.T("Windows 사용자 계정을 확인할 수 없습니다.","Could not identify the Windows user account.","Windows ユーザーアカウントを確認できません。"));
        foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        temp.SetAccessControl(acl);
        foreach(var f in Directory.EnumerateFiles(temporary,"cookies-*.txt"))
            if(IsOwnedCookieArtifact(f,".txt"))TrackCookieCleanup(CleanupExportArtifactWithRetryAsync(f,".txt",true));
        foreach(var directory in Directory.EnumerateDirectories(temp.FullName,"cookies-*.work"))
            if(IsOwnedCookieArtifact(directory,".work"))TrackCookieCleanup(CleanupExportArtifactWithRetryAsync(directory,".work",true));
    }
    public static string FilterCookies(string text)
    {
        var output=new StringBuilder("# Netscape HTTP Cookie File\n");int count=0;
        foreach(var original in text.Split('\n'))
        {
            var line=original.TrimEnd('\r');if(line.Length==0 || (line.StartsWith('#') && !line.StartsWith("#HttpOnly_")))continue;
            var fields=line.Split('\t');if(fields.Length!=7)continue;
            var domain=fields[0].Replace("#HttpOnly_","").TrimStart('.').ToLowerInvariant();
            if(domain=="youtube.com" || domain.EndsWith(".youtube.com") || domain=="google.com" || domain.EndsWith(".google.com")) { output.AppendLine(line);count++; }
        }
        if(count==0)throw new InvalidDataException(UiText.T("YouTube 또는 Google 쿠키가 없는 파일입니다. Netscape cookies.txt 형식을 확인하세요.","This file contains no YouTube or Google cookies. Check the Netscape cookies.txt format.","このファイルには YouTube または Google の Cookie がありません。Netscape cookies.txt 形式を確認してください。"));
        return output.ToString();
    }
    public void ImportFile(string path)
    {
        EnsureSessionUsable(recoveryWrite:true);
        Store(FilterCookies(ReadCookieText(path)));
    }
    private static string ReadCookieText(string path)
    {
        FileBoundary.EnsureRegularPath(path);
        // The size decision and read use the same open file. Deny concurrent
        // write/delete sharing instead of checking FileInfo before a second open.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan);
        FileBoundary.EnsureRegularPath(path);
        if(stream.Length>8*1024*1024)throw new InvalidDataException(UiText.T("쿠키 파일이 너무 큽니다.","The cookie file is too large.","Cookie ファイルが大きすぎます。"));
        using var reader=new StreamReader(stream,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
        return reader.ReadToEnd();
    }
    private void Store(string text)
    {
        EnsureSessionUsable(recoveryWrite:true);
        var bytes=Encoding.UTF8.GetBytes(text);
        try
        {
            var encrypted=ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
            StoreEncryptedFile(SecretPath,encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public CookieLease Acquire()
    {
        EnsureSessionUsable();
        if(_selectedChannelId.Length>0)return AcquireSelected();
        return AcquireBase();
    }
    public CookieLease AcquireBase()
    {
        EnsureSessionUsable();
        FileBoundary.EnsureRegularPath(SecretPath);
        if(!IsConnected)return CreateCookieLease(null);
        FileBoundary.EnsureRegularPath(Path.Combine(_paths.Data,"temp"));
        byte[]? encrypted=null;byte[]? bytes=null;
        try
        {
            encrypted=FileBoundary.ReadBoundedFile(SecretPath,MaximumEncryptedSessionBytes);
            try{bytes=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);}
            catch(CryptographicException){throw new InvalidDataException(UiText.T("이 Windows 계정에서 세션을 열 수 없습니다. 로그인 연결을 갱신하세요.","This Windows account cannot open the session. Refresh your sign-in connection.","この Windows アカウントではセッションを開けません。ログイン接続を更新してください。"));}
            return CreateCookieFile(stream=>stream.Write(bytes));
        }
        finally
        {
            if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);
            if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);
        }
    }
    public async Task ConnectFirefoxAsync(string profile,CancellationToken cancellationToken)
    {
        EnsureSessionUsable(recoveryWrite:true);
        if(!Directory.Exists(profile))throw new InvalidDataException(UiText.T("Firefox 프로필 폴더를 찾을 수 없습니다.","The Firefox profile folder could not be found.","Firefox プロファイルフォルダーが見つかりません。"));
        FileBoundary.EnsureRegularPath(Path.Combine(_paths.Data,"temp"));
        var temp=Path.Combine(_paths.Data,"temp","cookies-"+Guid.NewGuid().ToString("N")+".txt");
        var stopReceipt=new ProcessStopReceipt();
        try
        {
            var result=await ProcessRunner.RunAsync(_paths.Python,[Path.Combine(_paths.Root,"worker","export_session.py"),"--profile",profile,"--output",temp],_paths.Root,TimeSpan.FromSeconds(45),cancellationToken,stopReceipt:stopReceipt);
            if(result.ExitCode!=0 || !File.Exists(temp))throw new InvalidDataException(UiText.T("Firefox 세션을 읽지 못했습니다. 선택한 프로필에서 YouTube 로그인 후 다시 연결하세요.","Could not read the Firefox session. Sign in to YouTube in the selected profile, then reconnect.","Firefox のセッションを読み取れませんでした。選択したプロファイルで YouTube にログインしてから、再接続してください。"));
            ImportFile(temp);
        }
        finally
        {
            // The process stop receipt is independent of the export result.
            // Cleanup errors belong to the transition/exit drain and must never
            // replace the operation's original failure or cancellation.
            TrackCookieCleanup(CleanupExportAfterStopAsync(temp,stopReceipt));
        }
    }
    private static bool IsOwnedCookieArtifact(string path,string extension)
    {
        var name=Path.GetFileName(path);
        return name.StartsWith("cookies-",StringComparison.Ordinal) && name.EndsWith(extension,StringComparison.Ordinal) &&
            Guid.TryParseExact(name[8..^extension.Length],"N",out _);
    }
    private void QueueExportCleanup(string output,bool consumersStopped)
    {
        TrackCookieCleanup(CleanupExportArtifactWithRetryAsync(output,".txt",consumersStopped));
        TrackCookieCleanup(CleanupExportArtifactWithRetryAsync(Path.ChangeExtension(output,".work"),".work",consumersStopped));
    }
    private async Task CleanupExportAfterStopAsync(string output,ProcessStopReceipt receipt)
    {
        await receipt.Completion.ConfigureAwait(false);
        QueueExportCleanup(output,true);
    }
    private async Task CleanupExportArtifactWithRetryAsync(string path,string extension,bool consumersStopped)
    {
        if(!consumersStopped)throw new IOException(UiText.T("Firefox 내보내기 프로세스 종료를 확인하지 못해 임시 로그인 파일 정리를 보류했습니다.","Temporary sign-in file cleanup was deferred because the Firefox export process could not be confirmed as stopped.","Firefox のエクスポートプロセスの終了を確認できなかったため、一時ログインファイルの削除を保留しました。"));
        int[] delays=[50,100,200];
        for(var attempt=0;;attempt++)
        {
            try{DeleteOwnedExportArtifact(path,extension);return;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {
                if(attempt==delays.Length)throw new IOException(UiText.T("Firefox 임시 로그인 파일을 정리하지 못했습니다.","Could not remove the temporary Firefox sign-in file.","Firefox の一時ログインファイルを削除できませんでした。"),ex);
            }
            await Task.Delay(delays[attempt]).ConfigureAwait(false);
        }
    }
    private void DeleteOwnedExportArtifact(string path,string extension)
    {
        var temporary=Path.GetFullPath(Path.Combine(_paths.Data,"temp"));
        var full=Path.GetFullPath(path);
        if(!string.Equals(Path.GetDirectoryName(full),temporary,StringComparison.OrdinalIgnoreCase) || !IsOwnedCookieArtifact(full,extension))
            throw new IOException(UiText.T("임시 로그인 파일의 소유 경로가 일치하지 않습니다.","The temporary sign-in file does not match its owned path.","一時ログインファイルの所有パスが一致しません。"));
        FileBoundary.EnsureRegularPath(full);
        if(extension==".txt"){File.Delete(full);return;}
        if(extension!=".work")throw new IOException(UiText.T("임시 로그인 파일 형식이 일치하지 않습니다.","The temporary sign-in file format does not match.","一時ログインファイルの形式が一致しません。"));
        if(!Directory.Exists(full))return;
        // Inspect descendants without following links before recursive deletion.
        // A reparse point or inaccessible child leaves this exact GUID container
        // for a later safe cleanup; no linked target is traversed.
        var pending=new Stack<string>();pending.Push(full);
        while(pending.Count>0)
            foreach(var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes=File.GetAttributes(child);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException(UiText.T("임시 로그인 폴더에 연결된 경로가 있습니다.","The temporary sign-in folder contains a linked path.","一時ログインフォルダーにリンクされたパスがあります。"));
                if((attributes&FileAttributes.Directory)!=0)pending.Push(child);
            }
        Directory.Delete(full,true);
    }
    private static bool IsOwnedChannelSession(string path)
    {
        var name=Path.GetFileName(path);
        return name.Length==70 && name.EndsWith(".dpapi",StringComparison.OrdinalIgnoreCase) &&
            name.AsSpan(0,64).IndexOfAnyExcept("0123456789ABCDEFabcdef".AsSpan())<0;
    }
    public void Disconnect()
    {
        EnsureSessionUsable();
        FileBoundary.EnsureRegularPath(SecretPath);
        if(File.Exists(SecretPath)){_activeMutation?.Capture(SecretPath);DeleteSessionFile(SecretPath);_activeMutation?.Changed(SecretPath);}
        ForgetSelectedChannel();
        var directory=Path.Combine(_paths.Data,"channels");
        FileBoundary.EnsureRegularPath(directory);
        if(!Directory.Exists(directory))return;
        foreach(var file in Directory.EnumerateFiles(directory,"*.dpapi"))
        {
            if(!IsOwnedChannelSession(file))continue;
            FileBoundary.EnsureRegularPath(file);
            _activeMutation?.Capture(file);DeleteSessionFile(file);_activeMutation?.Changed(file);
        }
    }
    public static IReadOnlyList<FirefoxProfile> ListFirefoxProfiles()
        =>ListFirefoxProfiles(out _);
    public static IReadOnlyList<FirefoxProfile> ListFirefoxProfiles(out string? warning)
        =>ListFirefoxProfiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Mozilla","Firefox"),out warning);
    private static IReadOnlyList<FirefoxProfile> ListFirefoxProfiles(string root,out string? warning)
    {
        warning=null;
        var result=new List<FirefoxProfile>();
        var file=Path.Combine(root,"profiles.ini");
        var section=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var invalidSection=false;
        void Add()
        {
            try
            {
                if(section.TryGetValue("Path",out var path))
                {
                    var full=Path.GetFullPath(section.GetValueOrDefault("IsRelative")=="1"?Path.Combine(root,path):path);
                    if(Directory.Exists(full))result.Add(new(section.GetValueOrDefault("Name",Path.GetFileName(full)),full));
                }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){invalidSection=true;}
            finally{section.Clear();}
        }
        try
        {
            foreach(var line in File.ReadLines(file)){if(line.StartsWith('[')){Add();continue;}var i=line.IndexOf('=');if(i>0)section[line[..i]]=line[(i+1)..];}
            Add();
            if(invalidSection)warning=UiText.T("일부 Firefox 프로필 경로를 읽지 못했습니다. 필요한 프로필 폴더를 직접 선택할 수 있습니다.","Some Firefox profile paths could not be read. You can select the required profile folder manually.","一部の Firefox プロファイルのパスを読み取れませんでした。必要なプロファイルフォルダーを手動で選択できます。");
        }
        catch(FileNotFoundException){}
        catch(DirectoryNotFoundException){}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {result.Clear();warning=UiText.T("Firefox 프로필 목록을 읽지 못했습니다. 게스트로 사용하거나 프로필 폴더를 직접 선택할 수 있습니다.","The Firefox profile list could not be read. You can use guest mode or select a profile folder manually.","Firefox プロファイル一覧を読み取れませんでした。ゲストとして使用するか、プロファイルフォルダーを手動で選択できます。");}
        return result;
    }
}
