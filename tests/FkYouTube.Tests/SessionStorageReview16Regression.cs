using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using FkYouTube;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class SessionStorageReview16Regression
{
    private const string Cookies="# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n";
    public static Task RunDisconnectRollbackAsync(string workspace)=>RunAsync(workspace,"disconnect mutation restores only its owned session files");
    public static Task RunSettingsPublicationFailureAsync(string workspace)=>RunAsync(workspace,"session and settings retain their old pair when settings publication fails");
    public static async Task RunAsync(string workspace,string? only=null)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","session-storage-review16-r2"));
        var root=Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var failures=new List<Exception>();
        try
        {
            async Task Run(string name,Func<Task> test)
            {
                if(only is not null && name!=only)return;
                try{await test();Console.WriteLine("SESSION STORAGE R2 PASS "+name);}
                catch(Exception ex){Console.WriteLine("SESSION STORAGE R2 FAIL "+name+": "+ex);failures.Add(new InvalidOperationException(name,ex));}
            }
            await Run("partial cookie write closes and cleans its owned file",()=>PartialCookieAsync(root,false));
            await Run("partial cookie write preserves original failure and registers bounded retry",()=>PartialCookieAsync(root,true));
            await Run("Firefox export preserves failure and retries its exact artifacts after stop",()=>FirefoxExportCleanupAsync(workspace,root,false));
            await Run("Firefox export persistent artifact locks are separately observed and bounded",()=>FirefoxExportCleanupAsync(workspace,root,true));
            await Run("startup access denied cleanup is confined to owned artifacts",()=>StartupExportCleanupAsync(root));
            await Run("export cleanup requires stop proof and exact owned paths",()=>ExportCleanupGuardsAsync(root));
            await Run("export cleanup rejects nested reparse targets",()=>ExportCleanupReparseAsync(root));
            await Run("transient encrypted publication sharing lock can recover without losing old bytes",()=>EncryptedPublicationLockAsync(root));
            await Run("transient commit marker sharing lock can recover without losing commit",()=>CommitMarkerLockAsync(root));
            await Run("base encrypted save preserves preexisting fixed temporary file",()=>EncryptedTempAsync(root));
            await Run("channel encrypted writer preserves unrelated fixed temporary file",()=>ChannelEncryptedTempAsync(root));
            await Run("optional Firefox profile read lock becomes warning",()=>FirefoxReadFailureAsync(root));
            await Run("invalid Firefox section preserves valid relative and absolute profiles",()=>FirefoxSectionsAsync(root));
            await Run("transient settings sharing lock retries without defaulting",()=>SettingsLockAsync(root,false));
            await Run("persistent settings sharing lock remains bounded and write protected",()=>SettingsLockAsync(root,true));
            await Run("settings retries accept only Win32 sharing and lock violations",LockErrorCodesAsync);
            await Run("session mutation restores old encrypted session and selected identity",()=>MutationRollbackAsync(root));
            await Run("disconnect mutation restores only its owned session files",()=>DisconnectRollbackAsync(root));
            await Run("session mutation commit keeps new encrypted session",()=>MutationCommitAsync(root));
            await Run("failed session rollback blocks subsequent acquisition",()=>MutationFailureAsync(root));
            await Run("session and settings retain their old pair when settings publication fails",()=>SettingsPublicationFailureAsync(root));
            await Run("committed session stays committed when marker cleanup fails",()=>CommitMarkerFailureAsync(root));
            await Run("explicit fresh session recovery keeps failure blocked until commit",()=>ExplicitRecoveryAsync(root));
            await Run("known settings load failure rejects account persistence preflight",()=>SettingsPreflightAsync(root));
            if(failures.Count>0)throw new AggregateException(failures);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Check(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"fixture escaped its owned root");
            Directory.Delete(root,true);
        }
    }
    private static async Task PartialCookieAsync(string root,bool locked)
    {
        var paths=new RuntimePaths(Path.Combine(root,"partial-"+locked));var session=new SessionService(paths);
        var expected=new IOException("synthetic partial write failure");FileStream? held=null;string? created=null;
        Action<Stream> write=stream=>
        {
            created=((FileStream)stream).Name;stream.Write(Encoding.UTF8.GetBytes("synthetic partial secret"));
            if(locked)
            {
                // Open after the writer closes but before its synchronous cleanup.
                // The callback's stream is closed explicitly to model late flush/dispose failure.
                stream.Dispose();held=new FileStream(created,FileMode.Open,FileAccess.Read,FileShare.Read);
            }
            throw expected;
        };
        var failure=Capture(()=>Invoke(session,"CreateCookieFile",write));
        try
        {
            Check(ReferenceEquals(failure,expected),"partial cookie cleanup replaced the original writer failure");
            Check(created is not null,"partial writer did not create its synthetic file");
            if(locked)Check(File.Exists(created),"locked partial file was unexpectedly removed");
        }
        finally{held?.Dispose();}
        await session.DrainCookieCleanupAsync();
        Check(!File.Exists(created),"partially written cookie file escaped cleanup ownership");
        Check(!Directory.EnumerateFiles(Path.Combine(paths.Data,"temp"),"cookies-*.txt").Any(),"partial issue retained an owned cookie file");
    }
    private static Task EncryptedTempAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"encrypted-temp"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);
        var fixedTemp=Path.Combine(paths.Data,"session.dpapi.new");File.WriteAllText(fixedTemp,"unrelated sentinel");
        session.ImportFile(source);
        Check(File.ReadAllText(fixedTemp)=="unrelated sentinel","session save consumed a preexisting fixed temporary file");
        using(var lease=session.AcquireBase())Check(File.ReadAllText(lease.Path!).Contains("offline-only"),"base session no longer round trips");
        Check(!Directory.EnumerateFiles(paths.Data,"session-*.new").Any(),"successful encrypted save retained its temporary file");
        return Task.CompletedTask;
    }
    private static async Task FirefoxExportCleanupAsync(string workspace,string root,bool persistent)
    {
        var paths=new RuntimePaths(Path.Combine(root,"firefox-export-"+persistent));var session=new SessionService(paths);
        var runtime=Path.GetDirectoryName(paths.Python)!;Directory.CreateDirectory(runtime);
        foreach(var source in Directory.EnumerateFiles(Path.Combine(workspace,"runtime","python")))File.Copy(source,Path.Combine(runtime,Path.GetFileName(source)));
        var worker=Path.Combine(paths.Root,"worker");Directory.CreateDirectory(worker);
        File.WriteAllText(Path.Combine(worker,"export_session.py"),"""
            import pathlib, sys, time
            output=pathlib.Path(sys.argv[sys.argv.index('--output')+1])
            work=output.with_suffix('.work');work.mkdir()
            (work/'synthetic.txt').write_text('offline export fixture')
            output.write_text('offline export fixture')
            pathlib.Path('export-ready.txt').write_text(str(output))
            while not pathlib.Path('export-release.txt').exists():time.sleep(.01)
            raise SystemExit(1)
            """,new UTF8Encoding(false));
        var profile=Path.Combine(paths.Root,"synthetic-profile");Directory.CreateDirectory(profile);
        var operation=session.ConnectFirefoxAsync(profile,CancellationToken.None);
        var ready=Path.Combine(paths.Root,"export-ready.txt");var timer=Stopwatch.StartNew();
        while(!File.Exists(ready) && !operation.IsCompleted && timer.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(10);
        Check(File.Exists(ready),"offline Firefox worker did not publish its fixture boundary");
        var output=File.ReadAllText(ready);var directory=Path.ChangeExtension(output,".work");
        var sibling=Path.Combine(paths.Data,"temp","cookies-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(sibling,"unrelated invocation");
        Exception? failure;Exception? cleanupFailure=null;
        using(var heldOutput=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var heldWork=new FileStream(Path.Combine(directory,"synthetic.txt"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            Check(File.Exists(output) && Directory.Exists(directory),"live consumer artifacts were deleted before process termination");
            File.WriteAllText(Path.Combine(paths.Root,"export-release.txt"),"exit");
            try{await operation;failure=null;}catch(Exception ex){failure=ex;}
            Check(failure is InvalidDataException,"artifact cleanup replaced the original export failure: "+failure?.GetType().Name);
            Check(File.Exists(output) && Directory.Exists(directory),"locked export artifacts unexpectedly disappeared");
            if(persistent)
            {
                timer.Restart();
                try{await session.DrainCookieCleanupAsync();}catch(Exception ex){cleanupFailure=ex;}
                Check(cleanupFailure is IOException && timer.Elapsed<TimeSpan.FromSeconds(2),"persistent export cleanup was hidden or exceeded its retry budget");
            }
        }
        if(!persistent)
        {
            await session.DrainCookieCleanupAsync();
            Check(!File.Exists(output) && !Directory.Exists(directory),"released export artifact lock escaped the shared cleanup drain");
        }
        Check(File.ReadAllText(sibling)=="unrelated invocation","export cleanup consumed a different invocation's artifact");
    }
    private static async Task StartupExportCleanupAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"startup-export"));var temporary=Path.Combine(paths.Data,"temp");Directory.CreateDirectory(temporary);
        var output=Path.Combine(temporary,"cookies-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(output,"offline fixture");
        var directory=Path.Combine(temporary,"cookies-"+Guid.NewGuid().ToString("N")+".work");Directory.CreateDirectory(directory);
        var child=Path.Combine(directory,"synthetic.txt");File.WriteAllText(child,"offline fixture");
        var unrelated=Path.Combine(temporary,"cookies-not-owned.work");Directory.CreateDirectory(unrelated);File.WriteAllText(Path.Combine(unrelated,"preserve.txt"),"preserve");
        File.SetAttributes(output,FileAttributes.ReadOnly);File.SetAttributes(child,FileAttributes.ReadOnly);
        try
        {
            SessionService? session=null;var failure=Capture(()=>session=new SessionService(paths));
            Check(failure is null && session is not null,"one inaccessible startup artifact escaped its cleanup boundary");
            Exception? cleanupFailure=null;try{await session!.DrainCookieCleanupAsync();}catch(Exception ex){cleanupFailure=ex;}
            Check(cleanupFailure is IOException,"startup artifact access denial was silently discarded");
            Check(File.Exists(output) && File.Exists(child) && File.ReadAllText(Path.Combine(unrelated,"preserve.txt"))=="preserve","startup cleanup changed inaccessible or unrelated fixture bytes");
        }
        finally
        {
            if(File.Exists(output))File.SetAttributes(output,FileAttributes.Normal);
            if(File.Exists(child))File.SetAttributes(child,FileAttributes.Normal);
        }
    }
    private static async Task EncryptedPublicationLockAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"encrypted-publication-lock"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var file=Path.Combine(paths.Data,"session.dpapi");var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>{try{Thread.Sleep(60);held.Dispose();release.SetResult();}catch(Exception ex){release.SetException(ex);}}){IsBackground=true}.Start();
        File.WriteAllText(source,Cookies.Replace("offline-only","after-short-lock"));
        Exception? failure;try{failure=Capture(()=>session.ImportFile(source));}finally{await release.Task;}
        Console.WriteLine($"SESSION STORAGE R3 ENCRYPTED LOCK exception={failure?.GetType().Name??"none"} hresult={failure?.HResult:X8}");
        Check(failure is null,"transient encrypted publication lock rejected a recoverable session write");
        using var lease=session.AcquireBase();Check(File.ReadAllText(lease.Path!).Contains("after-short-lock"),"retried encrypted publication did not commit its new bytes");
    }
    private static async Task ExportCleanupGuardsAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"export-cleanup-guards"));var session=new SessionService(paths);
        var output=Path.Combine(paths.Data,"temp","cookies-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(output,"offline fixture");
        Invoke(session,"QueueExportCleanup",output,false);
        Exception? stoppedFailure=null;try{await session.DrainCookieCleanupAsync();}catch(Exception ex){stoppedFailure=ex;}
        Check(stoppedFailure is IOException && File.Exists(output),"export cleanup deleted artifacts without confirmed consumer termination");
        var outside=Path.Combine(paths.Root,"cookies-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(outside,"outside sentinel");
        Invoke(session,"QueueExportCleanup",outside,true);
        Exception? pathFailure=null;try{await session.DrainCookieCleanupAsync();}catch(Exception ex){pathFailure=ex;}
        Check(pathFailure is IOException && File.ReadAllText(outside)=="outside sentinel","export cleanup escaped the exact temp parent");
        var unrelated=Path.Combine(paths.Data,"temp","cookies-unowned.txt");File.WriteAllText(unrelated,"unowned sentinel");
        Invoke(session,"QueueExportCleanup",unrelated,true);
        Exception? nameFailure=null;try{await session.DrainCookieCleanupAsync();}catch(Exception ex){nameFailure=ex;}
        Check(nameFailure is IOException && File.ReadAllText(unrelated)=="unowned sentinel","export cleanup accepted an unowned artifact name");
        Invoke(session,"QueueExportCleanup",output,true);await session.DrainCookieCleanupAsync();
        Check(!File.Exists(output),"confirmed cleanup did not release its exact owned artifact");
    }
    private static async Task ExportCleanupReparseAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"export-cleanup-reparse"));var session=new SessionService(paths);
        var output=Path.Combine(paths.Data,"temp","cookies-"+Guid.NewGuid().ToString("N")+".txt");
        var directory=Path.ChangeExtension(output,".work");Directory.CreateDirectory(directory);
        var outside=Path.Combine(paths.Root,"outside");Directory.CreateDirectory(outside);
        var sentinel=Path.Combine(outside,"preserve.txt");File.WriteAllText(sentinel,"outside sentinel");
        var link=Path.Combine(directory,"linked");
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"/d","/c","mklink","/J",link,outside})start.ArgumentList.Add(argument);
        using(var create=Process.Start(start)!)
        {
            var standardOutput=create.StandardOutput.ReadToEndAsync();var standardError=create.StandardError.ReadToEndAsync();
            await create.WaitForExitAsync();await Task.WhenAll(standardOutput,standardError);
            Check(create.ExitCode==0 && (File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"offline reparse fixture could not be created");
        }
        try
        {
            Invoke(session,"QueueExportCleanup",output,true);
            Exception? failure=null;try{await session.DrainCookieCleanupAsync();}catch(Exception ex){failure=ex;}
            Check(failure is IOException && Directory.Exists(directory) && Directory.Exists(link) && File.ReadAllText(sentinel)=="outside sentinel",
                "export cleanup followed or removed its nested reparse target");
        }
        finally{if(Directory.Exists(link))Directory.Delete(link);}
        Invoke(session,"QueueExportCleanup",output,true);await session.DrainCookieCleanupAsync();
        Check(!Directory.Exists(directory) && File.ReadAllText(sentinel)=="outside sentinel","safe retry after reparse removal changed outside content");
    }
    private static async Task CommitMarkerLockAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"commit-marker-lock"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);
        using var mutation=session.BeginMutation();session.ImportFile(source);
        var held=new FileStream(Path.Combine(paths.Data,"session-recovery-required"),FileMode.Open,FileAccess.Read,FileShare.Read);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>{try{Thread.Sleep(60);held.Dispose();release.SetResult();}catch(Exception ex){release.SetException(ex);}}){IsBackground=true}.Start();
        Exception? failure;try{failure=Capture(mutation.Commit);}finally{await release.Task;}
        Check(failure is null && mutation.IsCommitted && !RecoveryRequired(session),"transient marker lock permanently blocked an otherwise committed session");
    }
    private static Task ChannelEncryptedTempAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"channel-temp"));var session=new SessionService(paths);
        var directory=Path.Combine(paths.Data,"channels");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,new string('A',64)+".dpapi");File.WriteAllText(path+".new","unrelated channel sentinel");
        var encrypted=ProtectedData.Protect(Encoding.UTF8.GetBytes("synthetic encrypted channel"),null,DataProtectionScope.CurrentUser);
        Invoke(session,"StoreEncryptedFile",path,encrypted);
        Check(File.ReadAllText(path+".new")=="unrelated channel sentinel" && File.ReadAllBytes(path).SequenceEqual(encrypted),"channel encrypted writer touched unrelated temporary storage");
        return Task.CompletedTask;
    }
    private static Task FirefoxReadFailureAsync(string root)
    {
        var directory=Path.Combine(root,"profiles-lock");Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"profiles.ini");File.WriteAllText(file,"[Profile0]\nName=synthetic\nIsRelative=1\nPath=profile\n");
        using var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);
        var profiles=Profiles(directory,out var warning);
        Check(profiles.Count==0 && warning is not null,"optional profile read failure escaped its warning boundary");
        return Task.CompletedTask;
    }
    private static Task FirefoxSectionsAsync(string root)
    {
        var directory=Path.Combine(root,"profiles-sections");Directory.CreateDirectory(directory);
        var relative=Path.Combine(directory,"relative");var absolute=Path.Combine(root,"absolute-profile");
        Directory.CreateDirectory(relative);Directory.CreateDirectory(absolute);
        File.WriteAllText(Path.Combine(directory,"profiles.ini"),"[ProfileBad]\nPath=bad\0path\nIsRelative=1\n[ProfileRelative]\nName=relative fixture\nPath=relative\nIsRelative=1\n[ProfileAbsolute]\nName=absolute fixture\nPath="+absolute+"\nIsRelative=0\n");
        var profiles=Profiles(directory,out var warning);
        Check(warning is not null && profiles.Count==2 && profiles.Any(p=>p.Path==relative) && profiles.Any(p=>p.Path==absolute),"bad Firefox section prevented valid profile discovery");
        Check(Profiles(Path.Combine(root,"missing-firefox"),out var missing).Count==0 && missing is null,"missing Firefox installation became an error");
        return Task.CompletedTask;
    }
    private static async Task SettingsLockAsync(string root,bool persistent)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-retry-"+persistent));var store=new DataStore(paths);
        var file=Path.Combine(paths.Data,"settings.json");var original="{\"volume\":27,\"accountKey\":\"synthetic-old-account\"}";File.WriteAllText(file,original);
        var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);
        var unlock=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!persistent)new Thread(()=>{try{Thread.Sleep(60);held.Dispose();unlock.SetResult();}catch(Exception ex){unlock.SetException(ex);}}){IsBackground=true}.Start();
        var timer=Stopwatch.StartNew();AppSettings loaded;
        try{loaded=store.LoadSettings();}finally{if(persistent)held.Dispose();else await unlock.Task;}
        Check(timer.Elapsed<TimeSpan.FromSeconds(2),"settings lock retries exceeded their bounded budget");
        if(persistent)
        {
            Check(store.HasSettingsLoadFailure && Capture(()=>store.SaveSettings(loaded)) is IOException,"persistent sharing lock lost original protection");
        }
        else Check(!store.HasSettingsLoadFailure && loaded.Volume==27 && loaded.AccountKey=="synthetic-old-account","transient lock permanently defaulted readable settings");
        Check(File.ReadAllText(file)==original,"settings lock handling modified its source");
    }
    private static Task LockErrorCodesAsync()
    {
        var method=typeof(DataStore).GetMethod("IsTransientSettingsLock",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var code in new[]{unchecked((int)0x80070020),unchecked((int)0x80070021),unchecked((int)0x80070005),unchecked((int)0x80130020),unchecked((int)0x80070070)})
        {
            var expected=code is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            Check((bool)Call(method,null,[new IOException("synthetic lock classification",code)])! == expected,"settings retried an unrelated I/O error or skipped a supported lock violation");
        }
        return Task.CompletedTask;
    }
    private static Task MutationRollbackAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"mutation-rollback"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var file=Path.Combine(paths.Data,"session.dpapi");var original=File.ReadAllBytes(file);
        var channel=new YouTubeChannel{Id="synthetic-selected",DataSyncId="fixture_user||",AuthUser=1};
        Set(session,"_selectedChannelId",channel.Id);typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(session,channel);
        var unrelated=Path.Combine(paths.Data,"channels","unrelated.dpapi");Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);File.WriteAllText(unrelated,"preserve unrelated fixture");
        using(var mutation=(IDisposable)Invoke(session,"BeginMutation")!)
        {
            File.WriteAllText(source,Cookies.Replace("offline-only","new-offline-value"));session.ImportFile(source);session.ForgetSelectedChannel();
        }
        Check(File.ReadAllBytes(file).SequenceEqual(original) && session.SelectedChannel?.Id==channel.Id && File.ReadAllText(unrelated)=="preserve unrelated fixture","uncommitted mutation failed to restore its previous exact session and identity");
        Check(!File.Exists(Path.Combine(paths.Data,"session-recovery-required")),"successful rollback retained the recovery marker");
        return Task.CompletedTask;
    }
    private static Task MutationCommitAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"mutation-commit"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);
        using(var mutation=(IDisposable)Invoke(session,"BeginMutation")!){session.ImportFile(source);Invoke(mutation,"Commit");}
        using(var lease=session.AcquireBase())Check(File.ReadAllText(lease.Path!).Contains("offline-only"),"committed mutation was rolled back");
        return Task.CompletedTask;
    }
    private static Task DisconnectRollbackAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"disconnect-rollback"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var directory=Path.Combine(paths.Data,"channels");Directory.CreateDirectory(directory);
        var files=new[]{Path.Combine(paths.Data,"session.dpapi"),Path.Combine(directory,new string('A',64)+".dpapi"),Path.Combine(directory,new string('B',64)+".dpapi")};
        foreach(var file in files.Skip(1))File.WriteAllBytes(file,ProtectedData.Protect(Encoding.UTF8.GetBytes("synthetic channel "+Path.GetFileName(file)),null,DataProtectionScope.CurrentUser));
        var originals=files.ToDictionary(file=>file,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);
        var unrelated=Path.Combine(directory,"unrelated.dpapi");var stale=files[1]+".new";
        File.WriteAllText(unrelated,"unrelated fixture");File.WriteAllText(stale,"unowned temporary fixture");
        using(var mutation=(IDisposable)Invoke(session,"BeginMutation")!)
        {session.Disconnect();Check(files.All(file=>!File.Exists(file)),"disconnect did not remove its owned fixture files");}
        Check(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) && File.ReadAllText(unrelated)=="unrelated fixture" && File.ReadAllText(stale)=="unowned temporary fixture",
            "disconnect rollback missed an owned session or changed an unrelated file");
        return Task.CompletedTask;
    }
    private static Task MutationFailureAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"mutation-failure"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var mutation=(IDisposable)Invoke(session,"BeginMutation")!;session.ImportFile(source);
        using(var held=new FileStream(Path.Combine(paths.Data,"session.dpapi"),FileMode.Open,FileAccess.Read,FileShare.Read))
            Check(Capture(mutation.Dispose) is IOException,"rollback persistence failure was hidden");
        Check((bool)typeof(SessionService).GetProperty("HasSessionRecoveryFailure")!.GetValue(session)! && !session.IsConnected &&
            Capture(()=>session.AcquireBase()) is IOException && Capture(()=>session.Acquire()) is IOException && Capture(()=>session.ImportFile(source)) is IOException,
            "session remained usable after a failed rollback");
        var restarted=new SessionService(paths);
        Check(RecoveryRequired(restarted) && Capture(()=>restarted.AcquireBase()) is IOException,"restart silently reused an unrecovered session/settings pair");
        return Task.CompletedTask;
    }
    private static Task SettingsPublicationFailureAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"pair-publication"));var session=new SessionService(paths);var store=new DataStore(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        store.SaveSettings(new(){AccountKey="synthetic-old",AuthMode="synthetic"});
        var sessionFile=Path.Combine(paths.Data,"session.dpapi");var settingsFile=Path.Combine(paths.Data,"settings.json");
        var oldSession=File.ReadAllBytes(sessionFile);var oldSettings=File.ReadAllBytes(settingsFile);
        Exception? failure=null;
        using(var held=new FileStream(settingsFile,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var mutation=(IDisposable)Invoke(session,"BeginMutation")!)
        {
            File.WriteAllText(source,Cookies.Replace("offline-only","fresh-session"));session.ImportFile(source);
            failure=Capture(()=>store.SaveSettings(new(){AccountKey="synthetic-new"}));
        }
        var sessionRestored=File.ReadAllBytes(sessionFile).SequenceEqual(oldSession);var settingsPreserved=File.ReadAllBytes(settingsFile).SequenceEqual(oldSettings);
        Console.WriteLine($"SESSION STORAGE R2 PUBLICATION exception={failure?.GetType().Name??"none"} hresult={failure?.HResult:X8} sessionRestored={sessionRestored} settingsPreserved={settingsPreserved}");
        Check((failure is IOException or UnauthorizedAccessException) && sessionRestored && settingsPreserved,
            $"settings publication check failed: exception={failure?.GetType().Name??"none"}, sessionRestored={sessionRestored}, settingsPreserved={settingsPreserved}");
        return Task.CompletedTask;
    }
    private static Task CommitMarkerFailureAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"commit-marker"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        var mutation=(IDisposable)Invoke(session,"BeginMutation")!;
        File.WriteAllText(source,Cookies.Replace("offline-only","committed-fresh-session"));session.ImportFile(source);
        var committed=File.ReadAllBytes(Path.Combine(paths.Data,"session.dpapi"));
        using(var held=new FileStream(Path.Combine(paths.Data,"session-recovery-required"),FileMode.Open,FileAccess.Read,FileShare.Read))
            Check(Capture(()=>Invoke(mutation,"Commit")) is IOException,"locked recovery marker cleanup was not observed");
        mutation.Dispose();
        Check((bool)mutation.GetType().GetProperty("IsCommitted")!.GetValue(mutation)! && RecoveryRequired(session) &&
            File.ReadAllBytes(Path.Combine(paths.Data,"session.dpapi")).SequenceEqual(committed),"marker cleanup failure rolled back an already committed session");
        return Task.CompletedTask;
    }
    private static Task ExplicitRecoveryAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"explicit-recovery"));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");File.WriteAllText(source,Cookies);session.ImportFile(source);
        File.WriteAllText(Path.Combine(paths.Data,"session-recovery-required"),"synthetic interrupted transaction");
        session=new SessionService(paths);
        Check(RecoveryRequired(session) && Capture(()=>Invoke(session,"BeginMutation")) is IOException,"recovery marker allowed an automatic account mutation");
        using(var abandoned=(IDisposable)Invoke(session,"BeginMutation",true)!)
        {
            File.WriteAllText(source,Cookies.Replace("offline-only","abandoned-recovery"));session.ImportFile(source);
            Check(Capture(()=>session.AcquireBase()) is IOException && Capture(session.Disconnect) is IOException,"incomplete recovery unblocked session consumption or disconnect");
        }
        Check(RecoveryRequired(session) && File.Exists(Path.Combine(paths.Data,"session-recovery-required")),"abandoned recovery cleared its durable failure barrier");
        using(var recovery=(IDisposable)Invoke(session,"BeginMutation",true)!)
        {
            File.WriteAllText(source,Cookies.Replace("offline-only","committed-recovery"));session.ImportFile(source);
            Check(Capture(()=>session.AcquireBase()) is IOException,"fresh recovery session became usable before settings commit");
            Invoke(recovery,"Commit");
        }
        Check(!RecoveryRequired(session) && !File.Exists(Path.Combine(paths.Data,"session-recovery-required")),"committed fresh session did not clear recovery failure");
        using(var lease=session.AcquireBase())Check(File.ReadAllText(lease.Path!).Contains("committed-recovery"),"recovery commit did not publish its fresh session");
        return Task.CompletedTask;
    }
    private static Task SettingsPreflightAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-preflight"));var store=new DataStore(paths);
        Invoke(store,"EnsureSettingsWritable");File.WriteAllText(Path.Combine(paths.Data,"settings.json"),"{malformed");store.LoadSettings();
        Check(Capture(()=>Invoke(store,"EnsureSettingsWritable")) is IOException,"known settings load failure passed account preflight");
        return Task.CompletedTask;
    }
    private static IReadOnlyList<FirefoxProfile> Profiles(string directory,out string? warning)
    {
        var method=typeof(SessionService).GetMethod("ListFirefoxProfiles",BindingFlags.NonPublic|BindingFlags.Static,null,[typeof(string),typeof(string).MakeByRefType()],null)!;
        object?[] args=[directory,null];
        var result=(IReadOnlyList<FirefoxProfile>)Call(method,null,args)!;warning=(string?)args[1];return result;
    }
    private static object? Invoke(object owner,string name,params object?[] args)
    {
        var method=owner.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Missing regression boundary: "+name);
        var parameters=method.GetParameters();
        if(args.Length<parameters.Length && parameters.Skip(args.Length).All(parameter=>parameter.HasDefaultValue))
            args=args.Concat(parameters.Skip(args.Length).Select(parameter=>parameter.DefaultValue)).ToArray();
        return Call(method,owner,args);
    }
    private static object? Call(MethodInfo method,object? owner,object?[] args)
    {
        try{return method.Invoke(owner,args);}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,value);
    private static bool RecoveryRequired(SessionService session)=>(bool)typeof(SessionService).GetProperty("HasSessionRecoveryFailure")!.GetValue(session)!;
    private static Exception? Capture(Action action){try{action();return null;}catch(Exception ex){return ex;}}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
