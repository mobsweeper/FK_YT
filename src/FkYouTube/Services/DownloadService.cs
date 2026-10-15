using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using FkYouTube.ProcessSupport;

namespace FkYouTube.Services;

public sealed record DownloadResult(string Destination,string? BackupPath);
// Keep process output independent of whichever UI language was active when it arrived.
public enum DownloadProgressKind { Downloading, Merging, Remuxing }
public sealed record DownloadProgressUpdate(DownloadProgressKind Kind,double Percent=0);

public sealed class DownloadService(RuntimePaths paths)
{
    private static readonly PendingCleanupTracker StageCleanups=new();
    public static string SuggestedName(VideoItem video)
    {
        var invalid=Path.GetInvalidFileNameChars();
        var title=new string(video.Title.Select(c=>invalid.Contains(c)?'_':c).Take(100).ToArray()).Trim().TrimEnd('.');
        if(title.Length==0)title="YouTube";
        return title+" ["+video.Id+"].mkv";
    }
    public async Task<DownloadResult> DownloadAsync(string url,int maxHeight,string destination,string? cookiePath,IProgress<DownloadProgressUpdate> progress,CancellationToken ct,YouTubeChannel? accountContext=null,ProcessStopReceipt? stopReceipt=null)
    {
        ct.ThrowIfCancellationRequested();
        if(!YouTubeService.TryVideoUrl(url,out _,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        var ffmpeg=Path.Combine(paths.Root,"runtime","ffmpeg","ffmpeg.exe");
        if(!File.Exists(ffmpeg))throw new IOException(UiText.T("FFmpeg가 포함된 새 배포본을 실행해 주세요.","Run a newer distribution that includes FFmpeg.","FFmpeg が含まれた新しい配布版を実行してください。"));
        destination=Path.GetFullPath(destination);
        if(!string.Equals(Path.GetExtension(destination),".mkv",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(UiText.T("MKV 파일로 저장해 주세요.","Save as an MKV file.","MKV ファイルとして保存してください。"));
        YouTubeAccountOptions.Validate(accountContext);
        var existing=new FileInfo(destination);
        var existed=existing.Exists;var stamp=existed?existing.LastWriteTimeUtc:(DateTime?)null;var length=existed?existing.Length:(long?)null;
        var stage=CreateStageDirectory();
        stopReceipt??=new ProcessStopReceipt();
        try
        {
            var args=new List<string>{"--ignore-config","--no-plugin-dirs","--no-cache-dir","--no-playlist","--no-mark-watched","--no-write-comments","--no-write-info-json","--no-write-thumbnail","--no-write-subs","--no-write-auto-subs","--socket-timeout","20","--retries","2","--fragment-retries","2","--js-runtimes","deno:"+paths.Deno,"--ffmpeg-location",ffmpeg,"--format",YouTubeService.VideoFormat(maxHeight),"--merge-output-format","mkv","--remux-video","mkv","--newline","--no-colors","--progress","--progress-template","download:FK_PROGRESS:%(progress._percent_str)s","--output",Path.Combine(stage,"video.%(ext)s").Replace("%","%%").Replace("%%(ext)s","%(ext)s")};
            var extractor=YouTubeAccountOptions.ExtractorArguments(accountContext);
            if(extractor.Length>0)args.AddRange(["--extractor-args",extractor]);
            if(cookiePath!=null)args.AddRange(["--cookies",cookiePath]);args.Add(canonical);
            var command=YouTubeAccountOptions.CreateCommand(paths,args,accountContext);
            stopReceipt.MarkStarted();
            OwnedProcess process;
            try{process=OwnedProcess.Start(command.Executable,command.Arguments,paths.Root);}
            catch{stopReceipt.ConfirmStopped();throw;}
            using var cancel=ct.Register(()=>{try{process.Kill();}catch{}});
            var stdout=ReadProgressAsync(process.StandardOutput,progress,ct);
            var stderr=ReadProgressAsync(process.StandardError,progress,ct);
            var exitCode=-1;
            try
            {
                if(command.StandardInput is not null)
                {
                    await process.StandardInput.WriteLineAsync(command.StandardInput.AsMemory(),ct);
                    await process.StandardInput.FlushAsync(ct);process.StandardInput.Close();
                }
                // Observe each reader while the child is alive. A failed reader
                // cannot drain its pipe, so waiting only for exit can deadlock.
                var active=new List<Task>{stdout,stderr,process.WaitForExitAsync(ct)};
                while(active.Count>0){var finished=await Task.WhenAny(active);await finished;active.Remove(finished);}
                exitCode=process.ExitCode;
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                await cancel.DisposeAsync().ConfigureAwait(false);
                try{await ProcessRunner.CompleteOwnedProcessAsync(process,[stdout,stderr],stopReceipt);}
                catch(Exception ex)when(ct.IsCancellationRequested && ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException){}
            }
            var completed=Path.Combine(stage,"video.mkv");
            if(exitCode!=0 || !File.Exists(completed) || new FileInfo(completed).Length==0)throw new IOException(UiText.T("다운로드하지 못했습니다. 영상의 접근 권한이나 네트워크를 확인해 주세요.","The download failed. Check video access permissions and your network connection.","ダウンロードできませんでした。動画へのアクセス権限とネットワークを確認してください。"));
            var backup=await PublishAsync(completed,destination,existed,stamp,length,ct);
            return new(destination,backup);
        }
        finally
        {
            var cleanup=QueueStageCleanup(stage,stopReceipt);
            if(stopReceipt.ConsumersStopped)
            {
                // Publication/cancellation keeps its original outcome. The
                // tracked drain reports deletion failures, including late ones.
                try{await cleanup.ConfigureAwait(false);}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
            }
        }
    }
    public static Task DrainStageCleanupAsync()=>DrainStageCleanupAsync(TimeSpan.FromSeconds(5));
    internal static Task DrainStageCleanupAsync(TimeSpan timeout)=>StageCleanups.DrainAsync(timeout,
        "Some download staging cleanup is still pending or could not be completed.");
    private static Task QueueStageCleanup(string stage,ProcessStopReceipt receipt)
    {
        var cleanup=CleanupStageAfterStopAsync(stage,receipt);
        StageCleanups.Track(cleanup);
        return cleanup;
    }
    private static async Task CleanupStageAfterStopAsync(string stage,ProcessStopReceipt receipt)
    {
        await receipt.Completion.ConfigureAwait(false);
        await CleanupStageAsync(stage).ConfigureAwait(false);
    }
    private static bool IsOwnedStagePath(string stage)
    {
        var resolved=Path.GetFullPath(stage);var name=Path.GetFileName(resolved);
        return string.Equals(Path.GetDirectoryName(resolved),StagingRoot,StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("job-",StringComparison.Ordinal) && Guid.TryParseExact(name[4..],"N",out _);
    }
    internal static string StagingRoot=>Path.GetFullPath(Path.Combine(Path.GetTempPath(),"FK_YT","downloads"));
    internal static string CreateStageDirectory()
    {
        FileBoundary.EnsureRegularPath(StagingRoot);
        var temporary=Directory.CreateDirectory(StagingRoot);
        FileBoundary.EnsureRegularPath(temporary.FullName);
        var user=WindowsIdentity.GetCurrent().User ?? throw new IOException(UiText.T("Windows 사용자 계정을 확인할 수 없습니다.","Could not identify the Windows user account.","Windows ユーザーアカウントを確認できません。"));
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        temporary.SetAccessControl(acl);
        var stage=Directory.CreateDirectory(Path.Combine(temporary.FullName,"job-"+Guid.NewGuid().ToString("N"))).FullName;
        FileBoundary.EnsureRegularPath(stage);
        return stage;
    }
    internal static async Task CleanupStageAsync(string stage)
    {
        var resolved=Path.GetFullPath(stage);
        if(!IsOwnedStagePath(resolved))return;
        for(var attempt=0;attempt<4;attempt++)
        {
            try
            {
                FileBoundary.EnsureRegularPath(StagingRoot);
                if(Directory.Exists(resolved))
                {
                    if((File.GetAttributes(resolved)&FileAttributes.ReparsePoint)!=0)Directory.Delete(resolved);
                    else Directory.Delete(resolved,true);
                }
                return;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {
                if(attempt==3)throw;
                await Task.Delay(100*(attempt+1));
            }
        }
    }
    internal static async Task<string?> PublishAsync(string completed,string destination,bool existed,DateTime? stamp,long? length,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FileBoundary.EnsureRegularPath(completed);
        destination=Path.GetFullPath(destination);
        var parent=Path.GetDirectoryName(destination)!;
        var publish=Path.Combine(parent,".fkyt-publish-"+Guid.NewGuid().ToString("N")+".tmp");
        var stem=Path.GetFileNameWithoutExtension(destination);
        var backup=existed?Path.Combine(parent,stem[..Math.Min(96,stem.Length)]+".fkyt-backup-"+Guid.NewGuid().ToString("N")+Path.GetExtension(destination)):null;
        try
        {
            CheckDestination(destination,existed,stamp,length);
            await using(var input=new FileStream(completed,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
            await using(var output=new FileStream(publish,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output,ct);
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            // The copy is always complete before the only destination mutation.
            // Both names are in one directory, so replacement is an atomic rename
            // even when the downloaded source lives on a different volume.
            for(var attempt=0;;attempt++)
            {
                ct.ThrowIfCancellationRequested();
                CheckDestination(destination,existed,stamp,length);
                try
                {
                    if(existed)
                    {
                        // Capture the version present at the atomic replacement,
                        // including edits that retain the earlier size/time stamp.
                        if(File.Exists(backup))throw new IOException(UiText.T("기존 파일 백업 이름이 사용 중입니다. 다른 이름으로 저장해 주세요.","The backup file name is already in use. Save under a different name.","既存ファイルのバックアップ名は使用中です。別の名前で保存してください。"));
                        File.Replace(publish,destination,backup);
                    }
                    else File.Move(publish,destination);
                    return backup;
                }
                catch(IOException ex)when((ex.HResult&0xffff) is 32 or 33 && attempt<4)
                {await Task.Delay(100*(attempt+1),ct);}
            }
        }
        finally
        {
            // This exact CreateNew name belongs only to this invocation.
            for(var attempt=0;attempt<4;attempt++)
            {
                try{File.Delete(publish);break;}
                catch(IOException)when(attempt<3){await Task.Delay(100*(attempt+1));}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){break;}
            }
        }
    }
    private static void CheckDestination(string destination,bool existed,DateTime? stamp,long? length)
    {
        var current=new FileInfo(destination);
        if(current.Exists!=existed || (existed && (current.LastWriteTimeUtc!=stamp || current.Length!=length)))
            throw new IOException(UiText.T("저장 중 같은 이름의 파일이 변경되었습니다. 다른 이름으로 다시 저장해 주세요.","A file with the same name changed during saving. Save again under a different name.","保存中に同じ名前のファイルが変更されました。別の名前で保存し直してください。"));
    }
    private static async Task ReadProgressAsync(StreamReader reader,IProgress<DownloadProgressUpdate> progress,CancellationToken ct)
    {
        var lines=new BoundedLineReader(reader,64*1024);
        while(await lines.ReadLineAsync(ct).ConfigureAwait(false) is {} line)
        {
            if(line.StartsWith("FK_PROGRESS:",StringComparison.Ordinal))
            {
                var value=line[12..].Trim().TrimEnd('%');
                if(double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var percent))progress.Report(new(DownloadProgressKind.Downloading,Math.Clamp(percent,0,100)));
            }
            else if(line.StartsWith("[Merger]",StringComparison.Ordinal))progress.Report(new(DownloadProgressKind.Merging));
            else if(line.StartsWith("[VideoRemuxer]",StringComparison.Ordinal))progress.Report(new(DownloadProgressKind.Remuxing));
        }
    }
}
