using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Globalization;
using System.Text;

namespace FkYouTube.Services;

public sealed class DataStore
{
    private readonly string _connectionString;
    private readonly string _settingsPath;
    private readonly string _databasePath;
    private readonly object _settingsSync=new();
    public bool HasSettingsLoadFailure=>SettingsLoadFailureMessage is not null;
    public string? SettingsLoadFailureMessage {get;private set;}
    public DataStore(RuntimePaths paths)
    {
        _settingsPath = Path.Combine(paths.Data, "settings.json");
        _databasePath = Path.Combine(paths.Data,"viewer.sqlite");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling=true }.ToString();
        using var db=Open();
        using var command=db.CreateCommand();
        command.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS history(account TEXT NOT NULL, video_id TEXT NOT NULL, metadata TEXT NOT NULL,
              position REAL NOT NULL, duration REAL NOT NULL, updated TEXT NOT NULL, sync_status TEXT NOT NULL DEFAULT 'local',
              PRIMARY KEY(account,video_id));
            CREATE INDEX IF NOT EXISTS history_account_updated ON history(account,updated DESC);
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        // SQLite opens sidecars itself. Check all app-owned paths before the
        // first database open, including its parent directories.
        FileBoundary.EnsureRegularFileAndSiblings(_databasePath,"-wal","-shm","-journal");
        var connection=new SqliteConnection(_connectionString);
        try { connection.Open();return connection; }
        catch { connection.Dispose();throw; }
    }
    public AppSettings LoadSettings()
    {
        lock(_settingsSync)
        {
        try
        {
            FileBoundary.EnsureRegularPath(_settingsPath);
            var settings=JsonSerializer.Deserialize<AppSettings>(ReadSettingsText(),JsonDefaults.Options)
                ?? throw new JsonException(UiText.T("설정 형식이 올바르지 않습니다.","The settings format is invalid.","設定の形式が正しくありません。"));
            settings.AuthMode ??= "guest";
            settings.FirefoxProfile ??= "";
            settings.AccountKey ??= "guest";
            settings.SelectedChannelId ??= "";
            settings.ChannelHistoryKeys ??= [];
            foreach(var key in settings.ChannelHistoryKeys.Where(pair=>pair.Value is null).Select(pair=>pair.Key).ToArray())
                settings.ChannelHistoryKeys.Remove(key);
            settings.PlaylistUrl ??= "";
            settings.UiLanguage=UiText.NormalizeLanguage(settings.UiLanguage);
            if(settings.QualityPreferenceVersion<1)
            {settings.MaxHeight=0;settings.QualityPreferenceVersion=1;}
            NormalizeNumericSettings(settings);
            SettingsLoadFailureMessage=null;
            return settings;
        }
        catch(FileNotFoundException) { SettingsLoadFailureMessage=null;return new(); }
        catch(JsonException){return UnreadableSettings();}
        catch(InvalidDataException){return UnreadableSettings();}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
        {return UnreadableSettings();}
        }
    }
    private AppSettings UnreadableSettings()
    {
        SettingsLoadFailureMessage=UiText.T(
            "설정 파일을 읽지 못해 재생·로그인 연결·시청 기록 및 설정 저장을 일시 중지했습니다. 원본 파일은 보존했습니다. 설정 파일의 형식이나 접근 문제를 해결한 뒤 앱을 다시 실행하세요.",
            "Playback, sign-in connections, history, and settings saving are paused because the settings file could not be read. The original file was preserved. Fix the file format or access issue, then restart the app.",
            "設定ファイルを読み取れないため、再生・ログイン接続・視聴履歴・設定の保存を一時停止しています。元のファイルは保護されています。ファイルの形式やアクセスの問題を解決してから、アプリを再起動してください。");
        return new(){Incognito=true,SyncHistory=false};
    }
    private static void NormalizeNumericSettings(AppSettings settings)
    {
        settings.Volume=double.IsFinite(settings.Volume)?Math.Clamp(settings.Volume,0,100):70;
        if(settings.Speed is not (0.5 or 1 or 1.25 or 1.5 or 2))settings.Speed=1;
        if(settings.MaxHeight is <0 or >16384)settings.MaxHeight=0;
    }
    private string ReadSettingsText()
    {
        int[] delays=[25,50,100];
        for(var attempt=0;;attempt++)
        {
            try
            {
                using var bytes=new MemoryStream(FileBoundary.ReadBoundedFile(_settingsPath,1024*1024),writable:false);
                using var reader=new StreamReader(bytes,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
                return reader.ReadToEnd();
            }
            catch(IOException ex)when(IsTransientSettingsLock(ex) && attempt<delays.Length)
            {Thread.Sleep(delays[attempt]);}
        }
    }
    private static bool IsTransientSettingsLock(IOException error)=>
        error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
    public void EnsureSettingsWritable()
    {
        lock(_settingsSync)
            if(HasSettingsLoadFailure)
                throw new IOException(UiText.T("기존 설정을 읽지 못했으므로 원본을 덮어쓰지 않았습니다. 파일 접근 문제를 해결한 뒤 앱을 다시 실행하세요.","The original settings were preserved because they could not be read. Resolve the file access issue, then restart the app.","既存の設定を読み取れなかったため、元のファイルを上書きしませんでした。ファイルへのアクセスの問題を解決してから、アプリを再起動してください。"));
    }
    public void SaveSettings(AppSettings settings)
    {
        lock(_settingsSync)
        {
            EnsureSettingsWritable();
            FileBoundary.EnsureRegularPath(_settingsPath);
            NormalizeNumericSettings(settings);
            settings.QualityPreferenceVersion=1;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(settings,JsonDefaults.Options);
            var temp=Path.Combine(Path.GetDirectoryName(_settingsPath)!,"settings-"+Guid.NewGuid().ToString("N")+".new");
            var created=false;
            try
            {
                FileBoundary.EnsureRegularPath(temp);
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {created=true;stream.Write(bytes);stream.Flush(true);}
                FileBoundary.EnsureRegularPath(_settingsPath);
                FileBoundary.EnsureRegularPath(temp);
                File.Move(temp,_settingsPath,true);
                created=false;
            }
            finally
            {
                if(created)try{FileBoundary.EnsureRegularPath(temp);File.Delete(temp);}
                    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            }
        }
    }
    public void SavePosition(string account,VideoItem video,double position,double duration)
    {
        if(string.IsNullOrWhiteSpace(video.Id) || !double.IsFinite(position) || position<0) return;
        using var db=Open(); using var c=db.CreateCommand();
        c.CommandText="""
          INSERT INTO history(account,video_id,metadata,position,duration,updated,sync_status) VALUES($a,$v,$m,$p,$d,$t,'local')
          ON CONFLICT(account,video_id) DO UPDATE SET metadata=$m,position=$p,duration=$d,updated=$t;
          """;
        c.Parameters.AddWithValue("$a",account); c.Parameters.AddWithValue("$v",video.Id);
        c.Parameters.AddWithValue("$m",JsonSerializer.Serialize(video with{RecommendationActions=null},JsonDefaults.Options));
        c.Parameters.AddWithValue("$p",position); c.Parameters.AddWithValue("$d",double.IsFinite(duration)?duration:0);
        c.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O")); c.ExecuteNonQuery();
    }
    public void SetSyncStatus(string account,string videoId,string status)
    {
        SetSyncStatuses(account,[videoId],status);
    }
    public void SetSyncStatuses(string account,IReadOnlyList<string> videoIds,string status)
    {
        if(videoIds.Count==0)return;
        using var db=Open();using var transaction=db.BeginTransaction();using var c=db.CreateCommand();
        c.Transaction=transaction;c.CommandText="UPDATE history SET sync_status=$s WHERE account=$a AND video_id=$v";
        c.Parameters.AddWithValue("$s",status);c.Parameters.AddWithValue("$a",account);
        var video=c.Parameters.Add("$v",SqliteType.Text);c.Prepare();
        foreach(var id in videoIds){video.Value=id;c.ExecuteNonQuery();}
        transaction.Commit();
    }
    public IReadOnlyList<HistoryEntry> GetHistory(string account,int count=500)=>GetHistory(account,out _,count);
    public IReadOnlyList<HistoryEntry> GetHistory(string account,out int skippedCount,int count=500)
    {
        using var db=Open();using var c=db.CreateCommand(); c.CommandText="SELECT metadata,position,duration,updated,sync_status,video_id FROM history WHERE account=$a ORDER BY updated DESC LIMIT $n";
        c.Parameters.AddWithValue("$a",account);c.Parameters.AddWithValue("$n",count);
        using var r=c.ExecuteReader();var result=new List<HistoryEntry>();
        skippedCount=0;
        while(r.Read())
        {
            try
            {
                var v=JsonSerializer.Deserialize<VideoItem>(r.GetString(0),JsonDefaults.Options);
                var position=r.GetDouble(1);var duration=r.GetDouble(2);
                if(v==null || string.IsNullOrWhiteSpace(v.Id) || v.Id!=r.GetString(5) || v.Title is null || v.Url is null ||
                    v.Channel is null || v.Kind is null || !double.IsFinite(position) || position<0 ||
                    !double.IsFinite(duration) || duration<0 || !DateTimeOffset.TryParse(r.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.None,out var updated))
                {skippedCount++;continue;}
                result.Add(new(v,position,duration,updated,r.GetString(4)));
            }
            catch(Exception ex)when(ex is JsonException or FormatException or InvalidCastException or OverflowException)
            {skippedCount++;}
        }
        return result;
    }
    public double GetResumePosition(string account,string videoId)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT position,duration FROM history WHERE account=$a AND video_id=$v";
        c.Parameters.AddWithValue("$a",account);c.Parameters.AddWithValue("$v",videoId);using var r=c.ExecuteReader();
        if(!r.Read())return 0;var p=r.GetDouble(0);var d=r.GetDouble(1);return d>0 && p>=d-3?0:p;
    }
}
