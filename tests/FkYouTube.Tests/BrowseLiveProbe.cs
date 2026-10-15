using System.IO;
using System.Text.Json;
using FkYouTube;
using FkYouTube.Services;

internal static class BrowseLiveProbe
{
    // Caller prepares an isolated root containing encrypted copies only. Never
    // construct SessionService against the user's active data directory here.
    public static async Task RunAsync(string workspace,string root)
    {
        root=Path.GetFullPath(root);
        if(!root.StartsWith(Path.Combine(workspace,"artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(Path.Combine(root,"isolated-browse-probe.marker")))throw new InvalidOperationException("Isolated browse root required.");
        var paths=new RuntimePaths(root);
        var session=new SessionService(paths);
        using(var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.Data,"settings.json"))))
        {
            if(settings.RootElement.TryGetProperty("selectedChannelId",out var id) && id.ValueKind==JsonValueKind.String)
                session.RestoreSelectedChannel(id.GetString()??"");
        }
        await using var feed=new FeedService(paths);
        FeedNavigationMetadata? metadata=null;
        var generation=1;
        var identities=new HashSet<string>();
        async Task Read(bool open,string? filterId=null)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var items=new List<VideoItem>();
            var batch=await feed.ReadAsync(open,"home",generation,open&&filterId==null?session.Acquire():null,
                items.Add,timeout.Token,accountContext:session.SelectedChannel,onMetadata:value=>metadata=value,
                filterId:filterId,previousGeneration:filterId==null?null:generation-1);
            if(batch.Warning!=null || batch.Count==0 || metadata==null)throw new InvalidOperationException("Home browse returned no usable batch.");
            foreach(var item in items)
                if(!identities.Add(item.Kind+":"+item.Id))throw new InvalidOperationException("Duplicate item within selected feed.");
            Console.WriteLine(JsonSerializer.Serialize(new {probe="home",generation,count=batch.Count,total=identities.Count,
                hasMore=batch.HasMore,channelLinks=items.Count(item=>item.CanOpenChannel),avatars=items.Count(item=>!string.IsNullOrWhiteSpace(item.ChannelThumbnail)),
                filters=metadata.Filters.Select(item=>new{item.Label,item.Selected})},JsonDefaults.Options));
        }
        try
        {
            await Read(true);
            await Read(false);
            var selected=metadata!.Filters.FirstOrDefault(item=>item.Selected)?.Label;
            var alternative=metadata.Filters.FirstOrDefault(item=>!item.Selected && (item.Label.Contains("Music",StringComparison.OrdinalIgnoreCase)||item.Label.Contains("음악")))
                ??metadata.Filters.FirstOrDefault(item=>!item.Selected);
            if(alternative==null)throw new InvalidOperationException("Server did not return a selectable home filter.");
            identities.Clear();generation++;await Read(true,alternative.Id);
            foreach(var label in new[]{"Mixes","New to you"})
            {
                var target=metadata!.Filters.FirstOrDefault(item=>item.Label.Equals(label,StringComparison.OrdinalIgnoreCase));
                if(target==null)continue; // Labels are supplied by this account, not fabricated.
                identities.Clear();generation++;await Read(true,target.Id);
                if(metadata!.Filters.FirstOrDefault(item=>item.Selected)?.Label!=label)throw new InvalidOperationException("Filter selection did not match the requested chip.");
            }
            var original=metadata!.Filters.FirstOrDefault(item=>item.Label==selected);
            if(original==null)throw new InvalidOperationException("Original home filter missing after switch.");
            identities.Clear();generation++;await Read(true,original.Id);
            var youtube=new YouTubeService(paths,session);
            using var ownerTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(65));
            var related=await youtube.GetRelatedPageAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ",ownerTimeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new {probe="watch-owner",owner=related.Channel!=null,
                clickable=YouTubeService.TryChannelUrl(related.Channel?.Url,out _),avatar=!string.IsNullOrWhiteSpace(related.Channel?.Thumbnail),related=related.Items.Count},JsonDefaults.Options));
            if(related.Channel==null || !YouTubeService.TryChannelUrl(related.Channel.Url,out _))throw new InvalidOperationException("Watch owner missing.");
        }
        finally
        {
            feed.Stop();
            Console.WriteLine("BROWSE managed-plaintext-files="+Directory.EnumerateFiles(Path.Combine(paths.Data,"temp"),"cookies-*.txt").Count());
        }
    }
}
