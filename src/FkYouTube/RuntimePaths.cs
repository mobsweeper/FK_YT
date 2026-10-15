using System.Security.Cryptography;
using System.Text;

namespace FkYouTube;

public sealed class RuntimePaths
{
    public string Root { get; }
    public string InstanceMutexName => "Local\\FKYT-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
    public string Data => Path.Combine(Root, "data");
    public string Python => Path.Combine(Root, "runtime", "python", "python.exe");
    public string FeedWorker => Path.Combine(Root, "worker", "feed_worker.py");
    public string Mpv => Path.Combine(Root, "runtime", "mpv", "mpv.exe");
    public string YtDlp => Path.Combine(Root, "runtime", "yt-dlp", "yt-dlp.exe");
    public string Deno => Path.Combine(Root, "runtime", "deno", "deno.exe");
    public RuntimePaths(string? root = null)
    {
        Root = NormalizeRoot(root ?? FindRoot());
        Directory.CreateDirectory(Data);
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
