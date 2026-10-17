using System.Globalization;
using System.Text;

namespace FkYouTube.Playback;

/// <summary>The same multi-stream EDL representation used by mpv's built-in yt-dlp hook.</summary>
public static class MpvPreparedMedia
{
    public static string BuildEdlUrl(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count is < 1 or > 2) throw new ArgumentException("One muxed stream or one video and one audio stream are required.", nameof(urls));
        foreach (var value in urls)
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("A resolved HTTPS media URL is required.", nameof(urls));
        if (urls.Count == 1) return urls[0];
        return "edl://" + string.Join(";", urls.Select(url => "!new_stream;!no_clip;!no_chapters;" + Escape(url)));
    }

    internal static string Escape(string value) => "%" + Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + "%" + value;

    /// <summary>Preserves an open-ended single live source while preventing yt-dlp fallback.</summary>
    public static string BuildLiveEdlUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || url.Any(char.IsControl))
            throw new ArgumentException("A resolved HTTPS live media URL is required.", nameof(url));
        return "edl://!no_clip;!no_chapters;" + Escape(url);
    }
}
