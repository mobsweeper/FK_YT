using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace FkYouTube.Services;

public sealed class ThumbnailCache : IDisposable
{
    private const int MaxDownloads = 4, MaxPending = 128, MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 3, MaxDecodeDimension = 360, MaxSourceDimension = 8192;
    private const long MaxSourcePixels = 16L * 1024 * 1024;
    private const int MaxDiskEntries = 8192, MaxDiskTrimCandidates = 64;
    private const long MemoryLimit = 96L * 1024 * 1024, DiskLimit = 512L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly string _directory;
    private readonly object _sync = new();
    private readonly Dictionary<string, (BitmapSource Bitmap, long Size, LinkedListNode<string> Node)> _memory = [];
    private readonly LinkedList<string> _lru = [];
    private readonly Dictionary<string, Request> _inflight = [];
    private readonly LinkedList<Request> _pending = [];
    private readonly SemaphoreSlim _diskIo = new(1, 1);
    // Only background workers holding _diskIo access the disk inventory.
    private readonly Dictionary<string, DiskEntry> _disk = [];
    private readonly SortedSet<DiskEntry> _diskAge = new(Comparer<DiskEntry>.Create((a, b) =>
    {
        var order = a.Written.CompareTo(b.Written);
        return order != 0 ? order : StringComparer.Ordinal.Compare(a.File, b.File);
    }));
    private long _bytes, _diskBytes;
    private int _active, _generation, _diskTrimWorkers, _diskTrimGeneration = -1;
    private bool _privateMode, _diskIndexed, _disposed;

    public ThumbnailCache(RuntimePaths paths, bool privateMode = false, HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        _directory = Path.Combine(paths.Data, "thumbnails");
        _privateMode = privateMode;
        _http = httpClient ?? Http;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public void SetPrivateMode(bool privateMode)
    {
        lock (_sync)
        {
            if (_disposed || _privateMode == privateMode) return;
            _privateMode = privateMode;
            CancelAllLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelAllLocked();
            if (_active == 0 && _diskTrimWorkers == 0) ClearDiskInventory();
        }
    }

    private void CancelAllLocked()
    {
        _generation++;
        _memory.Clear();
        _lru.Clear();
        _bytes = 0;
        var requests = _inflight.Values.ToArray();
        _inflight.Clear();
        _pending.Clear();
        // Cancellation callbacks may finish a worker inline; remove the old queue first.
        foreach (var request in requests)
        {
            request.Pending = null;
            request.Cancellation.Cancel();
            request.Completion.TrySetResult(null);
            if (!request.Running) request.Cancellation.Dispose();
        }
    }

    public Task<BitmapSource?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<BitmapSource?>(cancellationToken);
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsPermittedUri(uri))
            return Task.FromResult<BitmapSource?>(null);
        Request request;
        lock (_sync)
        {
            if (_disposed) return Task.FromResult<BitmapSource?>(null);
            if (_memory.TryGetValue(url, out var cached))
            {
                _lru.Remove(cached.Node);
                _lru.AddLast(cached.Node);
                return Task.FromResult<BitmapSource?>(cached.Bitmap);
            }
            if (!_inflight.TryGetValue(url, out request!))
            {
                if (_pending.Count >= MaxPending) AbandonLocked(_pending.Last!.Value);
                request = new Request(url, _generation, _privateMode);
                _inflight.Add(url, request);
                request.Pending = _pending.AddFirst(request);
            }
            else if (request.Pending != null)
            {
                // A newly visible consumer gives shared queued work priority.
                _pending.Remove(request.Pending);
                _pending.AddFirst(request.Pending);
            }
            request.Consumers++;
            StartWorkersLocked();
        }
        return WaitForConsumerAsync(request, cancellationToken);
    }

    private static bool IsPermittedUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".ggpht.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage?> GetResponseAsync(string url, CancellationToken token)
    {
        var current = new Uri(url);
        for (var redirects = 0; ; redirects++)
        {
            var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var responseUri = response.RequestMessage?.RequestUri ?? current;
            if (!IsPermittedUri(responseUri)) { response.Dispose(); return null; }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            var permitted = location != null && Uri.TryCreate(responseUri, location, out var next) && IsPermittedUri(next);
            response.Dispose();
            if (!permitted || redirects >= MaxRedirects) return null;
            current = new Uri(responseUri, location!);
        }
    }

    private async Task<BitmapSource?> WaitForConsumerAsync(Request request, CancellationToken token)
    {
        try { return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                request.Consumers--;
                if (request.Consumers == 0 && IsCurrentLocked(request)) AbandonLocked(request);
            }
        }
    }

    private bool IsCurrentLocked(Request request) => request.Generation == _generation &&
        _inflight.TryGetValue(request.Url, out var current) && ReferenceEquals(current, request);

    private void AbandonLocked(Request request)
    {
        _inflight.Remove(request.Url);
        if (request.Pending != null) _pending.Remove(request.Pending);
        request.Pending = null;
        request.Cancellation.Cancel();
        request.Completion.TrySetResult(null);
        if (!request.Running) request.Cancellation.Dispose();
    }

    private void StartWorkersLocked()
    {
        if (_disposed) return;
        while (_active < MaxDownloads && _pending.First != null)
        {
            var request = _pending.First.Value;
            _pending.RemoveFirst();
            request.Pending = null;
            request.Running = true;
            _active++;
            _ = Task.Run(() => FetchAsync(request));
        }
    }

    private async Task FetchAsync(Request request)
    {
        BitmapSource? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token);
            timeout.CancelAfter(_requestTimeout);
            var token = timeout.Token;
            token.ThrowIfCancellationRequested();
            var file = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Url))) + ".img");
            var bytes = request.PrivateMode ? null : await ReadDiskAsync(request, file, token).ConfigureAwait(false);
            var downloaded = bytes == null;
            var bitmap = bytes == null ? null : Decode(bytes);
            if (bytes != null && bitmap == null)
            {
                // Only disk-derived failures invalidate the cache. The single
                // fetch below also shares the original request's deadline.
                await InvalidateDiskAsync(request, file, token).ConfigureAwait(false);
                bytes = null;
                downloaded = true;
            }
            if (bytes == null)
            {
                using var response = await GetResponseAsync(request.Url, token).ConfigureAwait(false);
                if (response == null) return;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxImageBytes) return;
                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                bytes = await ReadBoundedAsync(body, token).ConfigureAwait(false);
                if (bytes == null) return;
                bitmap = Decode(bytes);
            }
            token.ThrowIfCancellationRequested();
            if (bitmap == null) return;
            if (downloaded && !request.PrivateMode)
                await WriteDiskAsync(request, file, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!IsCurrentLocked(request)) return;
                var size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                var node = _lru.AddLast(request.Url);
                _memory[request.Url] = (bitmap, size, node);
                _bytes += size;
                while (_bytes > MemoryLimit && _lru.First != null)
                {
                    var old = _lru.First.Value;
                    _bytes -= _memory[old].Size;
                    _memory.Remove(old);
                    _lru.RemoveFirst();
                }
                result = bitmap;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or
            UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException or
            ArgumentException or System.Runtime.InteropServices.COMException) { }
        finally
        {
            lock (_sync)
            {
                if (IsCurrentLocked(request)) _inflight.Remove(request.Url);
                else result = null;
                request.Completion.TrySetResult(result);
                request.Cancellation.Dispose();
                _active--;
                if (_disposed && _active == 0 && _diskTrimWorkers == 0) ClearDiskInventory();
                StartWorkersLocked();
            }
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Query dimensions without materializing pixels, then cap the
                // longer side. A fixed width can expand a tiny tall image hugely.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                    frame.PixelWidth > MaxSourceDimension || frame.PixelHeight > MaxSourceDimension ||
                    (long)frame.PixelWidth * frame.PixelHeight > MaxSourcePixels) return null;
                if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = Math.Min(MaxDecodeDimension, frame.PixelWidth);
                else bitmap.DecodePixelHeight = Math.Min(MaxDecodeDimension, frame.PixelHeight);
                stream.Position = 0;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException or
            ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }

    private async Task InvalidateDiskAsync(Request request, string file, CancellationToken token)
    {
        await _diskIo.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(request) || _privateMode) return;
                FileBoundary.EnsureRegularPath(_directory);
                File.Delete(file);
                if (_disk.TryGetValue(file, out var entry)) RemoveDiskEntry(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _diskIo.Release(); }
    }

    private async Task<byte[]?> ReadDiskAsync(Request request, string file, CancellationToken token)
    {
        await _diskIo.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeDisk(request, token);
            token.ThrowIfCancellationRequested();
            lock (_sync) { if (!IsCurrentLocked(request) || _privateMode) return null; }
            if (!_disk.TryGetValue(file, out var entry) || entry.Size > MaxImageBytes) return null;
            try
            {
                FileBoundary.EnsureRegularPath(file);
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaxImageBytes) return null;
                return await ReadBoundedAsync(stream, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { RemoveDiskEntry(entry); return null; }
            catch (DirectoryNotFoundException) { RemoveDiskEntry(entry); return null; }
            // A sharing violation or transient read error does not mean that
            // the owned file stopped occupying disk space.
            catch (IOException) { return null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally { _diskIo.Release(); }
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + count > MaxImageBytes) return null;
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }

    private void InitializeDisk(Request request, CancellationToken token)
    {
        FileBoundary.EnsureRegularPath(_directory);
        if (_diskIndexed)
        {
            // A private-mode transition may have stopped an earlier batch.
            // Resume from the same inventory on the next public disk read.
            if (_diskBytes > DiskLimit || _disk.Count > MaxDiskEntries)
                ScheduleDiskTrim(request.Generation);
            return;
        }
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentLocked(request) || _privateMode) return;
            Directory.CreateDirectory(_directory);
            FileBoundary.EnsureRegularPath(_directory);
        }
        ClearDiskInventory();
        // One inventory, off the dispatcher; later writes use accounted sizes and ages.
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.img"))
        {
            token.ThrowIfCancellationRequested();
            if (!IsOwnedCacheFile(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            AddDiskEntry(new DiskEntry(file.FullName, file.Length, file.LastWriteTimeUtc));
        }
        _diskIndexed = true;
        if (!TrimDisk(request, token))
            ScheduleDiskTrim(request.Generation);
    }

    private void ScheduleDiskTrim(int generation)
    {
        lock (_sync)
        {
            if (!CanTrimDiskLocked(generation) || _diskTrimGeneration == generation) return;
            _diskTrimGeneration = generation;
            _diskTrimWorkers++;
            _ = Task.Run(() => ContinueDiskTrimAsync(generation));
        }
    }

    private bool CanTrimDiskLocked(int generation) => !_disposed && !_privateMode && generation == _generation;

    private async Task ContinueDiskTrimAsync(int generation)
    {
        DiskEntry? previous = null;
        try
        {
            while (true)
            {
                // Continue the original inventory; never rescan the directory.
                // Release disk ownership and yield between bounded batches so
                // reads/writes and mode transitions can make progress too.
                await Task.Yield();
                await _diskIo.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (_sync) { if (!CanTrimDiskLocked(generation)) return; }
                    IEnumerable<DiskEntry> candidates = _diskAge;
                    if (previous != null)
                    {
                        if (_diskAge.Max is not { } newest || _diskAge.Comparer.Compare(previous, newest) >= 0) return;
                        candidates = _diskAge.GetViewBetween(previous, newest)
                            .SkipWhile(candidate => _diskAge.Comparer.Compare(candidate, previous) <= 0);
                    }
                    var batch = candidates.Take(MaxDiskTrimCandidates).ToArray();
                    if (batch.Length == 0) return;
                    // Advance past failed deletions as well. One pass can reach
                    // later unlocked files, and stops even if every file is locked.
                    previous = batch[^1];
                    if (TrimDiskBatch(generation, null, CancellationToken.None, candidates: batch)) return;
                }
                finally { _diskIo.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            lock (_sync)
            {
                _diskTrimWorkers--;
                if (_diskTrimGeneration == generation) _diskTrimGeneration = -1;
                if (_disposed && _active == 0 && _diskTrimWorkers == 0) ClearDiskInventory();
            }
        }
    }

    private async Task WriteDiskAsync(Request request, string file, byte[] bytes, CancellationToken token)
    {
        await _diskIo.WaitAsync(token).ConfigureAwait(false);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            InitializeDisk(request, token);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(request) || _privateMode) return;
            }
            // Reserve before creating any new disk file. A cache that cannot
            // free its budget still serves the decoded image from memory.
            if (!TrimDisk(request, token, bytes.LongLength, file)) return;
            FileBoundary.EnsureRegularPath(temporary);
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(request) || _privateMode) return;
                FileBoundary.EnsureRegularPath(file);
                // The transition and final rename share this lock: old work cannot commit afterwards.
                File.Move(temporary, file, true);
                AddDiskEntry(new DiskEntry(file, bytes.LongLength, DateTime.UtcNow));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { FileBoundary.EnsureRegularPath(_directory); File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _diskIo.Release();
        }
    }

    private static bool IsOwnedCacheFile(string name) => name.Length == 68 &&
        name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && name.AsSpan(0, 64).IndexOfAnyExcept(
            "0123456789ABCDEFabcdef".AsSpan()) < 0;

    private void AddDiskEntry(DiskEntry entry)
    {
        if (_disk.TryGetValue(entry.File, out var old)) RemoveDiskEntry(old);
        _disk.Add(entry.File, entry);
        _diskAge.Add(entry);
        _diskBytes += entry.Size;
    }

    private void ClearDiskInventory()
    {
        _disk.Clear();
        _diskAge.Clear();
        _diskBytes = 0;
    }

    private void RemoveDiskEntry(DiskEntry entry)
    {
        _disk.Remove(entry.File);
        _diskAge.Remove(entry);
        _diskBytes -= entry.Size;
    }

    private bool TrimDisk(Request request, CancellationToken token, long incomingBytes = 0, string? incomingFile = null)
        => TrimDiskBatch(request.Generation, request, token, incomingBytes, incomingFile);

    private bool TrimDiskBatch(int generation, Request? request, CancellationToken token,
        long incomingBytes = 0, string? incomingFile = null, DiskEntry[]? candidates = null)
    {
        bool HasRoom()
        {
            var replacing = incomingFile is not null && _disk.TryGetValue(incomingFile, out var existing)
                ? existing : null;
            var count = _disk.Count + (incomingFile is not null && replacing is null ? 1 : 0);
            return _diskBytes - (replacing?.Size ?? 0) + incomingBytes <= DiskLimit && count <= MaxDiskEntries;
        }

        if (HasRoom()) return true;
        // A locked oldest file must not block other eligible candidates. Bound
        // the work per reservation and leave failed deletions fully accounted.
        foreach (var candidate in candidates ?? _diskAge.Take(MaxDiskTrimCandidates).ToArray())
        {
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!CanTrimDiskLocked(generation) || (request != null && !IsCurrentLocked(request))) return false;
                // Preserve an existing replacement target until rename succeeds.
                if (candidate.File == incomingFile) continue;
                FileBoundary.EnsureRegularPath(_directory);
                try
                {
                    FileBoundary.EnsureRegularPath(candidate.File);
                    File.Delete(candidate.File);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                RemoveDiskEntry(candidate);
            }
            if (HasRoom()) return true;
        }
        return HasRoom();
    }

    private sealed record DiskEntry(string File, long Size, DateTime Written);
    private sealed class Request(string url, int generation, bool privateMode)
    {
        public string Url { get; } = url;
        public int Generation { get; } = generation;
        public bool PrivateMode { get; } = privateMode;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<BitmapSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Request>? Pending;
        public int Consumers;
        public bool Running;
    }
}
