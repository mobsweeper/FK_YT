using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FkYouTube;
using FkYouTube.Services;

internal static class ThumbnailRegression
{
    private static readonly byte[] ImageBytes = CreateBitmap();

    public static async Task RunAsync()
    {
        await RunReviewAsync();
        await StalledBodyTimesOut();
        await SharedConsumersCancelIndependently();
        await ObsoleteQueueIsReleased();
        await PrivateModeUsesOnlyMemory();
        await TransitionRejectsLateCompletions();
        await TransitionCancelsQueuedWork();
        await DisposalCancelsWork();
    }

    public static async Task RunReviewAsync()
    {
        await CorruptDiskCacheIsFetchedOnce();
        await InvalidReplacementIsNotRetried();
        await CorruptDiskRecoverySharesDeadline();
        await DiskBudgetReviewAsync();
    }

    private static async Task DiskBudgetReviewAsync()
    {
        var failures = new List<Exception>();
        await Case("locked-oldest-alternative", LockedOldestAllowsAlternativeEviction);
        await Case("locked-budget-memory-only", LockedBudgetKeepsNewImagesInMemory);
        await Case("locked-read-stays-accounted", LockedReadRetainsDiskAccounting);
        await Case("bounded-entry-reservation", EntryReservationStopsAtBoundedLockedCandidates);
        await Case("startup-over-budget-cache-hits-converge", StartupEntryOverflowConvergesWithCacheHits);
        await Case("startup-over-budget-locked-prefix-converges", StartupEntryOverflowSkipsLockedPrefix);
        await Case("over-budget-locked-maintenance-stops", LockedOverflowMaintenanceStops);
        await Case("over-budget-maintenance-respects-mode-generation", MaintenanceRejectsObsoleteModeGeneration);
        if (failures.Count != 0) throw new AggregateException(failures);

        async Task Case(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("THUMBNAIL-BUDGET passed=" + name); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
        }
    }

    private static async Task LockedOldestAllowsAlternativeEviction()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var oldest = CacheFile(fixture, Url("budget-oldest"));
        var alternative = CacheFile(fixture, Url("budget-alternative"));
        File.WriteAllBytes(oldest, ImageBytes); File.WriteAllBytes(alternative, ImageBytes);
        var unrelated = Path.Combine(fixture.Thumbnails, "keep-unowned.img");
        File.WriteAllText(unrelated, "unrelated fixture file");
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        // Real files are 58 bytes. Only the isolated inventory size is synthetic:
        // this tests the 512 MiB arithmetic without allocating a 512 MiB file.
        SeedDiskInventory(cache, [(oldest, DiskLimit - ImageBytes.Length), (alternative, ImageBytes.LongLength)]);
        using var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var url = Url("budget-new");
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null,
            "a locked oldest thumbnail must not prevent memory service");
        await Idle(cache);
        Check(!File.Exists(alternative) && File.Exists(oldest) && File.Exists(CacheFile(fixture, url)) &&
              DiskBytes(cache) <= DiskLimit && File.ReadAllText(unrelated) == "unrelated fixture file",
            "a locked oldest file blocked eviction of a deletable owned alternative or exceeded the disk budget");
        Check(await cache.GetAsync(url) != null && handler.Requests == 1, "budget recovery did not preserve the memory cache");
        Console.WriteLine($"THUMBNAIL-BUDGET scope=real-delete-sharing-lock synthetic-inventory-bytes actual-owned-bytes={ImageBytes.Length * 2}");
    }

    private static async Task LockedBudgetKeepsNewImagesInMemory()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var oldest = CacheFile(fixture, Url("budget-unavailable")); File.WriteAllBytes(oldest, ImageBytes);
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        SeedDiskInventory(cache, [(oldest, DiskLimit)]);
        using var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var url = Url("budget-memory");
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null,
            "failure to reserve disk space discarded a valid decoded thumbnail");
        await Idle(cache);
        Check(!File.Exists(CacheFile(fixture, url)) && DiskBytes(cache) == DiskLimit && Count(cache, "_disk") == 1 &&
              !Directory.EnumerateFiles(fixture.Thumbnails, "*.tmp").Any(),
            "a new disk image was committed without budget or left a temporary file");
        Check(await cache.GetAsync(url) != null && handler.Requests == 1, "memory-only fallback redownloaded a cached thumbnail");
    }

    private static async Task LockedReadRetainsDiskAccounting()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var url = Url("budget-locked-read"); var locked = CacheFile(fixture, url);
        var anchor = CacheFile(fixture, Url("budget-read-anchor"));
        File.WriteAllBytes(locked, ImageBytes); File.WriteAllBytes(anchor, ImageBytes);
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        SeedDiskInventory(cache, [(locked, ImageBytes.LongLength), (anchor, DiskLimit - ImageBytes.Length)]);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null && handler.Requests == 1,
            "a sharing-locked cache read did not fall back to a bounded download and memory service");
        await Idle(cache);
        var inventory = (System.Collections.IDictionary)typeof(ThumbnailCache).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Check(inventory.Contains(locked) && Count(cache, "_disk") == 2 && DiskBytes(cache) == DiskLimit,
            "a transient IOException on an existing locked file removed it from disk accounting");
    }

    private static async Task EntryReservationStopsAtBoundedLockedCandidates()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        var entries = Enumerable.Range(0, 8192).Select(index =>
            (File: CacheFile(fixture, Url("entry-budget-" + index)), Size: ImageBytes.LongLength)).ToArray();
        var locks = new List<FileStream>();
        try
        {
            // Only the first 64 files exist (3712 bytes total). The remaining
            // inventory entries model an indexed cache at its entry limit.
            foreach (var entry in entries.Take(64))
            {
                File.WriteAllBytes(entry.File, ImageBytes);
                locks.Add(new FileStream(entry.File, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            SeedDiskInventory(cache, entries);
            var url = Url("entry-budget-new");
            Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null,
                "an unavailable entry reservation prevented memory thumbnail service");
            await Idle(cache);
            Check(!File.Exists(CacheFile(fixture, url)) && Count(cache, "_disk") == 8192 &&
                  DiskBytes(cache) == 8192L * ImageBytes.Length,
                "entry reservation committed over the limit or scanned beyond the 64-candidate eviction budget");
            Console.WriteLine($"THUMBNAIL-BUDGET scope=synthetic-8192-entry-inventory real-locked-files=64 actual-bytes={64 * ImageBytes.Length}");
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }

    private static async Task StartupEntryOverflowConvergesWithCacheHits()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        const int entryLimit = 8192, excess = 130;
        for (var index = 0; index < entryLimit + excess; index++)
            File.WriteAllBytes(CacheFile(fixture, Url("startup-overflow-" + index)), ImageBytes);
        var url = Url("startup-overflow-0");
        var anchor = CacheFile(fixture, url);
        File.SetLastWriteTimeUtc(anchor, DateTime.UtcNow.AddMinutes(1));
        var unrelated = Path.Combine(fixture.Thumbnails, "keep-unowned.img");
        File.WriteAllText(unrelated, "unrelated fixture file");
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(10)) != null,
            "an existing valid thumbnail was not served while indexing an over-budget cache");
        // The first 64-candidate batch cannot remove the 130 excess entries.
        // From here every request is a memory hit; no new download/write can
        // accidentally supply another reservation-driven trim.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (Count(cache, "_disk") > entryLimit && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            Check(await cache.GetAsync(url) != null, "startup maintenance discarded the cached image");
            await Task.Delay(10);
        }
        await Idle(cache);
        Check(Count(cache, "_disk") <= entryLimit &&
              Directory.EnumerateFiles(fixture.Thumbnails, "*.img").Count() == entryLimit + 1 &&
              File.Exists(anchor) && File.ReadAllText(unrelated) == "unrelated fixture file" && handler.Requests == 0,
            "an already oversized cache stopped after its first 64 deletions when only cache hits followed");
        Console.WriteLine($"THUMBNAIL-BUDGET scope=real-startup-entry-overflow excess={excess} actual-bytes={(entryLimit + excess) * ImageBytes.Length} network-requests={handler.Requests}");
    }

    private static async Task StartupEntryOverflowSkipsLockedPrefix()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        const int entryLimit = 8192, lockedCount = 64;
        var files = Enumerable.Range(0, entryLimit + 1)
            .Select(index => CacheFile(fixture, Url("startup-locked-prefix-" + index))).ToArray();
        var written = DateTime.UtcNow.AddDays(-1);
        for (var index = 0; index < files.Length; index++)
        {
            File.WriteAllBytes(files[index], ImageBytes);
            File.SetLastWriteTimeUtc(files[index], written.AddSeconds(index));
        }
        var locks = new List<FileStream>();
        try
        {
            foreach (var file in files.Take(lockedCount))
                locks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            var handler = new FixtureHandler(); handler.Release.TrySetResult();
            using var http = Client(handler);
            using var cache = new ThumbnailCache(fixture.Paths, false, http);
            var url = Url("startup-locked-prefix-" + entryLimit);
            Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(10)) != null,
                "a locked oldest batch prevented an existing thumbnail from being served");
            await Idle(cache);
            Check(Count(cache, "_disk") == entryLimit &&
                  Directory.EnumerateFiles(fixture.Thumbnails, "*.img").Count() == entryLimit &&
                  files.Take(lockedCount).All(File.Exists) && !File.Exists(files[lockedCount]) &&
                  File.Exists(files[^1]) && handler.Requests == 0,
                "startup maintenance stopped at 64 locked oldest files and never considered a deletable later entry");
            Console.WriteLine($"THUMBNAIL-BUDGET scope=real-startup-locked-prefix owned-files={files.Length} locked-files={lockedCount} network-requests={handler.Requests}");
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }

    private static async Task LockedOverflowMaintenanceStops()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var url = Url("maintenance-locked");
        var file = CacheFile(fixture, url);
        var files = new[] { file }.Concat(Enumerable.Range(1, 64)
            .Select(index => CacheFile(fixture, Url("maintenance-locked-" + index)))).ToArray();
        foreach (var owned in files) File.WriteAllBytes(owned, ImageBytes);
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        SeedDiskInventory(cache, files.Select((owned, index) =>
            (owned, index == 0 ? DiskLimit + 1 - 64 * ImageBytes.Length : ImageBytes.LongLength)));
        var locks = new List<FileStream>();
        try
        {
            foreach (var owned in files)
                locks.Add(new FileStream(owned, FileMode.Open, FileAccess.Read, FileShare.None));
            Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null,
                "a locked over-budget cache prevented memory thumbnail service");
            await Idle(cache);
            Check(files.All(File.Exists) && Count(cache, "_disk") == files.Length && DiskBytes(cache) == DiskLimit + 1 &&
                  DiskTrimWorkers(cache) == 0 && !Directory.EnumerateFiles(fixture.Thumbnails, "*.tmp").Any(),
                "maintenance failed to stop after a locked-only pass, lost accounting, or leaked a replacement temporary file");
            Check(await cache.GetAsync(url) != null && handler.Requests == 1,
                "a locked-only maintenance stop lost the successfully decoded memory thumbnail");
            Console.WriteLine($"THUMBNAIL-BUDGET scope=synthetic-over-budget-bytes real-locked-files={files.Length} background-workers={DiskTrimWorkers(cache)}");
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }

    private static async Task MaintenanceRejectsObsoleteModeGeneration()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var oldest = CacheFile(fixture, Url("maintenance-transition-oldest"));
        var url = Url("maintenance-transition-anchor");
        var anchor = CacheFile(fixture, url);
        File.WriteAllBytes(oldest, ImageBytes); File.WriteAllBytes(anchor, ImageBytes);
        var handler = new FixtureHandler(); handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        SeedDiskInventory(cache, [(oldest, DiskLimit), (anchor, 1)]);
        var diskIo = (SemaphoreSlim)typeof(ThumbnailCache)
            .GetField("_diskIo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        await diskIo.WaitAsync();
        try
        {
            typeof(ThumbnailCache).GetMethod("ScheduleDiskTrim", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cache, [0]);
            Check(DiskTrimWorkers(cache) == 1, "over-budget maintenance was not scheduled");
            cache.SetPrivateMode(true);
        }
        finally { diskIo.Release(); }
        await Idle(cache);
        Check(File.Exists(oldest) && File.Exists(anchor) && DiskBytes(cache) == DiskLimit + 1 &&
              Count(cache, "_disk") == 2 && handler.Requests == 0,
            "queued public maintenance deleted disk cache entries after entering private mode");
        cache.SetPrivateMode(false);
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) != null,
            "returning to public mode did not resume cache service");
        await Idle(cache);
        Check(!File.Exists(oldest) && File.Exists(anchor) && Count(cache, "_disk") == 1 &&
              DiskBytes(cache) == 1 && handler.Requests == 0,
            "a canceled maintenance generation blocked fresh public cleanup or removed the served anchor");
    }

    private static readonly long DiskLimit = (long)typeof(ThumbnailCache).GetField("DiskLimit", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
    private static long DiskBytes(ThumbnailCache cache) => (long)typeof(ThumbnailCache).GetField("_diskBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    private static void SeedDiskInventory(ThumbnailCache cache, IEnumerable<(string File, long Size)> entries)
    {
        // Called before starting workers; the fixture exclusively owns this cache.
        var type = typeof(ThumbnailCache).GetNestedType("DiskEntry", BindingFlags.NonPublic)!;
        var add = typeof(ThumbnailCache).GetMethod("AddDiskEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var age = DateTime.UtcNow.AddDays(-1);
        foreach (var entry in entries)
        {
            var value = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [entry.File, entry.Size, age], null)!;
            add.Invoke(cache, [value]); age = age.AddTicks(1);
        }
        typeof(ThumbnailCache).GetField("_diskIndexed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, true);
    }

    private static async Task CorruptDiskCacheIsFetchedOnce()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var url = Url("corrupt-disk");
        var file = Path.Combine(fixture.Thumbnails, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        File.WriteAllText(file, "synthetic damaged image");
        var handler = new FixtureHandler();
        handler.Release.TrySetResult();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        var image = await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3));
        Check(image != null && handler.Requests == 1, "corrupt disk thumbnail must be replaced by one bounded download");
        await Idle(cache);
        Check(File.ReadAllBytes(file).SequenceEqual(ImageBytes), "recovered thumbnail must replace the corrupt owned cache file");
        Check(await cache.GetAsync(url) != null && handler.Requests == 1, "recovered thumbnail must be served from memory");
    }

    private static async Task InvalidReplacementIsNotRetried()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var url = Url("corrupt-replacement");
        var file = Path.Combine(fixture.Thumbnails, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        File.WriteAllText(file, "synthetic damaged cache");
        var handler = new InvalidImageHandler();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http);
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) == null && handler.Requests == 1,
            "an invalid replacement must not trigger repeated network attempts");
        await Idle(cache);
        Check(!File.Exists(file), "a corrupt disk entry must be invalidated even if its replacement is invalid");
    }

    private sealed class InvalidImageHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }

    private static async Task CorruptDiskRecoverySharesDeadline()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Thumbnails);
        var url = Url("corrupt-recovery-timeout");
        var file = Path.Combine(fixture.Thumbnails, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        File.WriteAllText(file, "synthetic damaged cache");
        var handler = new FixtureHandler();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, false, http, TimeSpan.FromMilliseconds(100));
        Check(await cache.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(3)) == null && handler.Requests == 1 && handler.CanceledReads == 1,
            "corrupt disk recovery exceeded the original request timeout or its one-download budget");
        await Idle(cache);
        Check(!File.Exists(file), "timed-out recovery restored a corrupt disk entry");
    }

    private static async Task StalledBodyTimesOut()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler();
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, true, http, TimeSpan.FromMilliseconds(100));
        var requests = Enumerable.Range(0, 5).Select(i => cache.GetAsync(Url("timeout-" + i))).ToArray();
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(4));
        Check(results.All(image => image == null), "stalled body must finish without an image");
        Check(handler.Requests == 5 && handler.CanceledReads == 5, "body timeout must free all four download slots");
        await Idle(cache);
    }

    private static async Task SharedConsumersCancelIndependently()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler();
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, true, http);
        using var offscreen = new CancellationTokenSource();
        var first = cache.GetAsync(Url("shared"), offscreen.Token);
        var second = cache.GetAsync(Url("shared"));
        await Until(() => handler.Reads == 1);
        offscreen.Cancel();
        await ExpectCanceled(first);
        Check(!second.IsCompleted && handler.CanceledReads == 0, "one consumer must not cancel shared download");
        handler.Release.TrySetResult();
        Check(await second.WaitAsync(TimeSpan.FromSeconds(3)) != null, "remaining visible consumer must receive image");
        Check(handler.Requests == 1, "concurrent identical URLs must share one download");
        await Idle(cache);
    }

    private static async Task ObsoleteQueueIsReleased()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler { ImmediateVisible = true };
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, true, http);
        var blockers = Enumerable.Range(0, 4).Select(_ => new CancellationTokenSource()).ToArray();
        var active = blockers.Select((source, i) => cache.GetAsync(Url("blocker-" + i), source.Token)).ToArray();
        await Until(() => handler.Reads == 4);
        using var obsolete = new CancellationTokenSource();
        var queued = Enumerable.Range(0, 400).Select(i => cache.GetAsync(Url("obsolete-" + i), obsolete.Token)).ToArray();
        Check(Count(cache, "_pending") <= 128 && Count(cache, "_inflight") <= 132, "pending work must stay bounded");
        obsolete.Cancel();
        await Task.WhenAll(queued.Select(IgnoreCanceled)).WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => Count(cache, "_pending") == 0 && Count(cache, "_inflight") == 4);
        var visible = cache.GetAsync(Url("visible"));
        blockers[0].Cancel();
        Check(await visible.WaitAsync(TimeSpan.FromSeconds(3)) != null, "new visible work must use released slot immediately");
        Check(handler.Requests == 5, "obsolete queued requests must never reach the network");
        foreach (var source in blockers) source.Cancel();
        await Task.WhenAll(active.Select(IgnoreCanceled)).WaitAsync(TimeSpan.FromSeconds(3));
        await Idle(cache);
        foreach (var source in blockers) source.Dispose();
    }

    private static async Task PrivateModeUsesOnlyMemory()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler();
        handler.Release.TrySetResult();
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, true, http);
        var first = await cache.GetAsync(Url("a"));
        Check(first != null && ReferenceEquals(first, await cache.GetAsync(Url("a"))), "private thumbnails may use memory cache");
        Check(!Directory.Exists(fixture.Thumbnails), "private initialization/download must not create thumbnail directory");
        cache.SetPrivateMode(false);
        Check(Count(cache, "_memory") == 0, "leaving private mode must clear private memory");
        Check(await cache.GetAsync(Url("a")) != null && handler.Requests == 2, "private image must be downloaded again in public mode");
        var publicFile = CacheFile(fixture, Url("a"));
        var written = File.GetLastWriteTimeUtc(publicFile);
        cache.SetPrivateMode(true);
        Check(await cache.GetAsync(Url("a")) != null && handler.Requests == 3, "private mode must bypass an existing disk cache image");
        Check(File.GetLastWriteTimeUtc(publicFile) == written, "private mode must not overwrite public disk cache");
        Check(await cache.GetAsync(Url("b")) != null && !File.Exists(CacheFile(fixture, Url("b"))), "private image must remain memory-only");
        cache.SetPrivateMode(false);
        Check(Count(cache, "_memory") == 0, "both mode transitions must clear memory");
        Check(await cache.GetAsync(Url("b")) != null && handler.Requests == 5, "leaving private mode must not reuse private image");
        await Idle(cache);
    }

    private static async Task TransitionRejectsLateCompletions()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler { IgnoreCancellation = true };
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, false, http);
        var oldPublic = cache.GetAsync(Url("late-public"));
        await Until(() => handler.Reads == 1);
        cache.SetPrivateMode(true);
        Check(await oldPublic.WaitAsync(TimeSpan.FromSeconds(1)) == null, "transition must release existing public consumers");
        handler.Release.TrySetResult();
        await Idle(cache);
        Check(!File.Exists(CacheFile(fixture, Url("late-public"))) && Count(cache, "_memory") == 0,
            "late public result must not commit disk or memory after transition");
        Check(!Directory.EnumerateFiles(fixture.Thumbnails, "*.tmp").Any(), "canceled writes must not leave temporary images");
        handler.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPrivate = cache.GetAsync(Url("late-private"));
        await Until(() => handler.Reads == 2);
        cache.SetPrivateMode(false);
        Check(await oldPrivate.WaitAsync(TimeSpan.FromSeconds(1)) == null, "transition must release existing private consumers");
        handler.Release.TrySetResult();
        await Idle(cache);
        Check(!File.Exists(CacheFile(fixture, Url("late-private"))) && Count(cache, "_memory") == 0,
            "late private result must not commit after leaving private mode");
    }

    private static async Task TransitionCancelsQueuedWork()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler();
        using var http = Client(handler);
        using var cache = new ThumbnailCache(fixture.Paths, true, http);
        var requests = Enumerable.Range(0, 20).Select(i => cache.GetAsync(Url("transition-" + i))).ToArray();
        await Until(() => handler.Reads == 4);
        var startedRequests = handler.Requests;
        cache.SetPrivateMode(false);
        Check((await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2))).All(image => image == null),
            "mode transition must release active and queued consumers");
        await Idle(cache);
        Check(handler.Requests == startedRequests && !Directory.Exists(fixture.Thumbnails),
            "mode transition must not start old queued private work or write its images");
        handler.Release.TrySetResult();
        Check(await cache.GetAsync(Url("after-transition")) != null, "new requests must run after a transition");
        await Idle(cache);
    }

    private static async Task DisposalCancelsWork()
    {
        using var fixture = new Fixture();
        var handler = new FixtureHandler();
        using var http = Client(handler);
        var cache = new ThumbnailCache(fixture.Paths, true, http);
        var requests = Enumerable.Range(0, 20).Select(i => cache.GetAsync(Url("dispose-" + i))).ToArray();
        await Until(() => handler.Reads == 4);
        var startedRequests = handler.Requests;
        cache.Dispose();
        cache.Dispose();
        Check((await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2))).All(image => image == null),
            "disposal must release active and queued consumers");
        await Idle(cache);
        Check(handler.Requests == startedRequests, "disposal must not start old queued requests during cancellation callbacks");
        Check(Count(cache, "_pending") == 0 && Count(cache, "_memory") == 0, "disposal must clear queued work and memory");
        var completedRequests = handler.Requests;
        Check(await cache.GetAsync(Url("after-dispose")) == null && handler.Requests == completedRequests,
            "disposed cache must not start new requests");
        handler.Release.TrySetResult();
        using var response = await http.GetAsync(Url("injected-client-still-owned-by-caller"));
        Check(response.IsSuccessStatusCode, "cache must not dispose the caller's injected HTTP client");
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    private static string Url(string name) => "https://i.ytimg.com/thumbnail-fixture-" + name + ".bmp";
    private static string CacheFile(Fixture fixture, string url) => Path.Combine(fixture.Thumbnails,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
    private static int Count(ThumbnailCache cache, string field)
    {
        var value = typeof(ThumbnailCache).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }
    private static int Active(ThumbnailCache cache) => (int)typeof(ThumbnailCache)
        .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    private static int DiskTrimWorkers(ThumbnailCache cache) => (int)typeof(ThumbnailCache)
        .GetField("_diskTrimWorkers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    private static Task Idle(ThumbnailCache cache) => Until(() => Active(cache) == 0 &&
        DiskTrimWorkers(cache) == 0 && Count(cache, "_inflight") == 0);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("consumer cancellation must cancel its wait");
    }
    private static async Task IgnoreCanceled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] CreateBitmap()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        bytes[54] = 80; bytes[55] = 120; bytes[56] = 200;
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Parent = Path.Combine(Path.GetTempPath(), "FK_YT-thumbnail-tests");
        private readonly string _root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public RuntimePaths Paths { get; }
        public string Thumbnails => Path.Combine(Paths.Data, "thumbnails");
        public Fixture() => Paths = new RuntimePaths(_root);
        public void Dispose()
        {
            if (!Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("thumbnail fixture escaped its temporary parent");
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests, Reads, CanceledReads;
        public bool IgnoreCancellation, ImmediateVisible;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            var immediate = ImmediateVisible && request.RequestUri!.AbsolutePath.Contains("visible", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FixtureBody(this, immediate))
            });
        }
    }

    private sealed class FixtureBody(FixtureHandler owner, bool immediate) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position == 0)
            {
                Interlocked.Increment(ref owner.Reads);
                if (!immediate)
                {
                    try { await owner.Release.Task.WaitAsync(owner.IgnoreCancellation ? CancellationToken.None : token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref owner.CanceledReads); throw; }
                }
            }
            var count = Math.Min(buffer.Length, ImageBytes.Length - _position);
            ImageBytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
