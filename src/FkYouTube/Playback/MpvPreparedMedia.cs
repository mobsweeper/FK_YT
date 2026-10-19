using System.Globalization;
using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;

namespace FkYouTube.Playback;

/// <summary>The same multi-stream EDL representation used by mpv's built-in yt-dlp hook.</summary>
public static class MpvPreparedMedia
{
    public static string BuildEdlUrl(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count is < 1 or > 2) throw new ArgumentException("One muxed stream or one video and one audio stream are required.", nameof(urls));
        foreach (var value in urls)
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("A resolved HTTPS media URL is required.", nameof(urls));
        if (urls.Count == 1) return urls[0];
        return "edl://" + string.Join(";", urls.Select(url => "!new_stream;!no_clip;!no_chapters;" + Escape(url)));
    }

    internal static string Escape(string value) => "%" + Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + "%" + value;

    /// <summary>Preserves an open-ended single live source while preventing yt-dlp fallback.</summary>
    public static string BuildLiveEdlUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || url.Any(char.IsControl))
            throw new ArgumentException("A resolved HTTPS live media URL is required.", nameof(url));
        return "edl://!no_clip;!no_chapters;" + Escape(url);
    }
}

/// <summary>Two owned native yt-dlp fragment consumers, never an arbitrary local URL.</summary>
public sealed class LiveFromStartTransport : IAsyncDisposable
{
    private static readonly PendingCleanupTracker Cleanups = new();
    private readonly CancellationTokenSource _stop;
    private readonly Track[] _tracks;
    private readonly string _stage;
    private readonly string _stageRoot;
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _cleanup;
    private int _disposed;

    internal string VideoId { get; }
    internal string MediaUrl { get; }
    // EOF is only a transport result. The caller must verify broadcast status;
    // a fragment producer's clean exit does not prove the broadcast has ended.
    internal Task<string> Completion => _ended.Task;

    private LiveFromStartTransport(RuntimePaths paths, string videoId, string url, CookieLease lease,
        YouTubeChannel? account, int maximumHeight, CancellationToken cancellationToken)
    {
        VideoId = videoId;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stageRoot = Path.Combine(paths.Data, "temp");
        try { _stage = CreateStage(_stageRoot); }
        catch { _stop.Dispose(); throw; }
        var tracks = new List<Track>();
        try
        {
            foreach (var role in new[] { "video", "audio" })
                tracks.Add(new Track(paths, url, lease, account, maximumHeight, role,
                    Path.Combine(_stage, role), _connect.Task, _stop.Token));
            _tracks = tracks.ToArray();
            // These sources are generated here, never from extractor JSON or UI
            // input. The normal HTTPS-only prepared-media boundary stays intact.
            MediaUrl = "edl://" + string.Join(';', _tracks.Select(track =>
                "!new_stream;!no_clip;!no_chapters;" + MpvPreparedMedia.Escape(track.PipePath)));
            _cleanup = ObserveAndCleanAsync();
            Cleanups.Track(_cleanup);
        }
        catch
        {
            CancelProducers();
            foreach (var track in tracks) track.ClosePipe();
            _tracks = tracks.ToArray();
            _cleanup = FinishCleanupAsync();
            Cleanups.Track(_cleanup);
            throw;
        }
    }

    internal static async Task<LiveFromStartTransport> StartAsync(RuntimePaths paths, string youtubeUrl,
        CookieLease lease, YouTubeChannel? account, int maximumHeight, CancellationToken cancellationToken)
    {
        if (!YouTubeService.TryVideoUrl(youtubeUrl, out var id, out var canonical) || canonical != youtubeUrl)
            throw new InvalidDataException("A canonical YouTube video URL is required.");
        YouTubeAccountOptions.Validate(account);
        if (maximumHeight is < 0 or > 16384) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        cancellationToken.ThrowIfCancellationRequested();
        FileBoundary.EnsureRegularPath(paths.Python);
        FileBoundary.EnsureRegularPath(paths.Deno);
        var transport = new LiveFromStartTransport(paths, id, canonical, lease, account, maximumHeight, cancellationToken);
        try
        {
            await Task.WhenAll(transport._tracks.Select(track => track.Ready.Task))
                .WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (transport.Completion.IsCompleted)
                throw new LiveFromStartException(await transport.Completion.ConfigureAwait(false));
            return transport;
        }
        catch (Exception error)
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                // Preserve cancellation, timeout and classified producer errors.
                // The tracker still owns pending cleanup and reports failed drains.
                error.Data["FKYT.LiveFromStartCleanupFailure"] = cleanupError;
            }
            throw;
        }
    }

    // Start the bounded consumer connection wait only when loadfile is ready to
    // be submitted. Preparation and preceding mpv settings do not use its budget.
    internal void BeginConnect() => _connect.TrySetResult();

    private async Task ObserveAndCleanAsync()
    {
        string reason;
        try
        {
            var first = await Task.WhenAny(_tracks.Select(track => track.Work)).ConfigureAwait(false);
            var firstTrack = _tracks.Single(track => ReferenceEquals(track.Work, first));
            reason = await firstTrack.ReadResultAsync().ConfigureAwait(false);
            if(reason=="eof")
            {
                // A final video segment may finish before its audio tail. A
                // successful producer must not truncate the other track.
                var other=_tracks.Single(track=>!ReferenceEquals(track,firstTrack));
                reason=await other.ReadResultAsync().ConfigureAwait(false);
            }
        }
        catch { reason = _stop.IsCancellationRequested ? "cancelled" : "generic"; }
        _ended.TrySetResult(reason);
        CancelProducers();
        foreach (var track in _tracks) track.ClosePipe();
        await FinishCleanupAsync().ConfigureAwait(false);
    }

    private async Task FinishCleanupAsync()
    {
        // A failed stop remains owned by ProcessRunner's recovery observer.
        // Its receipt, rather than the cancelled pump task, gates file deletion.
        foreach (var track in _tracks)
        {
            try { await track.Work.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or Win32Exception or InvalidOperationException) { }
        }
        await Task.WhenAll(_tracks.Select(track => track.Receipt.Completion)).ConfigureAwait(false);
        await RemoveStageAsync(_stageRoot, _stage).ConfigureAwait(false);
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && !_cleanup.IsCompleted)
        {
            CancelProducers();
            foreach (var track in _tracks) track.ClosePipe();
        }
        try { await _cleanup.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("Live stream cleanup is still pending.", ex); }
    }

    private void CancelProducers()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { } // Another completed drain already owns disposal.
    }

    internal static Task DrainCleanupAsync() => Cleanups.DrainAsync(TimeSpan.FromSeconds(6),
        "Some live stream cleanup is still pending.");

    // Call only after RuntimePaths.AcquireDataOwnership and before starting any
    // producers. A previous crash cannot have a surviving kill-on-close job.
    internal static async Task CleanupStaleAsync(RuntimePaths paths)
    {
        var root = Path.Combine(paths.Data, "temp");
        FileBoundary.EnsureRegularPath(root);
        if (!Directory.Exists(root)) return;
        foreach (var stage in Directory.EnumerateDirectories(root, "live-*.work"))
            if (IsOwnedStage(root, stage)) await RemoveStageAsync(root, stage).ConfigureAwait(false);
    }

    private static string CreateStage(string root)
    {
        FileBoundary.EnsureRegularPath(root);
        Directory.CreateDirectory(root);
        FileBoundary.EnsureRegularPath(root);
        var path = Path.Combine(root, "live-" + Guid.NewGuid().ToString("N") + ".work");
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Live stream staging path already exists.");
        DirectoryInfo? directory = null;
        try
        {
            directory = Directory.CreateDirectory(path);
            FileBoundary.EnsureRegularPath(path);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows user identity is unavailable.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            return path;
        }
        catch (Exception error)
        {
            if (directory is not null)
            {
                try { RollbackEmptyStage(root, path); }
                catch (Exception cleanupError) { error.Data["FKYT.LiveFromStartStageRollbackFailure"] = cleanupError; }
            }
            throw;
        }
    }

    private static void RollbackEmptyStage(string root, string stage)
    {
        if (!IsOwnedStage(root, stage)) throw new IOException("Invalid live stream staging path.");
        FileBoundary.EnsureRegularPath(stage);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: false);
    }

    private static bool IsOwnedStage(string root, string stage)
    {
        var full = Path.GetFullPath(stage);
        var name = Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            && name.Length == 42 && name.StartsWith("live-", StringComparison.Ordinal)
            && name.EndsWith(".work", StringComparison.Ordinal) && Guid.TryParseExact(name[5..37], "N", out _);
    }

    private static async Task RemoveStageAsync(string root, string stage)
    {
        if (!IsOwnedStage(root, stage)) throw new IOException("Invalid live stream staging path.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                FileBoundary.EnsureRegularPath(stage);
                if (!Directory.Exists(stage)) return;
                // Never traverse links or recursively delete an unrecognised tree.
                foreach (var role in Directory.EnumerateFileSystemEntries(stage))
                {
                    FileBoundary.EnsureRegularPath(role);
                    if (Path.GetFileName(role) is not ("video" or "audio") || !Directory.Exists(role))
                        throw new IOException("Unexpected entry in live stream staging.");
                    foreach (var file in Directory.EnumerateFileSystemEntries(role))
                    {
                        FileBoundary.EnsureRegularPath(file);
                        if (Directory.Exists(file) || !System.Text.RegularExpressions.Regex.IsMatch(
                            Path.GetFileName(file), @"^-Frag[0-9]+(?:\.part)?$"))
                            throw new IOException("Unexpected fragment staging entry.");
                        File.Delete(file);
                    }
                    Directory.Delete(role);
                }
                Directory.Delete(stage);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
        }
    }

    private sealed class Track
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly OwnedProcess _process;
        private readonly CancellationToken _cancellationToken;
        private readonly Task _connect;
        private string? _terminalReason;
        private Exception? _processCleanupFailure;
        internal ProcessStopReceipt Receipt { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<string> Work { get; }
        internal string PipePath { get; }

        internal Track(RuntimePaths paths, string url, CookieLease lease, YouTubeChannel? account,
            int maximumHeight, string role, string directory, Task connect, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            FileBoundary.EnsureRegularPath(directory);
            _cancellationToken = cancellationToken;
            _connect = connect;
            var name = "FKYT-Live-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            PipePath = @"\\.\pipe\" + name;
            _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            try
            {
                lease.AttachConsumer(Receipt);
                Receipt.MarkStarted();
                var args = new List<string> { "--fk-live-fragment-stream", "--ignore-config", "--no-plugin-dirs",
                    "--no-cache-dir", "--no-playlist", "--no-mark-watched", "--js-runtimes", "deno:" + paths.Deno };
                var extractor = YouTubeAccountOptions.ExtractorArguments(account);
                if (extractor.Length > 0) args.AddRange(["--extractor-args", extractor]);
                if (lease.Path is { } cookiePath) args.AddRange(["--cookies", cookiePath]);
                args.Add(url);
                _process = OwnedProcess.Start(paths.Python, paths.BuildPythonArguments(
                    Path.Combine(paths.Root, "worker", "selected_ytdlp.py"), args, unbuffered: true), directory);
            }
            catch { Receipt.ConfirmStopped(); _pipe.Dispose(); throw; }
            var request = JsonSerializer.Serialize(new { accountContext = account, operation = "live_fragment_stream",
                track = role, maxHeight = maximumHeight }, JsonDefaults.Options);
            Work = PumpAsync(request);
        }

        internal void ClosePipe() => _pipe.Dispose();

        internal async Task<string> ReadResultAsync()
        {
            try { return await Work.ConfigureAwait(false); }
            catch (Exception error) when (ReferenceEquals(error, _processCleanupFailure) && _terminalReason is not null)
            {
                // Work still faults and its process recovery retains the real
                // owner/receipt. Only the already computed media result survives.
                return _terminalReason;
            }
        }

        private string PreserveResult(string reason) => _terminalReason =
            reason is "eof" or "cancelled" || ProcessRunner.IsErrorCode(reason) ? reason : "generic";

        private async Task<string> PumpAsync(string request)
        {
            using var stop = _cancellationToken.Register(() => { try { _process.Kill(); } catch { } });
            var errors = BoundedTextReader.ReadToEndAsync(_process.StandardError, 65536, _cancellationToken);
            try
            {
                await _process.StandardInput.WriteLineAsync(request.AsMemory(), _cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(_cancellationToken).ConfigureAwait(false);
                _process.StandardInput.Close();
                var buffer = new byte[65536];
                var count = await _process.StandardOutput.BaseStream.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    await _process.WaitForExitAsync(_cancellationToken).ConfigureAwait(false);
                    var reason = ProcessRunner.ClassifyError(await errors.ConfigureAwait(false));
                    Ready.TrySetException(new LiveFromStartException(reason));
                    return PreserveResult(reason);
                }
                Ready.TrySetResult();
                await _connect.WaitAsync(_cancellationToken).ConfigureAwait(false);
                await _pipe.WaitForConnectionAsync(_cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), _cancellationToken).ConfigureAwait(false);
                do
                {
                    await _pipe.WriteAsync(buffer.AsMemory(0, count), _cancellationToken).ConfigureAwait(false);
                    count = await _process.StandardOutput.BaseStream.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false);
                } while (count != 0);
                await _process.WaitForExitAsync(_cancellationToken).ConfigureAwait(false);
                return PreserveResult(_process.ExitCode == 0 ? "eof" : ProcessRunner.ClassifyError(await errors.ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            { Ready.TrySetCanceled(_cancellationToken); return PreserveResult("cancelled"); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            { Ready.TrySetException(new IOException("Could not open the live fragment stream.")); return PreserveResult("generic"); }
            finally
            {
                await stop.DisposeAsync().ConfigureAwait(false);
                _pipe.Dispose();
                try { await ProcessRunner.CompleteOwnedProcessAsync(_process, [errors], Receipt).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or ObjectDisposedException)
                {
                    // CompleteOwnedProcessAsync has handed the same owner and
                    // receipt to recovery before propagating this stop failure.
                    _processCleanupFailure = error;
                    throw;
                }
            }
        }
    }
}

internal sealed class LiveFromStartException(string code) : IOException(ProcessRunner.ErrorMessage(code))
{
    internal string ErrorCode { get; } = ProcessRunner.IsErrorCode(code) ? code : "generic";
}
