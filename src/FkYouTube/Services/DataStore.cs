using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Globalization;
using System.Text;

namespace FkYouTube.Services;

public sealed record HistoryOutboxSession(bool Enabled,string Epoch);
public sealed record HistoryOutboxItem(string EventId,string Account,string Channel,string Epoch,string VideoId);
public sealed record HistoryOutboxCounts(int Pending,int Held,int Unknown,int Failed);

public sealed class DataStore
{
    private readonly string _connectionString;
    private readonly string _settingsPath;
    private readonly string _databasePath;
    private readonly object _settingsSync=new();
    public bool HasSettingsLoadFailure=>SettingsLoadFailureMessage is not null;
    public string? SettingsLoadFailureMessage {get;private set;}
    public bool HistorySyncPreferenceMigrated {get;private set;}
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
            CREATE TABLE IF NOT EXISTS history_outbox_control(
              id INTEGER NOT NULL PRIMARY KEY CHECK(id=1),
              enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),epoch TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history_outbox(
              event_id TEXT NOT NULL PRIMARY KEY,account TEXT NOT NULL,channel TEXT NOT NULL,
              epoch TEXT NOT NULL,video_id TEXT NOT NULL,created TEXT NOT NULL,
              state TEXT NOT NULL CHECK(state IN('pending','inflight','sent','failed','unknown','held')),
              error_code TEXT);
            CREATE INDEX IF NOT EXISTS history_outbox_scope_state ON history_outbox(account,channel,epoch,state);
            CREATE INDEX IF NOT EXISTS history_outbox_account_video ON history_outbox(account,video_id);
            CREATE INDEX IF NOT EXISTS history_outbox_visible_counts ON history_outbox(account,channel,state)
              WHERE state IN('pending','held','unknown','failed');
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        // SQLite opens sidecars itself. Check all app-owned paths before the
        // first database open, including its parent directories.
        FileBoundary.EnsureRegularFileAndSiblings(_databasePath,"-wal","-shm","-journal");
        var connection=new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // The outbox must commit its claim before any network request.
            // Set this per connection, including connections returned by pooling.
            using var command=connection.CreateCommand();command.CommandText="PRAGMA synchronous=FULL";
            command.ExecuteNonQuery();return connection;
        }
        catch { connection.Dispose();throw; }
    }
    public AppSettings LoadSettings()
    {
        lock(_settingsSync)
        {
        HistorySyncPreferenceMigrated=false;
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
            HistorySyncPreferenceMigrated=NormalizeHistorySyncPreference(settings);
            NormalizeNumericSettings(settings);
            SettingsLoadFailureMessage=null;
            return settings;
        }
        catch(FileNotFoundException)
        {
            SettingsLoadFailureMessage=null;
            return new(){HistorySyncPreferenceVersion=AppSettings.CurrentHistorySyncPreferenceVersion};
        }
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
    private static bool NormalizeHistorySyncPreference(AppSettings settings)
    {
        if(settings.HistorySyncPreferenceVersion>=AppSettings.CurrentHistorySyncPreferenceVersion)return false;
        // A legacy true value could be the old default rather than an explicit opt-in.
        settings.SyncHistory=false;
        settings.HistorySyncPreferenceVersion=AppSettings.CurrentHistorySyncPreferenceVersion;
        return true;
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
            NormalizeHistorySyncPreference(settings);
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
    public HistoryOutboxSession InitializeHistoryOutbox(bool enabled,bool holdPending)
    {
        using var db=Open();using var transaction=db.BeginTransaction();
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="INSERT OR IGNORE INTO history_outbox_control(id,enabled,epoch) VALUES(1,$enabled,$epoch)";
            c.Parameters.AddWithValue("$enabled",enabled?1:0);c.Parameters.AddWithValue("$epoch",NewHistoryOutboxEpoch());
            c.ExecuteNonQuery();
        }
        var session=ReadHistoryOutboxSession(db,transaction)
            ?? throw new InvalidDataException("The history outbox permission record could not be initialized.");
        // A stale settings true must never undo a database revocation whose
        // settings-file save failed. Only the explicit setter can enable it.
        if(!enabled && session.Enabled)
        {
            session=new(false,NewHistoryOutboxEpoch());
            WriteHistoryOutboxSession(db,transaction,session);
        }
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="UPDATE history_outbox SET state='unknown',error_code='interrupted' WHERE state='inflight'";
            c.ExecuteNonQuery();
        }
        if(!session.Enabled || holdPending)HoldPendingHistoryOutbox(db,transaction);
        transaction.Commit();return session;
    }
    public HistoryOutboxSession SetHistoryOutboxEnabled(bool enabled)
    {
        using var db=Open();using var transaction=db.BeginTransaction();
        var session=new HistoryOutboxSession(enabled,NewHistoryOutboxEpoch());
        WriteHistoryOutboxSession(db,transaction,session);
        // Enabling grants future work; it does not silently revive older work.
        HoldPendingHistoryOutbox(db,transaction);
        transaction.Commit();return session;
    }
    public HistoryOutboxSession HoldHistoryOutbox()
    {
        using var db=Open();using var transaction=db.BeginTransaction();
        var previous=ReadHistoryOutboxSession(db,transaction);
        var session=new HistoryOutboxSession(previous?.Enabled??false,NewHistoryOutboxEpoch());
        WriteHistoryOutboxSession(db,transaction,session);
        HoldPendingHistoryOutbox(db,transaction);
        transaction.Commit();return session;
    }
    public void HoldHistoryOutboxScope(string account,string channel)
    {
        ValidateHistoryOutboxIdentity(account,nameof(account));ValidateHistoryOutboxChannel(channel,nameof(channel));
        using var db=Open();using var c=db.CreateCommand();
        c.CommandText="UPDATE history_outbox SET state='held',error_code=NULL WHERE account=$account AND channel=$channel AND state='pending'";
        c.Parameters.AddWithValue("$account",account);c.Parameters.AddWithValue("$channel",channel);c.ExecuteNonQuery();
    }
    public int ResumeHeldHistoryOutbox(string account,string channel,string epoch)
    {
        ValidateHistoryOutboxScope(account,channel,epoch);
        using var db=Open();using var transaction=db.BeginTransaction();
        var session=ReadHistoryOutboxSession(db,transaction);
        if(session is not {Enabled:true} || session.Epoch!=epoch)return 0;
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="UPDATE history_outbox SET state='pending',epoch=$epoch,error_code=NULL WHERE account=$account AND channel=$channel AND state='held'";
        AddHistoryOutboxScope(c,account,channel,epoch);
        var resumed=c.ExecuteNonQuery();transaction.Commit();return resumed;
    }
    public bool EnqueueHistoryOutbox(string eventId,string account,string channel,string epoch,string videoId)
    {
        ValidateHistoryOutboxIdentity(eventId,nameof(eventId));
        ValidateHistoryOutboxScope(account,channel,epoch);
        ValidateHistoryOutboxVideoId(videoId,nameof(videoId));
        using var db=Open();using var transaction=db.BeginTransaction();
        var session=ReadHistoryOutboxSession(db,transaction);
        if(session is not {Enabled:true} || session.Epoch!=epoch)return false;
        var item=new HistoryOutboxItem(eventId,account,channel,epoch,videoId);
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="SELECT event_id,account,channel,epoch,video_id FROM history_outbox WHERE event_id=$event";
            c.Parameters.AddWithValue("$event",eventId);
            using var r=c.ExecuteReader();
            if(r.Read())
            {
                if(ReadHistoryOutboxItem(r)!=item)
                    throw new InvalidDataException("The history outbox event identifier belongs to different work.");
                r.Close();transaction.Commit();return true;
            }
        }
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="""
                INSERT INTO history_outbox(event_id,account,channel,epoch,video_id,created,state)
                  VALUES($event,$account,$channel,$epoch,$video,$created,'pending');
                UPDATE history SET sync_status='pending' WHERE account=$account AND video_id=$video;
                """;
            c.Parameters.AddWithValue("$event",eventId);AddHistoryOutboxScope(c,account,channel,epoch);
            c.Parameters.AddWithValue("$video",videoId);
            c.Parameters.AddWithValue("$created",DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture));
            c.ExecuteNonQuery();
        }
        transaction.Commit();return true;
    }
    public void PreserveHeldHistoryOutbox(string eventId,string account,string channel,string epoch,string videoId)
    {
        // The caller may only pass a draft accepted while consent was valid.
        // Its eventual storage must not grant permission to send it.
        ValidateHistoryOutboxIdentity(eventId,nameof(eventId));
        ValidateHistoryOutboxScope(account,channel,epoch);
        ValidateHistoryOutboxVideoId(videoId,nameof(videoId));
        using var db=Open();using var transaction=db.BeginTransaction();
        var item=new HistoryOutboxItem(eventId,account,channel,epoch,videoId);
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="SELECT event_id,account,channel,epoch,video_id FROM history_outbox WHERE event_id=$event";
            c.Parameters.AddWithValue("$event",eventId);
            using var r=c.ExecuteReader();
            if(r.Read())
            {
                if(ReadHistoryOutboxItem(r)!=item)
                    throw new InvalidDataException("The history outbox event identifier belongs to different work.");
                r.Close();transaction.Commit();return;
            }
        }
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="""
                INSERT INTO history_outbox(event_id,account,channel,epoch,video_id,created,state)
                  VALUES($event,$account,$channel,$epoch,$video,$created,'held');
                UPDATE history SET sync_status='held' WHERE account=$account AND video_id=$video;
                """;
            c.Parameters.AddWithValue("$event",eventId);AddHistoryOutboxScope(c,account,channel,epoch);
            c.Parameters.AddWithValue("$video",videoId);
            c.Parameters.AddWithValue("$created",DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture));
            c.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public HistoryOutboxItem? ClaimNextHistoryOutbox(string account,string channel,string epoch)
    {
        ValidateHistoryOutboxScope(account,channel,epoch);
        using var db=Open();using var transaction=db.BeginTransaction();
        var session=ReadHistoryOutboxSession(db,transaction);
        if(session is not {Enabled:true} || session.Epoch!=epoch)return null;
        HistoryOutboxItem item;
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="SELECT event_id,account,channel,epoch,video_id FROM history_outbox WHERE account=$account AND channel=$channel AND epoch=$epoch AND state='pending' ORDER BY rowid LIMIT 1";
            AddHistoryOutboxScope(c,account,channel,epoch);
            using var r=c.ExecuteReader();if(!r.Read())return null;
            item=ReadHistoryOutboxItem(r);
        }
        using(var c=db.CreateCommand())
        {
            c.Transaction=transaction;
            c.CommandText="UPDATE history_outbox SET state='inflight',error_code=NULL WHERE event_id=$event AND state='pending'";
            c.Parameters.AddWithValue("$event",item.EventId);
            if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("The history outbox item could not be claimed.");
        }
        // The caller must not send until this durable claim has committed.
        transaction.Commit();return item;
    }
    public void CompleteHistoryOutbox(string eventId,string state,string? errorCode=null,bool holdRemaining=false)
    {
        ValidateHistoryOutboxIdentity(eventId,nameof(eventId));
        if(state is not ("sent" or "failed" or "unknown"))
            throw new ArgumentException("A history outbox completion must be sent, failed, or unknown.",nameof(state));
        if(errorCode is not null)ValidateHistoryOutboxIdentity(errorCode,nameof(errorCode),64);
        using var db=Open();using var transaction=db.BeginTransaction();using var c=db.CreateCommand();
        c.Transaction=transaction;
        c.CommandText="UPDATE history_outbox SET state=$state,error_code=$error WHERE event_id=$event AND state='inflight'";
        c.Parameters.AddWithValue("$event",eventId);c.Parameters.AddWithValue("$state",state);
        c.Parameters.AddWithValue("$error",(object?)errorCode??DBNull.Value);
        if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("The history outbox inflight item could not be completed.");
        if(holdRemaining)
        {
            using var hold=db.CreateCommand();hold.Transaction=transaction;
            hold.CommandText="""
                UPDATE history_outbox SET state='held',error_code=NULL WHERE state='pending'
                  AND account=(SELECT account FROM history_outbox WHERE event_id=$event)
                  AND channel=(SELECT channel FROM history_outbox WHERE event_id=$event)
                """;
            hold.Parameters.AddWithValue("$event",eventId);hold.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public HistoryOutboxCounts GetHistoryOutboxCounts(string account,string channel)
    {
        ValidateHistoryOutboxIdentity(account,nameof(account));ValidateHistoryOutboxChannel(channel,nameof(channel));
        using var db=Open();using var c=db.CreateCommand();
        c.CommandText="""
            SELECT COALESCE(SUM(CASE WHEN state='pending' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='held' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='unknown' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='failed' THEN 1 ELSE 0 END),0)
            FROM history_outbox WHERE account=$account AND channel=$channel AND state IN('pending','held','unknown','failed')
            """;
        c.Parameters.AddWithValue("$account",account);c.Parameters.AddWithValue("$channel",channel);
        using var r=c.ExecuteReader();r.Read();
        return new(checked((int)r.GetInt64(0)),checked((int)r.GetInt64(1)),checked((int)r.GetInt64(2)),checked((int)r.GetInt64(3)));
    }
    private static string NewHistoryOutboxEpoch()=>Guid.NewGuid().ToString("N");
    private static HistoryOutboxSession? ReadHistoryOutboxSession(SqliteConnection db,SqliteTransaction transaction)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="SELECT enabled,epoch FROM history_outbox_control WHERE id=1";
        using var r=c.ExecuteReader();if(!r.Read())return null;
        var enabled=r.GetInt64(0);var epoch=r.GetString(1);
        if(enabled is not (0 or 1) || !IsHistoryOutboxIdentity(epoch))
            throw new InvalidDataException("The history outbox permission record is invalid.");
        return new(enabled==1,epoch);
    }
    private static void WriteHistoryOutboxSession(SqliteConnection db,SqliteTransaction transaction,HistoryOutboxSession session)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="INSERT INTO history_outbox_control(id,enabled,epoch) VALUES(1,$enabled,$epoch) ON CONFLICT(id) DO UPDATE SET enabled=$enabled,epoch=$epoch";
        c.Parameters.AddWithValue("$enabled",session.Enabled?1:0);c.Parameters.AddWithValue("$epoch",session.Epoch);
        c.ExecuteNonQuery();
    }
    private static void HoldPendingHistoryOutbox(SqliteConnection db,SqliteTransaction transaction)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="UPDATE history_outbox SET state='held',error_code=NULL WHERE state='pending'";c.ExecuteNonQuery();
    }
    private static void AddHistoryOutboxScope(SqliteCommand command,string account,string channel,string epoch)
    {
        command.Parameters.AddWithValue("$account",account);command.Parameters.AddWithValue("$channel",channel);
        command.Parameters.AddWithValue("$epoch",epoch);
    }
    private static HistoryOutboxItem ReadHistoryOutboxItem(SqliteDataReader reader)
    {
        var item=new HistoryOutboxItem(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4));
        if(!IsHistoryOutboxIdentity(item.EventId) || !IsHistoryOutboxIdentity(item.Account) ||
            !IsHistoryOutboxChannel(item.Channel) || !IsHistoryOutboxIdentity(item.Epoch) || !IsHistoryOutboxVideoId(item.VideoId))
            throw new InvalidDataException("The history outbox work identity is invalid.");
        return item;
    }
    private static bool IsHistoryOutboxIdentity(string? value,int maxLength=128)=>
        value is {Length:>0} && value.Length<=maxLength && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    public static bool IsHistoryOutboxChannel(string? value)
    {
        // youtube_session.parse_accounts produces this opaque local identifier;
        // SessionService preserves it rather than substituting a public UC ID.
        if(value is not {Length:72} || !value.StartsWith("channel:",StringComparison.Ordinal))return false;
        for(var index=8;index<value.Length;index++)
            if(value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f'))return false;
        return true;
    }
    private static bool IsHistoryOutboxVideoId(string? value)=>value is {Length:11} && IsHistoryOutboxIdentity(value,11);
    private static void ValidateHistoryOutboxIdentity(string? value,string parameterName,int maxLength=128)
    {
        if(!IsHistoryOutboxIdentity(value,maxLength))
            throw new ArgumentException($"History outbox identifiers must contain 1 to {maxLength} ASCII letters, digits, underscores, or hyphens.",parameterName);
    }
    private static void ValidateHistoryOutboxVideoId(string? value,string parameterName)
    {
        if(!IsHistoryOutboxVideoId(value))throw new ArgumentException("A history outbox video identifier must contain exactly 11 ASCII letters, digits, underscores, or hyphens.",parameterName);
    }
    private static void ValidateHistoryOutboxChannel(string? value,string parameterName)
    {
        if(!IsHistoryOutboxChannel(value))
            throw new ArgumentException("A history outbox channel identifier must be channel: followed by exactly 64 lowercase hexadecimal digits.",parameterName);
    }
    private static void ValidateHistoryOutboxScope(string account,string channel,string epoch)
    {
        ValidateHistoryOutboxIdentity(account,nameof(account));ValidateHistoryOutboxChannel(channel,nameof(channel));
        ValidateHistoryOutboxIdentity(epoch,nameof(epoch));
    }
    public IReadOnlyList<HistoryEntry> GetHistory(string account,int count=500)=>GetHistory(account,out _,count);
    public IReadOnlyList<HistoryEntry> GetHistory(string account,out int skippedCount,int count=500)
    {
        using var db=Open();using var c=db.CreateCommand(); c.CommandText="""
            SELECT h.metadata,h.position,h.duration,h.updated,
              CASE WHEN h.sync_status='confirmed' THEN h.sync_status ELSE
                COALESCE((SELECT o.state FROM history_outbox o WHERE o.account=h.account AND o.video_id=h.video_id ORDER BY o.rowid DESC LIMIT 1),h.sync_status)
              END,h.video_id
            FROM history h WHERE h.account=$a ORDER BY h.updated DESC LIMIT $n
            """;
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
