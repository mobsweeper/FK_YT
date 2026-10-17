using System.Text.Json;
using System.Text.RegularExpressions;

namespace FkYouTube.Services;

internal static class ToolchainStore
{
    private static readonly PendingCleanupTracker StageCleanups = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimePaths, HashSet<ToolchainSelection>> InvalidSelections = new();
    internal static bool IsKnownInvalid(RuntimePaths paths, ToolchainSelection selection)
    {
        var invalid = InvalidSelections.GetOrCreateValue(paths);
        lock (invalid) return invalid.Contains(selection);
    }
    private static void RememberInvalid(RuntimePaths paths, ToolchainSelection selection)
    {
        var invalid = InvalidSelections.GetOrCreateValue(paths);
        lock (invalid) invalid.Add(selection);
    }
    internal static string StoreRoot(RuntimePaths paths) => Path.Combine(paths.Data, "tools");
    internal static string VersionsRoot(RuntimePaths paths) => Path.Combine(StoreRoot(paths), "versions");
    internal static string StagingRoot(RuntimePaths paths) => Path.Combine(StoreRoot(paths), "staging");
    private static string StatePath(RuntimePaths paths) => Path.Combine(StoreRoot(paths), "state.json");
    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
    internal static FileStream AcquireLock(RuntimePaths paths)
    {
        var root = StoreRoot(paths);
        FileBoundary.EnsureRegularPath(root);
        Directory.CreateDirectory(root);
        FileBoundary.EnsureRegularPath(root);
        var path = Path.Combine(root, "update.lock");
        FileBoundary.EnsureRegularPath(path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { FileBoundary.EnsureRegularPath(path); return stream; }
        catch { stream.Dispose(); throw; }
    }
    internal static ToolchainState ReadState(RuntimePaths paths)
    {
        var path = StatePath(paths);
        if (!File.Exists(path)) { FileBoundary.EnsureRegularPath(path); return new(); }
        var state = ReadJson<ToolchainState>(path, 65536);
        if (state.Schema != 1 || state.Active is null) throw new InvalidDataException("The tool update state is not supported.");
        ValidateSelection(state.Active);
        if (state.Pending is not null) ValidateSelection(state.Pending);
        if (state.Previous is not null) ValidateSelection(state.Previous);
        if (state.ActivationRollback is not null) ValidateSelection(state.ActivationRollback);
        return state;
    }
    internal static void ValidateSelection(ToolchainSelection selection)
    {
        ValidateId(selection.YtDlp, "yt-dlp");
        ValidateId(selection.Deno, "deno");
    }
    private static void ValidateId(string? value, string kind)
    {
        if (value is not null && !Regex.IsMatch(value, "^" + kind + @"-\d+(?:\.\d+){1,3}-[a-f0-9]{32}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The tool update selection is invalid.");
    }
    internal static void WriteState(RuntimePaths paths, ToolchainState state) => AtomicJson(StatePath(paths), state);
    internal static void AtomicJson<T>(string path, T value)
    {
        FileBoundary.EnsureRegularPath(path);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".new-" + Guid.NewGuid().ToString("N"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            FileBoundary.EnsureRegularPath(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            FileBoundary.EnsureRegularPath(path);
            FileBoundary.EnsureRegularPath(temporary);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { FileBoundary.EnsureRegularPath(temporary); File.Delete(temporary); } }
    }
    private static T ReadJson<T>(string path, int bound)
    {
        try
        {
            var bytes = FileBoundary.ReadBoundedFile(path, bound);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                throw new InvalidDataException("The tool update record has an unsupported schema.");
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("The tool update record is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("The tool update record could not be read. The original file was preserved.", ex); }
    }
    internal static (string Python, string YtDlp, string Deno, string Ejs) BundledVersions(RuntimePaths paths)
    {
        try
        {
            using var document = JsonDocument.Parse(FileBoundary.ReadBoundedFile(Path.Combine(paths.Root, "dependencies.lock.json"), 1024 * 1024));
            var components = document.RootElement.GetProperty("components");
            string Read(string name) => components.GetProperty(name).GetProperty("version").GetString() ?? "unknown";
            return (Read("python"), Read("ytDlp"), Read("deno").TrimStart('v'), Read("ytDlpEjs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return ("unknown", "unknown", "unknown", "unknown"); }
    }
    private static RuntimeToolchainSnapshot Bundled(RuntimePaths paths)
    {
        var versions = BundledVersions(paths);
        return new(paths.BundledYtDlp, paths.BundledDeno, null, versions.YtDlp, versions.Deno, versions.Ejs);
    }
    internal static ToolchainManifest ReadManifest(RuntimePaths paths, string id, string tool, bool verify = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(id, tool);
        var directory = ToolchainValidation.ChildPath(VersionsRoot(paths), id);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var attributes = File.GetAttributes(manifestPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
            throw new InvalidDataException("The installed tool manifest path is a directory.");
        var manifest = ReadJson<ToolchainManifest>(manifestPath, 8 * 1024 * 1024);
        if (manifest.Schema != 1 || manifest.Tool != tool || manifest.Files is not { Count: > 0 and <= 15000 } || manifest.Assets is not { Count: > 0 and <= 10 })
            throw new InvalidDataException("The installed tool manifest is invalid.");
        var normalized = ToolchainValidation.NormalizeVersion(manifest.Version);
        if (!id.StartsWith(tool + "-" + normalized + "-", StringComparison.Ordinal)) throw new InvalidDataException("The installed tool version does not match its directory.");
        var pathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null) throw new InvalidDataException("The installed tool file manifest contains an empty record.");
            var relative = ToolchainValidation.SafeRelativePath(file.Path);
            if (!pathsSeen.Add(relative) || file.Size < 0 || file.Size > 250L * 1024 * 1024 || file.Sha256 is null || !Regex.IsMatch(file.Sha256, @"^[a-f0-9]{64}\z"))
                throw new InvalidDataException("The installed tool file manifest is invalid.");
            total = checked(total + file.Size);
            if (total > 600L * 1024 * 1024) throw new InvalidDataException("The installed tool exceeds its size limit.");
        }
        if (!pathsSeen.Contains(tool == "deno" ? "deno.exe" : "yt-dlp.exe") ||
            tool == "yt-dlp" && (!pathsSeen.Contains("site-packages/yt_dlp/__init__.py") || !pathsSeen.Contains("site-packages/yt_dlp_ejs/__init__.py")))
            throw new InvalidDataException("The installed tool set is incomplete.");
        if (verify)
        {
            var inventory = ToolchainValidation.Inventory(directory, cancellationToken);
            if (inventory.Count != manifest.Files.Count) throw new InvalidDataException("The installed tool contains missing or untracked files.");
            var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            if (inventory.Any(file => !expected.TryGetValue(file.Path, out var old) || old.Size != file.Size || old.Sha256 != file.Sha256))
                throw new InvalidDataException("An installed tool file failed its SHA256 check.");
        }
        return manifest;
    }
    private static ToolchainManifest ReadSnapshotManifest(RuntimePaths paths, ToolchainSelection selection,
        string id, string tool, bool verify, CancellationToken cancellationToken)
    {
        try { return ReadManifest(paths, id, tool, verify, cancellationToken); }
        catch (Exception ex) when (verify && !cancellationToken.IsCancellationRequested &&
            (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException))
        {
            // Only proven installed metadata/content failures quarantine a set.
            // Sharing/permission errors, compatibility checks, probes, and journal
            // publication remain outside this boundary and can still be retried.
            RememberInvalid(paths, selection);
            throw;
        }
    }
    internal static RuntimeToolchainSnapshot Snapshot(RuntimePaths paths, ToolchainSelection selection, bool verify = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSelection(selection);
        var snapshot = Bundled(paths);
        string? requiresDeno = ">=2.6.6";
        if (selection.YtDlp is { } yt)
        {
            var manifest = ReadSnapshotManifest(paths, selection, yt, "yt-dlp", verify, cancellationToken);
            var directory = ToolchainValidation.ChildPath(VersionsRoot(paths), yt);
            ToolchainValidation.RequireVersion(manifest.RequiresPython, BundledVersions(paths).Python);
            requiresDeno = manifest.RequiresDeno;
            snapshot = snapshot with { YtDlpPath = Path.Combine(directory, "yt-dlp.exe"), PackagesPath = Path.Combine(directory, "site-packages"), YtDlpVersion = manifest.Version, EjsVersion = manifest.EjsVersion };
        }
        if (selection.Deno is { } deno)
        {
            var manifest = ReadSnapshotManifest(paths, selection, deno, "deno", verify, cancellationToken);
            snapshot = snapshot with { DenoPath = ToolchainValidation.ChildPath(VersionsRoot(paths), deno + "/deno.exe"), DenoVersion = manifest.Version };
        }
        if (snapshot.DenoVersion != "unknown") ToolchainValidation.RequireVersion(requiresDeno, snapshot.DenoVersion);
        return snapshot;
    }
    internal static async Task ValidateOfflineAsync(RuntimePaths paths, RuntimeToolchainSnapshot snapshot, CancellationToken ct,
        ICollection<ProcessStopReceipt>? stopReceipts = null)
    {
        ProcessStopReceipt Receipt()
        {
            var receipt = new ProcessStopReceipt();
            stopReceipts?.Add(receipt);
            return receipt;
        }
        var cli = await ProcessRunner.RunAsync(snapshot.YtDlpPath, ["--ignore-config", "--no-plugin-dirs", "--version"], paths.Root, TimeSpan.FromSeconds(30), ct, stopReceipt: Receipt()).ConfigureAwait(false);
        if (cli.ExitCode != 0 || ToolchainValidation.CompareVersions(cli.Output.Trim(), snapshot.YtDlpVersion) != 0)
            throw new InvalidDataException("The downloaded yt-dlp executable did not pass its version check.");
        var packages = snapshot.PackagesPath ?? Path.Combine(paths.Root, "runtime", "python", "Lib", "site-packages");
        var python = await ProcessRunner.RunAsync(paths.Python, ["-B", Path.Combine(paths.Root, "worker", "toolchain_bootstrap.py"), "--packages", packages, "--check", snapshot.YtDlpVersion, snapshot.EjsVersion ?? ""],
            paths.Root, TimeSpan.FromSeconds(30), ct, stopReceipt: Receipt()).ConfigureAwait(false);
        if (python.ExitCode != 0) throw new InvalidDataException("The downloaded yt-dlp Python/EJS set is incompatible with this app's workers.");
        using var checkedVersions = JsonDocument.Parse(python.Output);
        if (ToolchainValidation.CompareVersions(checkedVersions.RootElement.GetProperty("ytDlp").GetString()!, snapshot.YtDlpVersion) != 0 ||
            ToolchainValidation.CompareVersions(checkedVersions.RootElement.GetProperty("ejs").GetString()!, snapshot.EjsVersion!) != 0)
            throw new InvalidDataException("The checked Python package set did not match the update.");
        var deno = await ProcessRunner.RunAsync(snapshot.DenoPath, ["--version"], paths.Root, TimeSpan.FromSeconds(30), ct, stopReceipt: Receipt()).ConfigureAwait(false);
        var versionMatch = Regex.Match(deno.Output, @"^deno\s+(\d+\.\d+\.\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        if (deno.ExitCode != 0 || !versionMatch.Success || ToolchainValidation.CompareVersions(versionMatch.Groups[1].Value, snapshot.DenoVersion) != 0)
            throw new InvalidDataException("The downloaded Deno executable did not pass its version check.");
    }
    internal static Task<(RuntimeToolchainSnapshot Snapshot, string? Message)> InitializeAsync(RuntimePaths paths, CancellationToken ct) =>
        Task.Run(() => InitializeCoreAsync(paths, ct), ct);
    private static async Task<(RuntimeToolchainSnapshot Snapshot, string? Message)> InitializeCoreAsync(RuntimePaths paths, CancellationToken ct)
    {
        // A new portable installation does not create a tools tree until the
        // user first requests an update. No network is used during startup.
        if (!Directory.Exists(StoreRoot(paths))) return (Bundled(paths), null);
        try
        {
            using var fileLock = AcquireLock(paths);
            var state = ReadState(paths);
            string? message = null;
            if (state.ActivationInProgress)
            {
                // Older journals used Previous for the interrupted transaction's
                // rollback. New journals retain the last successful history too.
                state.Active = state.ActivationRollback ?? state.Previous ?? new();
                state.Pending = null; state.ActivationInProgress = false; state.ActivationRollback = null;
                WriteState(paths, state);
                message = "The interrupted tool activation was rolled back.";
            }
            RuntimeToolchainSnapshot current;
            try { current = Snapshot(paths, state.Active, cancellationToken: ct); }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                current = Bundled(paths);
                var fallback = new ToolchainSelection();
                if (state.Previous is not null)
                    try { current = Snapshot(paths, state.Previous, cancellationToken: ct); fallback = state.Previous; }
                    catch (Exception previous) when (IsRecoverable(previous)) { }
                state.Active = fallback; state.Pending = null; state.ActivationInProgress = false; state.ActivationRollback = null;
                WriteState(paths, state);
                message = "The selected tool set failed verification. The previous verified or bundled tools were restored.";
            }
            if (state.Pending == state.Active)
            {
                // Repeating a restore, or canceling a prepared update by selecting
                // the current set, must not replace useful rollback history.
                state.Pending = null;
                WriteState(paths, state);
            }
            if (state.Pending is { } pending)
            {
                var previous = state.Active;
                var previousRollback = state.Previous;
                // A crash between the journal write and completion is detected
                // on the next start; existing tool directories remain immutable.
                state.ActivationRollback = previous; state.Active = pending; state.Pending = null; state.ActivationInProgress = true;
                WriteState(paths, state);
                try
                {
                    var candidate = Snapshot(paths, pending, cancellationToken: ct);
                    await ValidateOfflineAsync(paths, candidate, ct).ConfigureAwait(false);
                    state.Previous = previous; state.ActivationInProgress = false; state.ActivationRollback = null;
                    WriteState(paths, state);
                    current = candidate;
                    message = "The downloaded tools are active. Online YouTube compatibility remains experimental.";
                }
                catch (Exception ex) when (IsRecoverable(ex) || ex is OperationCanceledException)
                {
                    state.Active = previous; state.Previous = previousRollback; state.ActivationInProgress = false; state.ActivationRollback = null;
                    WriteState(paths, state);
                    message = "The prepared tool set failed startup checks. The previous tools were restored.";
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                }
            }
            return (current, message);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Preserve an unreadable state file instead of resetting it silently.
            return (Bundled(paths), "The tool update state could not be verified. Bundled tools are in use; the update records were preserved.");
        }
    }
    internal static bool IsRecoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OverflowException or FormatException or KeyNotFoundException or System.ComponentModel.Win32Exception;
    internal static void PreserveInvalidState(RuntimePaths paths)
    {
        var path = StatePath(paths);
        if (!File.Exists(path)) return;
        var backup = Path.Combine(StoreRoot(paths), "state-preserved-" + Guid.NewGuid().ToString("N") + ".json");
        FileBoundary.EnsureRegularPath(path);
        FileBoundary.EnsureRegularPath(backup);
        File.Move(path, backup); // same-directory rename preserves even an oversized invalid journal
    }
    internal static void DeleteStage(RuntimePaths paths, string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StagingRoot(paths))) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(prefix))
            throw new InvalidOperationException("Only this transaction's staging folder may be removed.");
        if (!Directory.Exists(full)) return;
        // Deletion requires regular paths, not readable or valid update payloads.
        // Avoid hashing partial downloads just to discard their contents.
        var directories = new Stack<string>();
        directories.Push(full);
        while (directories.TryPop(out var folder))
        {
            FileBoundary.EnsureRegularPath(folder);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                FileBoundary.EnsureRegularPath(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
        FileBoundary.EnsureRegularPath(full);
        Directory.Delete(full, true);
    }
    internal static Task QueueStageCleanup(RuntimePaths paths, string stage, IReadOnlyList<ProcessStopReceipt> receipts)
    {
        // Capture only this transaction's exact path and its probe receipts.
        // A late Windows exit must precede any attempt to delete probe files.
        var cleanup = CleanupAfterStopAsync();
        StageCleanups.Track(cleanup);
        return cleanup;
        async Task CleanupAfterStopAsync()
        {
            await Task.WhenAll(receipts.Select(receipt => receipt.Completion)).ConfigureAwait(false);
            int[] delays = [50, 100, 200];
            for (var attempt = 0; ; attempt++)
            {
                try { DeleteStage(paths, stage); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == delays.Length) throw new IOException("Could not remove the tool update staging folder after its probes stopped.", ex);
                }
                await Task.Delay(delays[attempt]).ConfigureAwait(false);
            }
        }
    }
    internal static Task DrainStageCleanupAsync(TimeSpan timeout) => StageCleanups.DrainAsync(timeout,
        "Some tool update staging cleanup is still pending or could not be completed.");
}
