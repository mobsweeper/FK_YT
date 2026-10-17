using System.Text.Json.Serialization;

namespace FkYouTube;

public sealed record VideoItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Channel { get; init; } = "";
    public string? Thumbnail { get; init; }
    public double? Duration { get; init; }
    public long? ViewCount { get; init; }
    public string Kind { get; init; } = "video";
    public string LiveStatus { get; init; } = "unknown";
    public bool MembersOnly { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelUrl { get; init; }
    public string? ChannelThumbnail { get; init; }
    [JsonIgnore] public bool CanOpenChannel=>Services.YouTubeService.TryChannelUrl(ChannelUrl,out _) ||
        Services.YouTubeService.TryChannelUrl("https://www.youtube.com/channel/"+ChannelId,out _);
    public Dictionary<string,string>? RecommendationActions { get; init; }
    [JsonIgnore] public bool CanNotInterested=>RecommendationActions?.ContainsKey("not_interested")==true;
    [JsonIgnore] public bool CanDontRecommendChannel=>RecommendationActions?.ContainsKey("dont_recommend_channel")==true;
    [JsonIgnore] public string? LocalDetail { get; init; }
    [JsonIgnore] public Func<string>? LocalDetailFactory { get; init; }
    public string DurationText => Duration is > 0 ? FormatTime(Duration.Value) : Kind == "playlist" ? UiText.T("재생목록","Playlist","再生リスト") : Kind == "channel" ? UiText.T("채널","Channel","チャンネル") : "";
    public string DetailText => LocalDetailFactory?.Invoke() ?? LocalDetail ?? Channel + (ViewCount.HasValue ? UiText.F(" · 조회 {0:N0}회"," · {0:N0} views"," · {0:N0} 回視聴",ViewCount.Value) : "");
    public static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds) return "";
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return seconds >= 3600 ? string.Format(UiText.Culture,"{0}:{1:00}:{2:00}",(long)time.TotalHours,time.Minutes,time.Seconds) : time.ToString(@"m\:ss",UiText.Culture);
    }
}

public sealed record FeedFilter
{
    public string Id {get;init;}="";
    public string Label {get;init;}="";
    public bool Selected {get;init;}
}
public sealed record ChannelPageInfo
{
    public string Id {get;init;}="";
    public string Title {get;init;}="";
    public string Url {get;init;}="";
    public string? Thumbnail {get;init;}
    public string? Description {get;init;}
}
public sealed record FeedNavigationMetadata
{
    public string Title {get;init;}="";
    public string FilterKind {get;init;}="";
    public ChannelPageInfo? Channel {get;init;}
    public IReadOnlyList<FeedFilter> Filters {get;init;}=[];
}
public sealed record RelatedVideosResult(IReadOnlyList<VideoItem> Items,ChannelPageInfo? Channel,VideoRating? Rating=null,string? Description=null);
public sealed record VideoRating(string VideoId,string Value,bool CanRate,bool Verified=false);
// Return YouTube Dislike values include estimates, rather than an official YouTube total.
public sealed record DislikeStats(string VideoId,long Likes,long Dislikes);

public sealed record CommentItem(string Author, string Text, long Likes, string? Parent);
public sealed record HistoryEntry(VideoItem Video, double Position, double Duration, DateTimeOffset UpdatedAt, string SyncStatus);
public sealed record YouTubeChannel
{
    public string Id {get;init;}="";
    public string Name {get;init;}="";
    public string Handle {get;init;}="";
    public bool IsSelected {get;init;}
    public string PageId {get;init;}="";
    public string DataSyncId {get;init;}="";
    public int AuthUser {get;init;}
    public string? SigninUrl {get;init;}
    public override string ToString()=>string.IsNullOrWhiteSpace(Handle)?Name:$"{Name} ({Handle})";
}
public sealed class AppSettings
{
    public string UiLanguage { get; set; } = "ko";
    public string AuthMode { get; set; } = "guest";
    public string FirefoxProfile { get; set; } = "";
    public string AccountKey { get; set; } = "guest";
    public int MaxHeight { get; set; } = 0;
    public int QualityPreferenceVersion { get; set; } = 0;
    public bool SyncHistory { get; set; } = true;
    public bool HideMembersOnly { get; set; } = true;
    public bool AutoPlayNext { get; set; } = true;
    public bool Incognito { get; set; }
    public string SelectedChannelId { get; set; }="";
    public Dictionary<string,string> ChannelHistoryKeys { get; set; }=[];
    public double Volume { get; set; } = 70;
    public double Speed { get; set; } = 1;
    public string PlaylistUrl { get; set; } = "";
}

public static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = false };
}
