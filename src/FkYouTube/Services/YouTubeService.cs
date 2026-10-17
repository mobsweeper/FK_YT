using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FkYouTube.Services;

public sealed class YouTubeService
{
    public const int MaxSubtitleLabelLength=160;
    private readonly RuntimePaths _paths;
    private readonly SessionService _session;
    private static readonly HttpClient PublicDislikeHttp=CreateDislikeHttpClient();
    private readonly HttpClient _dislikeHttp;
    private readonly TimeSpan _dislikeTimeout;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _dislikeRequestGate=new(1,1);
    private readonly object _dislikeCacheLock=new();
    private readonly Dictionary<string,DislikeCacheEntry> _dislikeCache=new(StringComparer.Ordinal);
    private DateTimeOffset _dislikeRetryAt;
    private sealed record DislikeCacheEntry(DislikeStats? Stats,DateTimeOffset ExpiresAt);
    public YouTubeService(RuntimePaths paths,SessionService session):this(paths,session,PublicDislikeHttp,TimeSpan.FromSeconds(10),()=>DateTimeOffset.UtcNow){}
    internal YouTubeService(RuntimePaths paths,SessionService session,HttpClient dislikeHttp,TimeSpan dislikeTimeout,Func<DateTimeOffset> utcNow)
    {_paths=paths;_session=session;_dislikeHttp=dislikeHttp;_dislikeTimeout=dislikeTimeout;_utcNow=utcNow;}
    private static HttpClientHandler CreateDislikeHttpHandler()=>new()
    {
        UseCookies=false,AllowAutoRedirect=false,UseDefaultCredentials=false,Credentials=null,DefaultProxyCredentials=null
    };
    private static HttpClient CreateDislikeHttpClient()=>new(CreateDislikeHttpHandler()){Timeout=Timeout.InfiniteTimeSpan};
    public static bool TryNormalizeYouTubeUrl(string input, out string canonical)
    {
        canonical = "";
        if (!TryYouTubeUri(input, out var uri)) return false;
        // Shared HTTP and scheme-less links always leave the UI as HTTPS URLs.
        canonical = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
        return true;
    }

    public static bool TryChannelUrl(string? input,out string canonical)
    {
        canonical="";
        if(string.IsNullOrWhiteSpace(input) || !TryYouTubeUri(input,out var uri) || uri.Host=="youtu.be")return false;
        var parts=uri.AbsolutePath.Trim('/').Split('/');
        var length=parts[0].StartsWith('@')?1:2;
        if(parts.Length<length || parts.Length>length+1 || (parts.Length>length && parts[^1] is not ("videos" or "shorts" or "streams" or "playlists")))return false;
        if(length==1)
        {
            var handle=Uri.UnescapeDataString(parts[0][1..]);
            if(handle.Length is <1 or >128 || handle.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||"/\\?#".Contains(c)))return false;
        }
        else if(parts[0]=="channel")
        {if(!Regex.IsMatch(parts[1],"^UC[A-Za-z0-9_-]{22}\\z"))return false;}
        else if(parts[0] is "c" or "user")
        {var name=Uri.UnescapeDataString(parts[1]);if(name.Length is <1 or >128 || name.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||"/\\?#".Contains(c)))return false;}
        else return false;
        canonical="https://www.youtube.com/"+string.Join('/',parts.Take(length))+"/videos";
        return true;
    }

    public static bool TryPlaylistUrl(string input, out string canonical)
    {
        canonical = "";
        if (!TryYouTubeUri(input, out var uri) || uri.Host == "youtu.be" ||
            (uri.AbsolutePath != "/playlist" && uri.AbsolutePath != "/watch")) return false;
        var list = QueryValue(uri, "list");
        if (list is null || !Regex.IsMatch(list, "^[A-Za-z0-9_-]+\\z")) return false;
        var seed=QueryValue(uri,"v");
        if(list.StartsWith("RD",StringComparison.Ordinal))
        {
            if(seed is null && Regex.IsMatch(list,"^RD[A-Za-z0-9_-]{11}\\z"))seed=list[2..];
            if(seed is not null && Regex.IsMatch(seed,"^[A-Za-z0-9_-]{11}\\z"))
            {canonical="https://www.youtube.com/watch?v="+seed+"&list="+list;return true;}
        }
        canonical = "https://www.youtube.com/playlist?list=" + list;
        return true;
    }

    public static bool TryVideoUrl(string input, out string id, out string canonical)
    {
        id = canonical = "";
        if (!TryYouTubeUri(input, out var uri)) return false;
        string? candidate = null;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host == "youtu.be")
        {
            if (segments.Length == 1) candidate = Uri.UnescapeDataString(segments[0]);
        }
        else if (uri.AbsolutePath == "/watch") candidate = QueryValue(uri, "v");
        else if (segments.Length == 2 && (segments[0] == "shorts" || segments[0] == "live"))
            candidate = Uri.UnescapeDataString(segments[1]);
        if (candidate is null || !Regex.IsMatch(candidate, "^[A-Za-z0-9_-]{11}\\z")) return false;
        id = candidate;
        canonical = "https://www.youtube.com/watch?v=" + id;
        return true;
    }

    private static bool TryYouTubeUri(string input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var value = input.Trim();
        // Uri accepts some whitespace/backslash variants that other URL consumers
        // interpret differently. Reject them before inspecting the authority.
        if (value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.Contains('\\')) return false;
        var absolute = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? value :
            value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : "https://" + value;
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        var authority = absolute.AsSpan(absolute.IndexOf("://", StringComparison.Ordinal) + 3);
        var boundary = authority.IndexOfAny('/', '?', '#');
        if (boundary >= 0) authority = authority[..boundary];
        if (authority.Contains('@')) return false;
        var host = parsed.Host;
        if (host != "youtu.be" && host != "youtube.com" &&
            !host.EndsWith(".youtube.com", StringComparison.Ordinal)) return false;
        uri = parsed;
        return true;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = field.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == name)
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
    private List<string> BaseArguments(string? cookiePath,YouTubeChannel? channel,string? youtubeExtra=null)
    {
        var args=new List<string>{"--ignore-config","--no-plugin-dirs","--no-cache-dir","--no-playlist","--skip-download","--no-mark-watched","--no-write-info-json","--no-write-thumbnail","--socket-timeout","15","--retries","1","--extractor-retries","1","--js-runtimes","deno:"+_paths.Deno};
        var extractor=YouTubeAccountOptions.ExtractorArguments(channel,youtubeExtra);
        if(extractor.Length>0)args.AddRange(["--extractor-args",extractor]);
        if(cookiePath!=null){args.Add("--cookies");args.Add(cookiePath);}return args;
    }
    private Task<CommandResult> RunYtDlpAsync(IReadOnlyList<string> args,YouTubeChannel? channel,TimeSpan timeout,CancellationToken ct,bool subtitleCatalog=false,CookieLease? lease=null)
    {
        var receipt=new ProcessStopReceipt();lease?.AttachConsumer(receipt);
        var command=YouTubeAccountOptions.CreateCommand(_paths,args,channel);
        if(subtitleCatalog)return ProcessRunner.RunAsync(_paths.Python,_paths.BuildPythonArguments(Path.Combine(_paths.Root,"worker","selected_ytdlp.py"),args),_paths.Root,timeout,ct,
            JsonSerializer.Serialize(new{accountContext=channel,operation="subtitle_catalog"},JsonDefaults.Options),receipt);
        return ProcessRunner.RunAsync(command.Executable,command.Arguments,_paths.Root,timeout,ct,command.StandardInput,receipt);
    }
    public async Task<IReadOnlyList<CommentItem>> GetCommentsAsync(string url,bool newest,CancellationToken ct,bool incognito=false)
    {
        if(!TryVideoUrl(url,out _,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        using var lease=incognito?new CookieLease(null):_session.Acquire();var channel=incognito?null:_session.SelectedChannel;
        var args=BaseArguments(lease.Path,channel,$"comment_sort={(newest?"new":"top")};max_comments=100,100,30,3");
        args.AddRange(["--write-comments","--dump-single-json",canonical]);
        var result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(90),ct,lease:lease);
        if(result.ExitCode!=0)throw new IOException(ProcessRunner.SafeError(result.Error));
        using var doc=JsonDocument.Parse(result.Output);
        return ParseComments(doc.RootElement);
    }
    public static IReadOnlyList<CommentItem> ParseComments(JsonElement video)
    {
        RequireObject(video);
        if(!video.TryGetProperty("comments",out var comments) || comments.ValueKind==JsonValueKind.Null)return [];
        if(comments.ValueKind!=JsonValueKind.Array)throw InvalidMetadata();
        var result=new List<CommentItem>();
        foreach(var comment in comments.EnumerateArray().Take(100))
        {
            if(comment.ValueKind!=JsonValueKind.Object)continue;
            var author=comment.TryGetProperty("author",out var a)&&a.ValueKind==JsonValueKind.String?a.GetString()!:UiText.T("사용자","User","ユーザー");
            var text=comment.TryGetProperty("text",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString()!:"";
            var likes=comment.TryGetProperty("like_count",out var l)&&l.ValueKind==JsonValueKind.Number&&l.TryGetInt64(out var count)?Math.Max(0,count):0;
            var parent=comment.TryGetProperty("parent",out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
            result.Add(new(author,text,likes,parent));
        }
        return result;
    }
    public async Task<bool> MarkWatchedAsync(string url,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out _,out var canonical) || !_session.IsConnected)return false;
        using var lease=_session.Acquire();var channel=_session.SelectedChannel;
        var args=BaseArguments(lease.Path,channel);
        // A timed-out write may already have reached YouTube. Do not replay it,
        // including through yt-dlp's shared extraction/download retry settings.
        args.AddRange(["--retries","0","--extractor-retries","0","--no-write-comments","--simulate","--mark-watched",canonical]);
        var result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(60),ct,lease:lease);
        ct.ThrowIfCancellationRequested();
        if(result.ExitCode==0 && !result.Error.Contains("Unable to mark",StringComparison.OrdinalIgnoreCase) && result.Output.Contains("Marking",StringComparison.OrdinalIgnoreCase))return true;
        throw new YouTubeAccessException(ProcessRunner.ClassifyError(result.Error),lease.Path is not null);
    }

    public async Task<IReadOnlyList<int>> GetResolutionsAsync(string url,CancellationToken ct,bool incognito=false)
    {
        if(!TryVideoUrl(url,out _,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var channel=incognito?null:_session.SelectedChannel;
        var args=ResolutionArguments(lease.Path,channel,canonical);
        var result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(90),ct,lease:lease);
        if(result.ExitCode!=0)throw new IOException(ProcessRunner.SafeError(result.Error));
        using var doc=JsonDocument.Parse(result.Output);
        return ParseResolutions(doc.RootElement);
    }
    private List<string> ResolutionArguments(string? cookiePath,YouTubeChannel? channel,string canonical)
    {
        var args=BaseArguments(cookiePath,channel);
        // This request lists metadata. Recent HLS formats may ask yt-dlp to
        // probe media; those probes must not delay or erase the quality list.
        args.AddRange(["--no-write-comments","--no-check-formats","--dump-single-json",canonical]);
        return args;
    }
    public async Task<IReadOnlyList<AvailableSubtitle>> GetSubtitleCatalogAsync(string url,CancellationToken ct,bool incognito=false)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        var channel=incognito?null:_session.SelectedChannel;
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var result=await RunYtDlpAsync(SubtitleCatalogArguments(lease.Path,channel,canonical),channel,TimeSpan.FromSeconds(90),ct,true,lease);
        if(result.ExitCode!=0)throw new IOException(ProcessRunner.SafeError(result.Error));
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc=JsonDocument.Parse(result.Output);
            return ParseSubtitleCatalog(doc.RootElement,videoId);
        }
        catch(JsonException ex){throw new InvalidDataException(UiText.T("자막 목록 응답 형식이 올바르지 않습니다. 다시 확인해 주세요.","The subtitle list response is invalid. Try again.","字幕一覧の応答形式が正しくありません。もう一度確認してください。"),ex);}
    }
    private List<string> SubtitleCatalogArguments(string? cookiePath,YouTubeChannel? channel,string canonical)
    {
        var args=BaseArguments(cookiePath,channel);
        // The pinned extractor only constructs every translated caption when
        // writeautomaticsub/list-subtitles is enabled. Simulation prevents files,
        // while this empty language regex prevents selecting/downloading captions.
        args.AddRange(["--no-write-comments","--no-check-formats","--ignore-no-formats-error","--simulate",
            "--no-write-subs","--write-auto-subs","--sub-langs","^$","--dump-single-json",canonical]);
        return args;
    }
    public static IReadOnlyList<AvailableSubtitle> ParseSubtitleCatalog(JsonElement video,string expectedVideoId)
    {
        RequireObject(video);
        if(!Regex.IsMatch(expectedVideoId,"^[A-Za-z0-9_-]{11}\\z") || !video.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String || id.GetString()!=expectedVideoId)
            throw new InvalidDataException(UiText.T("자막 목록의 영상이 현재 영상과 일치하지 않습니다.","The subtitle list does not match the current video.","字幕一覧の動画が現在の動画と一致しません。"));
        var result=new List<AvailableSubtitle>();var seen=new HashSet<(string Language,bool Automatic)>();
        foreach(var kind in new[]{("subtitles",false),("automatic_captions",true)})
        {
            if(!video.TryGetProperty(kind.Item1,out var catalog) || catalog.ValueKind==JsonValueKind.Null)continue;
            if(catalog.ValueKind!=JsonValueKind.Object)throw new InvalidDataException(UiText.T("자막 목록 응답 형식이 올바르지 않습니다.","The subtitle list response is invalid.","字幕一覧の応答形式が正しくありません。"));
            foreach(var entry in catalog.EnumerateObject().Take(1024))
            {
                if(result.Count==1024)break;
                if(!IsValidSubtitleLanguage(entry.Name) || entry.Value.ValueKind!=JsonValueKind.Array || !seen.Add((entry.Name,kind.Item2)))continue;
                string? chosenUrl=null;string? name=null;var priority=int.MaxValue;
                foreach(var format in entry.Value.EnumerateArray().Take(128))
                {
                    if(format.ValueKind!=JsonValueKind.Object || !format.TryGetProperty("ext",out var ext) || ext.ValueKind!=JsonValueKind.String)continue;
                    var rank=ext.GetString() switch{"vtt"=>0,"srt"=>1,"ass"=>2,"ssa"=>3,_=>int.MaxValue};
                    if(rank>=priority || !format.TryGetProperty("url",out var suppliedUrl) || suppliedUrl.ValueKind!=JsonValueKind.String ||
                        !TrySubtitleCatalogUrl(suppliedUrl.GetString(),out var canonical))continue;
                    chosenUrl=canonical;priority=rank;name=null;
                    if(format.TryGetProperty("name",out var suppliedName) && suppliedName.ValueKind==JsonValueKind.String)
                    {
                        var text=suppliedName.GetString()!.Trim();
                        if(text.Length is >0 and <=256 && !text.Any(char.IsControl))name=text;
                    }
                }
                if(chosenUrl==null)continue;
                result.Add(new(entry.Name,SubtitleCatalogDisplayLabel(entry.Name,name,kind.Item2),chosenUrl,kind.Item2)
                    {RawName=name,CatalogLabelGenerated=true});
            }
        }
        return result.OrderBy(item=>item.Language,StringComparer.OrdinalIgnoreCase).ThenBy(item=>item.Automatic).ToArray();
    }
    public static bool IsValidSubtitleLanguage(string? language)=>language is not null && language.Length<=64 &&
        Regex.IsMatch(language,"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,16}){0,3}\\z");
    public static string PreferredSubtitlePattern(string? language)
    {
        if(language!=null && !IsValidSubtitleLanguage(language))throw new InvalidDataException(UiText.T("선택한 자막 언어가 올바르지 않습니다.","The selected subtitle language is invalid.","選択した字幕言語が正しくありません。"));
        var baseLanguage=language!=null && language.EndsWith("-orig",StringComparison.OrdinalIgnoreCase)?language[..^5]:language;
        return (baseLanguage!=null?"(?i:"+baseLanguage+"(?:-orig)?),":"")+"ko,en,ko-orig,en-orig";
    }
    internal static int NormalizeMaxHeight(int maxHeight)=>maxHeight<=0?0:Math.Clamp(maxHeight,1,16384);
    internal static string VideoFormat(int maxHeight)
    {
        var height=NormalizeMaxHeight(maxHeight);
        return height==0?"bv*+ba/b":$"bv*[height<={height}]+ba/b[height<={height}]";
    }
    public static bool TrySubtitleCatalogUrl(string? input,out string canonical)
    {
        canonical="";
        if(input is null || input.Length is <1 or >16384 || input.Any(character=>char.IsWhiteSpace(character)||char.IsControl(character)) || input.Contains('\\') ||
            !Uri.TryCreate(input,UriKind.Absolute,out var uri) || uri.Scheme!=Uri.UriSchemeHttps || uri.Host!="www.youtube.com" ||
            uri.AbsolutePath!="/api/timedtext" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))return false;
        canonical=uri.AbsoluteUri;return true;
    }
    private static string CatalogSubtitleLanguageLabel(string language)=>language.Split('-')[0].ToLowerInvariant() switch
    {"ko" or "kor"=>UiText.T("한국어","Korean","韓国語"),"en" or "eng"=>UiText.T("영어","English","英語"),"ja" or "jpn"=>UiText.T("일본어","Japanese","日本語"),"zh" or "zho" or "chi"=>UiText.T("중국어","Chinese","中国語"),"es" or "spa"=>UiText.T("스페인어","Spanish","スペイン語"),"fr" or "fra" or "fre"=>UiText.T("프랑스어","French","フランス語"),_=>language};
    internal static string SubtitleCatalogDisplayLabel(string language,string? rawName,bool automatic)
    {
        var name=rawName??CatalogSubtitleLanguageLabel(language);
        var suffix=" ("+language+") · "+(automatic?UiText.T("자동 자막·번역","Automatic captions/translations","自動字幕・翻訳"):UiText.T("수동 자막","Manual captions","手動字幕"));
        var nameLength=Math.Min(name.Length,MaxSubtitleLabelLength-suffix.Length);
        if(nameLength>0 && char.IsHighSurrogate(name[nameLength-1]))nameLength--;
        return name[..nameLength]+suffix;
    }
    public async Task<IReadOnlyList<VideoItem>> GetRelatedAsync(string url,CancellationToken ct,bool incognito=false)=>
        (await GetRelatedPageAsync(url,ct,incognito)).Items;
    public async Task<RelatedVideosResult> GetRelatedPageAsync(string url,CancellationToken ct,bool incognito=false)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var accountContext=incognito?null:_session.SelectedChannel;
        var canRate=!incognito && _session.IsConnected && accountContext!=null && lease.Path!=null;
        var request=JsonSerializer.Serialize(new{url=canonical,cookiePath=lease.Path,accountContext,maxItems=30},JsonDefaults.Options);
        var receipt=new ProcessStopReceipt();lease.AttachConsumer(receipt);
        var result=await ProcessRunner.RunAsync(_paths.Python,_paths.BuildPythonArguments(Path.Combine(_paths.Root,"worker","related_feed.py")),_paths.Root,TimeSpan.FromSeconds(50),ct,request,receipt);
        if(result.ExitCode!=0)throw new IOException(UiText.T("관련 추천을 불러오지 못했습니다. 다시 불러오기를 눌러 주세요.","Could not load related recommendations. Select Reload.","関連動画のおすすめを読み込めませんでした。「再読み込み」を押してください。"));
        try
        {
            using var doc=JsonDocument.Parse(result.Output);
            return ParseRelatedPage(doc.RootElement,videoId,canRate);
        }
        catch(JsonException ex){throw new InvalidDataException(UiText.T("관련 영상 응답 형식이 올바르지 않습니다. 다시 시도해 주세요.","The related video response is invalid. Try again.","関連動画の応答形式が正しくありません。もう一度お試しください。"),ex);}
    }
    internal static RelatedVideosResult ParseRelatedPage(JsonElement response,string videoId,bool canRate)
    {
        RequireResponseVideo(response,"related",videoId);
        if(!response.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)throw InvalidMetadata();
        var channel=response.TryGetProperty("channel",out var owner) && owner.ValueKind==JsonValueKind.Object?owner.Deserialize<ChannelPageInfo>(JsonDefaults.Options):null;
        VideoRating? rating=null;
        if(response.TryGetProperty("rating",out var suppliedRating))
        {
            rating=ParseRatingValue(suppliedRating,videoId);
            if(!canRate)rating=new(videoId,"unknown",false);
        }
        string? description=null;
        if(response.TryGetProperty("description",out var suppliedDescription) && suppliedDescription.ValueKind!=JsonValueKind.Null)
        {
            if(suppliedDescription.ValueKind!=JsonValueKind.String)throw InvalidMetadata();
            description=new string(suppliedDescription.GetString()!.Where(character=>!char.IsControl(character) || character is '\n' or '\r' or '\t').Take(32768).ToArray());
            if(description.Length>0 && char.IsHighSurrogate(description[^1]))description=description[..^1];
        }
        return new(items.EnumerateArray().Take(30).Where(item=>item.ValueKind==JsonValueKind.Object).Select(item=>item.Deserialize<VideoItem>(JsonDefaults.Options)!).ToArray(),channel,rating,description);
    }
    public async Task<VideoRating> GetRatingAsync(string url,CancellationToken ct,bool incognito=false)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        if(incognito || !_session.IsConnected || _session.SelectedChannel==null)return new(videoId,"unknown",false);
        return await RatingRequestAsync(canonical,videoId,null,ct);
    }
    public async Task<VideoRating> SetRatingAsync(string url,string rating,CancellationToken ct,bool incognito=false)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        if(rating is not ("like" or "dislike" or "none"))throw new InvalidDataException(UiText.T("지원되지 않는 평가입니다.","This rating is not supported.","この評価はサポートされていません。"));
        if(incognito || !_session.IsConnected || _session.SelectedChannel==null)
            throw new InvalidOperationException(UiText.T("평가하려면 일반 모드에서 YouTube 로그인 채널을 선택하세요.","To rate a video, select a signed-in YouTube channel in normal mode.","評価するには、通常モードでログイン済みの YouTube チャンネルを選択してください。"));
        return await RatingRequestAsync(canonical,videoId,rating,ct);
    }
    private async Task<VideoRating> RatingRequestAsync(string canonical,string videoId,string? rating,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var channel=_session.SelectedChannel;
        if(!_session.IsConnected || channel==null)throw new InvalidOperationException(UiText.T("YouTube 로그인 채널을 선택하세요.","Select a signed-in YouTube channel.","ログイン済みの YouTube チャンネルを選択してください。"));
        YouTubeAccountOptions.Validate(channel);
        using var lease=_session.Acquire();
        if(lease.Path==null)throw new InvalidOperationException(UiText.T("YouTube 로그인 연결을 갱신하세요.","Refresh your YouTube sign-in connection.","YouTube のログイン接続を更新してください。"));
        var request=JsonSerializer.Serialize(new{command=rating==null?"rating.get":"rating.set",url=canonical,cookiePath=lease.Path,accountContext=channel,rating},JsonDefaults.Options);
        try
        {
            var receipt=new ProcessStopReceipt();lease.AttachConsumer(receipt);
            var result=await ProcessRunner.RunAsync(_paths.Python,_paths.BuildPythonArguments(Path.Combine(_paths.Root,"worker","related_feed.py")),_paths.Root,TimeSpan.FromSeconds(50),ct,request,receipt);
            if(result.ExitCode!=0)throw new IOException(UiText.T("내 평가를 확인하지 못했습니다. 로그인 연결을 갱신하고 다시 시도해 주세요.","Could not check your rating. Refresh your sign-in connection and try again.","自分の評価を確認できませんでした。ログイン接続を更新してから、もう一度お試しください。"));
            using var doc=JsonDocument.Parse(result.Output);
            var parsed=ParseRating(doc.RootElement,videoId,rating!=null);
            if(rating!=null && (!parsed.CanRate || parsed.Value!=rating))throw new InvalidDataException(UiText.T("평가 결과를 확인하지 못했습니다. 내 평가 확인을 눌러 주세요.","Could not verify the rating result. Select Check my rating.","評価結果を確認できませんでした。「自分の評価を確認」を押してください。"));
            ct.ThrowIfCancellationRequested();
            return parsed;
        }
        catch(Exception ex)when(rating!=null && ex is IOException or InvalidDataException or JsonException)
        {throw new IOException(UiText.T("평가 결과를 확인하지 못했습니다. 내 평가 확인을 눌러 주세요.","Could not verify the rating result. Select Check my rating.","評価結果を確認できませんでした。「自分の評価を確認」を押してください。"),ex);}
        catch(JsonException ex){throw new InvalidDataException(UiText.T("내 평가 응답 형식이 올바르지 않습니다. 다시 확인해 주세요.","The response for your rating is invalid. Check again.","自分の評価の応答形式が正しくありません。もう一度確認してください。"),ex);}
    }
    public static VideoRating ParseRating(JsonElement response,string videoId,bool requireVerified=false)
    {
        RequireResponseVideo(response,"rating",videoId);
        var rating=ParseRatingValue(response,videoId);
        if(requireVerified && !rating.Verified)throw new InvalidDataException(UiText.T("평가 결과를 확인하지 못했습니다. 내 평가 확인을 눌러 주세요.","Could not verify the rating result. Select Check my rating.","評価結果を確認できませんでした。「自分の評価を確認」を押してください。"));
        return rating;
    }
    private static VideoRating ParseRatingValue(JsonElement rating,string videoId)
    {
        RequireObject(rating);
        if(!rating.TryGetProperty("value",out var value) || value.ValueKind!=JsonValueKind.String || value.GetString() is not ("like" or "dislike" or "none" or "unknown") ||
            !rating.TryGetProperty("canRate",out var canRate) || canRate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw InvalidMetadata();
        var verified=false;
        if(rating.TryGetProperty("verified",out var suppliedVerified))
        {
            if(suppliedVerified.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw InvalidMetadata();
            verified=suppliedVerified.GetBoolean();
        }
        return new(videoId,value.GetString()!,canRate.GetBoolean(),verified);
    }
    private static void RequireResponseVideo(JsonElement response,string type,string videoId)
    {
        RequireObject(response);
        if(!Regex.IsMatch(videoId,"^[A-Za-z0-9_-]{11}\\z") ||
            !response.TryGetProperty("type",out var responseType) || responseType.ValueKind!=JsonValueKind.String || responseType.GetString()!=type ||
            !response.TryGetProperty("sourceVideoId",out var source) || source.ValueKind!=JsonValueKind.String || source.GetString()!=videoId)throw InvalidMetadata();
    }
    public async Task<DislikeStats?> GetDislikeStatsAsync(string url,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out _))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        if(TryDislikeCache(videoId,out var cached))return cached;
        await _dislikeRequestGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if(TryDislikeCache(videoId,out cached))return cached;
            if(_utcNow()<_dislikeRetryAt)throw new IOException(UiText.T("싫어요 수치 요청이 잠시 제한되었습니다. 잠시 후 다시 확인해 주세요.","Dislike count requests are temporarily limited. Try again later.","低評価数のリクエストが一時的に制限されています。しばらくしてから確認してください。"));
            // This request has no session, browser cookie, account context, or vote write.
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://returnyoutubedislikeapi.com/votes?videoId="+videoId);
            request.Headers.Accept.ParseAdd("application/json");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_dislikeTimeout);
            try
            {
                using var response=await _dislikeHttp.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
                if(response.StatusCode==HttpStatusCode.NotFound)
                {ct.ThrowIfCancellationRequested();CacheDislikeStats(videoId,null);return null;}
                if(response.StatusCode==HttpStatusCode.TooManyRequests)
                {
                    ct.ThrowIfCancellationRequested();
                    var retry=response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date?date-_utcNow():TimeSpan.FromMinutes(1));
                    _dislikeRetryAt=_utcNow()+TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds,1,900));
                    throw new IOException(UiText.T("싫어요 수치 요청이 잠시 제한되었습니다. 잠시 후 다시 확인해 주세요.","Dislike count requests are temporarily limited. Try again later.","低評価数のリクエストが一時的に制限されています。しばらくしてから確認してください。"));
                }
                if(!response.IsSuccessStatusCode)
                {
                    ct.ThrowIfCancellationRequested();
                    _dislikeRetryAt=_utcNow()+TimeSpan.FromSeconds(30);
                    throw new IOException(UiText.T("싫어요 추정 수치를 불러오지 못했습니다. 잠시 후 다시 확인해 주세요.","Could not load the estimated dislike count. Try again later.","低評価数の推定値を読み込めませんでした。しばらくしてから確認してください。"));
                }
                const int bodyLimit=64*1024;
                if(response.Content.Headers.ContentLength is >bodyLimit)throw new InvalidDataException(UiText.T("싫어요 수치 응답이 너무 큽니다.","The dislike count response is too large.","低評価数の応答が大きすぎます。"));
                await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);
                using var body=new MemoryStream();var buffer=new byte[4096];
                while(true)
                {
                    var read=await stream.ReadAsync(buffer.AsMemory(),deadline.Token);
                    if(read==0)break;
                    if(body.Length+read>bodyLimit)throw new InvalidDataException(UiText.T("싫어요 수치 응답이 너무 큽니다.","The dislike count response is too large.","低評価数の応答が大きすぎます。"));
                    body.Write(buffer,0,read);
                }
                deadline.Token.ThrowIfCancellationRequested();
                using var doc=JsonDocument.Parse(body.ToArray(),new JsonDocumentOptions{MaxDepth=16});
                var stats=ParseDislikeStats(doc.RootElement,videoId);
                ct.ThrowIfCancellationRequested();CacheDislikeStats(videoId,stats);return stats;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw new OperationCanceledException(ct);}
            catch(OperationCanceledException ex){throw new IOException(UiText.T("싫어요 수치 조회 시간이 초과되었습니다. 다시 확인해 주세요.","The dislike count request timed out. Try again.","低評価数の取得がタイムアウトしました。もう一度確認してください。"),new TimeoutException("The public dislike request exceeded its time limit.",ex));}
            catch(HttpRequestException ex){throw new IOException(UiText.T("싫어요 추정 수치를 불러오지 못했습니다. 연결 상태를 확인해 주세요.","Could not load the estimated dislike count. Check your connection.","低評価数の推定値を読み込めませんでした。接続状態を確認してください。"),ex);}
            catch(JsonException ex){throw new InvalidDataException(UiText.T("싫어요 수치 응답 형식이 올바르지 않습니다.","The dislike count response is invalid.","低評価数の応答形式が正しくありません。"),ex);}
        }
        finally{_dislikeRequestGate.Release();}
    }
    public static DislikeStats? ParseDislikeStats(JsonElement response,string videoId)
    {
        RequireObject(response);
        if(!Regex.IsMatch(videoId,"^[A-Za-z0-9_-]{11}\\z") || !response.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String || id.GetString()!=videoId ||
            !response.TryGetProperty("likes",out var likesValue) || likesValue.ValueKind!=JsonValueKind.Number || !likesValue.TryGetInt64(out var likes) || likes<0 ||
            !response.TryGetProperty("dislikes",out var dislikesValue) || dislikesValue.ValueKind!=JsonValueKind.Number || !dislikesValue.TryGetInt64(out var dislikes) || dislikes<0 ||
            !response.TryGetProperty("deleted",out var deleted) || deleted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(UiText.T("싫어요 수치 응답 형식이 올바르지 않습니다.","The dislike count response is invalid.","低評価数の応答形式が正しくありません。"));
        return deleted.GetBoolean()?null:new(videoId,likes,dislikes);
    }
    private bool TryDislikeCache(string videoId,out DislikeStats? stats)
    {
        lock(_dislikeCacheLock)
        {
            if(_dislikeCache.TryGetValue(videoId,out var entry) && entry.ExpiresAt>_utcNow())
            {stats=entry.Stats;return true;}
            _dislikeCache.Remove(videoId);stats=null;return false;
        }
    }
    private void CacheDislikeStats(string videoId,DislikeStats? stats)
    {
        lock(_dislikeCacheLock)
        {
            var now=_utcNow();
            foreach(var expired in _dislikeCache.Where(item=>item.Value.ExpiresAt<=now).Select(item=>item.Key).ToArray())_dislikeCache.Remove(expired);
            if(_dislikeCache.Count>=128 && !_dislikeCache.ContainsKey(videoId))_dislikeCache.Remove(_dislikeCache.MinBy(item=>item.Value.ExpiresAt).Key);
            _dislikeCache[videoId]=new(stats,now+TimeSpan.FromMinutes(5));
        }
    }
    public async Task<PlaybackStatus> GetPlaybackStatusAsync(string url,CancellationToken ct,bool incognito=false)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw InvalidMetadata();
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var channel=incognito?null:_session.SelectedChannel;
        var args=BaseArguments(lease.Path,channel);
        // Upcoming broadcasts have no formats. Metadata remains useful without
        // probing streams, waiting for a broadcast, or changing watch history.
        args.AddRange(["--no-write-comments","--simulate","--no-check-formats","--ignore-no-formats-error","--dump-single-json",canonical]);
        var result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(45),ct,lease:lease);
        if(result.ExitCode!=0)throw new YouTubeAccessException(ProcessRunner.ClassifyError(result.Error),lease.Path is not null);
        try{using var doc=JsonDocument.Parse(result.Output);return ParsePlaybackStatus(doc.RootElement,videoId);}
        catch(JsonException){throw InvalidMetadata();}
    }

    public static bool IsKnownLiveStatus(string? status)=>status is "not_live" or "is_live" or "is_upcoming" or "was_live" or "post_live";

    public static PlaybackStatus ParsePlaybackStatus(JsonElement video,string? expectedVideoId=null)
    {
        RequireObject(video);
        if(!video.TryGetProperty("id",out var idValue) || idValue.ValueKind!=JsonValueKind.String ||
            idValue.GetString() is not {} videoId || !Regex.IsMatch(videoId,"^[A-Za-z0-9_-]{11}\\z") ||
            expectedVideoId is not null && videoId!=expectedVideoId)throw InvalidMetadata();
        string status="unknown";
        if(video.TryGetProperty("live_status",out var supplied) && supplied.ValueKind!=JsonValueKind.Null)
        {
            if(supplied.ValueKind!=JsonValueKind.String || !IsKnownLiveStatus(supplied.GetString()))throw InvalidMetadata();
            status=supplied.GetString()!;
        }
        bool? Flag(string name)
        {
            if(!video.TryGetProperty(name,out var flag) || flag.ValueKind==JsonValueKind.Null)return null;
            if(flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw InvalidMetadata();
            return flag.GetBoolean();
        }
        var live=Flag("is_live");var wasLive=Flag("was_live");var upcoming=Flag("is_upcoming");
        if(status=="unknown")status=live==true?"is_live":upcoming==true?"is_upcoming":wasLive==true?"was_live":live==false && wasLive==false?"not_live":"unknown";
        if(live==true && status!="is_live" || live==false && status=="is_live" || upcoming==true && status!="is_upcoming")throw InvalidMetadata();
        DateTimeOffset? scheduled=null;
        if(video.TryGetProperty("release_timestamp",out var release) && release.ValueKind==JsonValueKind.Number && release.TryGetInt64(out var seconds))
            try{scheduled=DateTimeOffset.FromUnixTimeSeconds(seconds);}catch(ArgumentOutOfRangeException){throw InvalidMetadata();}
        return new(videoId,status,scheduled);
    }

    public async Task<LiveHlsMedia> ResolveLiveAsync(string url,int maxHeight,CancellationToken ct,bool incognito=false,string? subtitleLanguage=null)
    {
        // Obtain the catalog even when no HLS rendition is available, so an
        // unsupported transport gets an explicit message rather than an opaque
        // yt-dlp "requested format unavailable" extraction failure.
        var resolution=await ResolvePlaybackAsync(url,maxHeight,ct,incognito,subtitleLanguage);
        return resolution.Live??throw UnsupportedLiveMedia();
    }

    public async Task<PlaybackResolution> ResolvePlaybackAsync(string url,int maxHeight,CancellationToken ct,bool incognito=false,string? subtitleLanguage=null)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out var videoId,out var canonical))throw InvalidMetadata();
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var channel=incognito?null:_session.SelectedChannel;
        var args=BaseArguments(lease.Path,channel);
        // Retain the existing VOD selection. A live rendition is selected from
        // this same verified response's complete, yt-dlp ordered format catalog.
        args.AddRange(["--no-write-comments","--simulate","--no-live-from-start","--no-check-formats","--ignore-no-formats-error",
            "--write-subs","--write-auto-subs","--sub-langs",PreferredSubtitlePattern(subtitleLanguage),"--sub-format","vtt/best",
            "--format",VideoFormat(maxHeight),"--dump-single-json",canonical]);
        var result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(70),ct,lease:lease);
        if(result.ExitCode!=0)throw new YouTubeAccessException(ProcessRunner.ClassifyError(result.Error),lease.Path is not null);
        try
        {
            using var doc=JsonDocument.Parse(result.Output);
            ParsePlaybackStatus(doc.RootElement,videoId);
            return ParsePlaybackResolution(doc.RootElement,maxHeight);
        }
        catch(JsonException){throw InvalidMetadata();}
    }

    public static PlaybackResolution ParsePlaybackResolution(JsonElement video,int maxHeight=0)
    {
        var status=ParsePlaybackStatus(video);
        if(status.IsUpcoming || status.LiveStatus=="post_live")return new(status,null,null);
        if(!status.IsLive)return new(status,ParsePreparedVideo(video),null);
        var height=NormalizeMaxHeight(maxHeight);
        if(video.TryGetProperty("formats",out var formats) && formats.ValueKind==JsonValueKind.Array)
        {
            foreach(var format in formats.EnumerateArray().Reverse())
            {
                if(!IsPreparedHls(format) || !HasMuxedCodecs(format) || !format.TryGetProperty("height",out var pixels) ||
                    pixels.ValueKind!=JsonValueKind.Number || !pixels.TryGetInt32(out var h) || h is <=0 or >16384 || height>0 && h>height)continue;
                // Copy only this rendition and shared caption/header metadata.
                // A DASH selection's fragments/protocol must not leak into HLS.
                var candidate=new Dictionary<string,object?>
                {
                    ["id"]=status.VideoId,["live_status"]="is_live",["is_live"]=true,
                    ["requested_formats"]=new[]{format},["formats"]=formats
                };
                foreach(var field in new[]{"http_headers","requested_subtitles","has_drm"})
                    if(video.TryGetProperty(field,out var value))candidate[field]=value;
                try{return new(status,null,ParseLiveHlsMedia(JsonSerializer.SerializeToElement(candidate)));}
                catch(InvalidDataException){/* Try the next complete supported HLS rendition. */}
            }
            throw UnsupportedLiveMedia();
        }
        // Some extractors provide only the selected rendition.
        var single=ParseLiveHlsMedia(video);
        var selected=video.TryGetProperty("requested_formats",out var chosen)?chosen[0]:video;
        if(height>0 && selected.GetProperty("height").GetInt32()>height)throw UnsupportedLiveMedia();
        return new(status,null,single);
    }

    public static LiveHlsMedia ParseLiveHlsMedia(JsonElement video)
    {
        var status=ParsePlaybackStatus(video);
        if(!status.IsLive)throw UnsupportedLiveMedia();
        JsonElement format=video;
        if(video.TryGetProperty("requested_formats",out var requested))
        {
            if(requested.ValueKind!=JsonValueKind.Array || requested.GetArrayLength()!=1)throw UnsupportedLiveMedia();
            format=requested[0];
        }
        if(!IsPreparedHls(format) || !TryPreparedHlsUrl(format,"url","hls_playlist",out var source) ||
            !HasMuxedCodecs(format) || !format.TryGetProperty("height",out var selectedHeight) || selectedHeight.ValueKind!=JsonValueKind.Number ||
            !selectedHeight.TryGetInt32(out var selectedPixels) || selectedPixels is <=0 or >16384)throw UnsupportedLiveMedia();
        if(video.TryGetProperty("protocol",out var topProtocol) && topProtocol.ValueKind!=JsonValueKind.Null && !IsPreparedHls(video))throw UnsupportedLiveMedia();
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in new[]{video,format})
        {
            RequireObject(item);
            if(item.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))throw UnsupportedLiveMedia();
            if(HasUnsupportedLiveTransport(item))throw UnsupportedLiveMedia();
            if(item.TryGetProperty("http_headers",out var supplied) && supplied.ValueKind!=JsonValueKind.Null)
            {
                if(supplied.ValueKind!=JsonValueKind.Object)throw InvalidMetadata();
                var count=0;
                foreach(var header in supplied.EnumerateObject())
                {
                    if(++count>16 || !IsBenignLiveHeader(header.Name) || header.Value.ValueKind!=JsonValueKind.String)throw UnsupportedLiveMedia();
                    var value=header.Value.GetString()!;
                    if(value.Length>8192 || value.Any(char.IsControl) || headers.TryGetValue(header.Name,out var previous) && previous!=value)throw InvalidMetadata();
                    headers[header.Name]=value;
                }
            }
        }
        var resolutions=new SortedSet<int>();
        void AddResolution(JsonElement item)
        {
            if(IsPreparedHls(item) && HasMuxedCodecs(item) && !HasUnsupportedLiveTransport(item) && TryPreparedHlsUrl(item,"url","hls_playlist",out _) &&
                item.TryGetProperty("height",out var h) && h.ValueKind==JsonValueKind.Number && h.TryGetInt32(out var pixels) && pixels is >0 and <=16384)
                resolutions.Add(pixels);
        }
        AddResolution(format);
        if(video.TryGetProperty("formats",out var formats) && formats.ValueKind==JsonValueKind.Array)
            foreach(var item in formats.EnumerateArray())if(item.ValueKind==JsonValueKind.Object &&
                (!item.TryGetProperty("has_drm",out var drm) || drm.ValueKind is JsonValueKind.False or JsonValueKind.Null))AddResolution(item);
        var captions=new List<PreparedCaption>();
        if(video.TryGetProperty("requested_subtitles",out var subs) && subs.ValueKind==JsonValueKind.Object)
            foreach(var item in subs.EnumerateObject().Take(8))
                if(IsValidSubtitleLanguage(item.Name) && item.Value.ValueKind==JsonValueKind.Object && item.Value.TryGetProperty("url",out var caption) &&
                    caption.ValueKind==JsonValueKind.String && TrySubtitleCatalogUrl(caption.GetString(),out var canonical))captions.Add(new(item.Name,canonical));
        return new(status.VideoId,source.AbsoluteUri,headers,resolutions.ToArray(),captions);
    }

    private static bool HasMuxedCodecs(JsonElement format)
    {
        if(format.ValueKind!=JsonValueKind.Object)return false;
        foreach(var name in new[]{"vcodec","acodec"})
            if(!format.TryGetProperty(name,out var codec) || codec.ValueKind!=JsonValueKind.String || codec.GetString() is not {} text || text.Length is <1 or >128 ||
                text is "none" or "images" || text.Any(character=>!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))return false;
        return true;
    }
    private static bool HasUnsupportedLiveTransport(JsonElement item)
    {
        foreach(var field in new[]{"fragments","fragment_base_url","hls_aes","extra_param_to_segment_url","extra_param_to_key_url","cookies"})
            if(item.TryGetProperty(field,out var value) && value.ValueKind!=JsonValueKind.Null)return true;
        return item.TryGetProperty("downloader_options",out var options) && options.ValueKind!=JsonValueKind.Null &&
            (options.ValueKind!=JsonValueKind.Object || options.EnumerateObject().Any());
    }
    internal static bool IsBenignLiveHeader(string name)=>name.Equals("User-Agent",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Accept",StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Language",StringComparison.OrdinalIgnoreCase) || name.Equals("Sec-Fetch-Mode",StringComparison.OrdinalIgnoreCase);
    private static InvalidDataException UnsupportedLiveMedia()=>new(UiText.T("이 라이브의 전송 형식은 아직 지원하지 않습니다. 단일 HLS 영상·음성 스트림이 필요합니다.","This live transport is not supported yet. A single muxed HLS stream is required.","このライブ配信形式にはまだ対応していません。映像と音声を含む単一 HLS ストリームが必要です。"));

    public async Task<PreparedVideo> ResolveNextAsync(string url,int maxHeight,CancellationToken ct,bool incognito=false,string? subtitleLanguage=null)
    {
        ct.ThrowIfCancellationRequested();
        if(!TryVideoUrl(url,out _,out var canonical))throw new InvalidDataException(UiText.T("지원되지 않는 영상 주소입니다.","This video URL is not supported.","この動画 URL はサポートされていません。"));
        var subtitles=PreferredSubtitlePattern(subtitleLanguage);
        using var lease=incognito?new CookieLease(null):_session.Acquire();
        var channel=incognito?null:_session.SelectedChannel;
        var args=BaseArguments(lease.Path,channel);
        var format=VideoFormat(maxHeight);
        args.AddRange(["--no-write-comments","--simulate","--write-subs","--write-auto-subs","--sub-langs",subtitles,"--sub-format","vtt/best","--format",format,"--dump-single-json",canonical]);
        CommandResult result;
        try{result=await RunYtDlpAsync(args,channel,TimeSpan.FromSeconds(70),ct,lease:lease);}
        catch(IOException ex)when(ex.InnerException is TimeoutException){throw new YouTubeAccessException("network",lease.Path is not null);}
        if(result.ExitCode!=0)throw new YouTubeAccessException(ProcessRunner.ClassifyError(result.Error),lease.Path is not null);
        try
        {
            using var doc=JsonDocument.Parse(result.Output);
            return ParsePreparedVideo(doc.RootElement);
        }
        catch(JsonException){throw InvalidMetadata();}
    }
    public static PreparedVideo ParsePreparedVideo(JsonElement video)
    {
        RequireObject(video);
        // Keep the direct HTTP/EDL path; only the pinned extractor's selected,
        // muxed YouTube VOD HLS rendition can use the manifest path below.
        if(video.TryGetProperty("is_live",out var live) && live.ValueKind==JsonValueKind.True)throw new InvalidDataException(UiText.T("라이브는 일반 재생으로 전환합니다.","Live videos use standard playback.","ライブ動画は通常再生に切り替えます。"));
        if(video.TryGetProperty("requested_formats",out var requestedFormats)&&requestedFormats.ValueKind!=JsonValueKind.Array)throw InvalidMetadata();
        var formats=video.TryGetProperty("requested_formats",out var requested)?requested.EnumerateArray().Take(3).ToArray():[video];
        if(formats.Length is <1 or >2)throw new InvalidDataException(UiText.T("지원되지 않는 스트림 조합입니다.","This stream combination is not supported.","このストリームの組み合わせはサポートされていません。"));
        var isHls=IsPreparedHls(video)||formats.Any(IsPreparedHls);
        if(isHls)ValidatePreparedHls(video,formats);
        var urls=new List<string>();var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int? httpChunkSize=null;
        foreach(var format in formats)
        {
            RequireObject(format);
            if(!isHls && format.TryGetProperty("protocol",out var protocol) && (protocol.ValueKind!=JsonValueKind.String||protocol.GetString() is not ("https" or "http")))throw new InvalidDataException(UiText.T("이 영상은 일반 재생으로 전환합니다.","This video uses standard playback.","この動画は通常再生に切り替えます。"));
            if(format.TryGetProperty("has_drm",out var drm) && drm.ValueKind==JsonValueKind.True)throw new InvalidDataException(UiText.T("지원되지 않는 스트림입니다.","This stream is not supported.","このストリームはサポートされていません。"));
            if(!format.TryGetProperty("url",out var u) || u.ValueKind!=JsonValueKind.String || !Uri.TryCreate(u.GetString(),UriKind.Absolute,out var uri) || uri.Scheme!="https")throw new InvalidDataException(UiText.T("지원되지 않는 스트림 주소입니다.","This stream URL is not supported.","このストリーム URL はサポートされていません。"));
            urls.Add(uri.AbsoluteUri);
            if(format.TryGetProperty("downloader_options",out var downloader) && downloader.ValueKind!=JsonValueKind.Null)
            {
                if(downloader.ValueKind!=JsonValueKind.Object)throw InvalidMetadata();
                if(downloader.TryGetProperty("http_chunk_size",out var chunk) && chunk.ValueKind!=JsonValueKind.Null)
                {
                    if(chunk.ValueKind!=JsonValueKind.Number || !chunk.TryGetInt32(out var bytes) || bytes is <=0 or >PreparedVideo.MaxHttpChunkSize)throw InvalidMetadata();
                    httpChunkSize=Math.Min(httpChunkSize??bytes,bytes);
                }
            }
            if(format.TryGetProperty("http_headers",out var suppliedHeaders)&&suppliedHeaders.ValueKind!=JsonValueKind.Object)throw InvalidMetadata();
            var headerCount=0;
            if(format.TryGetProperty("http_headers",out var h))foreach(var property in h.EnumerateObject())
            {
                if(++headerCount>16 || !IsPreparedHeaderName(property.Name) || property.Value.ValueKind!=JsonValueKind.String)throw InvalidMetadata();
                var value=property.Value.GetString()??"";
                if(value.Length>8192)throw InvalidMetadata();
                if(value.IndexOfAny(['\r','\n','\0'])>=0 || property.Name.Any(char.IsControl))throw new InvalidDataException(UiText.T("잘못된 스트림 헤더입니다.","The stream headers are invalid.","ストリームヘッダーが正しくありません。"));
                if(headers.TryGetValue(property.Name,out var previous) && previous!=value)throw new InvalidDataException(UiText.T("서로 다른 스트림 헤더는 일반 재생으로 전환합니다.","Streams with different headers use standard playback.","ストリームヘッダーが異なるため、通常再生に切り替えます。"));
                if(headers.Count>=16 && !headers.ContainsKey(property.Name))throw InvalidMetadata();
                headers[property.Name]=value;
            }
        }
        var captions=new List<PreparedCaption>();
        if(video.TryGetProperty("requested_subtitles",out var subs) && subs.ValueKind==JsonValueKind.Object)
            foreach(var entry in subs.EnumerateObject().Take(8))
                if(IsValidSubtitleLanguage(entry.Name) && entry.Value.ValueKind==JsonValueKind.Object && entry.Value.TryGetProperty("url",out var captionUrl) &&
                    captionUrl.ValueKind==JsonValueKind.String && TrySubtitleCatalogUrl(captionUrl.GetString(),out _))
                    captions.Add(new(entry.Name,captionUrl.GetString()!));
        return new(Playback.MpvPreparedMedia.BuildEdlUrl(urls),headers,ParseResolutions(video),captions,httpChunkSize);
    }
    private static bool IsPreparedHeaderName(string name)=>name.Length is >0 and <=128 &&
        name.All(character=>char.IsAsciiLetterOrDigit(character)||"!#$%&'*+-.^_`|~".Contains(character));
    private static bool IsPreparedHls(JsonElement value)=>value.ValueKind==JsonValueKind.Object &&
        value.TryGetProperty("protocol",out var protocol) && protocol.ValueKind==JsonValueKind.String && protocol.GetString() is "m3u8" or "m3u8_native";
    private static void ValidatePreparedHls(JsonElement video,IReadOnlyList<JsonElement> formats)
    {
        // A selected account must keep its resolved rendition. A master playlist,
        // live stream, split HLS pair or unknown transport must not reselect media.
        if(formats.Count!=1 || !IsPreparedHls(formats[0]) ||
            (video.TryGetProperty("protocol",out _) && !IsPreparedHls(video)) ||
            !video.TryGetProperty("is_live",out var live) || live.ValueKind!=JsonValueKind.False ||
            !video.TryGetProperty("live_status",out var status) || status.ValueKind!=JsonValueKind.String || status.GetString()!="not_live")throw InvalidMetadata();
        var format=formats[0];
        foreach(var codecName in new[]{"vcodec","acodec"})
            if(!format.TryGetProperty(codecName,out var codec) || codec.ValueKind!=JsonValueKind.String ||
                string.IsNullOrWhiteSpace(codec.GetString()) || codec.GetString() is "none" or "images")throw InvalidMetadata();
        if(!format.TryGetProperty("height",out var height) || height.ValueKind!=JsonValueKind.Number || !height.TryGetInt32(out var pixels) || pixels is <=0 or >16384 ||
            !TryPreparedHlsUrl(format,"url","hls_playlist",out _) || !TryPreparedHlsUrl(format,"manifest_url","hls_variant",out _))throw InvalidMetadata();
        foreach(var metadata in new[]{video,format})
        {
            if(metadata.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))throw InvalidMetadata();
            if(metadata.TryGetProperty("is_live",out var formatLive) && formatLive.ValueKind!=JsonValueKind.False)throw InvalidMetadata();
            if(metadata.TryGetProperty("was_live",out var wasLive) && wasLive.ValueKind!=JsonValueKind.False)throw InvalidMetadata();
            if(metadata.TryGetProperty("live_status",out var formatStatus) && (formatStatus.ValueKind!=JsonValueKind.String || formatStatus.GetString()!="not_live"))throw InvalidMetadata();
            foreach(var field in new[]{"fragments","fragment_base_url","hls_aes","extra_param_to_segment_url","extra_param_to_key_url"})
                if(metadata.TryGetProperty(field,out var fragment) && fragment.ValueKind!=JsonValueKind.Null)throw InvalidMetadata();
            if(metadata.TryGetProperty("downloader_options",out var downloader) && downloader.ValueKind!=JsonValueKind.Null &&
                (downloader.ValueKind!=JsonValueKind.Object || downloader.TryGetProperty("http_chunk_size",out _)))throw InvalidMetadata();
            if(!metadata.TryGetProperty("http_headers",out var supplied))continue;
            if(supplied.ValueKind!=JsonValueKind.Object)throw InvalidMetadata();
            var count=0;
            foreach(var header in supplied.EnumerateObject())
            {
                // HLS subrequests can go to another CDN host. Never propagate
                // cookies, authorization or arbitrary required headers to them.
                // Benign yt-dlp defaults are accepted metadata; mpv retains its
                // narrower explicit forwarding policy for these headers.
                if(++count>16 || !(header.Name.Equals("User-Agent",StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Accept",StringComparison.OrdinalIgnoreCase) || header.Name.Equals("Accept-Language",StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Sec-Fetch-Mode",StringComparison.OrdinalIgnoreCase)) || header.Value.ValueKind!=JsonValueKind.String)throw InvalidMetadata();
                var value=header.Value.GetString()!;
                if(value.Length>8192 || value.Any(char.IsControl))throw InvalidMetadata();
            }
        }
    }
    private static bool TryPreparedHlsUrl(JsonElement value,string property,string manifestKind,out Uri uri)
    {
        uri=null!;
        if(!value.TryGetProperty(property,out var supplied) || supplied.ValueKind!=JsonValueKind.String)return false;
        var text=supplied.GetString();
        if(text is null || text.Length is <1 or >16384 || text.Any(character=>char.IsWhiteSpace(character)||char.IsControl(character)) || text.Contains('\\') ||
            !Uri.TryCreate(text,UriKind.Absolute,out var parsed) || parsed.Scheme!=Uri.UriSchemeHttps || parsed.Host!="manifest.googlevideo.com" ||
            !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment) ||
            !parsed.AbsolutePath.StartsWith("/api/manifest/"+manifestKind+"/",StringComparison.Ordinal) || !parsed.AbsolutePath.EndsWith(".m3u8",StringComparison.Ordinal))return false;
        uri=parsed;return true;
    }
    public static IReadOnlyList<int> ParseResolutions(JsonElement video)
    {
        RequireObject(video);
        if(!video.TryGetProperty("formats",out var formats) || formats.ValueKind!=JsonValueKind.Array)return [];
        return formats.EnumerateArray().Where(f=>
            f.ValueKind==JsonValueKind.Object && f.TryGetProperty("vcodec",out var codec) && codec.ValueKind==JsonValueKind.String && codec.GetString() is not (null or "none" or "images") &&
            !(f.TryGetProperty("has_drm",out var drm)&&drm.ValueKind==JsonValueKind.True) &&
            f.TryGetProperty("height",out var h) && h.ValueKind==JsonValueKind.Number && h.TryGetInt32(out var height) && height>0 && height<=16384)
            .Select(f=>f.GetProperty("height").GetInt32()).Distinct().Order().ToArray();
    }
    private static InvalidDataException InvalidMetadata()=>new(UiText.T("영상 응답 형식이 올바르지 않습니다. 다시 시도해 주세요.","The video response is invalid. Try again.","動画の応答形式が正しくありません。もう一度お試しください。"));
    private static void RequireObject(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw InvalidMetadata();
    }
}
public sealed record PreparedCaption(string Language,string Url);
public sealed record PlaybackStatus(string VideoId,string LiveStatus,DateTimeOffset? ScheduledStart=null)
{
    public bool IsLive=>LiveStatus=="is_live";
    public bool IsUpcoming=>LiveStatus=="is_upcoming";
    public bool HasEnded=>LiveStatus is "post_live" or "was_live";
}
public sealed record PlaybackResolution(PlaybackStatus Status,PreparedVideo? Vod,LiveHlsMedia? Live);
public sealed record LiveHlsMedia(string VideoId,string MediaUrl,IReadOnlyDictionary<string,string> Headers,IReadOnlyList<int> Resolutions,IReadOnlyList<PreparedCaption> Captions);
public sealed record PreparedVideo(string MediaUrl,IReadOnlyDictionary<string,string> Headers,IReadOnlyList<int> Resolutions,IReadOnlyList<PreparedCaption> Captions,int? HttpChunkSize=null)
{
    // App policy: accept the pinned extractor's 10 MiB limit, bounded to 64 MiB.
    public const int MaxHttpChunkSize=64*1024*1024;
}
public sealed record AvailableSubtitle(string Language,string Label,string Url,bool Automatic)
{
    [System.Text.Json.Serialization.JsonIgnore] public string? RawName {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public bool CatalogLabelGenerated {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayLabel=>CatalogLabelGenerated?YouTubeService.SubtitleCatalogDisplayLabel(Language,RawName,Automatic):Label;
}
public sealed class YouTubeAccessException:IOException
{
    public string Code {get;}
    public bool AuthenticationRequired=>Code=="signin_age";
    public YouTubeAccessException(string code,bool authenticated=false):base(ProcessRunner.ErrorMessage(code,authenticated))
    {Code=ProcessRunner.IsErrorCode(code)?code:"generic";}
}
