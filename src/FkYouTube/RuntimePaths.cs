using System.Security.Cryptography;
using System.Text;
using FkYouTube.Services;

namespace FkYouTube;

public sealed class RuntimePaths
{
    private const string InstallModeFileName = "FK_YT.install-mode";
    private readonly SemaphoreSlim _toolchainGate = new(1, 1);
    private RuntimeToolchainSnapshot? _toolchain;
    public string Root { get; }
    public string InstanceMutexName => "Local\\FKYT-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
    public string Data { get; }
    public string Python => Path.Combine(Root, "runtime", "python", "python.exe");
    public string FeedWorker => Path.Combine(Root, "worker", "feed_worker.py");
    public string Mpv => Path.Combine(Root, "runtime", "mpv", "mpv.exe");
    public string YtDlp => _toolchain?.YtDlpPath ?? BundledYtDlp;
    public string Deno => _toolchain?.DenoPath ?? BundledDeno;
    internal string BundledYtDlp => Path.Combine(Root, "runtime", "yt-dlp", "yt-dlp.exe");
    internal string BundledDeno => Path.Combine(Root, "runtime", "deno", "deno.exe");
    internal RuntimeToolchainSnapshot? Toolchain => _toolchain;
    public string? ToolchainStartupMessage { get; private set; }
    public RuntimePaths(string? root = null)
    {
        Root = NormalizeRoot(root ?? FindRoot());
        Data = ResolveDataPath(Root, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        FileBoundary.EnsureRegularPath(Data);
        Directory.CreateDirectory(Data);
        FileBoundary.EnsureRegularPath(Data);
    }
    // This fixed marker selects the installed application's per-user data policy.
    // Keep the resolver identical to the machine-v1 release, including rejection
    // of malformed, oversized, directory and linked markers.
    private static string ResolveDataPath(string root, string localApplicationData)
    {
        var marker = Path.Combine(root, InstallModeFileName);
        FileBoundary.EnsureRegularPath(marker);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(marker); }
        catch (FileNotFoundException) { return Path.Combine(root, "data"); }
        catch (DirectoryNotFoundException) { return Path.Combine(root, "data"); }
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The FK_YT installation mode marker must be a regular file.");
        if (!FileBoundary.ReadBoundedFile(marker, 11).AsSpan().SequenceEqual("machine-v1\n"u8))
            throw new InvalidDataException("The FK_YT installation mode marker is invalid.");
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        return Path.Combine(NormalizeRoot(localApplicationData), "FK_YT", "data");
    }
    // Invoke only after acquiring the app instance mutex. Selection is frozen
    // for this RuntimePaths object, so long-lived workers and mpv cannot mix sets.
    public async Task InitializeToolchainAsync(CancellationToken cancellationToken = default)
    {
        await _toolchainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_toolchain is not null) return;
            var result = await ToolchainStore.InitializeAsync(this, cancellationToken).ConfigureAwait(false);
            _toolchain = result.Snapshot;
            ToolchainStartupMessage = result.Message;
        }
        finally { _toolchainGate.Release(); }
    }
    public IReadOnlyList<string> BuildPythonArguments(string script, IEnumerable<string>? arguments = null, bool unbuffered = false)
    {
        var workerRoot = Path.GetFullPath(Path.Combine(Root, "worker")) + Path.DirectorySeparatorChar;
        var fullScript = Path.GetFullPath(script);
        if (!fullScript.StartsWith(workerRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullScript), Path.TrimEndingDirectorySeparator(workerRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only application workers can be launched by the toolchain.", nameof(script));
        FileBoundary.EnsureRegularPath(fullScript);
        var result = new List<string> { "-B" };
        if (unbuffered) result.Add("-u");
        if (_toolchain?.PackagesPath is { } packages)
        {
            FileBoundary.EnsureRegularPath(packages);
            FileBoundary.EnsureRegularPath(Path.Combine(Root, "worker", "toolchain_bootstrap.py"));
            result.AddRange([Path.Combine(Root, "worker", "toolchain_bootstrap.py"), "--packages", packages, "--script", fullScript, "--"]);
        }
        else result.Add(fullScript);
        if (arguments is not null) result.AddRange(arguments);
        return result;
    }
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        // Keep the volume/UNC root separator, but collapse equivalent folder spellings.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "runtime")) && Directory.Exists(Path.Combine(d.FullName, "worker"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
}
