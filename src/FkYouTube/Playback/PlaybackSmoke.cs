using System.Diagnostics;
using System.Text.Json;

namespace FkYouTube.Playback;

/// <summary>Opt-in integration probe. No account, browser, cookies, or history writes are used.</summary>
public static class PlaybackSmoke
{
    public static async Task RunAsync(MpvPlayerService player, nint host, string outputdir, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputdir);
        Directory.CreateDirectory(outputdir);
        var started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var errors = new List<string>();
        var properties = new Dictionary<string, object?>();
        var observedPids = new Dictionary<int, DateTime>();
        var metrics=new List<object>();
        using var sampling=new CancellationTokenSource();
        var sampleTask=Task.Run(async()=>
        {
            var previous=new Dictionary<int,(double Cpu,double Time)>();
            try
            {
                while(!sampling.IsCancellationRequested)
                {
                    var now=elapsed.Elapsed.TotalSeconds;long rss=0,privateBytes=0;double cpu=0;var count=0;
                    foreach(var pid in ProcessSupport.OwnedProcess.GetAllOwnedProcessIds().Append(Environment.ProcessId).Distinct())
                    {
                        try
                        {
                            using var process=Process.GetProcessById(pid);var total=process.TotalProcessorTime.TotalSeconds;
                            if(previous.TryGetValue(pid,out var old) && now>old.Time)cpu+=Math.Max(0,total-old.Cpu)/(now-old.Time)/Environment.ProcessorCount*100;
                            previous[pid]=(total,now);rss+=process.WorkingSet64;privateBytes+=process.PrivateMemorySize64;count++;
                        }
                        catch(ArgumentException){}catch(InvalidOperationException){}
                    }
                    var current=player.Snapshot;
                    metrics.Add(new{elapsedSeconds=now,workingSetBytes=rss,privateBytes,cpuPercentOfMachine=cpu,processCount=count,position=current.Position,loaded=current.Loaded});
                    await Task.Delay(250,sampling.Token);
                }
            }
            catch(OperationCanceledException){}
        });
        PlaybackSnapshot? playing = null;
        string? failure = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        void OnError(PlaybackFailure failure) { lock (errors) errors.Add(failure.Message); }
        player.PlaybackError += OnError;
        try
        {
            await player.StartAsync(host);
            await player.CommandAsync(token,"set_property","volume",0);
            CaptureProcesses(player, observedPids);
            await player.LoadAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ", null, 1080, 0, token);
            var generation = player.Snapshot.MediaGeneration;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                CaptureProcesses(player, observedPids);
                var snapshot = player.Snapshot;
                if (snapshot.MediaGeneration == generation && snapshot.Loaded && !snapshot.Ended && snapshot.Position > 1)
                { playing = snapshot; break; }
                if (snapshot.MediaGeneration == generation && snapshot.Ended) throw new InvalidOperationException("Test video ended or failed before position advanced beyond one second.");
                await Task.Delay(100, token);
            }
            checks.Add(new("real-playback-position", true, "> 1 second while loaded", playing.Position));
            await player.CommandAsync(token, "set_property", "pause", true);
            var paused = await player.GetPropertyAsync<bool>("pause", token);
            checks.Add(new("pause", paused, true, paused));

            var duration = await player.GetPropertyAsync<double>("duration", token);
            checks.Add(new("duration", duration > 0, "> 0", duration));
            var decoder = await player.GetPropertyAsync<string>("hwdec-current", token);
            properties["hwdec-current"] = decoder;
            checks.Add(new("decoder-reported", !string.IsNullOrWhiteSpace(decoder), "hardware decoder name or software fallback 'no'", decoder));
            // Hardware decode capability is recorded, not a mandatory assertion for every GPU/codec.
            properties["hardwareDecodeActive"] = !string.IsNullOrWhiteSpace(decoder) && decoder != "no";
            foreach (var name in new[] { "video-codec", "audio-codec", "video-params", "audio-params", "frame-drop-count", "decoder-frame-drop-count", "avsync", "demuxer-cache-duration" })
            {
                try { properties[name] = await player.GetPropertyAsync<JsonElement>(name, token); }
                catch (InvalidOperationException ex) { properties[name] = new { available = false, error = ex.Message }; }
            }
            checks.Add(new("video-codec", HasString(properties, "video-codec"), "nonempty codec", properties.GetValueOrDefault("video-codec")));
            checks.Add(new("audio-codec", HasString(properties, "audio-codec"), "nonempty codec", properties.GetValueOrDefault("audio-codec")));

            var seekTarget = Math.Min(2, Math.Max(.25, duration / 3));
            await player.CommandAsync(token, "seek", seekTarget, "absolute+exact");
            double sought = 0;
            var seekDeadline = Stopwatch.StartNew();
            while (seekDeadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                token.ThrowIfCancellationRequested();
                sought = await player.GetPropertyAsync<double>("time-pos", token);
                var seeking = await player.GetPropertyAsync<bool>("seeking", token);
                if (!seeking && Math.Abs(sought - seekTarget) < .35) break;
                await Task.Delay(100, token);
            }
            checks.Add(new("seek", Math.Abs(sought - seekTarget) < .35, seekTarget, sought));
            await player.CommandAsync(token, "set_property", "speed", 1.25);
            var speed = await player.GetPropertyAsync<double>("speed", token);
            checks.Add(new("speed", Math.Abs(speed - 1.25) < .001, 1.25, speed));
            await player.CommandAsync(token, "set_property", "volume", 33);
            var volume = await player.GetPropertyAsync<double>("volume", token);
            checks.Add(new("volume", Math.Abs(volume - 33) < .01, 33, volume));
            await player.CommandAsync(token,"set_property","volume",0);
            await player.CommandAsync(token, "set_property", "pause", false);
            var resumed = await player.GetPropertyAsync<bool>("pause", token);
            checks.Add(new("resume", !resumed, false, resumed));
            while(elapsed.Elapsed<TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                if(player.Snapshot.Ended)throw new InvalidOperationException("Playback ended during the 30-second process sampling window.");
                CaptureProcesses(player,observedPids);await Task.Delay(250,token);
            }
            foreach(var name in new[]{"frame-drop-count","decoder-frame-drop-count","avsync","demuxer-cache-duration"})
                properties[name]=await player.GetPropertyAsync<JsonElement>(name,token);
            CaptureProcesses(player, observedPids);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException && !ct.IsCancellationRequested ? "Playback smoke exceeded its 90 second deadline." : ex.Message;
            checks.Add(new("completion", false, "all control checks completed", failure));
        }
        finally
        {
            try { CaptureProcesses(player, observedPids); } catch (Exception ex) { failure ??= ex.Message; }
            try { await player.StopAsync(); } catch (Exception ex) { failure ??= "Stop failed: " + ex.Message; }
            sampling.Cancel();await sampleTask;
            player.PlaybackError -= OnError;
        }

        // Wait briefly for observed job members to disappear, using creation time to avoid PID reuse.
        var cleanupTimer = Stopwatch.StartNew();
        var remaining = RemainingProcesses(observedPids);
        while (remaining.Count != 0 && cleanupTimer.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100, CancellationToken.None);
            remaining = RemainingProcesses(observedPids);
        }
        checks.Add(new("observed-job-processes-exited", remaining.Count == 0, 0, remaining));
        string[] errorCopy;
        lock (errors) errorCopy = errors.ToArray();
        var report = new
        {
            startedUtc = started, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            testVideo = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            authenticated = false, markWatched = false,
            passed = failure is null && checks.All(c => c.Passed), failure,
            checks, playingSnapshot = playing, finalSnapshot = player.Snapshot, properties,
            observedOwnedProcessIds = observedPids.Keys.ToArray(), remainingObservedProcessIds = remaining,
            playbackErrors = errorCopy,metrics,
            limits = new[]
            {
                "HW decode selection and software fallback are reported; no hardware support is assumed.",
                "Checks exercise native player commands and decoded time, not mouse, keyboard focus, DPI, fullscreen, or visual rendering inspection.",
                "Cleanup checks only observed members of the app-owned job; transient processes between samples may not be listed.",
                "This probe does not access a browser, a user cookie file, account feeds, comments, or YouTube watch history."
                ,"CPU is normalized to the machine's logical cores; working sets sum shared pages. 250ms samples can miss very brief peaks. No browser baseline or GPU-engine percentage is measured."
            }
        };
        await File.WriteAllTextAsync(Path.Combine(outputdir, "playback-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
    }

    private static bool HasString(Dictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value) && value is JsonElement { ValueKind: JsonValueKind.String } element && !string.IsNullOrWhiteSpace(element.GetString());

    private static void CaptureProcesses(MpvPlayerService player, Dictionary<int, DateTime> observed)
    {
        foreach (var pid in player.OwnedProcessIds)
        {
            if (observed.ContainsKey(pid)) continue;
            try { using var process = Process.GetProcessById(pid); observed[pid] = process.StartTime.ToUniversalTime(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static List<int> RemainingProcesses(Dictionary<int, DateTime> observed)
    {
        var result = new List<int>();
        foreach (var pair in observed)
        {
            try
            {
                using var process = Process.GetProcessById(pair.Key);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == pair.Value) result.Add(pair.Key);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return result;
    }

    private sealed record SmokeCheck(string Name, bool Passed, object? Expected, object? Actual);
}
