using System.IO;
using System.Text.Json;
using FkYouTube;
using FkYouTube.Services;

internal static class MetadataSecurityRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        void Check(string name, Action check)
        {
            try { check(); Console.WriteLine("PASS metadata " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.GetType().Name); }
        }
        Check("malformed resolution rows are skipped", () =>
        {
            using var doc = JsonDocument.Parse("""{"formats":[null,42,{}, {"vcodec":"avc1","height":720}]}""");
            if (!YouTubeService.ParseResolutions(doc.RootElement).SequenceEqual(new[] { 720 })) throw new Exception();
        });
        foreach (var json in new[] { "[]", "null", "42" })
        {
            Check("resolution root " + json, () => ExpectSafeFailure(json, e => YouTubeService.ParseResolutions(e)));
            Check("prepared root " + json, () => ExpectSafeFailure(json, e => YouTubeService.ParsePreparedVideo(e)));
        }
        foreach (var json in new[]
        {
            """{"requested_formats":{}}""",
            """{"requested_formats":[null]}""",
            """{"url":"https://media.fixture.invalid/video","protocol":42}""",
            """{"url":{},"protocol":"https"}""",
            """{"url":"https://media.fixture.invalid/video","http_headers":{"User-Agent":42}}""",
            """{"url":"https://media.fixture.invalid/video","http_headers":[]}"""
        }) Check("prepared invalid field " + json, () => ExpectSafeFailure(json, e => YouTubeService.ParsePreparedVideo(e)));
        Check("prepared optional captions share timedtext boundary without dropping valid media", () =>
        {
            const string signedUrl="https://www.youtube.com/api/timedtext?v=fixture0001&sig=A%2fb%2BC%3D&lang=ja";
            var subtitles=new Dictionary<string,object>
            {
                ["ja"]=new{url=signedUrl},
                ["en"]=new{url="https://other.fixture.invalid/private"},
                ["ko"]=new{url="https://www.youtube.com/watch?v=fixture0001"},
                ["fr"]=new{url="https://user@www.youtube.com/api/timedtext"},
                ["de"]=new{url="https://www.youtube.com:8443/api/timedtext"},
                ["es"]=new{url="https://www.youtube.com/api/timedtext#fragment"},
                ["zh"]=new{url="https://www.youtube.com/api/timedtext\n"},
                ["en,all"]=new{url=signedUrl}
            };
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{url="https://media.fixture.invalid/video",protocol="https",
                http_headers=new Dictionary<string,string>{{"User-Agent","fixture-agent"},{"Cookie","fixture=offline-only"}},requested_subtitles=subtitles}));
            var prepared=YouTubeService.ParsePreparedVideo(doc.RootElement);
            if(prepared.Captions.Count!=1 || prepared.Captions[0]!=new PreparedCaption("ja",signedUrl) ||
                prepared.Headers["User-Agent"]!="fixture-agent" || prepared.Headers["Cookie"]!="fixture=offline-only" || string.IsNullOrEmpty(prepared.MediaUrl))throw new Exception();
        });
        PreparedDirectHeaderContracts(Check);
        Check("invalid time metadata cannot throw during rendering", () =>
        {
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.MaxValue, TimeSpan.MaxValue.TotalSeconds })
                if (VideoItem.FormatTime(value) != "") throw new Exception();
            if (VideoItem.FormatTime(65) != "1:05" || VideoItem.FormatTime(-1) != "0:00") throw new Exception();
        });
        Check("malformed comment rows and fields remain bounded", () =>
        {
            using var doc = JsonDocument.Parse("""{"comments":[null,42,{"author":{},"text":[],"like_count":"1","parent":{}},{"author":"fixture","text":"hello","like_count":3,"parent":"root"}]}""");
            var comments = YouTubeService.ParseComments(doc.RootElement);
            if (comments.Count != 2 || comments[0].Likes != 0 || comments[0].Parent != null ||
                comments[1] != new CommentItem("fixture", "hello", 3, "root")) throw new Exception();
            using var large = JsonDocument.Parse(JsonSerializer.Serialize(new { comments = Enumerable.Repeat(new { text = "fixture" }, 150) }));
            if (YouTubeService.ParseComments(large.RootElement).Count != 100) throw new Exception();
        });
        Check("wrong comments container returns safe error", () => ExpectSafeFailure("{\"comments\":{}}", e => YouTubeService.ParseComments(e)));
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static void PreparedDirectHeaderContracts(Action<string,Action> check)
    {
        Dictionary<string,string> HeaderSet(int first,int count)=>Enumerable.Range(first,count)
            .ToDictionary(index=>"X-Fixture-"+index,index=>"value-"+index,StringComparer.Ordinal);
        PreparedVideo Parse(string json)
        {using var doc=JsonDocument.Parse(json);return YouTubeService.ParsePreparedVideo(doc.RootElement);}
        void Reject(string name,string json)=>check("prepared headers "+name,()=>ExpectSafeFailure(json,e=>YouTubeService.ParsePreparedVideo(e)));

        check("prepared headers exact name and value limits preserve Cookie and tab values",()=>
        {
            var name=new string('X',128);var value=new string('v',8192);
            var headers=new Dictionary<string,string>{{name,value},{"Cookie","fixture=offline-only"},{"Referer","https://www.youtube.com/"},{"User-Agent","fixture\tagent"}};
            var prepared=Parse(DirectHeadersJson(headers));
            if(prepared.Headers.Count!=4 || prepared.Headers[name]!=value || prepared.Headers["Cookie"]!="fixture=offline-only" ||
                prepared.Headers["User-Agent"]!="fixture\tagent" || prepared.MediaUrl!="https://media.fixture.invalid/stream-0?sig=fixture")throw new Exception();
        });
        check("prepared headers all ASCII token punctuation remains valid",()=>
        {
            var name="AZaz09!#$%&'*+-.^_|~"+(char)96;
            if(Parse(DirectHeadersJson(new Dictionary<string,string>{{name,"fixture"}})).Headers[name]!="fixture")throw new Exception();
        });
        check("prepared headers value bound counts characters without forcing ASCII",()=>
        {
            var value=new string('가',8192);
            if(Parse(DirectHeadersJson(new Dictionary<string,string>{{"X-Fixture",value}})).Headers["X-Fixture"]!=value)throw new Exception();
        });
        check("prepared headers one object allows sixteen distinct names",()=>
        {
            if(Parse(DirectHeadersJson(HeaderSet(0,16))).Headers.Count!=16)throw new Exception();
        });
        check("prepared headers split objects allow sixteen merged names",()=>
        {
            var prepared=Parse(DirectHeadersJson(HeaderSet(0,8),HeaderSet(8,8)));
            if(prepared.Headers.Count!=16 || !prepared.MediaUrl.StartsWith("edl://",StringComparison.Ordinal))throw new Exception();
        });
        check("prepared headers matching split names merge case insensitively",()=>
        {
            var first=HeaderSet(0,16);
            var second=first.ToDictionary(pair=>pair.Key.ToLowerInvariant(),pair=>pair.Value,StringComparer.Ordinal);
            var prepared=Parse(DirectHeadersJson(first,second));
            if(prepared.Headers.Count!=16 || prepared.Headers["x-fixture-15"]!="value-15")throw new Exception();
        });
        Reject("object rejects seventeen entries",DirectHeadersJson(HeaderSet(0,17)));
        Reject("split rejects seventeen merged names",DirectHeadersJson(HeaderSet(0,8),HeaderSet(8,9)));
        Reject("name rejects length 129",DirectHeadersJson(new Dictionary<string,string>{{new string('X',129),"fixture"}}));
        Reject("value rejects length 8193",DirectHeadersJson(new Dictionary<string,string>{{"User-Agent",new string('v',8193)}}));
        var invalidNames=new[]{"","Bad Header","Bad:Header","Bad,Header","Bad/Header","Bad\\Header","Bad(Header)","Bad[Header]",
            "Bad=Header","Bad?Header","Bad{Header}","Bad@Header","Bad\"Header","Bad;Header","BadéHeader","Bad\tHeader","Bad\rHeader","Bad\nHeader","Bad\0Header","Bad\u007fHeader"};
        for(var index=0;index<invalidNames.Length;index++)
            Reject("rejects invalid ASCII token name "+index,DirectHeadersJson(new Dictionary<string,string>{{invalidNames[index],"fixture"}}));
        foreach(var value in new[]{"bad\rvalue","bad\nvalue","bad\0value"})
            Reject("existing forbidden value character "+(int)value[3],DirectHeadersJson(new Dictionary<string,string>{{"User-Agent",value}}));
        Reject("split keeps conflicting header rejection",DirectHeadersJson(
            new Dictionary<string,string>{{"User-Agent","first"}},new Dictionary<string,string>{{"user-agent","second"}}));
        var duplicateEntries=string.Join(",",Enumerable.Range(0,17).Select(index=>JsonSerializer.Serialize(index%2==0?"User-Agent":"user-agent")+":\"fixture\""));
        Reject("object counts repeated names before merge","{\"url\":\"https://media.fixture.invalid/video\",\"protocol\":\"https\",\"http_headers\":{"+duplicateEntries+"}}");
    }

    private static string DirectHeadersJson(params Dictionary<string,string>[] headers)
    {
        var streams=headers.Select((values,index)=>new{url="https://media.fixture.invalid/stream-"+index+"?sig=fixture",protocol="https",http_headers=values}).ToArray();
        return streams.Length==1?JsonSerializer.Serialize(streams[0]):JsonSerializer.Serialize(new{requested_formats=streams});
    }

    private static void ExpectSafeFailure(string json, Func<JsonElement, object> parse)
    {
        using var doc = JsonDocument.Parse(json);
        try { parse(doc.RootElement); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid metadata was not rejected with a handled data error.");
    }
}
