using System.Text.Json;
using FkYouTube;
using FkYouTube.Services;

internal static class BrowseRegression
{
    public static void Run()
    {
        const string channelId="UC1234567890123456789012";
        var accepted=new (string Input,string Canonical)[]
        {
            ("https://www.youtube.com/channel/"+channelId,"https://www.youtube.com/channel/"+channelId+"/videos"),
            ("//m.youtube.com/channel/"+channelId+"/shorts?view=0","https://www.youtube.com/channel/"+channelId+"/videos"),
            ("www.youtube.com/@FixtureCreator","https://www.youtube.com/@FixtureCreator/videos"),
            ("http://youtube.com/@FixtureCreator/streams#live","https://www.youtube.com/@FixtureCreator/videos"),
            ("https://www.youtube.com/@FixtureCreator/playlists?view=1","https://www.youtube.com/@FixtureCreator/videos"),
            ("https://www.youtube.com/c/FixtureCreator/videos","https://www.youtube.com/c/FixtureCreator/videos"),
            ("https://www.youtube.com/user/FixtureCreator/","https://www.youtube.com/user/FixtureCreator/videos"),
            ("https://www.youtube.com/@"+Uri.EscapeDataString("피팅채널"),"https://www.youtube.com/@"+Uri.EscapeDataString("피팅채널")+"/videos")
        };
        foreach(var sample in accepted)
            Assert(YouTubeService.TryChannelUrl(sample.Input,out var canonical) && canonical==sample.Canonical,"A supported explicit channel URL did not resolve to its videos page: "+sample.Input);

        string?[] rejected=[null,"","FixtureCreator","https://youtu.be/aqz-KE-bpKQ","https://www.youtube.com/watch?v=aqz-KE-bpKQ","https://www.youtube.com/playlist?list=PLfixture","https://www.youtube.com/results?search_query=FixtureCreator","https://www.youtube.com/channel/UCtooShort","https://www.youtube.com/channel/"+channelId+"extra","https://www.youtube.com/@","https://www.youtube.com/@FixtureCreator/about","https://www.youtube.com/@FixtureCreator//videos","https://www.youtube.com/@Fixture%20Creator","https://www.youtube.com/@Fixture%2FCreator","https://www.youtube.com/@Fixture%5CCreator","https://www.youtube.com/@Fixture%3FCreator","https://www.youtube.com.evil.invalid/@FixtureCreator","https://youtube.com@evil.invalid/@FixtureCreator","https://user@www.youtube.com/@FixtureCreator","https://www.youtube.com:444/@FixtureCreator","file:///youtube.com/@FixtureCreator","javascript:https://www.youtube.com/@FixtureCreator"];
        foreach(var input in rejected)
            Assert(!YouTubeService.TryChannelUrl(input,out var canonical) && canonical=="","An unresolved or unsafe channel input was accepted: "+(input??"<null>"));

        Assert(new VideoItem{ChannelUrl="https://www.youtube.com/@FixtureCreator"}.CanOpenChannel,"An explicit channel handle was disabled.");
        Assert(new VideoItem{ChannelId=channelId}.CanOpenChannel,"An exact channel ID was disabled.");
        Assert(new VideoItem{ChannelUrl="https://evil.invalid/@FixtureCreator",ChannelId=channelId}.CanOpenChannel,"An exact channel ID did not provide a safe fallback for an invalid URL.");
        Assert(!new VideoItem{Channel="FixtureCreator"}.CanOpenChannel,"A channel name was guessed into a navigation target.");
        Assert(!new VideoItem{Channel="FixtureCreator",ChannelId="UCtooShort",ChannelUrl="https://evil.invalid/@FixtureCreator"}.CanOpenChannel,"Invalid channel identity enabled navigation.");
        Assert(!new VideoItem().CanOpenChannel,"Missing owner identity enabled navigation.");

        var metadata=JsonSerializer.Deserialize<FeedNavigationMetadata>("""
            {"title":"Server channel title","filterKind":"channel","channel":{"id":"UC1234567890123456789012","title":"Server creator","url":"https://www.youtube.com/@FixtureCreator/videos","thumbnail":"https://images.fixture.invalid/avatar.png","description":"Server channel description"},"filters":[{"id":"opaque.latest","label":"서버 최신순","selected":true},{"id":"opaque.popular","label":"서버 인기순","selected":false},{"id":"opaque.oldest","label":"서버 오래된순","selected":false}]}
            """,JsonDefaults.Options)!;
        Assert(metadata.Title=="Server channel title" && metadata.FilterKind=="channel" && metadata.Channel is {Id:channelId,Title:"Server creator",Description:"Server channel description"},"Channel navigation metadata lost its server header.");
        Assert(metadata.Channel!.Url=="https://www.youtube.com/@FixtureCreator/videos" && metadata.Channel.Thumbnail=="https://images.fixture.invalid/avatar.png","Channel header lost its exact URL or avatar.");
        Assert(metadata.Filters.Select(filter=>filter.Id).SequenceEqual(new[]{"opaque.latest","opaque.popular","opaque.oldest"}) && metadata.Filters.Select(filter=>filter.Label).SequenceEqual(new[]{"서버 최신순","서버 인기순","서버 오래된순"}) && metadata.Filters.Single(filter=>filter.Selected).Id=="opaque.latest","Opaque server filter identity, order, label or selection was changed.");
        var empty=JsonSerializer.Deserialize<FeedNavigationMetadata>("{}",JsonDefaults.Options)!;
        Assert(empty.Channel==null && empty.Filters.Count==0,"Missing navigation metadata manufactured a channel or filters.");
        var home=JsonSerializer.Deserialize<FeedNavigationMetadata>("""
            {"title":"Server home","filterKind":"home","filters":[{"id":"home.new","label":"새로운 맞춤 동영상"},{"id":"home.recommended","label":"전체","selected":true},{"id":"home.music","label":"음악"},{"id":"home.mix","label":"믹스"}]}
            """,JsonDefaults.Options)!;
        Assert(home.Channel==null && home.FilterKind=="home" && home.Filters.Count==4 && home.Filters.Single(filter=>filter.Selected).Id=="home.recommended","Home chips were confused with channel sorting metadata.");
        var enriched=JsonSerializer.Deserialize<VideoItem>("""
            {"id":"fixture","title":"Fixture video","channel":"Server creator","channelId":"UC1234567890123456789012","channelUrl":"https://www.youtube.com/@FixtureCreator/videos","channelThumbnail":"https://images.fixture.invalid/avatar.png"}
            """,JsonDefaults.Options)!;
        Assert(enriched.CanOpenChannel && enriched.ChannelThumbnail==metadata.Channel.Thumbnail && enriched.ChannelUrl==metadata.Channel.Url,"Video card owner metadata did not deserialize independently of its thumbnail.");
        var oldHistory=JsonSerializer.Deserialize<VideoItem>("{\"id\":\"legacy\",\"channel\":\"Name only\"}",JsonDefaults.Options)!;
        Assert(oldHistory.Channel=="Name only" && !oldHistory.CanOpenChannel,"Legacy name-only history guessed a channel destination.");
    }

    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
}
