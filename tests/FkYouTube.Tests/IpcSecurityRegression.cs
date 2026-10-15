using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using FkYouTube;
using FkYouTube.Playback;
using FkYouTube.ProcessSupport;
using FkYouTube.Services;

internal static class IpcSecurityRegression
{
    public static async Task RunAsync(string workspace)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "artifacts", "ipc-regression", "run-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var python = Path.Combine(workspace, "runtime", "python", "python.exe");
        var failures = new List<string>();
        async Task Check(string name, Func<Task> check)
        {
            try { await check(); }
            catch (Exception ex) { failures.Add(name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }
        try
        {
            await Check("bounded process stdout", () => ProcessOutputLimitAsync(python, root, false));
            await Check("bounded process stderr", () => ProcessOutputLimitAsync(python, root, true));
            await Check("bounded feed line", () => FeedOutputLimitAsync(python, root));
            await Check("feed JSON type", () => FeedWrongTypeAsync(python, root));
            await Check("recommendation malformed connection cleanup", () => RecommendationCleanupAsync(python, root));
            await Check("mpv JSON array", () => MpvMalformedMessageAsync(python, root, "[]"));
            await Check("mpv request ID type", () => MpvMalformedMessageAsync(python, root, "{\"request_id\":\"wrong-type\"}"));
            await Check("mpv event type", () => MpvMalformedMessageAsync(python, root, "{\"event\":4}"));
            await Check("mpv broken connection process cleanup", () => MpvMalformedMessageAsync(python, root, "[]", true));
            await Check("feed item count limit", () => FeedItemLimitAsync(python, root));
        }
        finally
        {
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) when (attempt < 30) { await Task.Delay(100); }
                }
            }
            catch (Exception ex) { failures.Add("fixture cleanup: " + ex.GetType().Name); }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static async Task ProcessOutputLimitAsync(string python, string root, bool error)
    {
        var limit = error ? 2 * 1024 * 1024 : 32 * 1024 * 1024;
        // The finite fixture remains alive after exceeding the cap. The caller must
        // observe the reader failure and stop its job without waiting for timeout.
        var code = "import sys,time; sys." + (error ? "stderr" : "stdout") + ".write('x'*" + (limit + 1) + "); sys." +
            (error ? "stderr" : "stdout") + ".flush(); time.sleep(5)";
        var timer = Stopwatch.StartNew();
        await ExpectIOExceptionAsync(() => ProcessRunner.RunAsync(python, ["-u", "-c", code], root, TimeSpan.FromSeconds(12)),
            "Unbounded process output completed instead of being rejected.");
        Assert(timer.Elapsed < TimeSpan.FromSeconds(4), "A failed output reader left the process running until exit/timeout.");
    }

    private static FeedFixtureConnection FeedFixture(string python, string root, string response)
    {
        var paths = new RuntimePaths(root);
        var process = OwnedProcess.Start(python, ["-u", "-c",
            "import json,sys,time; r=json.loads(sys.stdin.readline()); " + response + "; time.sleep(30)"], root);
        var feed = new FeedService(paths);
        typeof(FeedService).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(feed, process);
        var observer = Process.GetProcessById(process.Id);
        _ = observer.SafeHandle;
        return new FeedFixtureConnection(feed, observer);
    }

    private static async Task FeedOutputLimitAsync(string python, string root)
    {
        await using var fixture = FeedFixture(python, root,
            "print(json.dumps({'requestId':r['requestId'],'generation':r['generation'],'type':'done','hasMore':False,'ignored':'x'*(4*1024*1024+1)}))");
        var feed = fixture.Feed;
        await ExpectIOExceptionAsync(() => feed.ReadAsync(false, "home", 1, null, _ => { }, CancellationToken.None),
            "An oversized valid feed response was accepted.");
    }

    private static async Task FeedWrongTypeAsync(string python, string root)
    {
        await using var fixture = FeedFixture(python, root, "print('[]')");
        var feed = fixture.Feed;
        await ExpectIOExceptionAsync(() => feed.ReadAsync(false, "home", 1, null, _ => { }, CancellationToken.None),
            "A wrong-type worker response did not become a controlled protocol failure.");
    }

    private static async Task RecommendationCleanupAsync(string python, string root)
    {
        await using var fixture = FeedFixture(python, root,
            "print(json.dumps({'requestId':r['requestId'],'type':'feedback.result','success':False,'message':42}))");
        var feed = fixture.Feed;
        var rejected = false;
        try { await feed.RecommendAsync(1, new VideoItem { Id = "fixture" }, "dismiss", "fixture", null, CancellationToken.None); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "A wrong-type recommendation response did not become an IOException.");
        Assert(typeof(FeedService).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feed) is null,
            "A broken recommendation worker remained attached after protocol failure.");
    }

    private static async Task FeedItemLimitAsync(string python, string root)
    {
        await using var fixture = FeedFixture(python, root,
            "[print(json.dumps({'requestId':r['requestId'],'generation':r['generation'],'type':'item','item':{'id':str(i)}})) for i in range(91)]; " +
            "print(json.dumps({'requestId':r['requestId'],'generation':r['generation'],'type':'done','hasMore':False}))");
        var feed = fixture.Feed;
        var delivered = 0;
        await ExpectIOExceptionAsync(() => feed.ReadAsync(false, "home", 1, null, _ => delivered++, CancellationToken.None),
            "A worker exceeded the requested feed batch size.");
        Assert(delivered == 90, "The feed cap did not preserve exactly the requested first 90 items.");
    }

    private static async Task MpvMalformedMessageAsync(string python, string root, string message, bool requireProcessExit = false)
    {
        var pipeName = "fkyt-ipc-fixture-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connect = client.ConnectAsync(5000);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await connect;
        var process = OwnedProcess.Start(python, ["-c", "import time; time.sleep(30)"], root);
        using var observer = Process.GetProcessById(process.Id);
        _ = observer.SafeHandle;
        await using var player = new MpvPlayerService(new RuntimePaths(root));
        try
        {
            var sessionType = typeof(MpvPlayerService).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [process, client], null)!;
            sessionType.GetField("Reader")!.SetValue(session, new StreamReader(client, new UTF8Encoding(false), false, 4096, true));
            typeof(MpvPlayerService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, session);
            var errorCount = 0;
            player.PlaybackError += _ => Interlocked.Increment(ref errorCount);
            var exit = (Task)typeof(MpvPlayerService).GetMethod("WatchExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, [session])!;
            sessionType.GetField("ExitTask")!.SetValue(session, exit);
            var read = (Task)typeof(MpvPlayerService).GetMethod("ReadMessagesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, [session])!;
            sessionType.GetField("ReadTask")!.SetValue(session, read);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(message);
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            if (requireProcessExit)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await exit.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(process.GetProcessIds().Count == 0, "A broken IPC session left children in its owned job.");
            }
            Assert(errorCount == 1, "Malformed mpv IPC did not produce exactly one controlled connection error.");
        }
        finally
        {
            await player.DisposeAsync();
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FeedFixtureConnection(FeedService feed, Process observer) : IAsyncDisposable
    {
        internal FeedService Feed => feed;
        public async ValueTask DisposeAsync()
        {
            await feed.DisposeAsync();
            try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { observer.Dispose(); }
        }
    }

    private static async Task ExpectIOExceptionAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
