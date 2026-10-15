using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FkYouTube;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static int _passed, _failed, _skipped;
    private static string _workspace = "";

    private static async Task<int> Main(string[] args)
    {
        _workspace = FindWorkspace(args);
        Console.OutputEncoding = new UTF8Encoding(false);
        if(args.Contains("--integrated-process-only"))
        {
            await TestAsync("Native Job lifetime and deferred cleanup ownership",()=>ProcessCleanupRegression.RunAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");return _failed==0?0:1;
        }
        if(args.Contains("--privacy-recovery-only"))
        {
            await TestAsync("Unread settings block authenticated playback and history",()=>AsyncLifecycleRegression.RunAsync(_workspace,"privacy-recovery"));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");return _failed==0?0:1;
        }
        if(args.Contains("--playback-r6-only") || args.Contains("--session-r6-only"))
        {
            if(args.Contains("--playback-r6-only"))
            {
                await TestAsync("Direct stream validation review6",()=>PlayerRootRegression.RunDirectValidationReview6Async(_workspace));
                await TestAsync("Atomic player disconnect review6",()=>PlayerRootRegression.RunDisconnectReview6Async(_workspace));
            }
            if(args.Contains("--session-r6-only")) await TestAsync("Session boundary and retained consumers review6",()=>SessionReviewR6Regression.RunAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--ui-language-only"))
        {
            await TestAsync("App UI language persistence live refresh isolation and rollback",()=>AsyncLifecycleRegression.RunAsync(_workspace,"ui-language"));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--direct-chunk-only"))
        {
            Test("Prepared HTTP chunk metadata policy",PreparedHttpChunkParsing);
            await TestAsync("Prepared and selected-account direct HTTP chunk options",()=>PlayerRootRegression.RunHttpChunkOptionsAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--mpv-owner-r4-only"))
        {
            await TestAsync("Mpv captured owner and cookie recovery review4",()=>PlayerRootRegression.RunOwnershipR4Async(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--session-storage16-r2-only") || args.Contains("--mpv16-r2-only"))
        {
            if(args.Contains("--session-storage16-r2-only")) await TestAsync("Session storage review16 r2",()=>SessionStorageReview16Regression.RunAsync(_workspace));
            if(args.Contains("--mpv16-r2-only")) await TestAsync("Mpv review16 r2",()=>PlayerRootRegression.RunReview16R2Async(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--lifecycle16-only"))
        {
            await TestAsync("Lifecycle review 16 models",()=>AsyncLifecycleRegression.RunAsync(_workspace,"review16"));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--ownership-review-only") || args.Contains("--storage16-only"))
        {
            if(args.Contains("--ownership-review-only"))
                await TestAsync("Media ownership review",()=>PlayerRootRegression.RunOwnershipReviewAsync(_workspace));
            if(args.Contains("--storage16-only"))
                await TestAsync("Storage review 16 models",()=>StorageReviewRegression.RunAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--partial-history-only"))
        {
            await TestAsync("Partial remote history preserves confirmed status",()=>AsyncLifecycleRegression.RunAsync(_workspace,"history-partial"));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--save-failure-only"))
        {
            await TestAsync("Playback survives local save failure and retries",()=>AsyncLifecycleRegression.RunAsync(_workspace,"save-failure"));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--download-review-only"))
        {
            await TestAsync("Download replacement preserves previous contents",()=>ServiceRegression.RunDownloadReviewAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--storage-review-only"))
        {
            await TestAsync("Storage review contracts",()=>FileSecurityRegression.RunStorageReviewAsync(_workspace));
            await TestAsync("Corrupt thumbnail recovery",ThumbnailRegression.RunReviewAsync);
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--access-errors-only"))
        {
            await TestAsync("Native playback access errors and stale results",()=>PlayerRootRegression.RunAccessErrorsAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--subtitles-only"))
        {
            await TestAsync("Subtitle language selection and media lifetime",()=>PlayerRootRegression.RunSubtitlesAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--subtitle-catalog-only"))
        {
            await TestAsync("All subtitle language metadata and account isolation",()=>QualityServiceRegression.RunSubtitleCatalogAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--ratings-only"))
        {
            await TestAsync("YouTube ratings and isolated RYD contracts",()=>ServiceRegression.RunRatingContractsAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--metadata-security-only"))
        {
            Test("Malformed video metadata regression",MetadataSecurityRegression.Run);
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");return _failed==0?0:1;
        }
        if(args.Contains("--selected-account-errors-only"))
        {
            await TestAsync("Selected-account malformed output contract",()=>QualityServiceRegression.RunSelectedAccountResponseErrorsAsync(_workspace));
            await RunUiGroupsAsync("selected-account-errors");
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--quality-ui-only"))
        {
            await RunUiGroupsAsync("quality-ui");
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if(args.Contains("--quality-only"))
        {
            await TestAsync("Resolution metadata request",()=>QualityServiceRegression.RunAsync(_workspace));
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if (args.Contains("--interaction-only") || args.Contains("--state-only") || args.Contains("--interaction-controls-only") ||
            args.Contains("--prepared-caption-ui-only") || args.Contains("--subtitle-selection-ui-only"))
        {
            var state=args.Contains("--state-only");
            if(state)await TestAsync("Selected-account playback fallback",()=>PlaybackStateRegression.RunPlayerAsync(_workspace));
            var suite=state?"state":args.Contains("--interaction-controls-only")?"controls":
                args.Contains("--prepared-caption-ui-only")?"prepared-subtitles":args.Contains("--subtitle-selection-ui-only")?"subtitle-selection":"interactions";
            await RunUiGroupsAsync(suite);
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed==0?0:1;
        }
        if (args.Contains("--audit-only"))
        {
            Test("Malformed video metadata regression", MetadataSecurityRegression.Run);
            await TestAsync("IPC boundary security regression", () => IpcSecurityRegression.RunAsync(_workspace));
            await TestAsync("Cookie file and thumbnail security regression", () => FileSecurityRegression.RunAsync(_workspace));
            await RunUiGroupsAsync();
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed == 0 ? 0 : 1;
        }
        if (!args.Contains("--probe-only") || args.Contains("--url-only"))
        {
            Test("YouTube URL normalization accepts shared links", YouTubeUrlNormalization);
            Test("YouTube URL helpers reject unsafe authorities and schemes", YouTubeUrlRejection);
            Test("video URLs preserve video opening when a playlist is present", VideoUrlParsing);
            Test("playlist URLs canonicalize both playlist and watch links", PlaylistUrlParsing);
            Test("Channel URL and browse metadata regression", BrowseRegression.Run);
        }
        if (args.Contains("--url-only"))
        {
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
            return _failed == 0 ? 0 : 1;
        }
        if (!args.Contains("--probe-only"))
        {
            Test("available resolutions exclude audio storyboards DRM and duplicates", AvailableResolutions);
            Test("Mix seed survives UI playlist normalization", MixSeedNormalization);
            Test("prepared video keeps separate media and rejects live or segmented streams", PreparedVideoParsing);
            Test("Prepared HTTP chunk metadata policy",PreparedHttpChunkParsing);
            Test("autoplay preserves playlist duplicates and skips nonvideos and members", AutoPlayQueue);
            Test("long video qualifies after 30 real seconds once", LongVideo);
            Test("short video qualifies at 80 percent", ShortVideo);
            Test("pause buffer seek and unloaded intervals do not count", InactiveIntervals);
            Test("unflagged position jump is not viewing", SeekJump);
            Test("duplicate position snapshots preserve the time anchor", DuplicateSnapshots);
            Test("2x speed counts actual wall time and media progress separately", DoubleSpeed);
            Test("cookie filter rejects lookalike domains", CookieDomainFilter);
            Test("DPAPI session leases preserve original and clean plaintext", SessionLeases);
            Test("startup cleans abandoned app session artifacts", SessionStartupCleanup);
            Test("SQLite resume persists and separates accounts", ResumePersistence);
            Test("position updates preserve account-specific sync status", SyncStatusPersistence);
            Test("invalid positions cannot corrupt saved progress", InvalidPositions);
            await TestAsync("OwnedProcess preserves UTF-8 and quoted arguments", OwnedProcessArguments);
            await TestAsync("OwnedProcess disposal terminates child and grandchild", OwnedProcessTree);
            await TestAsync("Native Job lifetime and deferred cleanup ownership",()=>ProcessCleanupRegression.RunAsync(_workspace));
            await TestAsync("Thumbnail lifetime queue and private cache regression", ThumbnailRegression.RunAsync);
            await TestAsync("Corrupt thumbnail recovery",ThumbnailRegression.RunReviewAsync);
            await TestAsync("Service timeout identity and download regression", ()=>ServiceRegression.RunAsync(_workspace));
            await TestAsync("Download completion retains backup result",()=>ServiceRegression.RunDownloadCompletionReviewAsync(_workspace));
            await TestAsync("Player and root regression", ()=>PlayerRootRegression.RunAsync(_workspace));
            await TestAsync("Prepared and selected-account direct HTTP chunk options",()=>PlayerRootRegression.RunHttpChunkOptionsAsync(_workspace));
            await TestAsync("Media ownership review",()=>PlayerRootRegression.RunOwnershipReviewAsync(_workspace));
            await TestAsync("Direct stream validation review6",()=>PlayerRootRegression.RunDirectValidationReview6Async(_workspace));
            await TestAsync("Atomic player disconnect review6",()=>PlayerRootRegression.RunDisconnectReview6Async(_workspace));
            await TestAsync("Session boundary and retained consumers review6",()=>SessionReviewR6Regression.RunAsync(_workspace));
            await TestAsync("Storage review 16 models",()=>StorageReviewRegression.RunAsync(_workspace));
            await TestAsync("Session storage review16 r2",()=>SessionStorageReview16Regression.RunAsync(_workspace));
            await TestAsync("Native playback access errors and stale results",()=>PlayerRootRegression.RunAccessErrorsAsync(_workspace));
            await TestAsync("Subtitle language selection and media lifetime",()=>PlayerRootRegression.RunSubtitlesAsync(_workspace));
            await TestAsync("Selected-account playback fallback",()=>PlaybackStateRegression.RunPlayerAsync(_workspace));
            await TestAsync("Resolution metadata request",()=>QualityServiceRegression.RunAsync(_workspace));
            await TestAsync("All subtitle language metadata and account isolation",()=>QualityServiceRegression.RunSubtitleCatalogAsync(_workspace));
            Test("Malformed video metadata regression", MetadataSecurityRegression.Run);
            await TestAsync("IPC boundary security regression", () => IpcSecurityRegression.RunAsync(_workspace));
            await TestAsync("Cookie file and thumbnail security regression", () => FileSecurityRegression.RunAsync(_workspace));
            await TestAsync("Storage review contracts",()=>FileSecurityRegression.RunStorageReviewAsync(_workspace));
            await RunUiGroupsAsync();
        }
        if (args.Contains("--public-probe"))
            await ProbePublicFeed();
        if (args.Contains("--account-probe"))
            await ProbeAccountFeeds(args);
        if (args.Contains("--comments-probe"))
            await ProbeComments();
        var browseProbe=Array.IndexOf(args,"--browse-probe-root");
        if(browseProbe>=0 && browseProbe+1<args.Length)
            await TestAsync("Read-only isolated account home filters and watch owner",()=>BrowseLiveProbe.RunAsync(_workspace,args[browseProbe+1]));
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped={_skipped}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task ProbeComments()
    {
        var paths = new RuntimePaths(_workspace);
        var session = new SessionService(paths);
        if (!session.IsConnected)
        {
            Console.WriteLine("COMMENTS state=failed category=auth");
            _failed++;
            return;
        }
        var youtube = new YouTubeService(paths, session);
        const string url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        foreach (var newest in new[] { false, true })
        {
            var timer = Stopwatch.StartNew();
            var label = newest ? "newest" : "top";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
                // The production service requests at most 100 comments and uses
                // its --no-mark-watched extraction path. Never print author/text.
                var comments = await youtube.GetCommentsAsync(url, newest, timeout.Token);
                var successful = comments.Count is > 0 and <= 100;
                Console.WriteLine($"COMMENTS order={label} requested=100 count={comments.Count} " +
                    $"state={(successful ? "success" : "failed")} seconds={timer.Elapsed.TotalSeconds:F2}");
                if (successful) _passed++; else _failed++;
            }
            catch (Exception error)
            {
                Console.WriteLine($"COMMENTS order={label} state=failed category={SafeProbeFailure(error)} seconds={timer.Elapsed.TotalSeconds:F2}");
                _failed++;
            }
        }
        Console.WriteLine("COMMENTS managed-plaintext-files=" +
            Directory.EnumerateFiles(Path.Combine(paths.Data, "temp"), "cookies-*.txt").Count());
    }

    private static async Task ProbePublicFeed()
    {
        var paths = new RuntimePaths(_workspace);
        // No SessionService construction/acquisition: this probe is unauthenticated.
        var successful = await ProbeFeed(paths, "search:lofi", 3, session: null, label: "public-search");
        if (successful) _passed++; else _failed++;
    }

    private static async Task ProbeAccountFeeds(string[] args)
    {
        var profileIndex = Array.IndexOf(args, "--connect-firefox");
        if (profileIndex < 0 || profileIndex + 1 >= args.Length)
        {
            Console.WriteLine("ACCOUNT connection=failed reason=profile-argument-required");
            _failed++;
            return;
        }
        // This path is supplied only after an explicit user request to connect it.
        var profile = Path.GetFullPath(args[profileIndex + 1]);
        var paths = new RuntimePaths(_workspace);
        var sessions = new SessionService(paths);
        try
        {
            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            await sessions.ConnectFirefoxAsync(profile, connectionTimeout.Token);
            var store = new DataStore(paths);
            var settings = store.LoadSettings();
            if (settings.AccountKey == "guest" || string.IsNullOrWhiteSpace(settings.AccountKey))
                settings.AccountKey = Guid.NewGuid().ToString("N");
            settings.AuthMode = "Firefox";
            settings.FirefoxProfile = profile;
            store.SaveSettings(settings);
            Console.WriteLine("ACCOUNT connection=success storage=dpapi settings=saved");
        }
        catch (Exception error)
        {
            // Raw error text can include personal paths; report an allowlisted category.
            Console.WriteLine("ACCOUNT connection=failed category=" + SafeProbeFailure(error));
            _failed++;
            return;
        }
        foreach (var source in new[] { "home", "subscriptions", "playlists", "history", "watchlater" })
        {
            var successful = await ProbeFeed(paths, source, 2, sessions, "account-" + source);
            if (successful) _passed++; else _failed++;
        }
        Console.WriteLine("ACCOUNT managed-plaintext-files=" +
            Directory.EnumerateFiles(Path.Combine(paths.Data, "temp"), "cookies-*.txt").Count() +
            " managed-work-directories=" +
            Directory.EnumerateDirectories(Path.Combine(paths.Data, "temp"), "cookies-*.work").Count());
    }

    private static async Task<bool> ProbeFeed(RuntimePaths paths, string source, int batches,
        SessionService? session, string label)
    {
        await using var feed = new FeedService(paths);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var duplicates = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            for (var batch = 1; batch <= batches; batch++)
            {
                var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                var batchTimer = Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
                var result = await feed.ReadAsync(batch == 1, source, 1,
                    batch == 1 ? session?.Acquire() : null,
                    item =>
                    {
                        // Personal titles/IDs remain in memory and are never printed or persisted.
                        total++;
                        if (!identities.Add(item.Kind + ":" + item.Id)) duplicates++;
                        kinds[item.Kind] = kinds.GetValueOrDefault(item.Kind) + 1;
                    }, timeout.Token);
                Console.WriteLine($"FEED label={label} batch={batch} count={result.Count} total={total} " +
                    $"video={kinds.GetValueOrDefault("video")} playlist={kinds.GetValueOrDefault("playlist")} " +
                    $"channel={kinds.GetValueOrDefault("channel")} duplicates={duplicates} " +
                    $"hasMore={result.HasMore.ToString().ToLowerInvariant()} seconds={batchTimer.Elapsed.TotalSeconds:F2}");
                if (!result.HasMore)
                {
                    Console.WriteLine($"FEED label={label} state=exhausted total={total} seconds={timer.Elapsed.TotalSeconds:F2}");
                    return true;
                }
            }
            Console.WriteLine($"FEED label={label} state=success total={total} seconds={timer.Elapsed.TotalSeconds:F2}");
            return true;
        }
        catch (Exception error)
        {
            Console.WriteLine($"FEED label={label} state=failed total={total} category={SafeProbeFailure(error)} seconds={timer.Elapsed.TotalSeconds:F2}");
            return false;
        }
    }

    private static string SafeProbeFailure(Exception error)
    {
        var message = error.Message;
        if (error is OperationCanceledException or TimeoutException) return "timeout";
        if (message.Contains("로그인", StringComparison.Ordinal) || message.Contains("세션", StringComparison.Ordinal)) return "auth";
        if (message.Contains("연결", StringComparison.Ordinal) || message.Contains("네트워크", StringComparison.Ordinal)) return "network";
        if (error is InvalidDataException) return "invalid-data";
        if (error is System.ComponentModel.Win32Exception) return "process";
        return "unavailable";
    }

    private static string FindWorkspace(string[] args)
    {
        var index = Array.IndexOf(args, "--root");
        if (index >= 0 && index + 1 < args.Length) return Path.GetFullPath(args[index + 1]);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "FkYouTube", "FkYouTube.csproj")))
                    return directory.FullName;
        throw new InvalidOperationException("Workspace root was not found. Supply --root.");
    }

    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }

    private static async Task TestAsync(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (SkipException error) { _skipped++; Console.WriteLine("SKIP " + name + ": " + error.Message); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }

    private static async Task RunUiGroupsAsync(string? suite=null)
    {
        // All reported groups share one STA/Application. Individual reports keep
        // their own deadline and preserve an aggregate failure in the exit code.
        try{await AsyncLifecycleRegression.RunAsync(_workspace,suite,TestAsync);}
        catch(Exception error){_failed++;Console.WriteLine("FAIL UI fixture host and teardown: "+error);}
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message)
        => Assert(Math.Abs(actual - expected) < 0.000001, message);

    private static bool Observe(WatchProgressTracker tracker, double clock, double position,
        double duration = 300, double speed = 1, bool paused = false, bool buffering = false,
        bool seeking = false, bool loaded = true)
        => tracker.Observe(clock, position, duration, speed, paused, buffering, seeking, loaded);

    private static void YouTubeUrlNormalization()
    {
        var cases = new Dictionary<string, string>
        {
            [" https://WWW.YouTube.com/watch?v=fixture0001 "] = "https://www.youtube.com/watch?v=fixture0001",
            ["http://www.youtube.com/playlist?list=PL_fixture-01"] = "https://www.youtube.com/playlist?list=PL_fixture-01",
            ["youtube.com/@fixture/videos"] = "https://youtube.com/@fixture/videos",
            ["m.youtube.com/watch?v=fixture0001"] = "https://m.youtube.com/watch?v=fixture0001",
            ["youtu.be/fixture0001?t=12"] = "https://youtu.be/fixture0001?t=12",
            ["//www.youtube.com/watch?list=PL_fixture-01"] = "https://www.youtube.com/watch?list=PL_fixture-01",
            ["HTTP://youtube.com:80/@fixture"] = "https://youtube.com/@fixture",
            ["https://youtube.com:443/"] = "https://youtube.com/"
        };
        foreach (var (input, expected) in cases)
        {
            Assert(YouTubeService.TryNormalizeYouTubeUrl(input, out var canonical), "A supported URL was rejected: " + input);
            Assert(canonical == expected, "A URL was not normalized correctly: " + input);
        }
    }

    private static void YouTubeUrlRejection()
    {
        foreach (var input in new[]
        {
            "", "  ", "fixture search text", "https://youtube.com.evil.invalid/watch?v=fixture0001",
            "https://notyoutube.com/watch?v=fixture0001", "https://youtu.be.evil.invalid/fixture0001",
            "https://evil.invalid/youtube.com/watch?v=fixture0001", "https://user:password@youtube.com/watch?v=fixture0001",
            "https://@youtube.com/watch?v=fixture0001", "youtube.com@evil.invalid/watch?v=fixture0001",
            "user@youtube.com/watch?v=fixture0001", "ftp://youtube.com/watch?v=fixture0001",
            "file://youtube.com/watch?v=fixture0001", "javascript:youtube.com/watch?v=fixture0001",
            "data:text/plain,youtube.com/watch?v=fixture0001", "https://youtube.com:8443/watch?v=fixture0001",
            "https://youtube.com\\@evil.invalid/watch?v=fixture0001", "https://you tube.com/watch?v=fixture0001",
            "https://youtube.com/watch?v=fixture0001\n&list=PL_fixture-01"
        })
        {
            Assert(!YouTubeService.TryNormalizeYouTubeUrl(input, out var normalized) && normalized == "",
                "An unsafe input was accepted by normalization.");
            Assert(!YouTubeService.TryVideoUrl(input, out var id, out var video) && id == "" && video == "",
                "An unsafe input was accepted as a video.");
            Assert(!YouTubeService.TryPlaylistUrl(input, out var playlist) && playlist == "",
                "An unsafe input was accepted as a playlist.");
        }
    }

    private static void VideoUrlParsing()
    {
        foreach (var input in new[]
        {
            "https://www.youtube.com/watch?v=fixture0001", "http://youtube.com/watch?v=fixture0001",
            "www.youtube.com/watch?v=fixture0001", "m.youtube.com/watch?list=PL_fixture-01&v=fixture0001",
            "youtube.com/watch?v=fixture0001&list=PL_fixture-01&index=2", "youtu.be/fixture0001?si=fixture",
            "http://youtu.be/fixture0001", "//youtu.be/fixture0001", "youtube.com/shorts/fixture0001",
            "https://www.youtube.com/live/fixture0001?feature=share", "youtube.com/watch?v=fixture%30%30%30%31"
        })
        {
            Assert(YouTubeService.TryVideoUrl(input, out var id, out var canonical), "A supported video URL was rejected: " + input);
            Assert(id == "fixture0001" && canonical == "https://www.youtube.com/watch?v=fixture0001",
                "A video URL retained playlist/tracking parameters or changed its ID.");
        }
        foreach (var input in new[]
        {
            "youtube.com/playlist?list=PL_fixture-01", "youtube.com/watch?list=PL_fixture-01",
            "youtu.be/fixture0001/extra", "youtube.com/shorts/fixture0001/extra", "youtube.com/watch?v=short",
            "youtube.com/watch?v=fixture0001%0A", "youtube.com/watch?v=fixture0001%2F", "youtube.com/watch?v="
        })
            Assert(!YouTubeService.TryVideoUrl(input, out var id, out var canonical) && id == "" && canonical == "",
                "An invalid or playlist-only URL was accepted as a video.");
    }

    private static void PlaylistUrlParsing()
    {
        foreach (var input in new[]
        {
            "https://www.youtube.com/playlist?list=PL_fixture-01", "http://youtube.com/playlist?list=PL_fixture-01",
            "www.youtube.com/playlist?list=PL_fixture-01&si=fixture", "m.youtube.com/watch?list=PL_fixture-01",
            "youtube.com/watch?v=fixture0001&list=PL_fixture-01&index=2",
            "youtube.com/watch?list=PL_fixture-01&v=fixture0001", "youtube.com/playlist?list=PL%5Ffixture%2D01"
        })
        {
            Assert(YouTubeService.TryPlaylistUrl(input, out var canonical), "A supported playlist URL was rejected: " + input);
            Assert(canonical == "https://www.youtube.com/playlist?list=PL_fixture-01",
                "A playlist URL did not discard video/tracking parameters.");
        }
        foreach (var input in new[]
        {
            "youtube.com/watch?v=fixture0001", "youtube.com/playlist?list=", "youtube.com/playlist?list=bad.id",
            "youtube.com/playlist?list=PL_fixture-01%0A", "youtube.com/playlist?list=PL_fixture-01%26evil=value",
            "youtube.com/@fixture?list=PL_fixture-01", "youtu.be/fixture0001?list=PL_fixture-01"
        })
            Assert(!YouTubeService.TryPlaylistUrl(input, out var canonical) && canonical == "",
                "An invalid playlist URL was accepted.");
        foreach (var special in new[] { "WL", "LL" })
            Assert(YouTubeService.TryPlaylistUrl("youtube.com/playlist?list=" + special, out var canonical) &&
                canonical == "https://www.youtube.com/playlist?list=" + special, "A supported special playlist was rejected.");
    }

    private static void MixSeedNormalization()
    {
        foreach(var input in new[]{"youtube.com/playlist?list=RD5EQSpS5WvdI","youtube.com/watch?v=5EQSpS5WvdI&list=RD5EQSpS5WvdI"})
            Assert(YouTubeService.TryPlaylistUrl(input,out var result) && result=="https://www.youtube.com/watch?v=5EQSpS5WvdI&list=RD5EQSpS5WvdI","Mix lost its seed video.");
        Assert(YouTubeService.TryPlaylistUrl("youtube.com/watch?v=fixture0001&list=RDMM",out var contextual) && contextual.Contains("v=fixture0001&list=RDMM"),"Context Mix lost its explicit seed.");
    }

    private static void PreparedVideoParsing()
    {
        using var data=JsonDocument.Parse("""{"requested_formats":[{"url":"https://media.example/video?a=1;2","protocol":"https","http_headers":{"User-Agent":"test"}},{"url":"https://media.example/audio","protocol":"https","http_headers":{"User-Agent":"test"}}],"formats":[{"vcodec":"vp9","height":720}]}""");
        var prepared=YouTubeService.ParsePreparedVideo(data.RootElement);
        Assert(prepared.MediaUrl.StartsWith("edl://") && prepared.MediaUrl.Contains("!new_stream") && prepared.Resolutions.SequenceEqual(new[]{720}),"Separate video/audio was not preserved.");
        foreach(var json in new[]{"""{"is_live":true,"url":"https://example/a"}""","""{"url":"https://example/a","protocol":"m3u8_native"}""","""{"url":"file:///a","protocol":"https"}""","""{"url":"https://example/a","has_drm":true}"""})
        {
            using var invalid=JsonDocument.Parse(json);var rejected=false;
            try{YouTubeService.ParsePreparedVideo(invalid.RootElement);}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"Unsupported prepared media was accepted.");
        }
    }

    private static void PreparedHttpChunkParsing()
    {
        var chunkProperty=typeof(PreparedVideo).GetProperty("HttpChunkSize");
        Assert(chunkProperty?.PropertyType==typeof(int?),"Prepared media discarded its optional typed HTTP chunk size.");
        foreach(var sample in new (string Json,int? Expected)[]
        {
            ("""{"url":"https://media.example/video","protocol":"https","downloader_options":{"http_chunk_size":10485760}}""",10485760),
            ("""{"requested_formats":[{"url":"https://media.example/video","downloader_options":{"http_chunk_size":10485760}},{"url":"https://media.example/audio","downloader_options":{"http_chunk_size":5242880}}]}""",5242880),
            ("""{"requested_formats":[{"url":"https://media.example/video"},{"url":"https://media.example/audio","downloader_options":{"http_chunk_size":10485760}}]}""",10485760),
            ("""{"url":"https://media.example/video","downloader_options":{"http_chunk_size":67108864}}""",67108864),
            ("""{"url":"https://media.example/video"}""",null),
            ("""{"url":"https://media.example/video","downloader_options":null}""",null),
            ("""{"url":"https://media.example/video","downloader_options":{"http_chunk_size":null}}""",null)
        })
        {
            using var data=JsonDocument.Parse(sample.Json);var prepared=YouTubeService.ParsePreparedVideo(data.RootElement);
            Assert(Equals(chunkProperty!.GetValue(prepared),sample.Expected),"Prepared HTTP chunk bytes were lost or did not use the smallest selected stream limit.");
        }
        foreach(var invalidOptions in new[]{"[]","true","\"untrusted\"","{\"http_chunk_size\":0}","{\"http_chunk_size\":-1}",
            "{\"http_chunk_size\":1.5}","{\"http_chunk_size\":\"10485760\"}","{\"http_chunk_size\":67108865}",
            "{\"http_chunk_size\":2147483648}","{\"http_chunk_size\":true}","{\"http_chunk_size\":[]}"})
        {
            using var invalid=JsonDocument.Parse("{\"url\":\"https://media.example/video\",\"downloader_options\":"+invalidOptions+"}");
            var rejected=false;try{YouTubeService.ParsePreparedVideo(invalid.RootElement);}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"Malformed or out-of-policy HTTP chunk metadata reached direct playback.");
        }
        Assert(chunkProperty!.GetValue(new PreparedVideo("https://media.example/video",new Dictionary<string,string>(),[],[])) is null,
            "Existing prepared-media callers gained an implicit HTTP chunk option.");
    }

    private static void LongVideo()
    {
        var tracker = new WatchProgressTracker();
        Assert(!Observe(tracker, 0, 0), "A baseline cannot count as viewing.");
        for (var second = 1; second < 30; second++)
            Assert(!Observe(tracker, second, second), "A long video qualified before 30 seconds.");
        Assert(Observe(tracker, 30, 30), "The 30-second transition was missed.");
        Assert(!Observe(tracker, 31, 31), "Qualification must only transition once.");
        Near(tracker.WatchedSeconds, 31, "Wall viewing time was not retained.");
        tracker.Reset();
        Assert(!tracker.Qualified && tracker.WatchedSeconds == 0 && tracker.WatchedMediaSeconds == 0,
            "A new video inherited the old qualification state.");
    }

    private static void ShortVideo()
    {
        var tracker = new WatchProgressTracker();
        Observe(tracker, 0, 0, duration: 20);
        for (var second = 1; second < 16; second++)
            Assert(!Observe(tracker, second, second, duration: 20), "Short video qualified before 80 percent.");
        Assert(Observe(tracker, 16, 16, duration: 20), "Short video did not qualify at 80 percent.");
        Near(tracker.WatchedMediaSeconds, 16, "Short-video progress is wrong.");
    }

    private static void InactiveIntervals()
    {
        foreach (var inactive in new[] { "pause", "buffer", "seek", "unloaded" })
        {
            var tracker = new WatchProgressTracker();
            Observe(tracker, 0, 0);
            for (var second = 1; second <= 40; second++)
                Observe(tracker, second, second, paused: inactive == "pause", buffering: inactive == "buffer",
                    seeking: inactive == "seek", loaded: inactive != "unloaded");
            Assert(!tracker.Qualified, inactive + " interval qualified as viewing.");
            Near(tracker.WatchedSeconds, 0, inactive + " interval accumulated wall time.");
            Observe(tracker, 41, 41);
            Observe(tracker, 42, 42);
            Near(tracker.WatchedSeconds, 1, "Resuming counted an inactive transition.");
        }
    }

    private static void SeekJump()
    {
        var tracker = new WatchProgressTracker();
        Observe(tracker, 0, 0);
        Observe(tracker, 1, 100);
        Near(tracker.WatchedSeconds, 0, "Seeking without a flag accumulated watch time.");
        Observe(tracker, 2, 101);
        Near(tracker.WatchedSeconds, 1, "Normal playback did not resume after the seek.");
    }

    private static void DuplicateSnapshots()
    {
        var tracker = new WatchProgressTracker();
        Observe(tracker, 0, 0);
        var transitions = 0;
        for (var second = 1; second <= 30; second++)
        {
            // mpv can emit volume/pause/cache observations with an unchanged time-pos.
            Observe(tracker, second - 0.75, second - 1);
            Observe(tracker, second - 0.50, second - 1);
            Observe(tracker, second - 0.25, second - 1);
            if (Observe(tracker, second, second)) transitions++;
        }
        Near(tracker.WatchedSeconds, 30, "Other property snapshots discarded time-pos watch time.");
        Assert(transitions == 1 && tracker.Qualified, "Duplicate snapshots prevented qualification.");
    }

    private static void DoubleSpeed()
    {
        var tracker = new WatchProgressTracker();
        Observe(tracker, 0, 0, speed: 2);
        for (var second = 1; second < 30; second++)
            Assert(!Observe(tracker, second, second * 2, speed: 2), "2x speed qualified by media time alone.");
        Assert(Observe(tracker, 30, 60, speed: 2), "2x playback did not qualify after 30 wall seconds.");
        Near(tracker.WatchedSeconds, 30, "2x wall time is wrong.");
        Near(tracker.WatchedMediaSeconds, 60, "2x media time is wrong.");
        tracker.Reset();
        Observe(tracker, 0, 0, duration: 20, speed: 2);
        for (var second = 1; second < 8; second++)
            Assert(!Observe(tracker, second, second * 2, duration: 20, speed: 2), "Short 2x video qualified early.");
        Assert(Observe(tracker, 8, 16, duration: 20, speed: 2), "Short 2x video failed its 80 percent threshold.");
    }

    private static readonly string FakeCookies = "# Netscape HTTP Cookie File\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t2147483647\tFAKE_YT\tfixture-yt-value\n" +
        "#HttpOnly_accounts.google.com\tFALSE\t/\tTRUE\t2147483647\tFAKE_GOOGLE\tfixture-google-value\n" +
        ".google.com\tTRUE\t/\tTRUE\t2147483647\tFAKE_ROOT\tfixture-root-value\n" +
        "google.com.evil.invalid\tFALSE\t/\tTRUE\t2147483647\tLOOKALIKE_1\tfixture-value\n" +
        "notgoogle.com\tFALSE\t/\tTRUE\t2147483647\tLOOKALIKE_2\tfixture-value\n" +
        "youtube.com.evil.invalid\tFALSE\t/\tTRUE\t2147483647\tLOOKALIKE_3\tfixture-value\n" +
        "private.example.invalid\tFALSE\t/\tTRUE\t2147483647\tUNRELATED\tfixture-value\n";

    private static void CookieDomainFilter()
    {
        var filtered = SessionService.FilterCookies(FakeCookies);
        Assert(filtered.Contains("FAKE_YT") && filtered.Contains("FAKE_GOOGLE") && filtered.Contains("FAKE_ROOT"),
            "Permitted Google/YouTube cookies were lost.");
        Assert(filtered.Contains("#HttpOnly_accounts.google.com"), "HttpOnly Netscape prefix was lost.");
        Assert(!filtered.Contains("LOOKALIKE") && !filtered.Contains("UNRELATED"),
            "An unrelated or lookalike domain passed the session filter.");
        var rejected = false;
        try { SessionService.FilterCookies("example.invalid\tFALSE\t/\tTRUE\t0\tFAKE\tfixture\n"); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "A file with no permitted session cookies was accepted.");
    }

    private static void SessionLeases()
    {
        using var sandbox = new Sandbox();
        var paths = new RuntimePaths(sandbox.Root);
        var original = Path.Combine(sandbox.Root, "fake-import.cookies.txt");
        File.WriteAllText(original, FakeCookies, new UTF8Encoding(false));
        var originalBytes = File.ReadAllBytes(original);
        var sessions = new SessionService(paths);
        Assert(!sessions.IsConnected && sessions.Acquire().Path is null, "Disconnected session had a cookie lease.");
        sessions.ImportFile(original);
        Assert(sessions.IsConnected, "Imported session was not connected.");
        var protectedBytes = File.ReadAllBytes(Path.Combine(paths.Data, "session.dpapi"));
        Assert(!Encoding.UTF8.GetString(protectedBytes).Contains("fixture-yt-value"), "Session file contained plaintext.");
        var first = sessions.Acquire();
        var second = sessions.Acquire();
        Assert(first.Path is not null && second.Path is not null && first.Path != second.Path,
            "Requests shared a plaintext lease.");
        Assert(File.ReadAllText(first.Path!).Contains("FAKE_YT"), "DPAPI session could not be read by its owner.");
        Assert(!File.ReadAllText(first.Path!).Contains("LOOKALIKE"), "A lease contained unrelated domains.");
        File.WriteAllText(first.Path!, "fixture-managed-cookie-update");
        first.Dispose(); first.Dispose(); second.Dispose();
        Assert(!File.Exists(first.Path) && !File.Exists(second.Path), "Disposed lease left plaintext cookies behind.");
        Assert(File.ReadAllBytes(original).SequenceEqual(originalBytes), "Session import or lease overwrote the original.");
        using (var renewed = sessions.Acquire())
            Assert(File.ReadAllText(renewed.Path!).Contains("FAKE_YT"), "Worker lease update corrupted the DPAPI session.");
        sessions.Disconnect();
        Assert(!sessions.IsConnected && sessions.Acquire().Path is null, "Disconnect retained a usable session.");
    }

    private static void SessionStartupCleanup()
    {
        using var sandbox = new Sandbox();
        var paths = new RuntimePaths(sandbox.Root);
        _ = new SessionService(paths);
        var temporary = Path.Combine(paths.Data, "temp");
        var stale = Path.Combine(temporary, "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        var work = Path.ChangeExtension(stale, ".work");
        File.WriteAllText(stale, "fixture-plaintext");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "fixture.sqlite"), "fixture-cookie-database-copy");
        var unrelated = Path.Combine(temporary, "keep.fixture");
        File.WriteAllText(unrelated, "fixture");
        _ = new SessionService(paths);
        Assert(!File.Exists(stale) && !Directory.Exists(work), "Abandoned session job artifacts survived startup.");
        Assert(File.Exists(unrelated), "Session cleanup removed an unrelated file.");
    }

    private static VideoItem SampleVideo() => new()
    {
        Id = "fixture0001", Title = "가짜 테스트 영상 \"A\"", Url = "https://www.youtube.com/watch?v=fixture0001",
        Channel = "Fixture", Duration = 200, Kind = "video"
    };

    private static void ResumePersistence()
    {
        using var sandbox = new Sandbox();
        var paths = new RuntimePaths(sandbox.Root);
        var store = new DataStore(paths);
        var video = SampleVideo();
        store.SavePosition("account-a", video, 12.25, 200);
        store.SavePosition("account-b", video, 77.50, 200);
        var reloaded = new DataStore(paths);
        Near(reloaded.GetResumePosition("account-a", video.Id), 12.25, "Account A resume was not persisted.");
        Near(reloaded.GetResumePosition("account-b", video.Id), 77.50, "Account B resume was not separated.");
        Near(reloaded.GetResumePosition("guest", video.Id), 0, "Another account inherited private progress.");
        Assert(reloaded.GetHistory("account-a").Single().Video.Title == video.Title, "History metadata lost Unicode or quotes.");
        store.SavePosition("account-a", video, 198, 200);
        Near(reloaded.GetResumePosition("account-a", video.Id), 0, "A completed video resumed at its last frame.");
        Near(reloaded.GetHistory("account-a").Single().Position, 198, "Completion erased the exact stored position.");
    }

    private static void SyncStatusPersistence()
    {
        using var sandbox = new Sandbox();
        var store = new DataStore(new RuntimePaths(sandbox.Root));
        var video = SampleVideo();
        store.SavePosition("account-a", video, 30, 200);
        store.SavePosition("account-b", video, 10, 200);
        store.SetSyncStatus("account-a", video.Id, "attempted");
        store.SavePosition("account-a", video, 44.5, 200);
        Assert(store.GetHistory("account-a").Single().SyncStatus == "attempted", "Resume save reset the sync attempt state.");
        Assert(store.GetHistory("account-b").Single().SyncStatus == "local", "Sync state crossed account boundaries.");
    }

    private static void InvalidPositions()
    {
        using var sandbox = new Sandbox();
        var store = new DataStore(new RuntimePaths(sandbox.Root));
        var video = SampleVideo();
        store.SavePosition("account-a", video, 10, 200);
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -5.0 })
            store.SavePosition("account-a", video, invalid, 200);
        Near(store.GetResumePosition("account-a", video.Id), 10, "Invalid player data corrupted saved progress.");
    }

    private static string PythonPath()
    {
        var python = Path.Combine(_workspace, "runtime", "python", "python.exe");
        if (!File.Exists(python)) throw new SkipException("Bundled Python runtime is not available.");
        return python;
    }

    private static void AvailableResolutions()
    {
        using var data=JsonDocument.Parse("""
            {"formats":[{"height":720,"vcodec":"vp9"},{"height":1080,"vcodec":"avc1"},{"height":720,"vcodec":"avc1"},
            {"height":90,"vcodec":"images"},{"height":1080,"vcodec":"none"},{"height":2160,"vcodec":"av01","has_drm":true},
            {"height":null,"vcodec":"vp9"},{"height":0,"vcodec":"vp9"},{"height":144,"vcodec":"avc1"},{"height":2160,"vcodec":"av01"}]}
            """);
        Assert(YouTubeService.ParseResolutions(data.RootElement).SequenceEqual(new[]{144,720,1080,2160}),"Unavailable or duplicate format heights appeared.");
    }
    private static void AutoPlayQueue()
    {
        VideoItem[] items=[new(){Id="duplicate"},new(){Kind="playlist"},new(){MembersOnly=true},new(){Id="duplicate"},new(){Kind="channel"}];
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(items,0,true)==3,"Next item lost an intentional repeated video or included a container/member.");
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(items,0,false)==2,"Visible members item was skipped.");
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(items,3,true)==-1,"End of playlist wrapped or chose a channel.");
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(items,-1,true)==0,"Play list did not begin at its first video.");
        VideoItem[] leadingUnavailable=[new(){Kind="playlist"},new(){MembersOnly=true},new(){Kind="channel"},new(){Id="first-playable"}];
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(leadingUnavailable,-1,true)==3,"Play list included a leading container or hidden member.");
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(leadingUnavailable,-1,false)==1,"Play list skipped a visible leading member.");
        Assert(FkYouTube.Playback.PlaybackQueue.NextIndex([], -1,true)==-1,"Empty list produced a playable position.");
        foreach(var invalidStart in new[]{int.MinValue,-2,items.Length,int.MaxValue})
            Assert(FkYouTube.Playback.PlaybackQueue.NextIndex(items,invalidStart,true)==-1,"Invalid queue start produced a playable position.");
    }

    private static async Task OwnedProcessArguments()
    {
        using var sandbox = new Sandbox();
        var script = Path.Combine(sandbox.Root, "argv-fixture.py");
        File.WriteAllText(script, "import sys,json\nsys.stdin.reconfigure(encoding='utf-8')\nsys.stdout.reconfigure(encoding='utf-8')\nprint(json.dumps({'args':sys.argv[1:],'line':sys.stdin.readline().rstrip('\\r\\n')},ensure_ascii=False),flush=True)\n", new UTF8Encoding(false));
        string[] expected = ["", "space value", "embedded\"quote", "C:\\trailing\\", "C:\\space path\\", "한글 경로 값", "back\\\"quote"];
        using var process = OwnedProcess.Start(PythonPath(), new[] { "-u", script }.Concat(expected).ToArray(), sandbox.Root);
        await process.StandardInput.WriteLineAsync("가짜 UTF-8 입력");
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert(line is not null, "Owned child produced no stdout.");
        using var result = JsonDocument.Parse(line!);
        var actual = result.RootElement.GetProperty("args").EnumerateArray().Select(element => element.GetString()).ToArray();
        Assert(actual.SequenceEqual(expected), "Windows argument quoting changed an argument.");
        Assert(result.RootElement.GetProperty("line").GetString() == "가짜 UTF-8 입력", "UTF-8 stdin/stdout was corrupted.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token);
        Assert(process.ExitCode == 0, "Owned fixture child failed.");
    }

    private static async Task OwnedProcessTree()
    {
        using var sandbox = new Sandbox();
        var script = Path.Combine(sandbox.Root, "tree-fixture.py");
        File.WriteAllText(script, "import sys,json,os,subprocess,time\nchild=subprocess.Popen([sys.executable,'-c','import time;time.sleep(120)'])\nprint(json.dumps({'parent':os.getpid(),'child':child.pid}),flush=True)\ntime.sleep(120)\n", new UTF8Encoding(false));
        // Keep the child's current-directory handle outside the disposable fixture.
        // Process termination is asserted below; delayed directory-handle release
        // must not turn a successful job-tree check into a cleanup failure.
        var process = OwnedProcess.Start(PythonPath(), ["-u", script], _workspace);
        var parentId = process.Id;
        var grandchildId = 0;
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert(line is not null, "Owned tree fixture produced no PID snapshot.");
            using var ids = JsonDocument.Parse(line!);
            grandchildId = ids.RootElement.GetProperty("child").GetInt32();
            Assert(ids.RootElement.GetProperty("parent").GetInt32() == parentId, "Owned child PID did not match.");
            var jobMembers = process.GetProcessIds();
            Assert(jobMembers.Contains(parentId) && jobMembers.Contains(grandchildId), "Grandchild was outside the owned job.");
            Assert(IsAlive(parentId) && IsAlive(grandchildId), "Fixture processes exited before disposal.");
        }
        finally
        {
            process.Dispose();
            // An assertion inside the try must still wait for Windows to release
            // the fixture's working directory, or cleanup masks the real failure.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(8) && (IsAlive(parentId) || IsAlive(grandchildId)))
                await Task.Delay(50);
        }
        Assert(!IsAlive(parentId) && !IsAlive(grandchildId), "Disposal left an owned child or grandchild running.");
        process.Dispose();
    }

    private static bool IsAlive(int id)
    {
        if (id == 0) return false;
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class SkipException(string message) : Exception(message) { }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Sandbox()
        {
            Root = Path.GetFullPath(Path.Combine(_workspace, "artifacts", "csharp-tests", "run-" + Guid.NewGuid().ToString("N")));
            Assert(Root.StartsWith(Path.Combine(Path.GetFullPath(_workspace), "artifacts") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Test artifact path escaped the workspace.");
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
