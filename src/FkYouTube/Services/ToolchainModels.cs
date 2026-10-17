namespace FkYouTube.Services;

public enum ToolchainTool { YtDlp, Deno }

public sealed record ToolchainUpdateProgress(string Message, long BytesReceived = 0, long? TotalBytes = null);

public sealed class ToolchainUpdateInfo
{
    public ToolchainTool Tool { get; }
    public string Version { get; }
    public string InstalledVersion { get; }
    public bool IsUpdateAvailable { get; }
    public string? EjsVersion { get; }
    public string ReleaseUrl { get; }
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal IReadOnlyList<ToolchainAsset> Assets { get; }
    internal string? RequiresPython { get; }
    internal string? RequiresDeno { get; }
    internal ToolchainUpdateInfo(ToolchainTool tool, string version, string installedVersion, string releaseUrl,
        IReadOnlyList<ToolchainAsset> assets, string? ejsVersion = null, string? requiresPython = null, string? requiresDeno = null)
    {
        Tool = tool; Version = version; InstalledVersion = installedVersion; ReleaseUrl = releaseUrl;
        Assets = assets; EjsVersion = ejsVersion; RequiresPython = requiresPython; RequiresDeno = requiresDeno;
        IsUpdateAvailable = ToolchainValidation.CompareVersions(version, installedVersion) > 0;
    }
}

public sealed record ToolchainStatus(string YtDlpVersion, string DenoVersion, string? PendingDescription,
    bool CanRollback, string? StartupMessage);

internal sealed record ToolchainAsset(string Name, string Url, string Sha256, long Size, string Kind);
internal sealed record RuntimeToolchainSnapshot(string YtDlpPath, string DenoPath, string? PackagesPath,
    string YtDlpVersion, string DenoVersion, string? EjsVersion);
internal sealed record ToolchainSelection(string? YtDlp = null, string? Deno = null);
internal sealed class ToolchainState
{
    public int Schema { get; set; } = 1;
    public ToolchainSelection Active { get; set; } = new();
    public ToolchainSelection? Pending { get; set; }
    public ToolchainSelection? Previous { get; set; }
    public bool ActivationInProgress { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ToolchainSelection? ActivationRollback { get; set; }
}
internal sealed class ToolchainManifest
{
    public int Schema { get; set; } = 1;
    public string Tool { get; set; } = "";
    public string Version { get; set; } = "";
    public string? EjsVersion { get; set; }
    public string? RequiresPython { get; set; }
    public string? RequiresDeno { get; set; }
    public string ReleaseUrl { get; set; } = "";
    public DateTimeOffset DownloadedAt { get; set; }
    public List<ToolchainAsset> Assets { get; set; } = [];
    public List<ToolchainFile> Files { get; set; } = [];
}
internal sealed record ToolchainFile(string Path, long Size, string Sha256);
