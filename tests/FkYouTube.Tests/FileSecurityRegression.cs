using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using FkYouTube;
using FkYouTube.Services;

internal static class FileSecurityRegression
{
    public static async Task RunAsync(string workspace)
    {
        var parent = Path.GetFullPath(Path.Combine(workspace, "artifacts", "file-security-regression"));
        var root = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<Exception>();
        try
        {
            async Task Run(string name, Func<Task> check)
            {
                try { await check(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
            }
            await Run("storage review regressions", () => RunStorageReviewAsync(workspace));
            await Run("thumbnail unsafe authorities", () => UnsafeAuthoritiesAsync(root));
            await Run("thumbnail redirected response authority", () => FinalAuthorityAsync(root));
            await Run("thumbnail permitted redirect and blocked redirect", () => RedirectsAsync(root));
            await Run("thumbnail tall image allocation", () => TallImageAsync(root));
            await Run("thumbnail source image dimensions", () => SourceImageBoundsAsync(root));
            await Run("thumbnail response byte bounds", () => ResponseBoundsAsync(root));
            await Run("thumbnail disk junction preserves external folder", () => ThumbnailJunctionAsync(root));
            await Run("thumbnail eviction preserves unrelated image files", () => UnrelatedThumbnailAsync(root));
            await Run("session startup preserves unrelated cookie files", () => UnrelatedCookieCleanupAsync(root));
            await Run("session temp junction is rejected before ACL or cleanup", () => SessionJunctionAsync(root));
            await Run("channel disconnect junction preserves external sessions", () => ChannelDisconnectJunctionAsync(root));
            await Run("channel disconnect removes only owned SHA256 files", () => ChannelOwnedCleanupAsync(root));
            await Run("channel session read and write reject a junction", () => ChannelReadWriteJunctionAsync(root, workspace));
            await Run("download staging source junction is rejected", () => DownloadSourceJunctionAsync(root));
            await Run("malformed channel responses fail as safe data errors", () => MalformedChannelResponsesAsync(root, workspace));
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "fixture cleanup escaped its owned root");
            Directory.Delete(root, true);
        }
    }

    public static async Task RunStorageReviewAsync(string workspace)
    {
        var parent = Path.GetFullPath(Path.Combine(workspace, "artifacts", "storage-review-regression"));
        var root = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<Exception>();
        try
        {
            async Task Run(string name, Func<Task> check)
            {
                try { await check(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
            }
            await Run("settings null fields retain normal preferences", () => SettingsNullFieldsAsync(root));
            await Run("history corruption preserves valid rows and original storage", () => HistoryCorruptionAsync(root));
            await Run("batch history status isolates accounts and preserves positions", () => HistoryBatchStatusAsync(root));
            await Run("long durations display total hours", () => LongDurationsAsync());
            await Run("thumbnail production handler has no cookies", () => ThumbnailCookiesAsync());
            await Run("feed detached lease retries and preserves replacement worker", () => FeedLeaseCleanupAsync(root, workspace));
            await Run("feed startup failure cleans transferred lease", () => FeedStartupFailureAsync(root));
            await Run("feed deletion exhaustion is bounded and diagnosed", () => FeedDeletionFailureAsync(root));
            await Run("feed canceled gate retains lease cleanup ownership", () => FeedCanceledGateAsync(root));
            await Run("feed output drain failure still deletes its lease", () => FeedOutputFailureAsync(root));
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "storage review fixture escaped its owned parent");
            Directory.Delete(root, true);
        }
    }

    private static Task SettingsNullFieldsAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "settings"));
        var store = new DataStore(paths);
        File.WriteAllText(Path.Combine(paths.Data, "settings.json"), """
            {"authMode":null,"firefoxProfile":null,"accountKey":null,"selectedChannelId":null,
             "channelHistoryKeys":null,"playlistUrl":null,"maxHeight":720,"qualityPreferenceVersion":1,"syncHistory":false,
             "hideMembersOnly":false,"autoPlayNext":false,"incognito":true,"volume":23,"speed":1.5}
            """);
        var settings = store.LoadSettings();
        Check(settings.AuthMode == "guest" && settings.FirefoxProfile == "" && settings.AccountKey == "guest" &&
            settings.SelectedChannelId == "" && settings.ChannelHistoryKeys is { Count: 0 } && settings.PlaylistUrl == "",
            "explicit null settings reached startup and channel state");
        Check(settings.MaxHeight == 720 && !settings.SyncHistory && !settings.HideMembersOnly && !settings.AutoPlayNext &&
            settings.Incognito && settings.Volume == 23 && settings.Speed == 1.5, "normal settings were reset during null recovery");
        File.WriteAllText(Path.Combine(paths.Data, "settings.json"), """
            {"authMode":"Firefox","firefoxProfile":"synthetic-profile","accountKey":"preserved-account",
             "selectedChannelId":"","channelHistoryKeys":{"valid-channel":"preserved-history-key","invalid-channel":null},"playlistUrl":"synthetic-playlist"}
            """);
        settings = store.LoadSettings();
        Check(settings.AuthMode == "Firefox" && settings.FirefoxProfile == "synthetic-profile" && settings.AccountKey == "preserved-account" &&
            settings.PlaylistUrl == "synthetic-playlist" && settings.ChannelHistoryKeys.Count == 1 &&
            settings.ChannelHistoryKeys["valid-channel"] == "preserved-history-key", "null dictionary entry recovery replaced normal settings or account history mappings");
        return Task.CompletedTask;
    }

    private static Task HistoryCorruptionAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "history"));
        var store = new DataStore(paths);
        var valid = new VideoItem { Id = "fixture0001", Title = "valid synthetic history", Url = "https://www.youtube.com/watch?v=fixture0001" };
        store.SavePosition("fixture-account", valid, 25, 100);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Data, "viewer.sqlite") }.ToString());
        db.Open();
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO history(account,video_id,metadata,position,duration,updated,sync_status) VALUES
            ('fixture-account','badjson','{',10,100,'2099-01-01T00:00:00+00:00','local'),
            ('fixture-account','baddate',$valid,10,100,'2098-corrupt-date','local'),
            ('fixture-account','nulljson','null',10,100,'2097-01-01T00:00:00+00:00','local'),
            ('fixture-account','badshape','{"id":null}',10,100,'2096-01-01T00:00:00+00:00','local');
            """;
        insert.Parameters.AddWithValue("$valid", JsonSerializer.Serialize(valid with { Id = "baddate" }, JsonDefaults.Options));
        insert.ExecuteNonQuery();
        var history = store.GetHistory("fixture-account", out var skipped);
        Check(history.Count == 1 && history[0].Video.Id == valid.Id && history[0].Position == 25,
            "a corrupt history row suppressed valid history or reached the UI");
        Check(skipped == 4, "history query did not report each skipped corrupt row");
        using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM history";
        Check((long)count.ExecuteScalar()! == 5, "history recovery deleted original evidence rows");
        return Task.CompletedTask;
    }

    private static Task HistoryBatchStatusAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "history-batch"));
        var store = new DataStore(paths);
        var first = new VideoItem { Id = "fixture0001" };
        var second = new VideoItem { Id = "fixture0002" };
        store.SavePosition("first-account", first, 25, 100); store.SavePosition("first-account", second, 30, 200);
        store.SavePosition("other-account", first, 40, 300);
        var before = store.GetHistory("first-account");
        store.SetSyncStatuses("first-account", [first.Id, second.Id, first.Id, "absent-id"], "synced");
        var after = store.GetHistory("first-account");
        Check(after.Count == 2 && after.All(entry => entry.SyncStatus == "synced") &&
            after.Zip(before).All(pair => pair.First.Position == pair.Second.Position && pair.First.Duration == pair.Second.Duration && pair.First.UpdatedAt == pair.Second.UpdatedAt),
            "batch status update changed progress, recency or an unrelated row");
        Check(store.GetHistory("other-account")[0].SyncStatus == "local", "batch status update crossed its account boundary");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Data, "viewer.sqlite") }.ToString());
        db.Open(); using var plan = db.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT metadata FROM history WHERE account='first-account' ORDER BY updated DESC LIMIT 500";
        using var reader = plan.ExecuteReader(); var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        Check(details.Any(detail => detail.Contains("history_account_updated", StringComparison.Ordinal)) &&
            details.All(detail => !detail.Contains("TEMP B-TREE", StringComparison.Ordinal)), "history query still required a temporary recency sort");
        return Task.CompletedTask;
    }

    private static Task LongDurationsAsync()
    {
        Check(VideoItem.FormatTime(24 * 3600 + 65) == "24:01:05" && VideoItem.FormatTime(49 * 3600) == "49:00:00" &&
            VideoItem.FormatTime(3600) == "1:00:00", "long duration wrapped total hours at one day");
        return Task.CompletedTask;
    }

    private static Task ThumbnailCookiesAsync()
    {
        var http = (HttpClient)typeof(ThumbnailCache).GetField("Http", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var handler = (HttpClientHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(http)!;
        Check(!handler.UseCookies && !handler.AllowAutoRedirect, "production thumbnail client retained shared HTTP cookie state");
        return Task.CompletedTask;
    }

    private static async Task FeedLeaseCleanupAsync(string root, string workspace)
    {
        var paths = new RuntimePaths(Path.Combine(root, "feed-cleanup"));
        Directory.CreateDirectory(Path.Combine(paths.Root, "runtime"));
        var python = Path.Combine(paths.Root, "runtime", "python");
        await CreateJunctionAsync(python, Path.Combine(workspace, "runtime", "python"));
        Directory.CreateDirectory(Path.Combine(paths.Root, "worker"));
        Directory.CreateDirectory(Path.Combine(paths.Data, "temp"));
        File.WriteAllText(paths.FeedWorker, """
            import json,sys
            for line in sys.stdin:
                request=json.loads(line)
                print(json.dumps({'type':'done','requestId':request['requestId'],'generation':request['generation'],'hasMore':False}),flush=True)
            """);
        var first = Path.Combine(paths.Data, "temp", "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        var replacement = Path.Combine(paths.Data, "temp", "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(first, "synthetic old lease"); File.WriteAllText(replacement, "synthetic replacement lease");
        var feed = new FeedService(paths);
        FileStream? locked = null;
        try
        {
            await feed.ReadAsync(true, "synthetic", 1, new CookieLease(first), _ => { }, CancellationToken.None);
            locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
            var timer = Stopwatch.StartNew(); feed.Stop();
            Check(timer.Elapsed < TimeSpan.FromSeconds(1), "synchronous feed stop blocked while a cookie file was locked");
            await feed.ReadAsync(true, "synthetic", 2, new CookieLease(replacement), _ => { }, CancellationToken.None);
            await Task.Delay(75); locked.Dispose(); locked = null;
            // A new worker is still live while prior cleanup is completing.
            await feed.DrainPendingAsync();
            Check(!File.Exists(first) && File.Exists(replacement), "prior cleanup retained its old lease or deleted the replacement lease");
            await feed.ReadAsync(false, "synthetic", 2, null, _ => { }, CancellationToken.None);
            await feed.DisposeAsync();
            Check(!File.Exists(replacement), "async feed disposal returned before deleting its current lease");
        }
        finally
        {
            locked?.Dispose();
            await feed.DisposeAsync();
            Directory.Delete(python);
        }
    }

    private static async Task FeedStartupFailureAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "feed-startup-failure"));
        var cookie = Path.Combine(paths.Data, "synthetic-owned-lease.txt");
        File.WriteAllText(cookie, "synthetic startup failure lease");
        var feed = new FeedService(paths);
        FileStream? locked = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Exception? failure = null;
            try { await feed.ReadAsync(true, "synthetic", 1, new CookieLease(cookie), _ => { }, CancellationToken.None); }
            catch (Exception error) { failure = error; }
            Check(failure is FileNotFoundException, "startup failure fixture did not reach the missing runtime path");
            await Task.Delay(75); locked.Dispose(); locked = null;
            await feed.DisposeAsync();
            Check(!File.Exists(cookie), "feed startup failure lost ownership of its transferred cookie lease");
        }
        finally { locked?.Dispose(); await feed.DisposeAsync(); }
    }

    private static async Task FeedDeletionFailureAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "feed-delete-failure"));
        var cookie = Path.Combine(paths.Data, "synthetic-owned-lease.txt");
        File.WriteAllText(cookie, "synthetic permanently locked lease");
        var feed = new FeedService(paths);
        using var locked = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            try { await feed.ReadAsync(true, "synthetic", 1, new CookieLease(cookie), _ => { }, CancellationToken.None); }
            catch (FileNotFoundException) { }
            Exception? failure = null; var timer = Stopwatch.StartNew();
            try { await feed.DisposeAsync(); } catch (Exception error) { failure = error; }
            Check(failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(error => error is IOException) &&
                timer.Elapsed < TimeSpan.FromSeconds(2) && File.Exists(cookie), "failed cookie deletion was hidden, unbounded or lost its retained evidence");
            Check(!failure!.ToString().Contains(cookie, StringComparison.Ordinal), "cookie cleanup failure exposed its private file path");
        }
        finally { locked.Dispose(); await feed.DisposeAsync(); File.Delete(cookie); }
    }

    private static async Task FeedCanceledGateAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "feed-canceled-gate"));
        var cookie = Path.Combine(paths.Data, "synthetic-owned-lease.txt");
        File.WriteAllText(cookie, "synthetic canceled gate lease");
        var feed = new FeedService(paths);
        FileStream? locked = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Exception? failure = null;
            try { await feed.ReadAsync(true, "synthetic", 1, new CookieLease(cookie), _ => { }, cancellation.Token); }
            catch (Exception error) { failure = error; }
            Check(failure is OperationCanceledException, "canceled gate fixture did not reject before worker startup");
            await Task.Delay(75); locked.Dispose(); locked = null;
            await feed.DisposeAsync();
            Check(!File.Exists(cookie), "a lease canceled before entering the feed gate escaped bounded cleanup");
        }
        finally { locked?.Dispose(); await feed.DisposeAsync(); }
    }

    private static async Task FeedOutputFailureAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "feed-output-failure"));
        var cookie = Path.Combine(paths.Data, "synthetic-owned-lease.txt");
        File.WriteAllText(cookie, "synthetic output failure lease");
        var feed = new FeedService(paths);
        typeof(FeedService).GetField("_lease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(feed, new CookieLease(cookie));
        typeof(FeedService).GetField("_drain", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(feed,
            Task.FromException(new InvalidDataException("synthetic unexpected output drain failure")));
        try
        {
            Exception? failure = null;
            try { await feed.DisposeAsync(); } catch (Exception error) { failure = error; }
            Check(failure is AggregateException && !File.Exists(cookie), "output drain failure skipped lease cleanup or was silently treated as success");
        }
        finally { await feed.DisposeAsync(); }
    }

    private static async Task UnsafeAuthoritiesAsync(string root)
    {
        var handler = new Handler(_ => ImageResponse());
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        foreach (var url in new[] { "https://i.ytimg.com:8443/fixture.bmp", "https://fixture:secret@i.ytimg.com/fixture.bmp" })
            Check(await cache.GetAsync(url) == null, "unsafe thumbnail authority returned an image");
        Check(handler.Requests == 0, "unsafe thumbnail authority reached the HTTP handler");
    }

    private static async Task FinalAuthorityAsync(string root)
    {
        var handler = new Handler(_ =>
        {
            var response = ImageResponse();
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/private-fixture");
            return response;
        });
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        Check(await cache.GetAsync("https://i.ytimg.com/final-uri.bmp") == null,
            "a response redirected outside the thumbnail policy was decoded");
    }

    private static async Task RedirectsAsync(string root)
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/permitted.bmp" => Redirect("https://lh3.googleusercontent.com/final.bmp"),
            "/blocked.bmp" => Redirect("https://127.0.0.1/private-fixture"),
            "/loop.bmp" => Redirect("/loop.bmp"),
            _ => ImageResponse()
        });
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        Check(await cache.GetAsync("https://i.ytimg.com/permitted.bmp") != null,
            "a permitted CDN redirect lost its image");
        var before = handler.Requests;
        Check(await cache.GetAsync("https://i.ytimg.com/blocked.bmp") == null && handler.Requests == before + 1,
            "blocked redirect was requested before validating its target");
        before = handler.Requests;
        Check(await cache.GetAsync("https://i.ytimg.com/loop.bmp") == null && handler.Requests - before <= 5,
            "redirect loop exceeded its request budget");
    }

    private static async Task TallImageAsync(string root)
    {
        // The old 360-wide decoder expands this valid 310-byte bitmap to 360x23040.
        // It proves the aspect-ratio problem with a bounded 33-MiB decoded fixture.
        var handler = new Handler(_ => ImageResponse(CreateBitmap(1, 64)));
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        var image = await cache.GetAsync("https://i.ytimg.com/tall.bmp");
        Check(image != null && image.PixelWidth <= 360 && image.PixelHeight <= 360,
            "a small tall thumbnail exceeded the decoded dimension budget");
    }

    private static async Task SourceImageBoundsAsync(string root)
    {
        // A valid 27-KiB source safely exercises the original-dimension boundary.
        var handler = new Handler(_ => ImageResponse(CreateBitmap(9000, 1)));
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        Check(await cache.GetAsync("https://i.ytimg.com/large-source.bmp") == null,
            "an excessive original image dimension reached the pixel decoder");
    }

    private static async Task ResponseBoundsAsync(string root)
    {
        var handler = new Handler(request =>
        {
            var bytes = new byte[5 * 1024 * 1024 + 1];
            HttpContent content = request.RequestUri!.AbsolutePath.Contains("unknown", StringComparison.Ordinal)
                ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var cache = Cache(root, http);
        foreach (var name in new[] { "known", "unknown" })
            Check(await cache.GetAsync("https://i.ytimg.com/oversized-" + name + ".bmp") == null,
                "oversized thumbnail response was accepted");
    }

    private static async Task ThumbnailJunctionAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "thumbnail-junction"));
        var outside = Path.Combine(root, "thumbnail-outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "preserved.img");
        File.WriteAllText(sentinel, "synthetic image boundary sentinel");
        var thumbnails = Path.Combine(paths.Data, "thumbnails");
        await CreateJunctionAsync(thumbnails, outside);
        try
        {
            using var http = new HttpClient(new Handler(_ => ImageResponse()));
            using var cache = new ThumbnailCache(paths, false, http);
            Check(await cache.GetAsync("https://i.ytimg.com/disk-junction.bmp") != null,
                "a blocked disk cache lost its permitted memory-only thumbnail");
            Check(Directory.EnumerateFiles(outside).SequenceEqual(new[] { sentinel }) &&
                File.ReadAllText(sentinel) == "synthetic image boundary sentinel",
                "thumbnail disk cache wrote into or modified a linked external directory");
        }
        finally { Directory.Delete(thumbnails); }
    }

    private static async Task UnrelatedThumbnailAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "thumbnail-unrelated"));
        var thumbnails = Path.Combine(paths.Data, "thumbnails");
        Directory.CreateDirectory(thumbnails);
        var unrelated = Path.Combine(thumbnails, "unrelated.img");
        using (var stream = new FileStream(unrelated, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // The sparse fixture has a logical size above the cache budget, with
            // no 513-MiB physical allocation and no large managed allocation.
            Check(DeviceIoControl(stream.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, IntPtr.Zero), "isolated volume cannot create a sparse image fixture");
            stream.SetLength(513L * 1024 * 1024);
        }
        using var http = new HttpClient(new Handler(_ => ImageResponse()));
        using var cache = new ThumbnailCache(paths, false, http);
        Check(await cache.GetAsync("https://i.ytimg.com/disk-unrelated.bmp") != null,
            "normal disk cache initialization lost its thumbnail");
        Check(File.Exists(unrelated), "thumbnail eviction deleted an image without an owned SHA256 cache name");
    }

    private static Task UnrelatedCookieCleanupAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "cleanup"));
        var temp = Path.Combine(paths.Data, "temp");
        Directory.CreateDirectory(temp);
        var unrelated = Path.Combine(temp, "cookies-manual.txt");
        File.WriteAllText(unrelated, "synthetic unrelated document");
        _ = new SessionService(paths);
        Check(File.Exists(unrelated), "startup wildcard cleanup deleted a file without an owned GUID name");
        return Task.CompletedTask;
    }

    private static async Task SessionJunctionAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "session-junction"));
        var outside = Path.Combine(root, "session-outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(sentinel, "synthetic boundary sentinel");
        var temp = Path.Combine(paths.Data, "temp");
        await CreateJunctionAsync(temp, outside);
        try
        {
            var rejected = false;
            try { _ = new SessionService(paths); }
            catch (IOException) { rejected = true; }
            Check(rejected && File.Exists(sentinel), "session startup followed its temp junction and modified the target");
        }
        finally { Directory.Delete(temp); }
    }

    private static async Task DownloadSourceJunctionAsync(string root)
    {
        var outside = Path.Combine(root, "download-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "video.mkv"), "synthetic external source");
        var stage = Path.Combine(root, "download-stage");
        await CreateJunctionAsync(stage, outside);
        var destination = Path.Combine(root, "junction-output.mkv");
        try
        {
            var publish = typeof(DownloadService).GetMethod("PublishAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var rejected = false;
            try { await (Task)publish.Invoke(null, [Path.Combine(stage, "video.mkv"), destination, false, null, null, CancellationToken.None])!; }
            catch (IOException) { rejected = true; }
            Check(rejected && !File.Exists(destination), "publication followed an app staging source junction");
        }
        finally { Directory.Delete(stage); }
    }

    private static async Task ChannelDisconnectJunctionAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "channel-disconnect-junction"));
        var session = new SessionService(paths);
        var outside = Path.Combine(root, "channel-disconnect-outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "unrelated.dpapi");
        var hashed = Path.Combine(outside, new string('A', 64) + ".dpapi");
        File.WriteAllText(sentinel, "synthetic unrelated channel sentinel");
        File.WriteAllText(hashed, "synthetic external SHA256 sentinel");
        var channels = Path.Combine(paths.Data, "channels");
        await CreateJunctionAsync(channels, outside);
        try
        {
            var rejected = false;
            try { session.Disconnect(); } catch (IOException) { rejected = true; }
            Check(rejected && File.Exists(sentinel) && File.Exists(hashed) &&
                File.ReadAllText(sentinel) == "synthetic unrelated channel sentinel" &&
                File.ReadAllText(hashed) == "synthetic external SHA256 sentinel",
                "disconnect followed a channel junction or changed external DPAPI sentinels");
        }
        finally { Directory.Delete(channels); }
    }

    private static Task ChannelOwnedCleanupAsync(string root)
    {
        var paths = new RuntimePaths(Path.Combine(root, "channel-owned-cleanup"));
        var session = new SessionService(paths);
        var channels = Path.Combine(paths.Data, "channels");
        Directory.CreateDirectory(channels);
        var owned = new[] { new string('A', 64) + ".dpapi", new string('b', 64) + ".dpapi" };
        var unrelated = new[] { "unrelated.dpapi", new string('G', 64) + ".dpapi", new string('C', 65) + ".dpapi",
            new string('D', 64) + ".dpapi.new" };
        foreach (var name in owned.Concat(unrelated)) File.WriteAllText(Path.Combine(channels, name), "synthetic session artifact");
        session.Disconnect();
        Check(owned.All(name => !File.Exists(Path.Combine(channels, name))) &&
            unrelated.All(name => File.ReadAllText(Path.Combine(channels, name)) == "synthetic session artifact"),
            "disconnect removed unrelated files or retained an owned SHA256 channel session");
        return Task.CompletedTask;
    }

    private static async Task ChannelReadWriteJunctionAsync(string root, string workspace)
    {
        var paths = new RuntimePaths(Path.Combine(root, "channel-read-write-junction"));
        var runtime = Path.Combine(paths.Root, "runtime");
        Directory.CreateDirectory(runtime);
        var python = Path.Combine(runtime, "python");
        await CreateJunctionAsync(python, Path.Combine(workspace, "runtime", "python"));
        var channels = Path.Combine(paths.Data, "channels");
        var outside = Path.Combine(root, "channel-read-write-outside");
        Directory.CreateDirectory(outside);
        var channel = new YouTubeChannel { Id = "fixture-channel-a", DataSyncId = "fixture_user||", AuthUser = 1 };
        try
        {
            var worker = Path.Combine(paths.Root, "worker");
            Directory.CreateDirectory(worker);
            File.WriteAllText(Path.Combine(worker, "youtube_session.py"), """
                import json, sys
                request=json.loads(sys.stdin.readline())
                account=request['accountContext']
                with open(request['outputCookiePath'],'w',encoding='utf-8') as output:
                    output.write('# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n')
                print(json.dumps({'type':'selected','verified':True,'id':account['id'],
                    'dataSyncId':account['dataSyncId'],'authUser':account['authUser']}),flush=True)
                """, new UTF8Encoding(false));
            var session = new SessionService(paths);
            // The ordinary path proves this guard still permits an owned encrypted
            // session and releases the selected plaintext lease after reading.
            await session.SelectChannelAsync(channel, CancellationToken.None);
            session.ForgetSelectedChannel();
            session.RestoreSelectedChannel(channel.Id);
            using (var lease = session.Acquire())
                Check(lease.Path != null && File.ReadAllText(lease.Path).Contains("offline-only"), "regular channel session no longer round-trips");
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel.Id))) + ".dpapi";
            var encrypted = File.ReadAllBytes(Path.Combine(channels, name));
            File.WriteAllBytes(Path.Combine(outside, name), encrypted);
            File.WriteAllText(Path.Combine(outside, "unrelated.dpapi"), "synthetic read/write sentinel");
            Check(Path.GetFullPath(channels).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "channel fixture cleanup escaped its owned root");
            Directory.Delete(channels, true);
            await CreateJunctionAsync(channels, outside);
            var readRejected = false;
            try { session.RestoreSelectedChannel(channel.Id); } catch (InvalidDataException) { readRejected = true; }
            var writeRejected = false;
            try { await session.SelectChannelAsync(channel with { Id = "fixture-channel-b" }, CancellationToken.None); }
            catch (IOException) { writeRejected = true; }
            Check(readRejected && writeRejected && Directory.EnumerateFiles(outside).Count() == 2 &&
                File.ReadAllBytes(Path.Combine(outside, name)).SequenceEqual(encrypted) &&
                File.ReadAllText(Path.Combine(outside, "unrelated.dpapi")) == "synthetic read/write sentinel",
                "channel read/write followed the junction or changed its external target");
            Check(!Directory.EnumerateFiles(Path.Combine(paths.Data, "temp"), "cookies-*.txt").Any(),
                "rejected channel operation retained a plaintext lease");
        }
        finally
        {
            if (Directory.Exists(channels) && (File.GetAttributes(channels) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(channels);
            Directory.Delete(python);
        }
    }

    private static async Task MalformedChannelResponsesAsync(string root, string workspace)
    {
        var paths = new RuntimePaths(Path.Combine(root, "channel-response"));
        var runtime = Path.Combine(paths.Root, "runtime");
        Directory.CreateDirectory(runtime);
        var python = Path.Combine(runtime, "python");
        await CreateJunctionAsync(python, Path.Combine(workspace, "runtime", "python"));
        try
        {
            var worker = Path.Combine(paths.Root, "worker");
            Directory.CreateDirectory(worker);
            var script = Path.Combine(worker, "youtube_session.py");
            var session = new SessionService(paths);
            var failures = new List<string>();
            async Task Reject(string response, Func<Task> call)
            {
                File.WriteAllText(script, "import sys\nsys.stdout.write(" + JsonSerializer.Serialize(response) + ")\n");
                Exception? failure = null;
                try { await call(); } catch (Exception error) { failure = error; }
                if (failure is not InvalidDataException)
                    failures.Add(response + " => " + (failure?.GetType().Name ?? "accepted"));
            }
            foreach (var response in new[] { "[]", "{\"type\":1}", "{\"type\":\"accounts\",\"channels\":{}}" })
                await Reject(response, async () => { await session.ListChannelsAsync(CancellationToken.None); });
            var channel = new YouTubeChannel { Id = "fixture-primary", DataSyncId = "fixture_user||", AuthUser = 1 };
            foreach (var response in new[]
            {
                "{\"type\":\"selected\",\"verified\":\"true\"}",
                "{\"type\":\"selected\",\"verified\":true,\"id\":3}",
                "{\"type\":\"selected\",\"verified\":true,\"id\":\"fixture-primary\",\"dataSyncId\":\"fixture_user||\",\"authUser\":\"1\"}"
            })
                await Reject(response, () => session.SelectChannelAsync(channel, CancellationToken.None));
            Check(failures.Count == 0, "malformed channel responses escaped the safe exception boundary: " + string.Join("; ", failures));
        }
        finally
        {
            // Remove this junction non-recursively before the fixture's recursive
            // cleanup. The packaged runtime target is never a cleanup argument.
            Directory.Delete(python);
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        // Names are generated under the caller's isolated test root. PowerShell's
        // native Junction operation avoids symbolic-link privilege requirements.
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
            link.Replace("'", "''") + "' -Value '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        Check(process.ExitCode == 0, "isolated junction fixture could not be created");
    }

    private static ThumbnailCache Cache(string root, HttpClient http) =>
        new(new RuntimePaths(Path.Combine(root, "thumb-" + Guid.NewGuid().ToString("N"))), true, http);

    private static HttpResponseMessage ImageResponse(byte[]? bytes = null) => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(bytes ?? CreateBitmap(1, 1)) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static byte[] CreateBitmap(int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + stride * height];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(respond(request));
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint control, IntPtr input, uint inputBytes,
        IntPtr output, uint outputBytes, out uint returnedBytes, IntPtr overlapped);
}
