using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FkYouTube;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class StorageReviewRegression
{
    public static async Task RunAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","storage-review-16models"));
        var root=Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures=new List<Exception>();
        try
        {
            async Task Run(string name,Func<Task> test)
            {
                try { await test(); Console.WriteLine("STORAGE PASS "+name); }
                catch(Exception ex) { failures.Add(new InvalidOperationException(name,ex)); }
            }
            await Run("unreadable settings preserve original after file unlock",()=>LockedSettingsAsync(root));
            await Run("malformed settings preserve original until explicit successful reload",()=>MalformedSettingsAsync(root));
            await Run("first-run settings save and atomic temp ownership",()=>FirstRunAsync(root));
            await Run("settings failures pause private activity in every UI language",()=>SettingsFailureDefaultsAsync(root));
            await Run("settings numeric values normalize without replacing the source",()=>NumericSettingsAsync(root));
            await Run("highest quality migrates once and preserves later explicit caps",()=>QualityPreferenceAsync(root));
            await Run("settings bounded reads retain BOM behavior and preserve oversized originals",()=>SettingsReadBoundaryAsync(root));
            await Run("bounded file reader honors exact limits and sharing locks",()=>BoundedFileAsync(root));
            await Run("oversized encrypted sessions fail before decryption and remain intact",()=>EncryptedSessionBoundaryAsync(root));
            await Run("data directory junction rejected before DB creation",()=>DataJunctionAsync(root));
            await Run("settings and SQLite sidecar junctions rejected",()=>FileJunctionsAsync(root));
            await Run("malformed encrypted channel sessions remain selected and safe",()=>ChannelStructureAsync(root));
            await Run("synchronous cookie release transfers retry ownership",()=>CookieCleanupAsync(root,false));
            await Run("cookie cleanup exhaustion is bounded and observable",()=>CookieCleanupAsync(root,true));
            await Run("SQLite existing timeout and durability defaults",()=>SqliteDefaultsAsync(root));
            if(failures.Count>0)throw new AggregateException(failures);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Check(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"fixture escaped owned parent");
            Directory.Delete(root,true);
        }
    }
    private static Task LockedSettingsAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-lock"));
        var store=new DataStore(paths);
        var path=Path.Combine(paths.Data,"settings.json");
        var original="{\"volume\":23,\"channelHistoryKeys\":{\"fixture-channel\":\"fixture-history\"}}";
        File.WriteAllText(path,original);
        AppSettings fallback;
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))fallback=store.LoadSettings();
        Exception? error=null;
        try { store.SaveSettings(fallback); }catch(Exception ex){error=ex;}
        Check(error is IOException && File.ReadAllText(path)==original,"default settings replaced an unreadable original after unlock");
        Check(HasFailure(store),"settings read failure was not observable by the caller");
        Check(fallback.Incognito && !fallback.SyncHistory && fallback.AuthMode=="guest" && fallback.SelectedChannelId=="",
            "unreadable settings retained authenticated or history-enabled defaults");
        return Task.CompletedTask;
    }
    private static Task MalformedSettingsAsync(string root)
    {
        foreach(var original in new[]{"{broken","null"})
        {
            var paths=new RuntimePaths(Path.Combine(root,"settings-malformed-"+Guid.NewGuid().ToString("N")));
            var store=new DataStore(paths);var path=Path.Combine(paths.Data,"settings.json");
            File.WriteAllText(path,original);var fallback=store.LoadSettings();
            Check(HasFailure(store) && fallback.Incognito && !fallback.SyncHistory,"malformed settings did not select the conservative fallback");
            Exception? error=null;try{store.SaveSettings(fallback);}catch(Exception ex){error=ex;}
            Check(error is IOException && File.ReadAllText(path)==original,"malformed settings were silently replaced");
            File.WriteAllText(path,"{\"volume\":42}");var loaded=store.LoadSettings();loaded.Speed=1.5;store.SaveSettings(loaded);
            Check(!HasFailure(store) && store.LoadSettings().Volume==42 && store.LoadSettings().Speed==1.5,"explicit successful reload did not recover settings persistence");
            Check(!loaded.Incognito && loaded.SyncHistory,"successful settings reload retained temporary privacy fallback defaults");
        }
        return Task.CompletedTask;
    }
    private static Task FirstRunAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"first-run"));var store=new DataStore(paths);
        var settings=store.LoadSettings();Check(!HasFailure(store),"normal first run became a settings load failure");
        Check(!settings.Incognito && settings.SyncHistory && settings.MaxHeight==0,"normal first-run privacy or highest-quality defaults changed");
        var unrelated=Path.Combine(paths.Data,"settings.json.new");File.WriteAllText(unrelated,"unrelated stale temp sentinel");
        settings.Volume=37;store.SaveSettings(settings);
        Check(store.LoadSettings().Volume==37 && File.ReadAllText(unrelated)=="unrelated stale temp sentinel","settings save overwrote a temp it did not own");
        Check(!Directory.EnumerateFiles(paths.Data,"settings-*.new").Any(),"atomic settings temp was retained after success");
        return Task.CompletedTask;
    }
    private static Task SettingsFailureDefaultsAsync(string root)
    {
        var previousLanguage=UiText.Language;
        try
        {
            foreach(var language in new[]{"ko","en","ja"})
            {
                UiText.SetLanguage(language);
                var paths=new RuntimePaths(Path.Combine(root,"failure-defaults-"+language));
                var store=new DataStore(paths);var file=Path.Combine(paths.Data,"settings.json");
                const string original="{malformed synthetic settings";File.WriteAllText(file,original);
                var settings=store.LoadSettings();
                Check(store.HasSettingsLoadFailure && settings.Incognito && !settings.SyncHistory,"failed settings did not pause private activity");
                var message=store.SettingsLoadFailureMessage??"";
                var required=language switch
                {
                    "en"=>new[]{"Playback","sign-in","history","restart"},
                    "ja"=>new[]{"再生","ログイン","履歴","再起動"},
                    _=>new[]{"재생","로그인","기록","다시 실행"}
                };
                Check(required.All(word=>message.Contains(word,StringComparison.OrdinalIgnoreCase)),"settings recovery message omitted paused activity or restart instructions");
                Check(Capture(()=>store.SaveSettings(settings)) is IOException && File.ReadAllText(file)==original,"failed settings replaced the original file");
            }
        }
        finally{UiText.SetLanguage(previousLanguage);}
        return Task.CompletedTask;
    }
    private static Task NumericSettingsAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-numeric"));var store=new DataStore(paths);
        var file=Path.Combine(paths.Data,"settings.json");
        foreach(var row in new[]{(-5d,0.75,-3,0d,1d,0),(120d,3d,16385,100d,1d,0),
            (55.5,1.25,16384,55.5,1.25,16384),(0d,0.5,720,0d,0.5,720),(100d,2d,0,100d,2d,0)})
        {
            var original=JsonSerializer.Serialize(new{volume=row.Item1,speed=row.Item2,maxHeight=row.Item3,qualityPreferenceVersion=1},JsonDefaults.Options);
            File.WriteAllText(file,original);var settings=store.LoadSettings();
            Check(!store.HasSettingsLoadFailure && settings.Volume==row.Item4 && settings.Speed==row.Item5 && settings.MaxHeight==row.Item6,
                "valid JSON numeric values were not normalized to their supported ranges");
            Check(File.ReadAllText(file)==original,"numeric normalization rewrote settings during a read");
        }
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            var settings=new AppSettings{Volume=value,Speed=value,MaxHeight=-1};store.SaveSettings(settings);
            var loaded=store.LoadSettings();
            Check(!store.HasSettingsLoadFailure && loaded.Volume==70 && loaded.Speed==1 && loaded.MaxHeight==0,
                "nonfinite runtime settings were serialized or retained");
        }
        return Task.CompletedTask;
    }
    private static Task QualityPreferenceAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-quality"));var store=new DataStore(paths);
        var file=Path.Combine(paths.Data,"settings.json");
        const string legacy="{\"maxHeight\":1080,\"volume\":29}";File.WriteAllText(file,legacy);
        var migrated=store.LoadSettings();
        Check(migrated.MaxHeight==0 && migrated.Volume==29 && !store.HasSettingsLoadFailure,"legacy quality preference did not migrate to highest quality");
        Check(File.ReadAllText(file)==legacy,"quality migration overwrote its original during loading");
        store.SaveSettings(migrated);
        using(var json=JsonDocument.Parse(File.ReadAllText(file)))
            Check(json.RootElement.GetProperty("qualityPreferenceVersion").GetInt32()==1,"saved settings did not record the quality preference migration");
        migrated.MaxHeight=720;store.SaveSettings(migrated);
        Check(store.LoadSettings().MaxHeight==720,"a later explicit quality cap was reset to highest on reload");
        File.WriteAllText(file,"{\"maxHeight\":1440,\"qualityPreferenceVersion\":2}");
        Check(store.LoadSettings().MaxHeight==1440,"a newer recorded preference version was mistaken for legacy settings");
        return Task.CompletedTask;
    }
    private static Task SettingsReadBoundaryAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"settings-read-boundary"));var store=new DataStore(paths);
        var file=Path.Combine(paths.Data,"settings.json");
        foreach(var encoding in new Encoding[]{new UTF8Encoding(true),Encoding.Unicode,Encoding.BigEndianUnicode,Encoding.UTF32,new UTF32Encoding(true,true)})
        {
            File.WriteAllText(file,"{\"volume\":41,\"qualityPreferenceVersion\":1}",encoding);
            Check(store.LoadSettings().Volume==41 && !store.HasSettingsLoadFailure,"bounded settings reads lost ReadAllText BOM decoding behavior");
        }
        const int maximum=1024*1024;
        const string json="{\"volume\":42,\"qualityPreferenceVersion\":1}";
        File.WriteAllText(file,json+new string(' ',maximum-json.Length));
        Check(new FileInfo(file).Length==maximum && store.LoadSettings().Volume==42 && !store.HasSettingsLoadFailure,
            "settings at the exact byte limit were rejected");
        File.AppendAllText(file," ");var original=HashFile(file);var fallback=store.LoadSettings();
        Check(store.HasSettingsLoadFailure && fallback.Incognito && !fallback.SyncHistory,"oversized settings were accepted or lost the conservative fallback");
        Check(Capture(()=>store.SaveSettings(fallback)) is IOException && HashFile(file).SequenceEqual(original),"oversized settings original was changed");
        return Task.CompletedTask;
    }
    private static Task BoundedFileAsync(string root)
    {
        var directory=Path.Combine(root,"bounded-file");Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"synthetic.bin");byte[] original=[1,2,3,4,5,6,7,8];File.WriteAllBytes(file,original);
        Check(ReadBoundedFile(file,original.Length).SequenceEqual(original),"bounded reader rejected an exact-size file");
        Check(Capture(()=>ReadBoundedFile(file,original.Length-1)) is InvalidDataException,"bounded reader accepted a file beyond the byte limit");
        using(var writer=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
            Check(Capture(()=>ReadBoundedFile(file,original.Length)) is IOException,"bounded reader allowed a concurrent writer");
        Check(File.ReadAllBytes(file).SequenceEqual(original),"bounded reads changed their source");
        Check(Capture(()=>ReadBoundedFile(Path.Combine(directory,"missing.bin"),8)) is FileNotFoundException,"missing bounded files lost their ordinary missing-file status");
        return Task.CompletedTask;
    }
    private static Task EncryptedSessionBoundaryAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"encrypted-read-boundary"));var session=new SessionService(paths);
        var baseFile=Path.Combine(paths.Data,"session.dpapi");
        using(var stream=new FileStream(baseFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.SetLength(16*1024*1024+1);
        var original=HashFile(baseFile);var previousLanguage=UiText.Language;
        try
        {
            UiText.SetLanguage("en");
            var baseFailure=Capture(()=>{using var lease=session.AcquireBase();});
            Check(baseFailure is InvalidDataException && ExceptionContains(baseFailure,"too large"),"oversized base session reached DPAPI instead of the file-size boundary");
            Check(HashFile(baseFile).SequenceEqual(original),"oversized base session was modified");
            var cookie=Path.Combine(paths.Root,"synthetic-import.txt");
            File.WriteAllText(cookie,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n");
            using(var mutation=session.BeginMutation())
                Check(Capture(()=>session.ImportFile(cookie)) is InvalidDataException,"session rollback capture bypassed the encrypted-file size bound");
            Check(HashFile(baseFile).SequenceEqual(original) && !session.HasSessionRecoveryFailure,"oversized rollback capture changed or blocked the untouched original");
            const string channelId="synthetic-size-boundary";
            var directory=Path.Combine(paths.Data,"channels");Directory.CreateDirectory(directory);
            var channelFile=Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channelId)))+".dpapi");
            File.Copy(baseFile,channelFile);
            var selectedFailure=Capture(()=>session.RestoreSelectedChannel(channelId));
            Check(selectedFailure is InvalidDataException && ExceptionContains(selectedFailure,"too large") && session.SelectedChannel is null,
                "oversized selected session reached DPAPI or fell back to a different account");
            Check(HashFile(channelFile).SequenceEqual(original) && !Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),"oversized encrypted sessions changed originals or retained plaintext");
        }
        finally{UiText.SetLanguage(previousLanguage);}
        return Task.CompletedTask;
    }
    private static byte[] ReadBoundedFile(string file,int maximum)
    {
        var method=typeof(DataStore).Assembly.GetType("FkYouTube.Services.FileBoundary")!.GetMethod("ReadBoundedFile",BindingFlags.NonPublic|BindingFlags.Static)
            ?? throw new InvalidOperationException("The bounded storage reader is missing.");
        try{return (byte[])method.Invoke(null,[file,maximum])!;}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    private static byte[] HashFile(string file){using var stream=File.OpenRead(file);return SHA256.HashData(stream);}
    private static Exception? Capture(Action action){try{action();return null;}catch(Exception ex){return ex;}}
    private static bool ExceptionContains(Exception? error,string text)=>error is not null &&
        (error.Message.Contains(text,StringComparison.OrdinalIgnoreCase) || ExceptionContains(error.InnerException,text));
    private static async Task DataJunctionAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"data-junction"));Directory.Delete(paths.Data);
        var outside=Path.Combine(root,"data-target");Directory.CreateDirectory(outside);await JunctionAsync(paths.Data,outside);
        try
        {
            Exception? error=null;try{_=new DataStore(paths);}catch(Exception ex){error=ex;}
            Check(error is IOException && !Directory.EnumerateFileSystemEntries(outside).Any(),"DataStore followed the data junction before checking its boundary");
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(paths.Data);}
    }
    private static async Task FileJunctionsAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"file-junctions"));var store=new DataStore(paths);
        var outside=Path.Combine(root,"sidecar-target");Directory.CreateDirectory(outside);
        foreach(var name in new[]{"settings.json","viewer.sqlite-wal","viewer.sqlite-shm","viewer.sqlite-journal","viewer.sqlite"})
        {
            SqliteConnection.ClearAllPools();var link=Path.Combine(paths.Data,name);
            if(File.Exists(link))File.Delete(link);
            await JunctionAsync(link,outside);
            try
            {
                Exception? error=null;
                try
                {
                    if(name=="settings.json") { var fallback=store.LoadSettings();store.SaveSettings(fallback); }
                    else store.GetHistory("synthetic");
                }
                catch(Exception ex){error=ex;}
                Check(error is IOException && !Directory.EnumerateFileSystemEntries(outside).Any(),"storage followed an app-owned path junction: "+name);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(link);}
        }
    }
    private static Task ChannelStructureAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"channel-structure"));var session=new SessionService(paths);
        const string id="fixture-channel";
        var channel=new YouTubeChannel{Id=id,DataSyncId="fixture_user||",AuthUser=1};
        var directory=Path.Combine(paths.Data,"channels");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))+".dpapi");
        var validCookie="# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n";
        var malformed=new[]{"{\"channel\":null,\"cookies\":\"offline-only\"}",
            JsonSerializer.Serialize(new{channel,cookies=(string?)null},JsonDefaults.Options),
            JsonSerializer.Serialize(new{channel,cookies=""},JsonDefaults.Options),
            JsonSerializer.Serialize(new{channel=channel with{DataSyncId=null!},cookies=validCookie},JsonDefaults.Options)};
        foreach(var json in malformed)
        {
            var original=ProtectedData.Protect(Encoding.UTF8.GetBytes(json),null,DataProtectionScope.CurrentUser);File.WriteAllBytes(path,original);
            Exception? restoreError=null;try{session.RestoreSelectedChannel(id);}catch(Exception ex){restoreError=ex;}
            Exception? acquireError=null;try{using var lease=session.Acquire();}catch(Exception ex){acquireError=ex;}
            Check(restoreError is InvalidDataException && acquireError is InvalidDataException && session.SelectedChannel is null && File.ReadAllBytes(path).SequenceEqual(original),
                "malformed channel session escaped safe error handling, changed the original or silently fell back to the base account");
        }
        var good=JsonSerializer.SerializeToUtf8Bytes(new{channel,cookies=validCookie},JsonDefaults.Options);
        File.WriteAllBytes(path,ProtectedData.Protect(good,null,DataProtectionScope.CurrentUser));session.RestoreSelectedChannel(id);
        using(var lease=session.Acquire())Check(session.SelectedChannel?.Id==id && File.ReadAllText(lease.Path!).Contains("offline-only"),"valid synthetic selected channel no longer restores");
        Check(!Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),"channel test retained plaintext");
        return Task.CompletedTask;
    }
    private static Task SqliteDefaultsAsync(string root)
    {
        var paths=new RuntimePaths(Path.Combine(root,"sqlite-defaults"));var store=new DataStore(paths);
        using var db=(SqliteConnection)typeof(DataStore).GetMethod("Open",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(store,null)!;
        using var command=db.CreateCommand();command.CommandText="PRAGMA synchronous";var synchronous=Convert.ToInt32(command.ExecuteScalar());
        command.CommandText="PRAGMA busy_timeout";var busyTimeout=Convert.ToInt32(command.ExecuteScalar());
        Console.WriteLine($"STORAGE SQLITE commandTimeoutSeconds={command.CommandTimeout} synchronous={synchronous} busyTimeoutMs={busyTimeout}");
        Check(command.CommandTimeout>0 && synchronous==2,"SQLite durability or bounded lock retry defaults changed unexpectedly");
        return Task.CompletedTask;
    }
    private static async Task CookieCleanupAsync(string root,bool exhaust)
    {
        var paths=new RuntimePaths(Path.Combine(root,"cookie-retry-"+exhaust));var session=new SessionService(paths);
        var source=Path.Combine(paths.Root,"synthetic-import.txt");
        File.WriteAllText(source,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n");
        session.ImportFile(source);var lease=session.AcquireBase();var path=lease.Path!;
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            lease.Dispose();Check(File.Exists(path),"locked plaintext was not retained for bounded cleanup");
            if(exhaust)
            {
                Exception? error=null;var timer=Stopwatch.StartNew();
                try{await session.DrainCookieCleanupAsync();}catch(Exception ex){error=ex;}
                Check(error is IOException && !error.Message.Contains(path,StringComparison.OrdinalIgnoreCase) && timer.Elapsed<TimeSpan.FromSeconds(5),
                    "cookie deletion exhaustion was unobserved, unbounded or disclosed its sensitive path");
            }
        }
        if(exhaust)
        {
            // A persistent external lock still needs the existing next-start
            // recovery; keep failure visible rather than claiming it was erased.
            _=new SessionService(paths);
        }
        else
        {
            await session.DrainCookieCleanupAsync();await lease.DisposeAsync();
            Check(!File.Exists(path),"released cookie lease was lost before its retry completed");
        }
        Check(!File.Exists(path) && File.ReadAllText(source).Contains("offline-only"),"cleanup left plaintext or modified its original import");
    }
    private static bool HasFailure(DataStore store)=>(bool)(typeof(DataStore).GetProperty("HasSettingsLoadFailure")?.GetValue(store)??false);
    private static async Task JunctionAsync(string link,string target)
    {
        var start=new ProcessStartInfo("pwsh.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Value '"+target.Replace("'","''")+"' | Out-Null");
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();await Task.WhenAll(output,error);Check(process.ExitCode==0,"isolated junction fixture creation failed");
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
