using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FkYouTube.Services;

/// <summary>Manual, stable-only upstream downloads. Never changes the running set.</summary>
public sealed class ToolchainUpdateService
{
    private readonly RuntimePaths _paths;
    private readonly HttpClient _http;
    private readonly Dictionary<string, ToolchainUpdateInfo> _issued = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly HttpClient OfficialHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        Credentials = null, DefaultProxyCredentials = null, AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };
    public ToolchainUpdateService(RuntimePaths paths) : this(paths, OfficialHttp) { }
    internal ToolchainUpdateService(RuntimePaths paths, HttpClient http) { _paths = paths; _http = http; }

    public Task<ToolchainStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetStatus(cancellationToken), cancellationToken);
    private ToolchainStatus GetStatus(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundled = ToolchainStore.BundledVersions(_paths);
        var selected = _paths.Toolchain;
        string? pending = null;
        var canRollback = false;
        try
        {
            var state = ToolchainStore.ReadState(_paths);
            canRollback = state.Previous is not null && state.Previous != state.Active && !ToolchainStore.IsKnownInvalid(_paths, state.Previous);
            if (state.Pending is { } selection)
            {
                var next = ToolchainStore.Snapshot(_paths, selection, verify: false);
                pending = $"yt-dlp {next.YtDlpVersion}; Deno {next.DenoVersion}";
            }
        }
        catch (Exception ex) when (ToolchainStore.IsRecoverable(ex)) { }
        return new ToolchainStatus(selected?.YtDlpVersion ?? bundled.YtDlp,
            selected?.DenoVersion ?? bundled.Deno, pending, canRollback, _paths.ToolchainStartupMessage);
    }

    public async Task<ToolchainUpdateInfo> CheckAsync(ToolchainTool tool, CancellationToken cancellationToken = default)
    {
        if (tool is not (ToolchainTool.YtDlp or ToolchainTool.Deno)) throw new ArgumentOutOfRangeException(nameof(tool));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var ct = deadline.Token;
            var repo = tool == ToolchainTool.YtDlp ? "yt-dlp/yt-dlp" : "denoland/deno";
            using var release = await JsonAsync("https://api.github.com/repos/" + repo + "/releases/latest", ct).ConfigureAwait(false);
            var root = release.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("The upstream response is not a stable release.");
            var tag = RequiredString(root, "tag_name");
            _ = ToolchainValidation.NormalizeVersion(tag);
            if (tool == ToolchainTool.Deno && !tag.StartsWith('v') || tool == ToolchainTool.YtDlp && tag.StartsWith('v'))
                throw new InvalidDataException("Unexpected upstream stable release tag.");
            var releaseUrl = "https://github.com/" + repo + "/releases/tag/" + tag;
            var assets = new List<ToolchainAsset>();
            var executable = tool == ToolchainTool.YtDlp ? "yt-dlp.exe" : "deno-x86_64-pc-windows-msvc.zip";
            assets.Add(GitHubAsset(root, repo, tag, executable, tool == ToolchainTool.YtDlp ? "exe" : "deno-zip"));
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            string? ejs = null, pythonRequirement = null, denoRequirement = null;
            if (tool == ToolchainTool.YtDlp)
            {
                using var package = await JsonAsync("https://pypi.org/pypi/yt-dlp/" + tag + "/json", ct).ConfigureAwait(false);
                var info = package.RootElement.GetProperty("info");
                if (ToolchainValidation.CompareVersions(RequiredString(info, "version"), tag) != 0) throw new InvalidDataException("The upstream executable and Python releases do not match.");
                pythonRequirement = RequiredString(info, "requires_python");
                ToolchainValidation.RequireVersion(pythonRequirement, ToolchainStore.BundledVersions(_paths).Python);
                var requirements = info.GetProperty("requires_dist").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
                var pythonVersion = ToolchainStore.BundledVersions(_paths).Python;
                if (requirements.Any(item => ToolchainValidation.DependencyMarkerApplies(item, "", pythonVersion)))
                    throw new InvalidDataException("The new yt-dlp has required dependencies this app does not manage. Update FK_YT first.");
                var ejsRequirements = requirements.Where(item => Regex.IsMatch(item, @"^yt-dlp-ejs\s*(?:[<>=!~;]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    ToolchainValidation.DependencyMarkerApplies(item, "default", pythonVersion)).ToArray();
                var ejsVersions = ejsRequirements.Select(item => Regex.Match(item, @"^yt-dlp-ejs\s*==\s*(\d+(?:\.\d+){1,3})\s*;", RegexOptions.CultureInvariant))
                    .Select(match => match.Success ? match.Groups[1].Value : throw new InvalidDataException("The new EJS requirement needs a newer FK_YT app.")).Distinct(StringComparer.Ordinal).ToArray();
                if (ejsVersions.Length != 1) throw new InvalidDataException("The upstream EJS dependency is missing or ambiguous.");
                ejs = ejsVersions[0];
                var denoDependencies = requirements.Where(item => Regex.IsMatch(item, @"^deno\s*(?:[<>=!~;]|$)", RegexOptions.CultureInvariant) &&
                    ToolchainValidation.DependencyMarkerApplies(item, "deno", pythonVersion)).Distinct(StringComparer.Ordinal).ToArray();
                if (denoDependencies.Length != 1) throw new InvalidDataException("The upstream Deno requirement is missing or ambiguous.");
                var denoDependency = denoDependencies[0];
                denoRequirement = denoDependency["deno".Length..].Split(';')[0].Trim();
                if (!Regex.IsMatch(denoRequirement, @"^>=\s*\d+(?:\.\d+){1,3}\z", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("The new Deno requirement needs a newer FK_YT app.");
                assets.Add(PyPiWheel(package.RootElement, "yt_dlp", "wheel"));
                using var ejsPackage = await JsonAsync("https://pypi.org/pypi/yt-dlp-ejs/" + ejs + "/json", ct).ConfigureAwait(false);
                var ejsInfo = ejsPackage.RootElement.GetProperty("info");
                if (ToolchainValidation.CompareVersions(RequiredString(ejsInfo, "version"), ejs) != 0) throw new InvalidDataException("The upstream EJS metadata does not match.");
                ToolchainValidation.RequireVersion(RequiredString(ejsInfo, "requires_python"), ToolchainStore.BundledVersions(_paths).Python);
                if (ejsInfo.TryGetProperty("requires_dist", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array && dependencies.GetArrayLength() != 0)
                    throw new InvalidDataException("The new EJS has dependencies this app does not manage. Update FK_YT first.");
                assets.Add(PyPiWheel(ejsPackage.RootElement, "yt_dlp_ejs", "ejs-wheel"));
            }
            var candidate = new ToolchainUpdateInfo(tool, tag.TrimStart('v'), tool == ToolchainTool.YtDlp ? status.YtDlpVersion : status.DenoVersion,
                releaseUrl, assets, ejs, pythonRequirement, denoRequirement);
            lock (_issued)
            {
                // Bound cached metadata even if a user checks repeatedly.
                if (_issued.Count >= 16) _issued.Clear();
                _issued[candidate.Id] = candidate;
            }
            return candidate;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The official update check timed out. Try again later."); }
        finally { _gate.Release(); }
    }

    public Task StageAsync(ToolchainUpdateInfo update, IProgress<ToolchainUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => StageCoreAsync(update, progress, cancellationToken), cancellationToken);
    private async Task StageCoreAsync(ToolchainUpdateInfo update, IProgress<ToolchainUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_issued)
            if (!_issued.TryGetValue(update.Id, out var issued) || !ReferenceEquals(issued, update))
                throw new InvalidOperationException("Check the official release again before downloading.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        var probeReceipts = new List<ProcessStopReceipt>();
        try
        {
            using var fileLock = ToolchainStore.AcquireLock(_paths);
            var state = ToolchainStore.ReadState(_paths);
            if (state.ActivationInProgress) throw new IOException("A tool activation must be recovered by restarting the app first.");
            var selection = state.Pending ?? state.Active;
            var current = ToolchainStore.Snapshot(_paths, selection, cancellationToken: cancellationToken);
            if (update.Tool == ToolchainTool.YtDlp)
                ToolchainValidation.RequireVersion(update.RequiresDeno, current.DenoVersion);
            var tool = update.Tool == ToolchainTool.YtDlp ? "yt-dlp" : "deno";
            var id = tool + "-" + ToolchainValidation.NormalizeVersion(update.Version) + "-" + Guid.NewGuid().ToString("N");
            FileBoundary.EnsureRegularPath(ToolchainStore.StagingRoot(_paths));
            Directory.CreateDirectory(ToolchainStore.StagingRoot(_paths));
            stage = ToolchainValidation.ChildPath(ToolchainStore.StagingRoot(_paths), id);
            Directory.CreateDirectory(stage);
            var downloads = ToolchainValidation.ChildPath(stage, "downloads");
            Directory.CreateDirectory(downloads);
            foreach (var asset in update.Assets)
            {
                progress?.Report(new("Downloading " + asset.Name, 0, asset.Size));
                await DownloadAsync(asset, ToolchainValidation.ChildPath(downloads, asset.Name), progress, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(new("Verifying and extracting the official update"));
            if (update.Tool == ToolchainTool.YtDlp)
            {
                File.Move(ToolchainValidation.ChildPath(downloads, "yt-dlp.exe"), Path.Combine(stage, "yt-dlp.exe"));
                var packageRoot = Path.Combine(stage, "site-packages");
                foreach (var asset in update.Assets.Where(item => item.Kind is "wheel" or "ejs-wheel"))
                {
                    var packageName = asset.Kind == "wheel" ? "yt_dlp" : "yt_dlp_ejs";
                    var packageVersion = asset.Kind == "wheel" ? update.Version : update.EjsVersion!;
                    ToolchainValidation.ExtractArchive(Path.Combine(downloads, asset.Name), packageRoot, 160L * 1024 * 1024,
                        path => WheelPathAllowed(path, packageName, packageVersion), cancellationToken: cancellationToken);
                }
                current = current with { YtDlpPath = Path.Combine(stage, "yt-dlp.exe"), PackagesPath = packageRoot, YtDlpVersion = update.Version, EjsVersion = update.EjsVersion };
            }
            else
            {
                var asset = update.Assets.Single(item => item.Kind == "deno-zip");
                ToolchainValidation.ExtractArchive(Path.Combine(downloads, asset.Name), stage, 250L * 1024 * 1024, path => path == "deno.exe", 4, cancellationToken);
                current = current with { DenoPath = Path.Combine(stage, "deno.exe"), DenoVersion = update.Version };
                // An already prepared yt-dlp set can impose a newer Deno floor.
                if (selection.YtDlp is { } selectedYt)
                    ToolchainValidation.RequireVersion(ToolchainStore.ReadManifest(_paths, selectedYt, "yt-dlp", false).RequiresDeno, update.Version);
                else ToolchainValidation.RequireVersion(">=2.6.6", update.Version);
            }
            await SaveNoticesAsync(stage, update, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Running offline version and worker compatibility checks"));
            await ToolchainStore.ValidateOfflineAsync(_paths, current, cancellationToken, probeReceipts).ConfigureAwait(false);
            var manifest = new ToolchainManifest
            {
                Tool = tool, Version = update.Version, EjsVersion = update.EjsVersion, RequiresPython = update.RequiresPython,
                RequiresDeno = update.RequiresDeno, ReleaseUrl = update.ReleaseUrl, DownloadedAt = DateTimeOffset.UtcNow,
                Assets = update.Assets.ToList(), Files = ToolchainValidation.Inventory(stage, cancellationToken)
            };
            ToolchainStore.AtomicJson(Path.Combine(stage, "manifest.json"), manifest);
            cancellationToken.ThrowIfCancellationRequested();
            FileBoundary.EnsureRegularPath(ToolchainStore.VersionsRoot(_paths));
            Directory.CreateDirectory(ToolchainStore.VersionsRoot(_paths));
            var installed = ToolchainValidation.ChildPath(ToolchainStore.VersionsRoot(_paths), id);
            Directory.Move(stage, installed);
            stage = null;
            // Only this small journal is mutable. A failure here leaves an
            // unselected complete version, never a partially activated runtime.
            state.Pending = update.Tool == ToolchainTool.YtDlp ? selection with { YtDlp = id } : selection with { Deno = id };
            ToolchainStore.WriteState(_paths, state);
            progress?.Report(new("Verified update prepared. Restart FK_YT to apply it."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The official tool download or verification timed out. The running tools were preserved."); }
        finally
        {
            if (stage is not null)
            {
                var cleanup = ToolchainStore.QueueStageCleanup(_paths, stage, probeReceipts);
                if (probeReceipts.All(receipt => receipt.ConsumersStopped))
                    try { await cleanup.ConfigureAwait(false); } catch (Exception ex) when (ToolchainStore.IsRecoverable(ex)) { }
            }
            _gate.Release();
        }
    }
    public Task RestoreBundledAsync(CancellationToken cancellationToken = default) => ScheduleAsync(true, cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken = default) => ScheduleAsync(false, cancellationToken);
    public static Task DrainStageCleanupAsync() => ToolchainStore.DrainStageCleanupAsync(TimeSpan.FromSeconds(5));
    private Task ScheduleAsync(bool bundled, CancellationToken ct) => Task.Run(() => ScheduleCoreAsync(bundled, ct), ct);
    private async Task ScheduleCoreAsync(bool bundled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fileLock = ToolchainStore.AcquireLock(_paths);
            ToolchainState state;
            try { state = ToolchainStore.ReadState(_paths); }
            catch (Exception ex) when (bundled && ToolchainStore.IsRecoverable(ex))
            { ToolchainStore.PreserveInvalidState(_paths); state = new(); }
            if (state.ActivationInProgress) throw new IOException("A tool activation must be recovered by restarting the app first.");
            var selection = bundled ? new ToolchainSelection() : state.Previous ?? throw new InvalidOperationException("No previous tool set is available.");
            _ = ToolchainStore.Snapshot(_paths, selection, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            state.Pending = selection;
            state.ActivationInProgress = false;
            ToolchainStore.WriteState(_paths, state);
        }
        finally { _gate.Release(); }
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new InvalidDataException("The official update metadata is incomplete.");
        return value;
    }
    private static ToolchainAsset GitHubAsset(JsonElement release, string repo, string tag, string name, string kind)
    {
        var matches = release.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The official Windows x64 update asset is missing or ambiguous.");
        var asset = matches[0];
        var url = RequiredString(asset, "browser_download_url");
        if (url != "https://github.com/" + repo + "/releases/download/" + tag + "/" + name)
            throw new InvalidDataException("The official release asset URL did not match its repository and version.");
        var digest = RequiredString(asset, "digest");
        if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The official asset has no supported SHA256 digest. No update was installed.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > (kind == "exe" ? 100L : 200L) * 1024 * 1024) throw new InvalidDataException("The official update exceeds its size limit.");
        return new(name, url, digest[7..].ToLowerInvariant(), size, kind);
    }
    private static ToolchainAsset PyPiWheel(JsonElement package, string packageName, string kind)
    {
        var matches = package.GetProperty("urls").EnumerateArray().Where(asset => asset.GetProperty("packagetype").GetString() == "bdist_wheel" &&
            asset.GetProperty("filename").GetString() is { } name && name.StartsWith(packageName + "-", StringComparison.Ordinal) && name.EndsWith("-py3-none-any.whl", StringComparison.Ordinal) &&
            !asset.GetProperty("yanked").GetBoolean()).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("A supported universal Python wheel is missing or ambiguous. Update FK_YT first.");
        var asset = matches[0];
        var name = ToolchainValidation.SafeRelativePath(RequiredString(asset, "filename"));
        if (name.Contains('/')) throw new InvalidDataException("The wheel filename is invalid.");
        var url = RequiredString(asset, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "files.pythonhosted.org" || !uri.AbsolutePath.StartsWith("/packages/", StringComparison.Ordinal))
            throw new InvalidDataException("The Python wheel is not hosted by the official package service.");
        var hash = RequiredString(asset.GetProperty("digests"), "sha256");
        var size = asset.GetProperty("size").GetInt64();
        if (!Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}\z") || size <= 0 || size > 30L * 1024 * 1024) throw new InvalidDataException("The Python wheel hash or size is invalid.");
        return new(name, url, hash.ToLowerInvariant(), size, kind);
    }
    internal static bool WheelPathAllowed(string path, string package, string version)
    {
        var root = path.Split('/')[0];
        if (root == package) return true;
        if (!root.StartsWith(package + "-", StringComparison.Ordinal)) return false;
        string suffix;
        if (root.EndsWith(".dist-info", StringComparison.Ordinal)) suffix = ".dist-info";
        else if (root.EndsWith(".data", StringComparison.Ordinal) && path.StartsWith(root + "/data/share/", StringComparison.Ordinal)) suffix = ".data";
        else return false;
        // Universal wheels may carry documentation/completions/manpages in
        // .data/data/share. Retain them privately, without installing a script,
        // entrypoint, .pth file, or global data file into the host environment.
        var recordedVersion = root[(package.Length + 1)..^suffix.Length];
        try { return ToolchainValidation.CompareVersions(recordedVersion, version) == 0; }
        catch (InvalidDataException) { return false; }
    }

    private async Task SaveNoticesAsync(string stage, ToolchainUpdateInfo update, CancellationToken ct)
    {
        var notices = Path.Combine(stage, "notices");
        Directory.CreateDirectory(notices);
        var tag = update.Tool == ToolchainTool.YtDlp ? update.Version : "v" + update.Version;
        var repo = update.Tool == ToolchainTool.YtDlp ? "yt-dlp/yt-dlp" : "denoland/deno";
        var licenseName = update.Tool == ToolchainTool.YtDlp ? "LICENSE" : "LICENSE.md";
        await SaveNoticeAsync("https://raw.githubusercontent.com/" + repo + "/" + tag + "/" + licenseName, Path.Combine(notices, licenseName), ct).ConfigureAwait(false);
        if (update.Tool == ToolchainTool.YtDlp)
            await SaveNoticeAsync("https://raw.githubusercontent.com/" + repo + "/" + tag + "/THIRD_PARTY_LICENSES.txt", Path.Combine(notices, "THIRD_PARTY_LICENSES.txt"), ct).ConfigureAwait(false);
        else
        {
            // These are original notices in the selected Deno source tag. They
            // are a retained subset, not a generated inventory of every crate,
            // V8/ICU dependency, TypeScript and Rust component in the binary.
            foreach (var notice in new[] { ("cli/tsc/dts/node/LICENSE", "node-types-LICENSE"),
                         ("cli/tsc/dts/node/undici/LICENSE", "undici-LICENSE"), ("ext/webgpu/LICENSE.md", "webgpu-LICENSE.md") })
                await SaveNoticeAsync("https://raw.githubusercontent.com/" + repo + "/" + tag + "/" + notice.Item1, Path.Combine(notices, notice.Item2), ct).ConfigureAwait(false);
            await SaveNoticeAsync("https://raw.githubusercontent.com/" + repo + "/" + tag + "/Cargo.lock", Path.Combine(notices, "Cargo.lock.upstream"), ct).ConfigureAwait(false);
        }
        var text = $"Official upstream {repo} {tag}\nRelease: {update.ReleaseUrl}\nSource: https://github.com/{repo}/archive/refs/tags/{tag}.zip\n" +
            $"Downloaded by FK_YT on {DateTimeOffset.UtcNow:O}.\n" +
            "This user-downloaded tool set is separate from the original FK_YT bundled release.\n" +
            "The manifest records upstream HTTPS download URLs, sizes and SHA256 digests. Hash checks are not independent publisher-signature verification.\n" +
            (update.Tool == ToolchainTool.YtDlp ? $"Python package: https://pypi.org/project/yt-dlp/{update.Version}/\nEJS package: https://pypi.org/project/yt-dlp-ejs/{update.EjsVersion}/\n" +
                $"EJS exact source tag: https://github.com/yt-dlp/ejs/tree/{update.EjsVersion}\nEJS source archive: https://github.com/yt-dlp/ejs/archive/refs/tags/{update.EjsVersion}.zip\n" +
                $"EJS wheel notices: site-packages/yt_dlp_ejs-{ToolchainValidation.NormalizeVersion(update.EjsVersion!)}.dist-info/licenses/ and embedded comments in site-packages/yt_dlp_ejs/yt/solver/*.js.\n" +
                "The combined official yt-dlp.exe is GPL-3.0-or-later; retained upstream third-party notices describe its included components.\n" :
                $"Deno source-release location: https://github.com/denoland/deno/releases/download/{tag}/deno_src.tar.gz (see release assets for availability).\n" +
                "Deno uses MIT and third-party component terms. LICENSE.md, the selected tag's Node/Undici/WebGPU notices, and Cargo.lock are retained as version-associated source records.\n" +
                "These files are NOT a complete binary third-party notice inventory or a complete corresponding-source closure. The Deno release ZIP and CLI do not supply such an inventory in the inspected release.\n" +
                "Upstream notice-tracking discussion: https://github.com/denoland/deno/issues/13515\n") +
            "A tagged upstream source archive alone is NOT asserted to include all dependency sources, patches, licenses or build materials needed when redistributing the combined executable.\n" +
            "Keep these version-specific files when copying this tool set. Original FK_YT corresponding-source archives describe only the original bundled versions.\n";
        File.WriteAllText(Path.Combine(notices, "SOURCE-AND-LICENSE.txt"), text, new UTF8Encoding(false));
    }
    private async Task SaveNoticeAsync(string url, string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await SendAsync(new Uri(url), deadline.Token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("An upstream license notice is empty.");
        await File.WriteAllBytesAsync(path, bytes, deadline.Token).ConfigureAwait(false);
    }
    private async Task<JsonDocument> JsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(url), ct).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, 8 * 1024 * 1024, ct).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException ex) { throw new InvalidDataException("The official update metadata could not be read.", ex); }
    }
    private async Task DownloadAsync(ToolchainAsset asset, string path, IProgress<ToolchainUpdateProgress>? progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await SendAsync(new Uri(asset.Url), deadline.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("The update download size differs from official metadata.");
        FileBoundary.EnsureRegularPath(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long received = 0, lastProgress = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        {
            received = checked(received + count);
            if (received > asset.Size) throw new InvalidDataException("The update download exceeded its declared size.");
            digest.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            if (received - lastProgress >= 512 * 1024) { lastProgress = received; progress?.Report(new("Downloading " + asset.Name, received, asset.Size)); }
        }
        if (received != asset.Size || Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != asset.Sha256)
            throw new InvalidDataException("The update download failed its size or SHA256 check. The active tools were preserved.");
        await output.FlushAsync(deadline.Token).ConfigureAwait(false);
        output.Flush(true);
        progress?.Report(new("Verified " + asset.Name, received, asset.Size));
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maximum) throw new InvalidDataException("The upstream response exceeds its size limit.");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("The upstream response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct)
    {
        // Requests never include a YouTube session, app cookies or account token.
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            ValidateOfficialUri(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("FK_YT/1.0 (manual-tool-update)");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == 5) throw new IOException("The official download redirected too many times.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new IOException(status is 403 or 429 ? "The official update service is rate limited. Try again later." : "The official update service returned HTTP " + status + ".");
            }
            return response;
        }
        throw new IOException("The official download could not be reached.");
    }
    internal static void ValidateOfficialUri(Uri uri)
    {
        var allowed = uri.Host is "api.github.com" or "github.com" or "raw.githubusercontent.com" or
            "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "pypi.org" or "files.pythonhosted.org";
        if (!allowed || uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("The update URL is outside the official HTTPS download services.");
    }
}
