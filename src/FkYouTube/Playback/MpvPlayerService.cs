using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;

namespace FkYouTube.Playback;

/// <summary>One app-owned mpv process, native video host, and asynchronous JSON IPC connection.</summary>
public sealed class MpvPlayerService : IAsyncDisposable
{
    private readonly RuntimePaths _paths;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<long, long> _entryGenerations = new();
    private readonly HashSet<long> _loadedEntries = new();
    private readonly Dictionary<long, JsonElement> _endedEntries = new();
    private readonly HashSet<Session> _retainedCookieSessions = [];
    private Session? _session;
    private Session? _lastLoadSession;
    private PreparedNext? _preparedNext;
    private nint _host;
    private long _mediaGeneration;
    private long _stoppedCookieConsumerGeneration = -1;
    private long? _activeEntry;
    private long _lastSnapshotAt;
    private PlaybackSnapshot _snapshot = new();
    private bool _subtitleOff;
    private string? _subtitleLanguage;
    private long _subtitleSelectionGeneration;
    private long? _subtitleSelectedId;
    private const int MaxSelectedExternalSubtitles = 12;
    private const int MaxRetainedPlaybackCookies = 12;
    private static string MediaFailureMessage => UiText.T("영상을 재생하지 못했습니다. 다시 재생하거나 다른 영상을 선택하세요.","Could not play this video. Try again or choose another video.","動画を再生できませんでした。再試行するか、別の動画を選択してください。");
    private const string ExtractionResultProperty = "user-data/mpv/ytdl/json-subprocess-result";
    private int _disposed;
    private int _disposeComplete;
    private static readonly JsonSerializerOptions IpcJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // One logical video's desired pause state spans its quality reloads. A
    // committed state is recorded only after native acknowledgement, never merely
    // because a later button requested it or a stale UI snapshot displayed it.
    public sealed class PauseIntent(bool paused)
    {
        private readonly object _gate=new();
        private bool _paused=paused,_confirmed=paused;
        private bool _confirmationKnown=true;
        private long _revision,_confirmedRevision;
        public readonly record struct State(long Revision,bool Paused);
        public bool Paused {get{lock(_gate)return _paused;}}
        public State Toggle() {lock(_gate){_paused=!_paused;return new(++_revision,_paused);}}
        internal State Capture() {lock(_gate)return new(_revision,_paused);}
        internal bool TryGetPending(long revision,out State state)
        {
            lock(_gate)
            {
                state=new(_revision,_paused);
                return _revision==revision && (!_confirmationKnown || _paused!=_confirmed);
            }
        }
        internal void Confirm(State state)
        {lock(_gate){if(state.Revision>=_confirmedRevision){_confirmed=state.Paused;_confirmedRevision=state.Revision;_confirmationKnown=true;}}}
        internal void MarkUncertain(State state)
        {lock(_gate){if(state.Revision>=_confirmedRevision)_confirmationKnown=false;}}
        public bool TryRevert(long revision)
        {lock(_gate){if(_revision!=revision || !_confirmationKnown)return false;_paused=_confirmed;return true;}}
    }

    public event Action<PlaybackSnapshot>? SnapshotChanged;
    public event Action<PlaybackFailure>? PlaybackError;
    public PlaybackSnapshot Snapshot { get { lock (_stateLock) return _snapshot; } }
    public string? PreferredSubtitleLanguage => Volatile.Read(ref _subtitleLanguage);
    public bool SubtitlesOff => Volatile.Read(ref _subtitleOff);
    public bool HasActiveMedia(long expectedGeneration)
    {
        lock (_stateLock)
            return expectedGeneration > 0 && _snapshot.MediaGeneration == expectedGeneration && _snapshot.Loaded && !_snapshot.Ended &&
                _session is { } session && !session.Process.HasExited && session.Pipe.IsConnected && !session.Token.IsCancellationRequested;
    }

    /// <summary>An opaque receipt naming one native session and its observed media generation.</summary>
    public sealed class PlaybackOwner
    {
        internal MpvPlayerService Player { get; }
        internal object? Session { get; }
        public long MediaGeneration { get; }
        internal PlaybackOwner(MpvPlayerService player, object? session, long generation)
        { Player = player; Session = session; MediaGeneration = generation; }
    }

    public PlaybackOwner CapturePlaybackOwner()
    {
        lock (_stateLock)
            return new(this, _session ?? (_lastLoadSession?.MediaGeneration == _snapshot.MediaGeneration ? _lastLoadSession : null),
                _snapshot.MediaGeneration);
    }

    /// <summary>Transfers a lease to its exact native consumer until process-tree termination is verified.</summary>
    public void RetainPlaybackCookies(PlaybackOwner owner, CookieLease lease)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(owner.Player, this)) throw new ArgumentException("The playback owner belongs to another player.", nameof(owner));
        var release = false;
        lock (_stateLock)
        {
            if (lease.Path is null || owner.Session is not Session session || session.CookieConsumersStopped) release = true;
            else { session.RetainedCookieLeases.Add(lease); _retainedCookieSessions.Add(session); }
        }
        if (release) lease.Dispose();
    }

    /// <summary>Stops only the captured session/generation; a stale caller cannot close newer playback.</summary>
    public async Task<bool> StopOwnedPlaybackAsync(PlaybackOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(owner.Player, this)) throw new ArgumentException("The playback owner belongs to another player.", nameof(owner));
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (owner.Session is not Session session) return true;
                lock (_stateLock)
                {
                    if (session.CookieConsumersStopped) return true;
                    if (!session.ConnectionClosed && (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != owner.MediaGeneration)) return false;
                }
                await CloseSessionAsync(session).ConfigureAwait(false);
                UpdateGeneration(owner.MediaGeneration, snapshot => snapshot with { Ended = true, Loaded = false, Buffering = false, Seeking = false });
                return session.CookieConsumersStopped;
            }
            finally { _startGate.Release(); }
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>The caller must also retain ownership of the lease issued for this exact load generation.</summary>
    public bool CanReleasePlaybackCookies(long expectedGeneration)
    {
        lock (_stateLock)
            return expectedGeneration > 0 && _snapshot.MediaGeneration == expectedGeneration &&
                (_snapshot.Loaded && _snapshot.CanReleaseCookies || _stoppedCookieConsumerGeneration == expectedGeneration);
    }

    public MpvPlayerService(RuntimePaths paths) => _paths = paths;

    public async Task StartAsync(nint host)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (host == 0) throw new ArgumentException("The native video host has not been created.", nameof(host));
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await DrainDetachedCookieSessionsAsync().ConfigureAwait(false);
            if (_session is { } existing && _host == host && !existing.Process.HasExited && existing.Pipe.IsConnected && !existing.Token.IsCancellationRequested) return;
            if (_session is { } old) await CloseSessionAsync(old).ConfigureAwait(false);
            await DrainDetachedCookieSessionsAsync().ConfigureAwait(false);
            _host = host;
            var pipeName = "fkyt-mpv-" + Guid.NewGuid().ToString("N");
            var args = new List<string>
            {
                "--no-config", "--load-scripts=no", "--idle=yes", "--force-window=yes", "--terminal=no", "--input-terminal=no",
                "--input-default-bindings=no", "--input-vo-keyboard=no", "--osc=no", "--osd-bar=no",
                "--load-console=no", "--load-commands=no", "--load-select=no", "--load-stats-overlay=no",
                "--hwdec=auto", "--vo=gpu-next", "--gpu-context=d3d11", "--ytdl=yes",
                "--cache-secs=15", "--demuxer-max-bytes=32MiB", "--demuxer-max-back-bytes=8MiB", "--prefetch-playlist=yes",
                // A prefetch opener can run before the next entry's file-local
                // options apply. Match the pinned YouTube extractor's 10 MiB
                // request limit from process start; per-file hints still follow.
                "--curl-max-request-size=10485760", "--stream-lavf-o=request_size=10485760",
                "--sub-auto=no", "--slang=ko,kor,en,eng", "--sub-visibility=yes",
                "--wid=" + unchecked((uint)(long)host).ToString(CultureInfo.InvariantCulture),
                "--input-ipc-server=\\\\.\\pipe\\" + pipeName,
                "--script-opts=ytdl_hook-ytdl_path=" + MpvListValue(_paths.YtDlp)
            };
            var process = OwnedProcess.Start(_paths.Mpv, args, _paths.Root);
            var session = new Session(process, new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous));
            lock (_stateLock)
            {
                _session = session;
                _entryGenerations.Clear(); _loadedEntries.Clear(); _endedEntries.Clear(); _activeEntry = null; _preparedNext = null;
            }
            session.OutputTask = DrainAsync(session.Process.StandardOutput, session);
            session.ErrorTask = DrainAsync(session.Process.StandardError, session);
            session.ExitTask = WatchExitAsync(session);
            try
            {
                await session.Pipe.ConnectAsync(10000, session.Token).ConfigureAwait(false);
                session.Reader = new StreamReader(session.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                session.Writer = new StreamWriter(session.Pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                session.ReadTask = ReadMessagesAsync(session);
                var observations = new[] { "time-pos", "duration", "pause", "paused-for-cache", "seeking", "speed", "volume", "hwdec-current",ExtractionResultProperty+"/status",ExtractionResultProperty+"/stderr" };
                for (var i = 0; i < observations.Length; i++)
                    await SendAsync(session, new object[] { "observe_property", i + 1, observations[i] }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await CloseSessionAsync(session).ConfigureAwait(false);
                throw new InvalidOperationException(UiText.T("mpv 연결을 시작하지 못했습니다. 다시 재생해 주세요.","Could not connect to mpv. Try playing the video again.","mpv に接続できませんでした。動画をもう一度再生してください。"), ex);
            }
        }
        finally { _startGate.Release(); }
    }

    public async Task LoadAsync(string youtubeUrl, string? cookiePath, int maxHeight, double startSeconds = 0,
        CancellationToken cancellationToken = default, YouTubeChannel? accountContext = null, PreparedVideo? resolvedStream = null,
        Func<CancellationToken, Task<PreparedVideo>>? resolveStream = null, bool startPaused = false, PauseIntent? pauseIntent = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Supplied metadata is known before ownership changes. A late account resolver
        // still runs after stop and follows the owned-load failure cleanup below.
        var directSource = resolvedStream is null ? null : ValidateDirectMedia(resolvedStream);
        if (!Uri.TryCreate(youtubeUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !(url.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A YouTube HTTPS video URL is required.", nameof(youtubeUrl));
        if (cookiePath is not null && !File.Exists(cookiePath)) throw new FileNotFoundException("The selected session cookie file is missing.", cookiePath);
        if (!File.Exists(_paths.YtDlp)) throw new FileNotFoundException("yt-dlp runtime is missing.", _paths.YtDlp);
        if (!File.Exists(_paths.Deno)) throw new FileNotFoundException("Deno runtime is missing.", _paths.Deno);
        var extractorArguments = YouTubeAccountOptions.ExtractorArguments(accountContext);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long generation = 0;
        Session? loadingSession = null;
        try
        {
            if (_host == 0) throw new InvalidOperationException("Call StartAsync with a live MpvHost before loading a video.");
            if (accountContext is { AuthUser: > 0 } && resolvedStream is null && resolveStream is null)
                lock (_stateLock)
                {
                    if (_session is not { } existing || _preparedNext is not { } candidate || !ReferenceEquals(candidate.Session, existing) ||
                        candidate.OriginGeneration != _snapshot.MediaGeneration || candidate.VideoUrl != youtubeUrl ||
                        candidate.MaxHeight != YouTubeService.NormalizeMaxHeight(maxHeight) || startSeconds != 0)
                        throw new InvalidOperationException(UiText.T("선택한 Google 로그인 계정의 영상은 계정 확인이 끝난 스트림 정보가 필요합니다. 다시 재생해 주세요.","Videos for the selected Google account require verified stream information. Try playing the video again.","選択した Google アカウントの動画には、アカウント確認済みのストリーム情報が必要です。もう一度再生してください。"));
                    if (existing.RetainedCookieLeases.Count >= MaxRetainedPlaybackCookies)
                        throw new InvalidOperationException(UiText.T("선택한 Google 로그인 계정의 스트림 정보를 다시 확인한 뒤 재생해 주세요.","Refresh the stream information for the selected Google account, then play the video again.","選択した Google アカウントのストリーム情報を再確認してから、もう一度再生してください。"));
                }
            // Unproven previous consumers keep their own leases. Bound the live-session
            // retention by draining that owner before a later valid load restarts mpv.
            await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Session? retained;
                lock (_stateLock) retained = _session is { } current && current.RetainedCookieLeases.Count >= MaxRetainedPlaybackCookies ? current : null;
                if (retained is not null) await CloseSessionAsync(retained).ConfigureAwait(false);
            }
            finally { _startGate.Release(); }
            await StartAsync(_host).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = _session ?? throw new InvalidOperationException("mpv is disconnected.");
            loadingSession = session;
            session.HasPlaybackCookies = cookiePath is not null;
            PreparedNext? prepared;
            lock (_stateLock)
            {
                prepared = _preparedNext is { } next && ReferenceEquals(next.Session, session) &&
                    next.OriginGeneration == _snapshot.MediaGeneration && next.VideoUrl == youtubeUrl &&
                    next.MaxHeight == YouTubeService.NormalizeMaxHeight(maxHeight) && startSeconds == 0 ? next : null;
                if (accountContext is { AuthUser: > 0 } && resolvedStream is null && prepared is null && resolveStream is null)
                    throw new InvalidOperationException(UiText.T("선택한 Google 로그인 계정의 영상은 계정 확인이 끝난 스트림 정보가 필요합니다. 다시 재생해 주세요.","Videos for the selected Google account require verified stream information. Try playing the video again.","選択した Google アカウントの動画には、アカウント確認済みのストリーム情報が必要です。もう一度再生してください。"));
                _preparedNext = null;
                generation = ++_mediaGeneration;
                session.MediaGeneration = generation; _lastLoadSession = session;
                session.CookieConsumerGeneration = cookiePath is null ? -1 : generation;
                session.ExtractionMetadata.Clear();session.ExtractionFailures.Clear();
                ResetExternalSubtitleCache(session,generation);
                _snapshot = new PlaybackSnapshot { MediaGeneration = generation, VideoUrl = youtubeUrl, Buffering = true, Paused = pauseIntent?.Paused??startPaused, Volume = _snapshot.Volume };
                _lastSnapshotAt = 0;
            }
            PublishSnapshot();
            await ConfigureSubtitleDefaultsAsync(session,cancellationToken).ConfigureAwait(false);
            if (prepared is not null)
            {
                if (await AdoptPreparedAsync(session, prepared, generation, cancellationToken, startPaused, pauseIntent).ConfigureAwait(false)) return;
            }
            // Stop the previous extractor before changing global hook options.
            await SendAsync(session, new object[] { "stop" }, cancellationToken).ConfigureAwait(false);
            if (accountContext is { AuthUser: > 0 } && resolvedStream is null)
            {
                if (resolveStream is null)
                    throw new InvalidOperationException(UiText.T("선택한 Google 로그인 계정의 영상은 계정 확인이 끝난 스트림 정보가 필요합니다. 다시 재생해 주세요.","Videos for the selected Google account require verified stream information. Try playing the video again.","選択した Google アカウントの動画には、アカウント確認済みのストリーム情報が必要です。もう一度再生してください。"));
                // A prepared entry can disappear or fail between UI selection and adoption.
                // Never fall through to mpv's default-account extractor for another login slot.
                resolvedStream = await resolveStream(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(UiText.T("선택한 Google 로그인 계정의 스트림 정보를 확인하지 못했습니다. 다시 재생해 주세요.","Could not verify stream information for the selected Google account. Try playing the video again.","選択した Google アカウントのストリーム情報を確認できませんでした。動画をもう一度再生してください。"));
                cancellationToken.ThrowIfCancellationRequested();
                directSource = ValidateDirectMedia(resolvedStream);
            }
            var playbackSource = directSource ?? youtubeUrl;
            var raw = new Dictionary<string, string>
            {
                ["ignore-config"] = "", ["no-plugin-dirs"] = "", ["no-cache-dir"] = "", ["no-mark-watched"] = "", ["no-write-info-json"] = "",
                ["no-write-comments"] = "", ["js-runtimes"] = "deno:" + _paths.Deno,
                ["sub-langs"] = YouTubeService.PreferredSubtitlePattern(PreferredSubtitleLanguage), ["write-subs"] = "", ["write-auto-subs"] = "", ["socket-timeout"] = "20",
                ["retries"] = "2", ["extractor-retries"] = "2", ["no-playlist"] = ""
            };
            if (cookiePath is not null) raw["cookies"] = Path.GetFullPath(cookiePath);
            if (extractorArguments.Length > 0) raw["extractor-args"] = extractorArguments;
            await SendAsync(session, new object[] { "set_property", "ytdl-raw-options", raw }, cancellationToken).ConfigureAwait(false);
            var format = YouTubeService.VideoFormat(maxHeight);
            await SendAsync(session, new object[] { "set_property", "ytdl-format", format }, cancellationToken).ConfigureAwait(false);
            var options = new Dictionary<string, string>
            {
                ["start"] = Math.Max(0, double.IsFinite(startSeconds) ? startSeconds : 0).ToString("R", CultureInfo.InvariantCulture),
                ["pause"] = startPaused ? "yes" : "no"
            };
            AddSubtitleOptions(options);
            if (resolvedStream is not null)
            {
                AddHttpHeaders(options, resolvedStream.Headers);
                AddHttpChunkOptions(options, resolvedStream.HttpChunkSize);
            }
            PauseIntent.State? submittedPause=null;
            await SendAsync(session, new object[] { "loadfile", playbackSource, "replace", -1, options }, cancellationToken,
                beforeWrite:()=>{submittedPause=pauseIntent?.Capture();options["pause"]=(submittedPause?.Paused??startPaused)?"yes":"no";}).ConfigureAwait(false);
            if(submittedPause is {} acceptedPause)pauseIntent!.Confirm(acceptedPause);
            var playlist = await SendAsync(session, new object[] { "get_property", "playlist" }, cancellationToken).ConfigureAwait(false);
            long? entry = null;
            if (playlist is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("filename", out var filename) && filename.GetString() == playbackSource && item.TryGetProperty("id", out var id) && id.TryGetInt64(out var candidate)) { entry = candidate; break; }
            }
            if (entry is null) throw new InvalidOperationException("mpv did not retain the requested playlist entry.");
            Interlocked.Exchange(ref session.DirectEntry, resolvedStream is not null ? entry.Value : -1);
            JsonElement? ended = null;
            lock (_stateLock)
            {
                _entryGenerations[entry.Value] = generation;
                if (_loadedEntries.Contains(entry.Value)) _snapshot = _snapshot with { Loaded = true, Buffering = false, CanReleaseCookies = resolvedStream is not null };
                if (_endedEntries.TryGetValue(entry.Value, out var end)) ended = end;
                // Retain a bounded amount of old IDs so delayed end-file events remain distinguishable.
                foreach (var old in _entryGenerations.Where(p => p.Value < generation - 8).Select(p => p.Key).ToArray())
                { _entryGenerations.Remove(old); _loadedEntries.Remove(old); _endedEntries.Remove(old); }
            }
            if (ended.HasValue) HandleEndFile(session, ended.Value);
            else await RefreshPropertiesAsync(session, generation, cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
            if (resolvedStream is null && Snapshot.Loaded) _ = RefreshCookieReleaseSafetyAsync(session, entry.Value, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (loadingSession is { } session && generation != 0 && IsCurrentLoad(session,generation))
            {
                await CloseFailedLoadSessionAsync(session,generation).ConfigureAwait(false);
                UpdateGeneration(generation, s => s with { Ended = true, Loaded = false, Buffering = false, Seeking = false });
            }
            throw;
        }
        catch (Exception ex)
        {
            var access=ex as YouTubeAccessException;
            var message=access?.Message ?? MediaFailureMessage;
            // A pre-ownership exception belongs to the attempted call, not the
            // previously playing video's generation. Its caller handles the throw.
            var current=generation!=0 && loadingSession is{ } session && IsCurrentLoad(session,generation);
            if (current)
            {
                // Owning the new generation also owns cleanup of the previous native media
                // and prepared entry, even when initialization fails before stop/loadfile.
                if(generation!=0 && loadingSession is{ } failedSession)
                    await CloseFailedLoadSessionAsync(failedSession,generation).ConfigureAwait(false);
                if (generation != 0) UpdateGeneration(generation, s => s with { Ended = true, EndReason="error", Loaded = false, Buffering = false, Seeking = false,
                    ErrorMessage=message,AuthenticationRequired=access?.AuthenticationRequired==true });
                RaiseError(message, generation);
            }
            throw;
        }
        finally { _loadGate.Release(); }
    }

    private bool IsCurrentLoad(Session session,long generation)
    {lock(_stateLock)return ReferenceEquals(_session,session) && _snapshot.MediaGeneration==generation;}

    private async Task CloseFailedLoadSessionAsync(Session session,long generation)
    {
        // The load gate is held by the caller. Also serialize direct StartAsync callers,
        // and recheck ownership before closing a possibly replaced session/generation.
        await _startGate.WaitAsync().ConfigureAwait(false);
        try{if(IsCurrentLoad(session,generation))await CloseSessionAsync(session).ConfigureAwait(false);}
        finally{_startGate.Release();}
    }

    /// <summary>Queues only one resolved next video; mpv buffers it when the current demuxer reaches EOF.</summary>
    public async Task<bool> PrepareNextAsync(string youtubeUrl, string resolvedMediaUrl, long expectedMediaGeneration, int maxHeight,
        IReadOnlyDictionary<string, string>? httpHeaders = null, CancellationToken cancellationToken = default, int? httpChunkSize = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateHttpChunkSize(httpChunkSize);
        if (!Uri.TryCreate(youtubeUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
            !(source.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || source.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || source.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A YouTube HTTPS video URL is required.", nameof(youtubeUrl));
        if (!(resolvedMediaUrl.StartsWith("edl://", StringComparison.Ordinal) ||
            Uri.TryCreate(resolvedMediaUrl, UriKind.Absolute, out var media) && media.Scheme == Uri.UriSchemeHttps) || resolvedMediaUrl.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A resolved media URL is required.", nameof(resolvedMediaUrl));
        resolvedMediaUrl = DirectMediaSource(resolvedMediaUrl);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? session = null;
        PreparedNext? added = null;
        Dictionary<long, PreparedPlaylistEntry>? beforeAppend = null;
        var appendSubmitted = false;
        long currentEntry;
        try
        {
            session = _session;
            lock (_stateLock)
                if (session is null || _snapshot.MediaGeneration != expectedMediaGeneration || !_snapshot.Loaded || _snapshot.Ended ||
                    _activeEntry is not { } active || !IsCurrentEntry(active)) return false;
            lock (_stateLock) currentEntry = _activeEntry!.Value;
            await ClearPreparedNextCoreAsync(session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
                if (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != expectedMediaGeneration || !_snapshot.Loaded || _snapshot.Ended) return false;
            // A failed history cleanup must not let every subsequent prepare grow the native playlist.
            var trimmed = await TryTrimPlaylistAsync(session, currentEntry, expectedMediaGeneration).ConfigureAwait(false);
            if (trimmed is null) return false;
            beforeAppend = ReadPreparedPlaylist(trimmed);
            if (beforeAppend.Count != 1 || !beforeAppend.ContainsKey(currentEntry)) return false;
            // Keep the built-in hook alive: toggling per-file ytdl unloads/reloads it and can
            // race the following ordinary YouTube load. The hook already ignores EDL URLs
            // and leaves successfully opened non-whitelisted direct media URLs untouched.
            var options = new Dictionary<string, string> { ["start"] = "0", ["pause"] = "yes" };
            AddSubtitleOptions(options);
            if (httpHeaders is not null) AddHttpHeaders(options, httpHeaders);
            AddHttpChunkOptions(options, httpChunkSize);
            // Do not abandon an IPC append reply on cancellation: the entry ID is needed for exact cleanup.
            cancellationToken.ThrowIfCancellationRequested();
            appendSubmitted = true;
            var result = await SendAsync(session, new object[] { "loadfile", resolvedMediaUrl, "append", -1, options }, CancellationToken.None).ConfigureAwait(false);
            long? entry = result is { ValueKind: JsonValueKind.Object } response && response.TryGetProperty("playlist_entry_id", out var entryId) && entryId.TryGetInt64(out var parsed) ? parsed : null;
            if (entry is null)
            {
                var playlist = ReadPreparedPlaylist(await SendAsync(session, new object[] { "get_property", "playlist" },
                    CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false));
                var candidates = playlist.Where(item => !beforeAppend.ContainsKey(item.Key)).ToArray();
                if (candidates.Length == 1 && candidates[0].Value.Source == resolvedMediaUrl) entry = candidates[0].Key;
            }
            if (entry is null or < 0 || beforeAppend.ContainsKey(entry.Value))
                throw new InvalidDataException(UiText.T("미리 준비한 다음 영상 항목을 확인하지 못했습니다.","Could not verify the preloaded next video entry.","事前準備した次の動画の項目を確認できませんでした。"));
            added = new PreparedNext(session, entry.Value, expectedMediaGeneration, youtubeUrl, YouTubeService.NormalizeMaxHeight(maxHeight));
            lock (_stateLock) _preparedNext = added;
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_session, session) || Snapshot.MediaGeneration != expectedMediaGeneration)
            {
                await ClearPreparedNextCoreAsync(session).ConfigureAwait(false);
                appendSubmitted = false;
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            return true;
        }
        catch
        {
            if (appendSubmitted && session is not null && beforeAppend is not null)
                await RecoverUnconfirmedAppendAsync(session, expectedMediaGeneration, beforeAppend, resolvedMediaUrl, added?.Entry).ConfigureAwait(false);
            throw;
        }
        finally { _loadGate.Release(); }
    }

    private sealed record PreparedPlaylistEntry(int Index, string Source, bool Active);

    private static Dictionary<long, PreparedPlaylistEntry> ReadPreparedPlaylist(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } playlist || playlist.GetArrayLength() > 65)
            throw new InvalidDataException(UiText.T("mpv 재생 목록을 확인하지 못했습니다.","Could not verify the mpv playlist.","mpv の再生リストを確認できませんでした。"));
        var entries = new Dictionary<long, PreparedPlaylistEntry>();
        var index = 0;
        foreach (var item in playlist.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var entry) || entry < 0 ||
                !item.TryGetProperty("filename", out var filename) || filename.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(UiText.T("mpv 재생 목록을 확인하지 못했습니다.","Could not verify the mpv playlist.","mpv の再生リストを確認できませんでした。"));
            var active = false;
            foreach (var name in new[] { "current", "playing" })
                if (item.TryGetProperty(name, out var flag))
                {
                    if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException(UiText.T("mpv 재생 목록을 확인하지 못했습니다.","Could not verify the mpv playlist.","mpv の再生リストを確認できませんでした。"));
                    active |= flag.ValueKind == JsonValueKind.True;
                }
            if (!entries.TryAdd(entry, new(index++, filename.GetString()!, active)))
                throw new InvalidDataException(UiText.T("mpv 재생 목록의 항목 ID가 중복되었습니다.","The mpv playlist contains duplicate entry IDs.","mpv の再生リストに重複した項目 ID があります。"));
        }
        return entries;
    }

    private async Task RecoverUnconfirmedAppendAsync(Session session, long generation,
        IReadOnlyDictionary<long, PreparedPlaylistEntry> before, string source, long? confirmedEntry)
    {
        // The load gate remains held. Never sweep by URL or reuse a stale index:
        // the current video and queued video may deliberately have the same URL.
        if (!IsCurrentLoad(session, generation)) return;
        lock (_stateLock)
            if (_preparedNext is { } prepared && ReferenceEquals(prepared.Session, session) && prepared.OriginGeneration == generation)
                _preparedNext = null;
        try
        {
            var current = ReadPreparedPlaylist(await SendAsync(session, ["get_property", "playlist"],
                CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            if (!IsCurrentLoad(session, generation)) return;
            var added = current.Where(item => !before.ContainsKey(item.Key)).ToArray();
            // A confirmed ID may have been removed before cancellation was observed.
            if (confirmedEntry.HasValue && added.Length == 0 && !current.ContainsKey(confirmedEntry.Value)) return;
            if (added.Length != 1 || added[0].Value.Source != source ||
                confirmedEntry.HasValue && confirmedEntry.Value != added[0].Key || added[0].Value.Active)
                throw new InvalidDataException(UiText.T("미리 준비한 영상의 소유 항목을 확인하지 못했습니다.","Could not identify the owned preloaded video entry.","事前準備した動画の所有項目を確認できませんでした。"));
            lock (_stateLock)
                if (_activeEntry == added[0].Key) throw new InvalidOperationException(UiText.T("미리 준비한 영상이 이미 전환되었습니다.","The preloaded video has already become the current video.","事前準備した動画にはすでに切り替わっています。"));
            await SendAsync(session, ["playlist-remove", added[0].Value.Index], CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var after = ReadPreparedPlaylist(await SendAsync(session, ["get_property", "playlist"],
                CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            if (after.Keys.Any(id => !before.ContainsKey(id)))
                throw new InvalidDataException(UiText.T("미리 준비한 영상 정리를 확인하지 못했습니다.","Could not verify cleanup of the preloaded video.","事前準備した動画のクリーンアップを確認できませんでした。"));
            return;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            if (!IsCurrentLoad(session, generation)) return;
            var message = UiText.T("다음 영상 미리 준비 결과를 확인하지 못해 플레이어를 정리했습니다. 현재 영상을 다시 재생해 주세요.","The player was stopped because the next video preload could not be verified. Play the current video again.","次の動画の事前準備結果を確認できなかったため、プレーヤーを停止しました。現在の動画をもう一度再生してください。");
            await CloseFailedLoadSessionAsync(session, generation).ConfigureAwait(false);
            UpdateGeneration(generation, snapshot => snapshot with { Ended = true, EndReason = "error", Loaded = false,
                Buffering = false, Seeking = false, ErrorMessage = message });
            RaiseError(message, generation);
        }
    }

    /// <summary>Prevents queued promotion. mpv may retain its bounded opener cache until the next load or session close.</summary>
    public async Task ClearPreparedNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (_session is { } session) await ClearPreparedNextCoreAsync(session).ConfigureAwait(false); }
        finally { _loadGate.Release(); }
    }

    private async Task ClearPreparedNextCoreAsync(Session session)
    {
        PreparedNext? prepared;
        lock (_stateLock) prepared = _preparedNext;
        if (prepared is null || !ReferenceEquals(prepared.Session, session) || !IsCurrentLoad(session,prepared.OriginGeneration)) return;
        try
        {
            var playlist = ReadPreparedPlaylist(await SendAsync(session, ["get_property", "playlist"],
                CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            if (!IsCurrentLoad(session,prepared.OriginGeneration)) return;
            if (playlist.TryGetValue(prepared.Entry,out var owned))
            {
                lock (_stateLock)
                    if (_activeEntry == prepared.Entry || owned.Active)
                        throw new InvalidOperationException(UiText.T("미리 준비한 영상이 이미 전환되었습니다.","The preloaded video has already become the current video.","事前準備した動画にはすでに切り替わっています。"));
                // Only this exact native ID is owned. The current item may have the same URL.
                await SendAsync(session, ["playlist-remove", owned.Index], CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                playlist = ReadPreparedPlaylist(await SendAsync(session, ["get_property", "playlist"],
                    CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false));
                if (!IsCurrentLoad(session,prepared.OriginGeneration)) return;
                if (playlist.ContainsKey(prepared.Entry))
                    throw new InvalidDataException(UiText.T("미리 준비한 영상 정리를 확인하지 못했습니다.","Could not verify cleanup of the preloaded video.","事前準備した動画のクリーンアップを確認できませんでした。"));
            }
            lock (_stateLock) if (ReferenceEquals(_preparedNext,prepared)) _preparedNext = null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // Retain ownership until exact removal or termination of this owner is confirmed.
            // A direct StartAsync can replace the session while IPC is pending.
            await _startGate.WaitAsync().ConfigureAwait(false);
            var closed = false;
            var message = UiText.T("다음 영상 준비 취소를 확인하지 못해 플레이어를 정리했습니다. 현재 영상을 다시 재생해 주세요.","The player was stopped because cancellation of the next video preload could not be verified. Play the current video again.","次の動画の準備のキャンセルを確認できなかったため、プレーヤーを停止しました。現在の動画をもう一度再生してください。");
            try
            {
                if (!IsCurrentLoad(session,prepared.OriginGeneration)) return;
                await CloseSessionAsync(session).ConfigureAwait(false);
                UpdateGeneration(prepared.OriginGeneration, snapshot => snapshot with { Ended = true, EndReason = "error", Loaded = false,
                    Buffering = false, Seeking = false, ErrorMessage = message });
                closed = true;
            }
            finally { _startGate.Release(); }
            if (closed) RaiseError(message, prepared.OriginGeneration);
        }
    }

    private async Task<bool> AdoptPreparedAsync(Session session, PreparedNext prepared, long generation, CancellationToken cancellationToken, bool startPaused,PauseIntent? pauseIntent)
    {
        var playlist = await SendAsync(session, new object[] { "get_property", "playlist" }, cancellationToken).ConfigureAwait(false);
        if (playlist is not { ValueKind: JsonValueKind.Array } list) return false;
        var index = 0;
        var found = false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var entry) && entry == prepared.Entry) { found = true; break; }
            index++;
        }
        if (!found) return false;
        bool active;
        JsonElement? ended = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FileReady[prepared.Entry] = ready;
        Interlocked.Exchange(ref session.AdoptingEntry, prepared.Entry);
        Interlocked.Exchange(ref session.DirectEntry, prepared.Entry);
        try
        {
            lock (_stateLock)
            {
                _entryGenerations[prepared.Entry] = generation;
                PruneEntryHistory();
                active = _activeEntry == prepared.Entry;
                if (_endedEntries.TryGetValue(prepared.Entry, out var end)) ended = end;
                if (!ended.HasValue && _loadedEntries.Contains(prepared.Entry))
                {
                    _snapshot = _snapshot with { Loaded = true, Ended = false, EndReason = null, Buffering = false, CanReleaseCookies = true };
                    ready.TrySetResult(true);
                }
            }
            if (ended.HasValue) return false; // Failed prefetch/expired media URL falls back to normal extraction.
            if (!active) await SendAsync(session, new object[] { "playlist-play-index", index }, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            bool loaded;
            try { loaded = await ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !session.Token.IsCancellationRequested) { return false; }
            if (!loaded) return false;
            lock (_stateLock) if (_endedEntries.ContainsKey(prepared.Entry)) return false;
            lock (_stateLock) _snapshot = _snapshot with { CanReleaseCookies = true };
            // Only trim after this exact entry has reached file-loaded. Resolve each removal's
            // fresh index by ID; old indexes shift as earlier history entries are removed.
            await TryTrimPlaylistAsync(session, prepared.Entry, generation).ConfigureAwait(false);
            // Per-file pause=yes is applied asynchronously during loading. Apply the
            // requested state after file-loaded so an early unpause cannot be overwritten.
            var pauseCommand=new object[]{"set_property","pause",startPaused};
            PauseIntent.State? submittedPause=null;
            await SendAsync(session,pauseCommand,cancellationToken,
                beforeWrite:()=>{submittedPause=pauseIntent?.Capture();pauseCommand[2]=submittedPause?.Paused??startPaused;}).ConfigureAwait(false);
            if(submittedPause is {} acceptedPause)pauseIntent!.Confirm(acceptedPause);
            try{await ApplySubtitlePreferenceAsync(session,generation,cancellationToken).ConfigureAwait(false);}
            catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException ||
                ex is IOException && ex.Message=="mpv IPC command timed out." && !session.Token.IsCancellationRequested && session.Pipe.IsConnected)
            { /* Subtitle metadata is optional; successful prepared playback remains usable. */ }
            await RefreshPropertiesAsync(session, generation, cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
            return true;
        }
        finally { session.FileReady.TryRemove(prepared.Entry, out _); Interlocked.Exchange(ref session.AdoptingEntry, -1); }
    }

    private async Task<JsonElement?> TryTrimPlaylistAsync(Session session, long currentEntry, long generation)
    {
        try
        {
            var previousCount = int.MaxValue;
            for (var removed = 0; removed <= 64; removed++)
            {
                lock (_stateLock)
                    if (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != generation ||
                        !_loadedEntries.Contains(currentEntry) || !IsCurrentEntry(currentEntry)) return null;
                var playlist = await SendAsync(session, new object[] { "get_property", "playlist" },
                    CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (playlist is not { ValueKind: JsonValueKind.Array } list)
                    throw new InvalidOperationException("mpv playlist is unavailable.");
                var count = list.GetArrayLength();
                if (count >= previousCount) throw new InvalidOperationException("mpv playlist cleanup made no progress.");
                long? active, prepared;
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != generation) return null;
                    active = _activeEntry;
                    prepared = _preparedNext is { } next && ReferenceEquals(next.Session, session) ? next.Entry : null;
                }
                var removal = FindPlaylistHistoryRemoval(list, currentEntry, prepared, active);
                if (removal is null)
                {
                    if (count > (prepared.HasValue ? 2 : 1))
                        throw new InvalidOperationException("mpv playlist cleanup is waiting for the current entry.");
                    session.PlaylistCleanupFailures = 0;
                    return list;
                }
                if (removed == 64) throw new InvalidOperationException("mpv playlist cleanup exceeded its bounded batch.");
                previousCount = count;
                await SendAsync(session, new object[] { "playlist-remove", removal.Value.Index },
                    CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // Keep good playback running. Later prepares retry and refuse to append until bounded.
            if (++session.PlaylistCleanupFailures == 2 && ReferenceEquals(_session, session) && !session.StopRequested)
                RaiseError(UiText.T("이전 재생 목록 정리를 완료하지 못했습니다. 다음 영상 미리 준비를 일시 중단합니다.","Could not finish cleaning up the previous playlist. Preloading the next video is paused.","前の再生リストのクリーンアップを完了できませんでした。次の動画の事前準備を一時停止します。"), generation, session);
        }
        return null;
    }

    private static (long Entry, int Index)? FindPlaylistHistoryRemoval(JsonElement playlist,
        long currentEntry, long? preparedEntry, long? activeEntry)
    {
        var protectedEntries = new HashSet<long> { currentEntry };
        if (preparedEntry.HasValue) protectedEntries.Add(preparedEntry.Value);
        if (activeEntry.HasValue) protectedEntries.Add(activeEntry.Value);
        foreach (var item in playlist.EnumerateArray())
            if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var entry) &&
                ((item.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.True) ||
                 (item.TryGetProperty("playing", out var playing) && playing.ValueKind == JsonValueKind.True)))
                protectedEntries.Add(entry);
        var currentIndex = -1;
        var position = 0;
        foreach (var item in playlist.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var entry) && entry == currentEntry)
            { currentIndex = position; break; }
            position++;
        }
        var index = 0;
        foreach (var item in playlist.EnumerateArray())
        {
            // Native EOF advancement only moves forward. Restrict history removal to entries
            // behind the confirmed loaded entry, so a concurrent advance cannot select the victim.
            if (index >= currentIndex) break;
            if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var entry) && !protectedEntries.Contains(entry))
                return (entry, index);
            index++;
        }
        return null;
    }

    private async Task RefreshCookieReleaseSafetyAsync(Session session, long entry, long generation)
    {
        var entered = false;
        try
        {
            await _loadGate.WaitAsync(session.Token).ConfigureAwait(false);
            entered = true;
            lock (_stateLock)
                if (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != generation ||
                    !_snapshot.Loaded || _snapshot.Ended || _activeEntry != entry || !IsCurrentEntry(entry)) return;
            if (Interlocked.Exchange(ref session.CookieMetadataEntry, entry) == entry) return;
            var result = await SendAsync(session, new object[] { "get_property", "user-data/mpv/ytdl/json-subprocess-result" },
                CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (result is not { } metadata || !IsConfirmedVodResult(metadata)) return;
            lock (_stateLock)
            {
                if (!ReferenceEquals(_session, session) || _snapshot.MediaGeneration != generation ||
                    !_snapshot.Loaded || _snapshot.Ended || _activeEntry != entry || !IsCurrentEntry(entry)) return;
                _snapshot = _snapshot with { CanReleaseCookies = true };
            }
            PublishSnapshot();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException or JsonException)
        { /* Missing or ambiguous metadata keeps the lease; it must never break playback. */ }
        finally { if (entered) _loadGate.Release(); }
    }

    private static bool IsConfirmedVodResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status", out var status) ||
            !status.TryGetInt32(out var exitCode) || exitCode != 0 ||
            !result.TryGetProperty("stdout", out var output) || output.ValueKind != JsonValueKind.String) return false;
        try
        {
            using var document = JsonDocument.Parse(output.GetString()!);
            var video = document.RootElement;
            return video.ValueKind == JsonValueKind.Object && video.TryGetProperty("is_live", out var live) &&
                live.ValueKind == JsonValueKind.False &&
                (!video.TryGetProperty("live_status", out var liveStatus) ||
                 liveStatus.ValueKind == JsonValueKind.String && liveStatus.GetString() is "not_live" or "was_live");
        }
        catch (JsonException) { return false; }
    }

    private static string DirectMediaSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("A resolved media URL is required.", nameof(source));
        if (source.StartsWith("edl://", StringComparison.Ordinal)) return source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A resolved HTTPS media URL is required.", nameof(source));
        // Outer EDL prevents the hook from re-extracting a direct stream on load failure,
        // without unloading/reloading the hook and racing the next ordinary YouTube load.
        return "edl://" + MpvListValue(source);
    }

    private static string ValidateDirectMedia(PreparedVideo media)
    {
        ValidateHttpChunkSize(media.HttpChunkSize);
        return DirectMediaSource(media.MediaUrl);
    }

    private static void ValidateHttpChunkSize(int? httpChunkSize)
    {
        if(httpChunkSize is <=0 or >PreparedVideo.MaxHttpChunkSize)
            throw new ArgumentOutOfRangeException(nameof(httpChunkSize),"The HTTP chunk size must be between 1 and 67108864 bytes.");
    }

    private static void AddHttpChunkOptions(Dictionary<string,string> options,int? httpChunkSize)
    {
        ValidateHttpChunkSize(httpChunkSize);
        if(httpChunkSize is not{ } bytes)return;
        var value=bytes.ToString(CultureInfo.InvariantCulture);
        options["curl-max-request-size"]=value;
        options["stream-lavf-o"]="request_size="+value;
    }

    private static void AddHttpHeaders(Dictionary<string, string> options, IReadOnlyDictionary<string, string> httpHeaders)
    {
        if (httpHeaders.TryGetValue("User-Agent", out var agent) && agent.IndexOfAny(['\r', '\n', '\0']) < 0) options["user-agent"] = agent;
        var headers = new List<string>();
        foreach (var name in new[] { "Cookie", "Referer", "X-Forwarded-For" })
            if (httpHeaders.TryGetValue(name, out var value) && value.IndexOfAny(['\r', '\n', '\0']) < 0) headers.Add(MpvListValue(name + ": " + value));
        if (headers.Count > 0) options["http-header-fields"] = string.Join(",", headers);
    }

    public Task<JsonElement?> CommandAsync(params object[] args)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return SendAsync(_session ?? throw new InvalidOperationException("mpv is disconnected."), args, CancellationToken.None);
    }

    public async Task SetPausedAsync(PauseIntent intent,long revision,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        PauseIntent.State submitted=default;
        var writeAttempted=false;
        var command=new object[]{"set_property","pause",false};
        try
        {
            await SendAsync(_session??throw new InvalidOperationException("mpv is disconnected."),command,cancellationToken,beforeWrite:()=>
            {
                if(!intent.TryGetPending(revision,out submitted))throw new PauseRequestSupersededException();
                command[2]=submitted.Paused;
                writeAttempted=true;
            }).ConfigureAwait(false);
            intent.Confirm(submitted);
        }
        catch(PauseRequestSupersededException) { }
        catch(MpvCommandException error){throw new PauseCommandRejectedException(error);}
        catch
        {
            // A partial write, timeout or malformed/lost reply cannot establish
            // the native state. A later toggle must not be skipped as redundant.
            if(writeAttempted)intent.MarkUncertain(submitted);
            throw;
        }
    }

    private sealed class PauseRequestSupersededException:Exception { }

    public sealed class PauseCommandRejectedException:InvalidOperationException
    {
        internal PauseCommandRejectedException(Exception inner):base(inner.Message,inner) { }
    }

    public Task<JsonElement?> CommandAsync(CancellationToken cancellationToken, params object[] args)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return SendAsync(_session ?? throw new InvalidOperationException("mpv is disconnected."), args, cancellationToken);
    }

    public async Task<T?> GetPropertyAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var data = await SendAsync(_session ?? throw new InvalidOperationException("mpv is disconnected."), new object[] { "get_property", name }, cancellationToken).ConfigureAwait(false);
        return data is null ? default : data.Value.Deserialize<T>();
    }

    public async Task<SubtitleSelection> GetSubtitlesAsync(CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        cancellationToken.ThrowIfCancellationRequested();
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session=_session ?? throw SubtitleMediaChanged();
            long generation;
            lock(_stateLock)generation=_snapshot.MediaGeneration;
            return await GetSubtitlesCoreAsync(session,generation,cancellationToken).ConfigureAwait(false);
        }
        finally{_loadGate.Release();}
    }

    public async Task SelectSubtitleAsync(long? id,long expectedGeneration,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        cancellationToken.ThrowIfCancellationRequested();
        if(id is <=0)throw new ArgumentOutOfRangeException(nameof(id));
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session=_session ?? throw SubtitleMediaChanged();
            await SelectSubtitleCoreAsync(session,id,expectedGeneration,cancellationToken).ConfigureAwait(false);
        }
        finally{_loadGate.Release();}
    }

    /// <summary>Loads only the user's chosen catalog caption and reuses bounded app-added tracks.</summary>
    public async Task SelectExternalSubtitleAsync(AvailableSubtitle caption,long expectedGeneration,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExternalSubtitle(caption);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session=_session ?? throw SubtitleMediaChanged();
            var beforeReadback=await GetSubtitleReadbackAsync(session,expectedGeneration,cancellationToken).ConfigureAwait(false);
            var before=beforeReadback.Selection;
            if(session.ExternalSubtitleGeneration!=expectedGeneration)ResetExternalSubtitleCache(session,expectedGeneration);
            var existingIds=before.Tracks.Select(track=>track.Id).ToHashSet();
            foreach(var missing in session.ExternalSubtitles.Where(pair=>!existingIds.Contains(pair.Value.Id)).Select(pair=>pair.Key).ToArray())
                session.ExternalSubtitles.Remove(missing);
            if(session.ExternalSubtitles.TryGetValue(caption.Url,out var existing))
            {
                await SelectSubtitleCoreAsync(session,existing.Id,expectedGeneration,cancellationToken,before).ConfigureAwait(false);
                session.ExternalSubtitles[caption.Url]=existing with{LastUsed=++session.ExternalSubtitleUse};
                _subtitleLanguage=caption.Language;
                return;
            }
            // Only cached IDs created by this method are candidates for removal.
            // Default/native tracks and prepared subtitles never enter this cache.
            var evicted=false;
            while(session.ExternalSubtitles.Count>=MaxSelectedExternalSubtitles)
            {
                var removable=session.ExternalSubtitles.Where(pair=>!before.Tracks.Any(track=>track.Id==pair.Value.Id && track.Selected))
                    .OrderBy(pair=>pair.Value.LastUsed).FirstOrDefault();
                if(removable.Key is null)throw new InvalidOperationException(UiText.T("추가 자막을 정리하지 못했습니다. 영상을 다시 재생해 주세요.","Could not remove the additional subtitles. Play the video again.","追加字幕を削除できませんでした。動画をもう一度再生してください。"));
                EnsureSubtitleMedia(session,expectedGeneration);
                await SendAsync(session,new object[]{"sub-remove",removable.Value.Id},cancellationToken).ConfigureAwait(false);
                EnsureSubtitleMedia(session,expectedGeneration);
                session.ExternalSubtitles.Remove(removable.Key);
                existingIds.Remove(removable.Value.Id);
                evicted=true;
            }
            if(evicted)
            {
                // Compare with the confirmed post-eviction state: a deliberate
                // LRU removal is not a side effect of the following sub-add.
                beforeReadback=await GetSubtitleReadbackAsync(session,expectedGeneration,cancellationToken).ConfigureAwait(false);
                before=beforeReadback.Selection;
                existingIds=before.Tracks.Select(track=>track.Id).ToHashSet();
            }
            EnsureSubtitleMedia(session,expectedGeneration);
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleSelection after;
            long addedId=0;MpvCommandException? rejection=null;var unchangedRejection=false;
            try
            {
                // JSON IPC cannot abort a submitted download. Keep the load gate
                // until its terminal reply, so a newer file/choice cannot race it.
                try{await SendAsync(session,new object[]{"sub-add",caption.Url,"select",caption.Label,caption.Language},CancellationToken.None).ConfigureAwait(false);}
                catch(MpvCommandException error){rejection=error;}
                using var readback=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var afterReadback=await GetSubtitleReadbackAsync(session,expectedGeneration,readback.Token).ConfigureAwait(false);
                after=afterReadback.Selection;
                var added=after.Tracks.Where(track=>!existingIds.Contains(track.Id)).ToArray();
                unchangedRejection=rejection is not null && added.Length==0 && SameSubtitleState(beforeReadback,afterReadback);
                if(!unchangedRejection)
                {
                    if(rejection is not null || added.Length!=1)throw new InvalidOperationException(UiText.T("선택한 언어의 추가 자막을 확인하지 못했습니다.","Could not verify the additional subtitles for the selected language.","選択した言語の追加字幕を確認できませんでした。"),rejection);
                    addedId=added[0].Id;
                }
            }
            catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                // An unconfirmed native operation must not later attach itself
                // to another file. Stop this owned session before releasing the gate.
                await CloseUnconfirmedSubtitleSessionAsync(session,expectedGeneration).ConfigureAwait(false);
                if(cancellationToken.IsCancellationRequested)throw new OperationCanceledException(cancellationToken);
                throw new IOException(UiText.T("자막 추가 결과를 확인하지 못했습니다. 영상을 다시 재생해 주세요.","Could not verify that the subtitles were added. Play the video again.","字幕の追加結果を確認できませんでした。動画をもう一度再生してください。"),error);
            }
            if(unchangedRejection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new SubtitleCommandRejectedException(UiText.T("선택한 자막을 불러오지 못했습니다. 다른 자막을 선택하거나 다시 시도해 주세요.","Could not load the selected subtitles. Choose another track or try again.","選択した字幕を読み込めませんでした。別の字幕を選ぶか、もう一度お試しください。"),rejection!);
            }
            session.ExternalSubtitles[caption.Url]=new(addedId,++session.ExternalSubtitleUse);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SelectSubtitleCoreAsync(session,addedId,expectedGeneration,cancellationToken,after).ConfigureAwait(false);
            }
            catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested)
            {
                await RemoveCancelledExternalSubtitleAsync(session,caption.Url,addedId,before).ConfigureAwait(false);
                throw;
            }
            // Preserve the catalog's exact language key (e.g. zh-Hans/en-orig)
            // for the next yt-dlp query, after confirmed per-file selection.
            _subtitleLanguage=caption.Language;
        }
        finally{_loadGate.Release();}
    }

    private static void ValidateExternalSubtitle(AvailableSubtitle caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        if(!YouTubeService.IsValidSubtitleLanguage(caption.Language) ||
            string.IsNullOrWhiteSpace(caption.Label) || caption.Label.Length>YouTubeService.MaxSubtitleLabelLength || caption.Label.Any(char.IsControl) ||
            !YouTubeService.TrySubtitleCatalogUrl(caption.Url,out _))
            throw new InvalidDataException(UiText.T("선택한 자막 정보가 올바르지 않습니다. 자막 목록을 다시 불러와 주세요.","The selected subtitle information is invalid. Reload the subtitle list.","選択した字幕情報が正しくありません。字幕一覧を読み込み直してください。"));
    }

    private static void ResetExternalSubtitleCache(Session session,long generation)
    {session.ExternalSubtitles.Clear();session.ExternalSubtitleGeneration=generation;session.ExternalSubtitleUse=0;}

    private async Task CloseUnconfirmedSubtitleSessionAsync(Session session,long generation)
    {
        await CloseSessionAsync(session).ConfigureAwait(false);
        UpdateGeneration(generation,snapshot=>snapshot with{Ended=true,Loaded=false,Buffering=false,Seeking=false});
    }

    private async Task RemoveCancelledExternalSubtitleAsync(Session session,string url,long id,SubtitleSelection before,bool forgetCached=true)
    {
        try
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            EnsureSubtitleMedia(session,before.MediaGeneration);
            await SendAsync(session,new object[]{"sub-remove",id},cleanup.Token).ConfigureAwait(false);
            EnsureSubtitleMedia(session,before.MediaGeneration);
            var previousId=before.Off?null:before.Tracks.FirstOrDefault(track=>track.Selected)?.Id;
            await SendAsync(session,new object[]{"set_property","slang",SubtitleLanguages(_subtitleLanguage)},cleanup.Token).ConfigureAwait(false);
            EnsureSubtitleMedia(session,before.MediaGeneration);
            await SendAsync(session,new object[]{"set_property","sid",previousId.HasValue?(object)previousId.Value:"no"},cleanup.Token).ConfigureAwait(false);
            EnsureSubtitleMedia(session,before.MediaGeneration);
            await SendAsync(session,new object[]{"set_property","sub-visibility",!before.Off},cleanup.Token).ConfigureAwait(false);
        }
        catch(Exception error)when(error is IOException or InvalidOperationException or OperationCanceledException)
        {await CloseUnconfirmedSubtitleSessionAsync(session,before.MediaGeneration).ConfigureAwait(false);}
        finally{if(forgetCached)session.ExternalSubtitles.Remove(url);}
    }

    private async Task SelectSubtitleCoreAsync(Session session,long? id,long generation,CancellationToken cancellationToken,SubtitleSelection? selection=null)
    {
        selection??=await GetSubtitlesCoreAsync(session,generation,cancellationToken).ConfigureAwait(false);
        var track=id.HasValue?selection.Tracks.FirstOrDefault(item=>item.Id==id.Value):null;
        if(id.HasValue && track==null)throw new InvalidOperationException(UiText.T("선택한 자막이 현재 영상에 없습니다. 자막 목록을 다시 열어 주세요.","The selected subtitles are unavailable for the current video. Reopen the subtitle list.","選択した字幕は現在の動画にありません。字幕一覧を開き直してください。"));
        var language=track?.Language;
        EnsureSubtitleMedia(session,generation);
        if(id.HasValue)await SendAsync(session,new object[]{"set_property","slang",SubtitleLanguages(language)},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        await SendAsync(session,new object[]{"set_property","sid",id.HasValue?(object)id.Value:"no"},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        await SendAsync(session,new object[]{"set_property","sub-visibility",id.HasValue},cancellationToken).ConfigureAwait(false);
        var confirmed=await GetSubtitlesCoreAsync(session,generation,cancellationToken).ConfigureAwait(false);
        if(id.HasValue?(confirmed.Off || !confirmed.Tracks.Any(item=>item.Id==id.Value && item.Selected)):!confirmed.Off)
            throw new InvalidOperationException(UiText.T("자막 선택 결과를 확인하지 못했습니다. 목록을 다시 열어 주세요.","Could not verify the subtitle selection. Reopen the list.","字幕の選択結果を確認できませんでした。一覧を開き直してください。"));
        cancellationToken.ThrowIfCancellationRequested();
        _subtitleOff=!id.HasValue;
        if(id.HasValue)_subtitleLanguage=language;
        _subtitleSelectedId=id;_subtitleSelectionGeneration=generation;
    }

    public async Task AddPreparedSubtitleAsync(PreparedCaption caption,long expectedGeneration,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(caption);
        if(!YouTubeService.TrySubtitleCatalogUrl(caption.Url,out _) || !YouTubeService.IsValidSubtitleLanguage(caption.Language))
            throw new InvalidDataException(UiText.T("준비된 자막 주소 또는 언어가 올바르지 않습니다.","The prepared subtitle URL or language is invalid.","準備された字幕の URL または言語が正しくありません。"));
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session=_session ?? throw SubtitleMediaChanged();
            await WaitForSubtitleMediaAsync(session,expectedGeneration,cancellationToken).ConfigureAwait(false);
            var beforeReadback=await GetSubtitleReadbackAsync(session,expectedGeneration,cancellationToken).ConfigureAwait(false);
            var before=beforeReadback.Selection;
            var existingIds=before.Tracks.Select(track=>track.Id).ToHashSet();
            cancellationToken.ThrowIfCancellationRequested();
            long addedId=0;MpvCommandException? rejection=null;var unchangedNoOp=false;
            try
            {
                // A submitted native download cannot be aborted through JSON IPC.
                // Keep the gate until a terminal reply and current-media readback.
                try{await SendAsync(session,new object[]{"sub-add",caption.Url,"auto",caption.Language,caption.Language},CancellationToken.None).ConfigureAwait(false);}
                catch(MpvCommandException error){rejection=error;}
                using var readback=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var afterReadback=await GetSubtitleReadbackAsync(session,expectedGeneration,readback.Token).ConfigureAwait(false);
                var added=afterReadback.Selection.Tracks.Where(track=>!existingIds.Contains(track.Id)).ToArray();
                unchangedNoOp=added.Length==0 && SameSubtitleState(beforeReadback,afterReadback);
                if(!unchangedNoOp)
                {
                    if(rejection is not null || added.Length!=1)throw new InvalidOperationException(UiText.T("준비된 추가 자막을 확인하지 못했습니다.","Could not verify the prepared additional subtitles.","準備された追加字幕を確認できませんでした。"),rejection);
                    addedId=added[0].Id;
                }
            }
            catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                await CloseUnconfirmedSubtitleSessionAsync(session,expectedGeneration).ConfigureAwait(false);
                if(cancellationToken.IsCancellationRequested)throw new OperationCanceledException(cancellationToken);
                throw new IOException(UiText.T("준비된 자막 추가 결과를 확인하지 못했습니다. 영상을 다시 재생해 주세요.","Could not verify that the prepared subtitles were added. Play the video again.","準備された字幕の追加結果を確認できませんでした。動画をもう一度再生してください。"),error);
            }
            if(unchangedNoOp)
            {
                // A terminal no-op cannot attach a late caption. Preserve the
                // valid video and its existing selection when optional captions fail.
                cancellationToken.ThrowIfCancellationRequested();
                var message=UiText.T("준비된 자막을 추가하지 못했습니다. 자막 메뉴에서 다시 선택해 주세요.","Could not add the prepared subtitles. Select them again in the subtitles menu.","準備された字幕を追加できませんでした。字幕メニューでもう一度選択してください。");
                if(rejection is not null)throw new SubtitleCommandRejectedException(message,rejection);
                throw new InvalidOperationException(message);
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplySubtitlePreferenceAsync(session,expectedGeneration,cancellationToken).ConfigureAwait(false);
            }
            catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested)
            {
                await RemoveCancelledExternalSubtitleAsync(session,caption.Url,addedId,before,false).ConfigureAwait(false);
                throw;
            }
        }
        finally{_loadGate.Release();}
    }

    private async Task WaitForSubtitleMediaAsync(Session session,long generation,CancellationToken cancellationToken)
    {
        long entry;TaskCompletionSource<bool>? ready=null;
        lock(_stateLock)
        {
            if(!ReferenceEquals(_session,session) || _snapshot.MediaGeneration!=generation || _snapshot.Ended)throw SubtitleMediaChanged();
            if(_snapshot.Loaded)return;
            var entries=_entryGenerations.Where(item=>item.Value==generation).Select(item=>item.Key).ToArray();
            if(entries.Length!=1)throw SubtitleMediaChanged();
            entry=entries[0];
            ready=session.FileReady.GetOrAdd(entry,_=>new(TaskCreationOptions.RunContinuationsAsynchronously));
            if(_loadedEntries.Contains(entry))ready.TrySetResult(true);
        }
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,session.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if(!await ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false))throw SubtitleMediaChanged();
        }
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested)
        {throw new IOException(UiText.T("자막을 추가할 영상 로딩을 기다리지 못했습니다. 다시 재생해 주세요.","Could not wait for the video to load before adding subtitles. Play it again.","字幕を追加する動画の読み込みを待機できませんでした。もう一度再生してください。"));}
        finally{session.FileReady.TryRemove(new KeyValuePair<long,TaskCompletionSource<bool>>(entry,ready));}
    }

    private sealed record SubtitleReadback(SubtitleSelection Selection,JsonElement[] Tracks,JsonElement Sid,JsonElement Visibility);

    private static bool SameSubtitleState(SubtitleReadback before,SubtitleReadback after)=>
        before.Selection.MediaGeneration==after.Selection.MediaGeneration && JsonElement.DeepEquals(before.Sid,after.Sid) &&
        JsonElement.DeepEquals(before.Visibility,after.Visibility) && before.Tracks.Length==after.Tracks.Length &&
        before.Tracks.Zip(after.Tracks).All(pair=>JsonElement.DeepEquals(pair.First,pair.Second));

    private async Task<SubtitleSelection> GetSubtitlesCoreAsync(Session session,long generation,CancellationToken cancellationToken)=>
        (await GetSubtitleReadbackAsync(session,generation,cancellationToken).ConfigureAwait(false)).Selection;

    private async Task<SubtitleReadback> GetSubtitleReadbackAsync(Session session,long generation,CancellationToken cancellationToken)
    {
        EnsureSubtitleMedia(session,generation);
        var tracks=await SendAsync(session,new object[]{"get_property","track-list"},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        var sid=await SendAsync(session,new object[]{"get_property","sid"},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        var visible=await SendAsync(session,new object[]{"get_property","sub-visibility"},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        if(tracks is not{ } trackList || sid is not{ } selected || visible is not{ } visibility)throw InvalidSubtitleMetadata();
        var selection=ParseSubtitleSelection(trackList,selected,visibility,generation);
        // Only subtitles are affected by sub-add. Decoder metadata on other
        // native tracks can legitimately change while the video keeps playing.
        var subtitleTracks=trackList.EnumerateArray().Where(track=>track.GetProperty("type").GetString()=="sub")
            .OrderBy(track=>track.GetProperty("id").GetInt64()).Select(track=>track.Clone()).ToArray();
        return new(selection,subtitleTracks,selected,visibility);
    }

    private void EnsureSubtitleMedia(Session session,long generation)
    {
        lock(_stateLock)
            if(!ReferenceEquals(_session,session) || _snapshot.MediaGeneration!=generation || !_snapshot.Loaded || _snapshot.Ended ||
                _activeEntry is not{ } entry || !IsCurrentEntry(entry))throw SubtitleMediaChanged();
    }
    private static InvalidOperationException SubtitleMediaChanged()=>new(UiText.T("재생 영상이 바뀌었거나 아직 로딩 중입니다. 자막 목록을 다시 열어 주세요.","The video has changed or is still loading. Reopen the subtitle list.","動画が変更されたか、まだ読み込み中です。字幕一覧を開き直してください。"));
    private static InvalidDataException InvalidSubtitleMetadata()=>new(UiText.T("현재 자막 목록을 확인하지 못했습니다. 다시 열어 주세요.","Could not verify the current subtitle list. Reopen it.","現在の字幕一覧を確認できませんでした。開き直してください。"));

    public static SubtitleSelection ParseSubtitleSelection(JsonElement trackList,JsonElement sid,JsonElement visibility,long generation)
    {
        if(generation<=0 || trackList.ValueKind!=JsonValueKind.Array || trackList.GetArrayLength()>256 ||
            visibility.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw InvalidSubtitleMetadata();
        long? selectedId=null;var automatic=false;
        if(sid.ValueKind==JsonValueKind.Number && sid.TryGetInt64(out var numeric) && numeric>0)selectedId=numeric;
        else if(sid.ValueKind==JsonValueKind.String && sid.GetString()=="auto")automatic=true;
        else if(sid.ValueKind!=JsonValueKind.False && !(sid.ValueKind==JsonValueKind.String && sid.GetString()=="no"))throw InvalidSubtitleMetadata();
        var parsed=new List<(long Id,string? Title,string? Language,bool Selected,bool Primary)>();var ids=new HashSet<long>();
        foreach(var track in trackList.EnumerateArray())
        {
            if(track.ValueKind!=JsonValueKind.Object || !track.TryGetProperty("type",out var type) || type.ValueKind!=JsonValueKind.String)throw InvalidSubtitleMetadata();
            if(type.GetString()!="sub")continue;
            if(!track.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.Number || !id.TryGetInt64(out var trackId) || trackId<=0 || !ids.Add(trackId))throw InvalidSubtitleMetadata();
            var title=SubtitleText(track,"title");var language=NormalizeSubtitleLanguage(SubtitleText(track,"lang"));
            var selected=false;var primary=false;
            if(track.TryGetProperty("selected",out var decoded))
            {
                if(decoded.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw InvalidSubtitleMetadata();
                selected=decoded.GetBoolean();
            }
            if(track.TryGetProperty("main-selection",out var main))
            {
                if(main.ValueKind!=JsonValueKind.Number || !main.TryGetInt64(out var order) || order is <0 or >1)throw InvalidSubtitleMetadata();
                primary=order==0;
            }
            parsed.Add((trackId,title,language,selected,primary));
        }
        if(selectedId.HasValue && !ids.Contains(selectedId.Value))throw InvalidSubtitleMetadata();
        if(automatic)
        {
            var candidates=parsed.Where(track=>track.Selected && track.Primary).ToArray();
            if(candidates.Length==0)candidates=parsed.Where(track=>track.Selected).ToArray();
            if(candidates.Length>1)throw InvalidSubtitleMetadata();
            selectedId=candidates.Length==1?candidates[0].Id:null;
        }
        var off=!visibility.GetBoolean() || !selectedId.HasValue;
        var tracks=parsed.Select(track=>new SubtitleTrack(track.Id,SubtitleLabel(track.Id,track.Title,track.Language),track.Language,!off && track.Id==selectedId)).ToArray();
        return new(generation,tracks,off);
    }
    private static string? SubtitleText(JsonElement track,string name)
    {
        if(!track.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)return null;
        if(value.ValueKind!=JsonValueKind.String)throw InvalidSubtitleMetadata();
        var text=value.GetString()!;
        return new string(text.Select(character=>char.IsControl(character)?' ':character).Take(160).ToArray()).Trim();
    }
    private static string? NormalizeSubtitleLanguage(string? language)=>language is not null && language!="und" &&
        Regex.IsMatch(language,"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,16}){0,3}\\z")?language.ToLowerInvariant():null;
    private static string SubtitleLabel(long id,string? title,string? language)
    {
        var name=language?.Split('-')[0] switch
        {"ko" or "kor"=>UiText.T("한국어","Korean","韓国語"),"en" or "eng"=>UiText.T("영어","English","英語"),"ja" or "jpn"=>UiText.T("일본어","Japanese","日本語"),"zh" or "zho" or "chi"=>UiText.T("중국어","Chinese","中国語"),"es" or "spa"=>UiText.T("스페인어","Spanish","スペイン語"),"fr" or "fra" or "fre"=>UiText.T("프랑스어","French","フランス語"),_=>language??UiText.T("언어 미상","Unknown language","不明な言語")};
        return name+(string.IsNullOrWhiteSpace(title)||title==language?"":" · "+title)+" · #"+id.ToString(CultureInfo.InvariantCulture);
    }
    private static string[] SubtitleLanguages(string? preferred)
    {
        var languages=new List<string>();
        if(preferred!=null)
        {
            languages.Add(preferred);
            switch(preferred.Split('-')[0])
            {case "ko":case "kor":languages.AddRange(["ko","kor"]);break;case "en":case "eng":languages.AddRange(["en","eng"]);break;
             case "ja":case "jpn":languages.AddRange(["ja","jpn"]);break;case "zh":case "zho":case "chi":languages.AddRange(["zh","zho","chi"]);break;}
        }
        languages.AddRange(["ko","kor","en","eng"]);return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void AddSubtitleOptions(Dictionary<string,string> options)
    {options["sid"]=_subtitleOff?"no":"auto";options["slang"]=string.Join(',',SubtitleLanguages(_subtitleLanguage));options["sub-visibility"]=_subtitleOff?"no":"yes";}
    private async Task ConfigureSubtitleDefaultsAsync(Session session,CancellationToken cancellationToken)
    {
        await SendAsync(session,new object[]{"set_property","slang",SubtitleLanguages(_subtitleLanguage)},cancellationToken).ConfigureAwait(false);
        await SendAsync(session,new object[]{"set_property","sid",_subtitleOff?"no":"auto"},cancellationToken).ConfigureAwait(false);
        await SendAsync(session,new object[]{"set_property","sub-visibility",!_subtitleOff},cancellationToken).ConfigureAwait(false);
    }
    private async Task ApplySubtitlePreferenceAsync(Session session,long generation,CancellationToken cancellationToken)
    {
        var selection=await GetSubtitlesCoreAsync(session,generation,cancellationToken).ConfigureAwait(false);
        long? id=null;
        if(!_subtitleOff)
        {
            if(_subtitleSelectionGeneration==generation && _subtitleSelectedId.HasValue && selection.Tracks.Any(track=>track.Id==_subtitleSelectedId.Value))id=_subtitleSelectedId;
            else
            {
                foreach(var language in SubtitleLanguages(_subtitleLanguage))
                {
                    var baseLanguage=WithoutOriginalSuffix(language);
                    // Prefer the requested key and its original-caption equivalent
                    // before the historical regional fallback, regardless of track order.
                    id=(selection.Tracks.FirstOrDefault(track=>string.Equals(track.Language,language,StringComparison.OrdinalIgnoreCase)) ??
                        selection.Tracks.FirstOrDefault(track=>track.Language is {} candidate &&
                            string.Equals(WithoutOriginalSuffix(candidate),baseLanguage,StringComparison.OrdinalIgnoreCase)) ??
                        selection.Tracks.FirstOrDefault(track=>track.Language?.StartsWith(language+"-",StringComparison.OrdinalIgnoreCase)==true))?.Id;
                    if(id.HasValue)break;
                }
                id??=selection.Tracks.FirstOrDefault(track=>track.Selected)?.Id ?? selection.Tracks.FirstOrDefault()?.Id;
            }
        }
        EnsureSubtitleMedia(session,generation);
        await SendAsync(session,new object[]{"set_property","sid",_subtitleOff?"no":id.HasValue?(object)id.Value:"auto"},cancellationToken).ConfigureAwait(false);
        EnsureSubtitleMedia(session,generation);
        await SendAsync(session,new object[]{"set_property","sub-visibility",!_subtitleOff},cancellationToken).ConfigureAwait(false);

        static string WithoutOriginalSuffix(string language)=>language.EndsWith("-orig",StringComparison.OrdinalIgnoreCase)?language[..^5]:language;
    }

    /// <summary>Releases the player and its children, while allowing a later StartAsync.</summary>
    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session is { } session) await CloseSessionAsync(session).ConfigureAwait(false);
                await DrainDetachedCookieSessionsAsync().ConfigureAwait(false);
                lock (_stateLock) _snapshot = _snapshot with { Ended = true, Loaded = false, Buffering = false, Seeking = false };
                PublishSnapshot();
            }
            finally { _startGate.Release(); }
        }
        finally { _loadGate.Release(); }
    }

    // Only this exception proves that mpv returned a terminal command rejection.
    // Transport failures and local validation errors remain unconfirmed operations.
    private sealed class MpvCommandException(string error):InvalidOperationException("mpv: "+error) { }

    /// <summary>A terminal subtitle rejection with current-media ownership and unchanged native subtitle state confirmed.</summary>
    public sealed class SubtitleCommandRejectedException:InvalidOperationException
    {
        internal SubtitleCommandRejectedException(string message,Exception innerException):base(message,innerException) { }
    }

    private async Task<JsonElement?> SendAsync(Session session, object[] args, CancellationToken cancellationToken, TimeSpan? timeout = null,Action? beforeWrite=null)
    {
        if (session.Token.IsCancellationRequested || session.Writer is null) throw new IOException("mpv IPC is disconnected.");
        var id = Interlocked.Increment(ref session.NextRequest);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = completion;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            await session.WriteGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                beforeWrite?.Invoke();
                var message = JsonSerializer.Serialize(new { command = args, request_id = id }, IpcJson);
                await session.Writer.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
                await session.Writer.FlushAsync(token).ConfigureAwait(false);
            }
            finally { session.WriteGate.Release(); }
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(session.Token.IsCancellationRequested ? "mpv IPC disconnected." : "mpv IPC command timed out.");
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private async Task ReadMessagesAsync(Session session)
    {
        try
        {
            var reader = new BoundedLineReader(session.Reader!, BoundedTextReader.MpvLineLimit);
            while (!session.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(session.Token).ConfigureAwait(false);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                ValidateIpcMessage(message);
                if (message.TryGetProperty("request_id", out var request) && request.TryGetInt64(out var id) && session.Pending.TryRemove(id, out var completion))
                {
                    var error = message.TryGetProperty("error", out var e) ? e.GetString() : null;
                    if (string.IsNullOrEmpty(error)) completion.TrySetException(new IOException("mpv IPC command reply omitted its status."));
                    else if (error != "success") completion.TrySetException(new MpvCommandException(error));
                    else completion.TrySetResult(message.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ? data.Clone() : null);
                }
                else if (message.TryGetProperty("event", out var eventName) && ReferenceEquals(_session, session))
                    HandleEvent(session, eventName.GetString(), message);
            }
            ReportUnexpectedDisconnect(session, UiText.T("mpv 연결이 끊어졌습니다. 다시 재생하면 플레이어를 재시작합니다.","The mpv connection was lost. Playing the video again will restart the player.","mpv との接続が切れました。もう一度再生するとプレーヤーが再起動します。"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            ReportUnexpectedDisconnect(session, UiText.T("mpv 연결이 끊어졌습니다. 다시 재생하면 플레이어를 재시작합니다.","The mpv connection was lost. Playing the video again will restart the player.","mpv との接続が切れました。もう一度再生するとプレーヤーが再起動します。"));
        }
        finally
        {
            CancelSession(session);
            FailPending(session, new IOException("mpv IPC disconnected."));
            // A disconnected reader can no longer control or drain this player.
            // Stop only its owned job, including any extractor descendants.
            try { session.Process.Kill(); } catch { }
        }
    }

    private static void ValidateIpcMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new JsonException("An mpv IPC message must be an object.");
        foreach (var name in new[] { "request_id", "playlist_entry_id" })
            if (message.TryGetProperty(name, out var number) && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out _)))
                throw new JsonException("Invalid mpv IPC numeric field: " + name);
        foreach (var name in new[] { "event", "error", "name", "reason" })
            if (message.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.String)
                throw new JsonException("Invalid mpv IPC string field: " + name);
    }

    private void HandleEvent(Session session, string? eventName, JsonElement message)
    {
        switch (eventName)
        {
            case "start-file":
                if (message.TryGetProperty("playlist_entry_id", out var id) && id.TryGetInt64(out var entry))
                    lock (_stateLock)
                    {if(!ReferenceEquals(_session,session))return;_activeEntry = entry;session.ExtractionMetadata.Remove(entry);session.ExtractionFailures.Remove(entry);}
                break;
            case "file-loaded":
                long? loadedEntry = null;
                long loadedGeneration = 0;
                lock (_stateLock)
                {
                    if(!ReferenceEquals(_session,session))return;
                    if (_activeEntry is { } active)
                    {
                        _loadedEntries.Add(active);
                        session.ExtractionMetadata.Remove(active);session.ExtractionFailures.Remove(active);
                        PruneEntryHistory();
                        if (session.FileReady.TryRemove(active, out var ready)) ready.TrySetResult(true);
                        if (IsCurrentEntry(active))
                        {
                            _snapshot = _snapshot with { Loaded = true, Ended = false, EndReason = null, Buffering = false,ErrorMessage=null,AuthenticationRequired=false,
                                CanReleaseCookies = Interlocked.Read(ref session.DirectEntry) == active };
                            loadedEntry = active; loadedGeneration = _snapshot.MediaGeneration;
                        }
                    }
                }
                PublishSnapshot();
                if (loadedEntry is { } actual && Interlocked.Read(ref session.DirectEntry) != actual)
                    _ = RefreshCookieReleaseSafetyAsync(session, actual, loadedGeneration);
                break;
            case "end-file": HandleEndFile(session, message); break;
            case "property-change":
                if (message.TryGetProperty("name", out var name) && message.TryGetProperty("data", out var data))
                {
                    var property=name.GetString();
                    if(property==ExtractionResultProperty+"/status" || property==ExtractionResultProperty+"/stderr")CaptureExtractionMetadata(session,property,data);
                    else ApplyProperty(session,property,data);
                }
                break;
            case "shutdown":
                ReportUnexpectedDisconnect(session, UiText.T("mpv가 종료되었습니다. 다시 재생하면 플레이어를 재시작합니다.","mpv has exited. Playing the video again will restart the player.","mpv が終了しました。もう一度再生するとプレーヤーが再起動します。"));
                CancelSession(session);
                FailPending(session, new IOException("mpv shut down."));
                break;
        }
    }

    private void CaptureExtractionMetadata(Session session,string property,JsonElement data)
    {
        lock(_stateLock)
        {
            if(!ReferenceEquals(_session,session) || _snapshot.MediaGeneration==0 || _activeEntry is not{ } entry ||
                (_entryGenerations.TryGetValue(entry,out var mapped) && mapped!=_snapshot.MediaGeneration))return;
            var generation=_snapshot.MediaGeneration;
            // on_after_end_file deletes both leaves. Keep only the completed safe
            // failure code; incomplete pairs cannot carry into a later extraction.
            if(data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined){session.ExtractionMetadata.Remove(entry);return;}
            var state=session.ExtractionMetadata.TryGetValue(entry,out var previous) && previous.Generation==generation?previous:new ExtractionMetadata(generation,null,null);
            if(property.EndsWith("/status",StringComparison.Ordinal))
            {
                if(data.ValueKind!=JsonValueKind.Number || !data.TryGetInt32(out var status))return;
                state=state with{Status=status};
                if(status==0)session.ExtractionFailures.Remove(entry);
            }
            else
            {
                if(data.ValueKind!=JsonValueKind.String || data.GetString() is not{ } text || text.Length>BoundedTextReader.CommandErrorLimit)return;
                // Retain the allowlisted code, never the diagnostic or signed URLs.
                state=state with{Code=ProcessRunner.ClassifyError(text)};
            }
            session.ExtractionMetadata[entry]=state;
            if(state.Status is{ } failed && failed!=0 && state.Code is{ } code)session.ExtractionFailures[entry]=new(generation,code);
            foreach(var old in session.ExtractionMetadata.Keys.OrderDescending().Skip(64).ToArray())session.ExtractionMetadata.Remove(old);
            foreach(var old in session.ExtractionFailures.Keys.OrderDescending().Skip(64).ToArray())session.ExtractionFailures.Remove(old);
        }
    }

    private void HandleEndFile(Session session, JsonElement message)
    {
        bool current;
        bool adopting;
        long generation,entry;
        var failed=message.TryGetProperty("reason",out var reason) && reason.GetString()=="error";
        lock (_stateLock)
        {
            if (!ReferenceEquals(_session, session)) return;
            if (!message.TryGetProperty("playlist_entry_id", out var id) || !id.TryGetInt64(out entry)) return;
            _endedEntries[entry] = message.Clone();
            PruneEntryHistory();
            if (session.FileReady.TryRemove(entry, out var ready)) ready.TrySetResult(false);
            current = IsCurrentEntry(entry);
            adopting = entry == Interlocked.Read(ref session.AdoptingEntry);
            generation=_snapshot.MediaGeneration;
            if (current && !adopting) _snapshot = _snapshot with { Ended = true, EndReason = message.TryGetProperty("reason",out var endReason)?endReason.GetString():null, Loaded = false, Buffering = false, Seeking = false,
                ErrorMessage=failed?MediaFailureMessage:_snapshot.ErrorMessage,AuthenticationRequired=failed?false:_snapshot.AuthenticationRequired };
        }
        if (!current || adopting) return;
        PublishSnapshot();
        if (failed && Interlocked.Exchange(ref session.ErrorMetadataGeneration,generation)!=generation)
        {
            // Never await an IPC command from its own read loop. The separate task
            // serializes with loads, then checks the session and generation again.
            _ = RefinePlaybackFailureAsync(session,entry,generation);
        }
    }

    private async Task RefinePlaybackFailureAsync(Session session,long entry,long generation)
    {
        var held=false;
        try
        {
            await _loadGate.WaitAsync(session.Token).ConfigureAwait(false);held=true;
            bool Current(){lock(_stateLock)return ReferenceEquals(_session,session) && _snapshot.MediaGeneration==generation && _activeEntry==entry && IsCurrentEntry(entry) && _snapshot.Ended && _snapshot.EndReason=="error";}
            if(!Current())return;
            var code="generic";
            if(Interlocked.Read(ref session.DirectEntry)!=entry)
            {
                bool Cached(){lock(_stateLock){if(session.ExtractionFailures.TryGetValue(entry,out var failure) && failure.Generation==generation){code=failure.Code;return true;}return false;}}
                if(!Cached())try
                {
                    var status=await SendAsync(session,["get_property",ExtractionResultProperty+"/status"],session.Token,TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if(status is{ValueKind:JsonValueKind.Number} value && value.TryGetInt32(out var number) && number!=0)
                    {
                        var stderr=await SendAsync(session,["get_property",ExtractionResultProperty+"/stderr"],session.Token,TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        if(stderr is{ValueKind:JsonValueKind.String} diagnostic && diagnostic.GetString() is{ } text && text.Length<=BoundedTextReader.CommandErrorLimit)
                            code=ProcessRunner.ClassifyError(text);
                    }
                }
                catch(Exception ex)when(ex is IOException or InvalidOperationException or JsonException){/* Missing metadata keeps the media error. */}
                Cached();
            }
            var message=code=="generic"?MediaFailureMessage:ProcessRunner.ErrorMessage(code,session.HasPlaybackCookies);
            lock(_stateLock)
            {
                if(!Current())return;
                _snapshot=_snapshot with{ErrorMessage=message,AuthenticationRequired=code=="signin_age"};
            }
            PublishSnapshot();RaiseError(message,generation,session);
        }
        catch(Exception ex)when(ex is OperationCanceledException or ObjectDisposedException){/* A replaced session cannot report on its successor. */}
        finally{if(held)_loadGate.Release();}
    }

    private bool IsCurrentEntry(long entry) => _entryGenerations.TryGetValue(entry, out var generation) && generation == _snapshot.MediaGeneration;

    // Paused prepared entries can be removed before being assigned a media generation.
    // Bound those event records as well as records from normal and adopted playback.
    private void PruneEntryHistory()
    {
        foreach (var old in _entryGenerations.Keys.Concat(_loadedEntries).Concat(_endedEntries.Keys).Distinct().OrderDescending().Skip(64).ToArray())
        { _entryGenerations.Remove(old); _loadedEntries.Remove(old); _endedEntries.Remove(old); }
    }

    private void ApplyProperty(Session session, string? name, JsonElement value, long? expectedGeneration = null)
    {
        bool publish;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_session, session) || _snapshot.Ended || _activeEntry is not { } active || !IsCurrentEntry(active) ||
                (expectedGeneration.HasValue && expectedGeneration.Value != _snapshot.MediaGeneration)) return;
            var s = _snapshot;
            switch (name)
            {
                case "time-pos" when value.TryGetDoubleSafe(out var position): s = s with { Position = Math.Max(0, position) }; break;
                case "duration" when value.TryGetDoubleSafe(out var duration): s = s with { Duration = Math.Max(0, duration) }; break;
                case "pause" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: s = s with { Paused = value.GetBoolean() }; break;
                case "paused-for-cache" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: s = s with { Buffering = !s.Loaded || value.GetBoolean() }; break;
                case "seeking" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: s = s with { Seeking = value.GetBoolean() }; break;
                case "speed" when value.TryGetDoubleSafe(out var speed): s = s with { Speed = speed }; break;
                case "volume" when value.TryGetDoubleSafe(out var volume): s = s with { Volume = volume }; break;
                case "hwdec-current": s = s with { HardwareDecoder = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "unavailable" : "unavailable" }; break;
                default: return;
            }
            _snapshot = s;
            var now = Environment.TickCount64;
            publish = name != "time-pos" || now - _lastSnapshotAt >= 250;
            if (publish) _lastSnapshotAt = now;
        }
        if (publish) PublishSnapshot();
    }

    private async Task RefreshPropertiesAsync(Session session, long generation, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "time-pos", "duration", "pause", "paused-for-cache", "seeking", "speed", "volume", "hwdec-current" })
        {
            try
            {
                var value = await SendAsync(session, new object[] { "get_property", name }, cancellationToken).ConfigureAwait(false);
                if (value is { } actual) ApplyProperty(session, name, actual, generation);
            }
            catch (InvalidOperationException) { /* Some properties are unavailable until a decoder exists. */ }
        }
    }

    private void UpdateGeneration(long generation, Func<PlaybackSnapshot, PlaybackSnapshot> update)
    {
        lock (_stateLock) { if (_snapshot.MediaGeneration != generation) return; _snapshot = update(_snapshot); }
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        PlaybackSnapshot snapshot;
        lock (_stateLock) snapshot = _snapshot;
        PublishSnapshot(snapshot);
    }

    private void PublishSnapshot(PlaybackSnapshot snapshot)
    {
        try { SnapshotChanged?.Invoke(snapshot); } catch { /* A UI subscriber must not stop the IPC reader. */ }
    }

    private void RaiseError(string message, long expectedGeneration, Session? expectedOwner = null)
    {
        PlaybackFailure failure;
        lock (_stateLock)
        {
            if (_snapshot.MediaGeneration != expectedGeneration ||
                expectedOwner is not null && !ReferenceEquals(_session, expectedOwner)) return;
            failure = new(_snapshot.MediaGeneration, _snapshot.VideoUrl, message);
        }
        // A UI dispatcher can deliver this after another video has started. Keep the
        // originating generation with the message so subscribers can reject it then.
        try { PlaybackError?.Invoke(failure); } catch { }
    }

    private static string MpvListValue(string value) => "%" + Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + "%" + value;

    private static async Task DrainAsync(StreamReader reader, Session session)
    {
        try
        {
            await BoundedTextReader.DrainAsync(reader, session.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task WatchExitAsync(Session session)
    {
        try
        {
            await session.Process.WaitForExitAsync().ConfigureAwait(false);
            ReportUnexpectedDisconnect(session, UiText.T("mpv가 종료되었습니다. 다시 재생하면 플레이어를 재시작합니다.","mpv has exited. Playing the video again will restart the player.","mpv が終了しました。もう一度再生するとプレーヤーが再起動します。"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        finally { FailPending(session, new IOException("mpv process exited.")); CancelSession(session); }
    }

    private void ReportUnexpectedDisconnect(Session session, string message)
    {
        PlaybackSnapshot failure;
        lock (_stateLock)
        {
            // Owner validation and the generation/state transition are one operation.
            // Process exit and pipe EOF may race; only one reports this disconnection.
            if (session.StopRequested || session.Token.IsCancellationRequested || session.DisconnectReported ||
                !ReferenceEquals(_session, session) || session.MediaGeneration != _snapshot.MediaGeneration) return;
            session.DisconnectReported = true;
            failure = _snapshot = _snapshot with { Ended = true, EndReason = "error", Loaded = false,
                Buffering = false, Seeking = false, ErrorMessage = _snapshot.ErrorMessage ?? message };
        }
        PublishSnapshot(failure);
        RaiseError(failure.ErrorMessage!, failure.MediaGeneration, session);
    }

    private static void FailPending(Session session, Exception error)
    {
        foreach (var request in session.Pending)
            if (session.Pending.TryRemove(request.Key, out var completion)) completion.TrySetException(error);
        foreach (var entry in session.FileReady)
            if (session.FileReady.TryRemove(entry.Key, out var ready)) ready.TrySetException(error);
    }

    private async Task CloseSessionAsync(Session session)
    {
        if (session.CookieConsumersStopped) return;
        if (session.ConnectionClosed)
        {
            if (await TryConfirmStoppedConsumersAsync(session).ConfigureAwait(false)) CompleteStoppedConsumers(session);
            return;
        }
        lock (_stateLock)
        {
            session.StopRequested = true;
            if (ReferenceEquals(_session, session)) _session = null;
            _retainedCookieSessions.Add(session);
            if (_preparedNext is { } prepared && ReferenceEquals(prepared.Session, session)) _preparedNext = null;
        }
        if (!session.Token.IsCancellationRequested && session.Writer is not null)
        {
            try { await SendAsync(session, new object[] { "quit" }, CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { try { session.Process.Kill(); } catch { } }
        var consumersStopped = await TryConfirmStoppedConsumersAsync(session).ConfigureAwait(false);
        CancelSession(session);
        FailPending(session, new IOException("mpv connection closed."));
        session.Pipe.Dispose();
        session.ConnectionClosed = true;
        // Keep an unconfirmed owner's real job/process handles for a later retry.
        // An empty query after OwnedProcess.Dispose cannot prove termination.
        if (consumersStopped) CompleteStoppedConsumers(session);
        try { await Task.WhenAll(session.ReadTask, session.OutputTask, session.ErrorTask, session.ExitTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { session.Reader?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { session.Writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        session.Cancellation.Dispose();
    }

    private static async Task<bool> TryConfirmStoppedConsumersAsync(Session session)
    {
        try
        {
            // Validate the native wait handle before querying the owned job. A disposed
            // OwnedProcess returns an empty job list and must never become a receipt.
            session.Process.Kill();
            _ = session.Process.HasExited;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (session.Process.GetProcessIds().Count != 0)
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            await session.Process.WaitForExitAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            return session.Process.HasExited;
        }
        catch { return false; }
    }

    private void CompleteStoppedConsumers(Session session)
    {
        CookieLease[] releasedCookies = [];
        lock (_stateLock)
        {
            if (session.CookieConsumersStopped) return;
            session.CookieConsumersStopped = true;
            releasedCookies = session.RetainedCookieLeases.ToArray(); session.RetainedCookieLeases.Clear();
            _retainedCookieSessions.Remove(session);
            if (session.CookieConsumerGeneration > 0 && _snapshot.MediaGeneration == session.CookieConsumerGeneration)
                _stoppedCookieConsumerGeneration = session.CookieConsumerGeneration;
        }
        session.Process.Dispose();
        foreach (var lease in releasedCookies) lease.Dispose();
    }

    private async Task DrainDetachedCookieSessionsAsync()
    {
        Session[] detached;
        lock (_stateLock) detached = _retainedCookieSessions.Where(session => session.ConnectionClosed && !session.CookieConsumersStopped).ToArray();
        foreach (var session in detached)
            if (await TryConfirmStoppedConsumersAsync(session).ConfigureAwait(false)) CompleteStoppedConsumers(session);
        lock (_stateLock)
            if (_retainedCookieSessions.Any(session => session.ConnectionClosed && !session.CookieConsumersStopped))
                throw new IOException(UiText.T("이전 재생 프로세스의 종료를 확인하지 못했습니다. 정리가 끝난 뒤 다시 재생해 주세요.","Could not verify that the previous playback process exited. Wait for cleanup, then play the video again.","前の再生プロセスの終了を確認できませんでした。クリーンアップ完了後にもう一度再生してください。"));
    }

    private static void CancelSession(Session session)
    {
        try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposeComplete) != 0) return;
        Interlocked.Exchange(ref _disposed, 1);
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session is { } session) await CloseSessionAsync(session).ConfigureAwait(false);
                await DrainDetachedCookieSessionsAsync().ConfigureAwait(false);
                Volatile.Write(ref _disposeComplete, 1);
            }
            finally { _startGate.Release(); }
        }
        finally { _loadGate.Release(); }
    }

    private sealed class Session
    {
        public Session(OwnedProcess process, NamedPipeClientStream pipe)
        {
            Process = process; Pipe = pipe;
            Token = Cancellation.Token;
        }

        public OwnedProcess Process { get; }
        public NamedPipeClientStream Pipe { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }
        public ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> Pending { get; } = new();
        public ConcurrentDictionary<long, TaskCompletionSource<bool>> FileReady { get; } = new();
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public StreamReader? Reader;
        public StreamWriter? Writer;
        public Task ReadTask = Task.CompletedTask, OutputTask = Task.CompletedTask, ErrorTask = Task.CompletedTask, ExitTask = Task.CompletedTask;
        public long NextRequest;
        public long AdoptingEntry = -1;
        public long DirectEntry = -1;
        public long CookieMetadataEntry = -1;
        public long ErrorMetadataGeneration = -1;
        public bool HasPlaybackCookies;
        public long MediaGeneration;
        public long CookieConsumerGeneration = -1;
        public bool CookieConsumersStopped;
        public bool ConnectionClosed;
        public HashSet<CookieLease> RetainedCookieLeases { get; } = [];
        public Dictionary<long,ExtractionMetadata> ExtractionMetadata {get;}=new();
        public Dictionary<long,ExtractionFailure> ExtractionFailures {get;}=new();
        public int PlaylistCleanupFailures;
        public long ExternalSubtitleGeneration,ExternalSubtitleUse;
        public Dictionary<string,SelectedExternalSubtitle> ExternalSubtitles {get;}=new(StringComparer.Ordinal);
        public volatile bool StopRequested;
        public bool DisconnectReported;
    }

    private sealed record PreparedNext(Session Session, long Entry, long OriginGeneration, string VideoUrl, int MaxHeight);
    private sealed record SelectedExternalSubtitle(long Id,long LastUsed);
    private sealed record ExtractionMetadata(long Generation,int? Status,string? Code);
    private sealed record ExtractionFailure(long Generation,string Code);
}

public sealed record PlaybackFailure(long MediaGeneration, string? VideoUrl, string Message);

public sealed record SubtitleTrack(long Id,string Label,string? Language,bool Selected);
public sealed record SubtitleSelection(long MediaGeneration,IReadOnlyList<SubtitleTrack> Tracks,bool Off);

internal static class MpvJsonValue
{
    public static bool TryGetDoubleSafe(this JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }
}
