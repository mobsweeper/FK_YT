using System.Text.RegularExpressions;
using System.Text.Json;

namespace FkYouTube.Services;

/// <summary>Validated options for the bundled yt-dlp YouTube adapter.</summary>
public static class YouTubeAccountOptions
{
    public static (string Executable,IReadOnlyList<string> Arguments,string? StandardInput) CreateCommand(RuntimePaths paths,IReadOnlyList<string> arguments,YouTubeChannel? channel)
    {
        Validate(channel);
        if(channel is null)return(paths.YtDlp,arguments,null);
        // --add-header is insufficient: yt-dlp's generated API headers can
        // override AuthUser. Reuse the bundled per-instance Python adapter.
        return(paths.Python,paths.BuildPythonArguments(Path.Combine(paths.Root,"worker","selected_ytdlp.py"),arguments),
            JsonSerializer.Serialize(new{accountContext=channel},JsonDefaults.Options));
    }
    public static string ExtractorArguments(YouTubeChannel? channel,string? youtubeExtra=null)
    {
        Validate(channel);
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        if(channel is not null)
        {
            values.Add("data_sync_id",channel.DataSyncId);
            values.Add("player_skip","webpage,configs");
            // Keep native authenticated/Premium clients and add cookie-capable HLS.
            values.Add("player_client","default,web_safari");
        }
        if(!string.IsNullOrEmpty(youtubeExtra))
        {
            foreach(var entry in youtubeExtra.Split(';'))
            {
                var pair=entry.Split('=',2);
                if(pair.Length!=2 || !Regex.IsMatch(pair[0],"^[a-z_]+\\z") ||
                    !Regex.IsMatch(pair[1],"^[A-Za-z0-9_,.\\-]+\\z") ||
                    pair[0] is "data_sync_id" or "player_skip" or "session_index" ||
                    !values.TryAdd(pair[0],pair[1]))
                    throw new InvalidDataException(UiText.T("YouTube 요청 옵션이 올바르지 않습니다.","The YouTube request options are invalid.","YouTube のリクエストオプションが正しくありません。"));
            }
        }
        // yt-dlp replaces a repeated extractor key rather than merging it.
        return values.Count==0?"":"youtube:"+string.Join(';',values.Select(pair=>pair.Key+"="+pair.Value));
    }

    public static void Validate(YouTubeChannel? channel)
    {
        if(channel is null)return;
        var parts=channel.DataSyncId?.Split("||",StringSplitOptions.None);
        if(string.IsNullOrEmpty(channel.Id) || channel.Id.Length>320 ||
            channel.Id.Any(char.IsControl) || channel.PageId is null || parts is not {Length:2} ||
            !Regex.IsMatch(parts[0],"^[A-Za-z0-9_-]{1,256}\\z") ||
            (parts[1].Length>0 && !Regex.IsMatch(parts[1],"^[A-Za-z0-9_-]{1,256}\\z")) ||
            channel.PageId!=(parts[1].Length==0?"":parts[0]) ||
            (channel.PageId.Length>0 && !Regex.IsMatch(channel.PageId,"^[0-9]{1,64}\\z")) ||
            channel.AuthUser is <0 or >99)
            throw new InvalidDataException(UiText.T("선택한 YouTube 채널 정보가 올바르지 않습니다. 채널 목록을 갱신하세요.","The selected YouTube channel information is invalid. Refresh the channel list.","選択した YouTube チャンネル情報が正しくありません。チャンネル一覧を更新してください。"));
    }
}
