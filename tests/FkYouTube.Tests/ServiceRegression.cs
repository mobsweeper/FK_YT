using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using FkYouTube;
using FkYouTube.Services;

public static class ServiceRegression
{
    public static async Task RunAsync(string workspace)
    {
        var root=Path.GetFullPath(Path.Combine(workspace,"artifacts","service-regression","run-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var python=Path.Combine(workspace,"runtime","python","python.exe");
            ArgumentContracts(root);
            ChannelResponseFailures();
            await ProcessTimeoutAndCancellationAsync(python,root);
            await WatchRetryContractsAsync();
            await RunRatingContractsAsync(workspace);
            await DownloadPublicationAsync(root);
            await DownloadProgressBoundsAsync();
            await DownloadProcessLifetimeAsync(workspace, root);
            var worker=Path.Combine(workspace,"worker");
            var result=await ProcessRunner.RunAsync(python,["-B","-m","unittest","test_selected_ytdlp"],worker,TimeSpan.FromSeconds(30));
            Assert(result.ExitCode==0,"The offline selected CLI adapter contract failed: "+result.Error);
        }
        finally
        {
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    public static async Task RunRatingContractsAsync(string workspace)
    {
        var root=Path.GetFullPath(Path.Combine(workspace,"artifacts","rating-service-regression","run-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            AccessErrorContracts();
            RatingParserContracts();
            var paths=new RuntimePaths(root);var session=new SessionService(paths);
            await RatingPrivacyGuardsAsync(paths,session);
            await PublicDislikeContractsAsync(paths,session);
            await PublicDislikeFailuresAsync(paths,session);
            await PublicDislikeCancellationAsync(paths,session);
        }
        finally
        {
            Assert(root.StartsWith(Path.GetFullPath(Path.Combine(workspace,"artifacts","rating-service-regression"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Rating fixture cleanup escaped its root.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    private static void ChannelResponseFailures()
    {
        var parse=typeof(SessionService).GetMethod("ParseChannelResponse",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var code in new[]{"auth","unsupported"})
        {
            var rejected=false;
            try{parse.Invoke(null,[JsonSerializer.Serialize(new{type="error",code,message="private upstream diagnostic"})]);}
            catch(TargetInvocationException error)when(error.InnerException is InvalidDataException failure)
            {
                rejected=true;
                Assert(!failure.Message.Contains("private upstream"),"Channel UI copied an upstream private diagnostic.");
                Assert(failure.Message.Contains("로그인 연결")==(code=="auth"),"Malformed channel data was mislabeled as an authentication failure.");
            }
            Assert(rejected,"Error channel response was accepted.");
        }
    }

    public static void AccessErrorContracts()
    {
        const string secret="https://private.fixture.invalid/watch?cookie=secret-account-token";
        foreach(var (input,code) in new[]{
            ("ERROR: Sign in to confirm your age. "+secret,"signin_age"),("LOGIN_REQUIRED", "signin_age"),
            ("Sign in to confirm you're not a bot. "+secret,"bot"),("YouTube is requiring a captcha challenge before playback","bot"),
            ("This video is members-only. Sign in. "+secret,"member"),("Private video. Sign in if granted access.","private"),
            ("HTTP Error 429: Too Many Requests. cookie file "+secret,"rate_limit"),("has been rate-limited by YouTube for up to an hour","rate_limit"),
            ("Unable to resolve hostname. "+secret,"network"),("Video unavailable. "+secret,"unavailable"),
            ("Your cookies are no longer valid","session"),("DRM-protected video. Sign in.","generic"),
            ("Unknown media error. "+secret,"generic"),("FK_YT_ERROR:member\n","member"),
            ("FK_YT_ERROR:unknown\nSign in","generic"),("FK_YT_ERROR:signin_age\nFK_YT_ERROR:bot","generic")})
        {
            Assert(ProcessRunner.ClassifyError(input)==code,"Extraction failure was classified incorrectly: "+code);
            var failure=new YouTubeAccessException(code,true);
            Assert(failure.Code==code && failure.AuthenticationRequired==(code=="signin_age") && failure.InnerException is null && !failure.Message.Contains(secret),
                "Typed access error lost its classification or exposed upstream diagnostics.");
            Assert(!ProcessRunner.SafeError(input).Contains("secret-account-token"),"Safe error exposed a signed URL or account token.");
        }
        Assert(new YouTubeAccessException("not-allowlisted").Code=="generic" && !new YouTubeAccessException("not-allowlisted").AuthenticationRequired,"An unknown reason became an authentication failure.");
        Assert(ProcessRunner.SafeError("Sign in to confirm your age",true).Contains("연결한 프로필") && ProcessRunner.SafeError("Sign in to confirm your age").Contains("일반 모드"),
            "The authenticated and guest recovery directions were confused.");
    }

    private static void RatingParserContracts()
    {
        const string id="fixture0001";
        foreach(var value in new[]{"like","dislike","none","unknown"})
        {
            using var valid=JsonDocument.Parse(JsonSerializer.Serialize(new{type="rating",sourceVideoId=id,value,canRate=value!="unknown",verified=true}));
            var rating=YouTubeService.ParseRating(valid.RootElement,id);
            Assert(rating.VideoId==id && rating.Value==value && rating.CanRate==(value!="unknown") && rating.Verified,"A valid rating response lost its fields.");
        }
        foreach(var invalid in new[]{
            "[]","null","{}",
            "{\"type\":\"related\",\"sourceVideoId\":\"fixture0001\",\"value\":\"like\",\"canRate\":true}",
            "{\"type\":\"rating\",\"sourceVideoId\":\"fixture0002\",\"value\":\"like\",\"canRate\":true}",
            "{\"type\":\"rating\",\"sourceVideoId\":11,\"value\":\"like\",\"canRate\":true}",
            "{\"type\":\"rating\",\"sourceVideoId\":\"fixture0001\",\"value\":\"LIKE\",\"canRate\":true}",
            "{\"type\":\"rating\",\"sourceVideoId\":\"fixture0001\",\"value\":\"like\",\"canRate\":\"true\"}",
            "{\"type\":\"rating\",\"sourceVideoId\":\"fixture0001\",\"value\":\"like\",\"canRate\":true,\"verified\":1}"})
        {
            using var doc=JsonDocument.Parse(invalid);
            ExpectInvalid(()=>YouTubeService.ParseRating(doc.RootElement,id),"An invalid/mismatched rating response was accepted.");
        }
        using(var unconfirmed=JsonDocument.Parse("{\"type\":\"rating\",\"sourceVideoId\":\"fixture0001\",\"value\":\"like\",\"canRate\":true,\"verified\":false}"))
            ExpectInvalid(()=>YouTubeService.ParseRating(unconfirmed.RootElement,id,true),"A write without readback verification became success.");
        using(var missing=JsonDocument.Parse("{\"type\":\"rating\",\"sourceVideoId\":\"fixture0001\",\"value\":\"none\",\"canRate\":true}"))
            ExpectInvalid(()=>YouTubeService.ParseRating(missing.RootElement,id,true),"A write with missing verification became success.");
        var parseRelated=typeof(YouTubeService).GetMethod("ParseRelatedPage",BindingFlags.NonPublic|BindingFlags.Static)!;
        using(var related=JsonDocument.Parse("{\"type\":\"related\",\"sourceVideoId\":\"fixture0001\",\"items\":[],\"rating\":{\"value\":\"dislike\",\"canRate\":true}}"))
        {
            var selected=(RelatedVideosResult)parseRelated.Invoke(null,[related.RootElement,id,true])!;
            var guest=(RelatedVideosResult)parseRelated.Invoke(null,[related.RootElement,id,false])!;
            Assert(selected.Rating is{Value:"dislike",CanRate:true} && selected.Rating.VideoId==id,"Related metadata lost selected-account rating.");
            Assert(guest.Rating is{Value:"unknown",CanRate:false},"Guest/incognito related metadata exposed an actionable account rating.");
            var rejected=false;
            try{parseRelated.Invoke(null,[related.RootElement,"fixture0002",true]);}
            catch(TargetInvocationException ex)when(ex.InnerException is InvalidDataException){rejected=true;}
            Assert(rejected,"Related metadata from another video was accepted.");
        }
        const string descriptionText="첫 줄\n다음 줄\t<b>원문 문자열</b>\0";
        using(var described=JsonDocument.Parse(JsonSerializer.Serialize(new{type="related",sourceVideoId=id,items=Array.Empty<object>(),description=descriptionText})))
        {
            var response=(RelatedVideosResult)parseRelated.Invoke(null,[described.RootElement,id,false])!;
            Assert(response.Description==descriptionText.Replace("\0",""),"Related description lost paragraph/text content or retained unsafe control characters.");
        }
        using(var oversized=JsonDocument.Parse(JsonSerializer.Serialize(new{type="related",sourceVideoId=id,items=Array.Empty<object>(),description=new string('x',32767)+"😀suffix"})))
        {
            var response=(RelatedVideosResult)parseRelated.Invoke(null,[oversized.RootElement,id,false])!;
            Assert(response.Description?.Length==32767 && !char.IsHighSurrogate(response.Description[^1]),"Related description exceeded its bound or cut a surrogate pair.");
        }
        using(var malformed=JsonDocument.Parse("{\"type\":\"related\",\"sourceVideoId\":\"fixture0001\",\"items\":[],\"description\":42}"))
        {
            var rejected=false;try{parseRelated.Invoke(null,[malformed.RootElement,id,false]);}catch(TargetInvocationException ex)when(ex.InnerException is InvalidDataException){rejected=true;}
            Assert(rejected,"Malformed related description was accepted.");
        }
        foreach(var json in new[]{
            "[]","null","{}",
            "{\"id\":\"fixture0002\",\"likes\":1,\"dislikes\":2,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":-1,\"dislikes\":2,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":1,\"dislikes\":-2,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":\"1\",\"dislikes\":2,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":1.5,\"dislikes\":2,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":1,\"dislikes\":9223372036854775808,\"deleted\":false}",
            "{\"id\":\"fixture0001\",\"likes\":1,\"dislikes\":2,\"deleted\":\"false\"}",
            "{\"id\":\"fixture0001\",\"likes\":1,\"dislikes\":2}"})
        {
            using var doc=JsonDocument.Parse(json);
            ExpectInvalid(()=>YouTubeService.ParseDislikeStats(doc.RootElement,id),"An invalid RYD number/schema/video id was accepted.");
        }
        using(var valid=JsonDocument.Parse("{\"id\":\"fixture0001\",\"likes\":0,\"dislikes\":9223372036854775807,\"deleted\":false}"))
            Assert(YouTubeService.ParseDislikeStats(valid.RootElement,id) is{Likes:0,Dislikes:long.MaxValue},"Valid Int64 vote totals were truncated.");
        using(var deleted=JsonDocument.Parse("{\"id\":\"fixture0001\",\"likes\":1,\"dislikes\":2,\"deleted\":true}"))
            Assert(YouTubeService.ParseDislikeStats(deleted.RootElement,id)==null,"A deleted RYD video exposed a numeric total.");
    }

    private static async Task RatingPrivacyGuardsAsync(RuntimePaths paths,SessionService session)
    {
        var service=new YouTubeService(paths,session);
        const string url="https://www.youtube.com/watch?v=fixture0001";
        Assert(await service.GetRatingAsync(url,CancellationToken.None) is{Value:"unknown",CanRate:false},"Guest rating tried to query an account.");
        await ExpectAsync<InvalidOperationException>(()=>service.SetRatingAsync(url,"like",CancellationToken.None),"Guest could issue a rating write.");
        var fake=Path.Combine(paths.Root,"fixture-cookies.txt");
        File.WriteAllText(fake,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n",new UTF8Encoding(false));
        session.ImportFile(fake);
        Assert(await service.GetRatingAsync(url,CancellationToken.None) is{Value:"unknown",CanRate:false},"An unselected account could query a personal rating.");
        await ExpectAsync<InvalidOperationException>(()=>service.SetRatingAsync(url,"dislike",CancellationToken.None),"An unselected account could issue a rating write.");
        typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(session,new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2});
        Assert(await service.GetRatingAsync(url,CancellationToken.None,true) is{Value:"unknown",CanRate:false},"Incognito could query a personal rating.");
        foreach(var rating in new[]{"like","dislike","none"})
            await ExpectAsync<InvalidOperationException>(()=>service.SetRatingAsync(url,rating,CancellationToken.None,true),"Incognito could issue a rating write.");
        await ExpectAsync<InvalidDataException>(()=>service.SetRatingAsync(url,"LIKE",CancellationToken.None),"An invalid rating could reach the worker.");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(()=>service.GetRatingAsync(url,canceled.Token),"Canceled rating read could acquire cookies/start a worker.");
        await ExpectAsync<OperationCanceledException>(()=>service.SetRatingAsync(url,"like",canceled.Token),"Canceled rating write could acquire cookies/start a worker.");
        await ExpectAsync<OperationCanceledException>(()=>service.GetRelatedPageAsync(url,canceled.Token),"Canceled related request could acquire cookies/start a worker.");
        Assert(!Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),"Rejected rating operations left plaintext session leases.");
    }

    private static async Task PublicDislikeContractsAsync(RuntimePaths paths,SessionService session)
    {
        var factory=typeof(YouTubeService).GetMethod("CreateDislikeHttpHandler",BindingFlags.NonPublic|BindingFlags.Static)!;
        using(var isolated=(HttpClientHandler)factory.Invoke(null,null)!)
            Assert(!isolated.UseCookies && !isolated.AllowAutoRedirect && !isolated.UseDefaultCredentials && isolated.Credentials==null && isolated.DefaultProxyCredentials==null,
                "Public dislike transport retained ambient cookies/credentials or redirects.");
        var now=DateTimeOffset.Parse("2026-10-15T00:00:00Z");
        using var handler=new RatingHttpHandler((request,_)=>Task.FromResult(VotesResponse(RequestId(request))));
        using var http=RatingHttpClient(handler);var service=RatingService(paths,session,http,()=>now);
        const string url="https://www.youtube.com/watch?v=fixture0001&list=private-context&authuser=2";
        var stats=await service.GetDislikeStatsAsync(url,CancellationToken.None);
        Assert(stats is{VideoId:"fixture0001",Likes:123,Dislikes:456},"Public dislike GET did not parse expected totals.");
        Assert(ReferenceEquals(stats,await service.GetDislikeStatsAsync("https://youtu.be/fixture0001",CancellationToken.None)) && handler.Requests==1,"Canonical video IDs did not share the bounded memory cache.");
        now+=TimeSpan.FromMinutes(5)+TimeSpan.FromSeconds(1);
        await service.GetDislikeStatsAsync(url,CancellationToken.None);
        Assert(handler.Requests==2,"Expired public dislike values stayed cached.");
        for(var index=0;index<130;index++)await service.GetDislikeStatsAsync("https://youtu.be/cache"+index.ToString("D6"),CancellationToken.None);
        var cache=(System.Collections.IDictionary)typeof(YouTubeService).GetField("_dislikeCache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
        Assert(cache.Count<=128,"Public dislike memory cache grew beyond its bound.");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var previous=handler.Requests;
        await ExpectAsync<OperationCanceledException>(()=>service.GetDislikeStatsAsync("https://youtu.be/cache000129",canceled.Token),"Canceled cached lookup did not preserve cancellation.");
        await ExpectAsync<InvalidDataException>(()=>service.GetDislikeStatsAsync("https://other.invalid/watch?v=fixture0001",CancellationToken.None),"A non-YouTube URL reached public dislike service.");
        Assert(handler.Requests==previous && !Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),"Public dislike query acquired private cookie leases or sent invalid/canceled requests.");
    }

    private static async Task PublicDislikeFailuresAsync(RuntimePaths paths,SessionService session)
    {
        const string url="https://youtu.be/fixture0001";
        var now=DateTimeOffset.Parse("2026-10-15T00:00:00Z");
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))
        using(var http=RatingHttpClient(handler))
        {
            var service=RatingService(paths,session,http,()=>now);
            Assert(await service.GetDislikeStatsAsync(url,CancellationToken.None)==null && await service.GetDislikeStatsAsync(url,CancellationToken.None)==null && handler.Requests==1,"404 was displayed as zero or was repeatedly requested.");
        }
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(VotesResponse("fixture0001",deleted:true))))
        using(var http=RatingHttpClient(handler))
        {
            var service=RatingService(paths,session,http,()=>now);
            Assert(await service.GetDislikeStatsAsync(url,CancellationToken.None)==null && await service.GetDislikeStatsAsync(url,CancellationToken.None)==null && handler.Requests==1,"Deleted-video absence did not use a bounded negative cache.");
        }
        var attempts=0;
        using(var handler=new RatingHttpHandler((request,_)=>
        {
            if(++attempts>1)return Task.FromResult(VotesResponse(RequestId(request)));
            var limited=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(limited);
        }))
        using(var http=RatingHttpClient(handler))
        {
            var service=RatingService(paths,session,http,()=>now);
            await ExpectAsync<IOException>(()=>service.GetDislikeStatsAsync(url,CancellationToken.None),"429 became a numeric response.");
            await ExpectAsync<IOException>(()=>service.GetDislikeStatsAsync("https://youtu.be/fixture0002",CancellationToken.None),"429 backoff did not cover the whole API client.");
            Assert(handler.Requests==1,"429 triggered an immediate network retry.");
            now+=TimeSpan.FromSeconds(121);
            Assert(await service.GetDislikeStatsAsync(url,CancellationToken.None)!=null && handler.Requests==2,"Retry-After prevented refresh after its deadline.");
        }
        attempts=0;
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(VotesResponse(++attempts==1?"fixture0002":"fixture0001"))))
        using(var http=RatingHttpClient(handler))
        {
            var service=RatingService(paths,session,http,()=>now);
            await ExpectAsync<InvalidDataException>(()=>service.GetDislikeStatsAsync(url,CancellationToken.None),"RYD response from another video became success.");
            Assert(await service.GetDislikeStatsAsync(url,CancellationToken.None)!=null && handler.Requests==2,"Rejected RYD response was cached.");
        }
        using(var handler=new RatingHttpHandler((_,_)=>
        {
            var redirect=new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location=new Uri("https://other.invalid/private");return Task.FromResult(redirect);
        }))
        using(var http=RatingHttpClient(handler))
        {
            await ExpectAsync<IOException>(()=>RatingService(paths,session,http,()=>now).GetDislikeStatsAsync(url,CancellationToken.None),"A redirect became a public dislike response.");
            Assert(handler.Requests==1,"Public dislike request followed an untrusted redirect.");
        }
        foreach(var knownLength in new[]{false,true})
        {
            var stream=new RatingBodyStream(new byte[70*1024]);
            using var handler=new RatingHttpHandler((_,_)=>
            {
                var content=new StreamContent(stream);
                if(knownLength)content.Headers.ContentLength=70*1024;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
            });
            using var http=RatingHttpClient(handler);
            await ExpectAsync<InvalidDataException>(()=>RatingService(paths,session,http,()=>now).GetDislikeStatsAsync(url,CancellationToken.None),"Oversized RYD body was accepted.");
            Assert(stream.BytesRead<=64*1024+4096 && (knownLength?stream.BytesRead==0:true),"RYD body read exceeded its 64 KiB limit.");
        }
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{broken")})))
        using(var http=RatingHttpClient(handler))
            await ExpectAsync<InvalidDataException>(()=>RatingService(paths,session,http,()=>now).GetDislikeStatsAsync(url,CancellationToken.None),"Malformed RYD JSON escaped the safe response error.");
    }

    private static async Task PublicDislikeCancellationAsync(RuntimePaths paths,SessionService session)
    {
        const string url="https://youtu.be/fixture0001";
        var never=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled=new RatingBodyStream(Encoding.UTF8.GetBytes(VotesJson("fixture0001")),never.Task);
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stalled)})))
        using(var http=RatingHttpClient(handler))
        {
            var failure=await CaptureAsync(()=>RatingService(paths,session,http,()=>DateTimeOffset.UtcNow,TimeSpan.FromMilliseconds(100)).GetDislikeStatsAsync(url,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(failure is IOException{InnerException:TimeoutException} && stalled.CanceledReads==1,"A stalled RYD body did not time out and cancel its stream read.");
        }
        stalled=new RatingBodyStream(Encoding.UTF8.GetBytes(VotesJson("fixture0001")),never.Task);
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stalled)})))
        using(var http=RatingHttpClient(handler))
        using(var caller=new CancellationTokenSource())
        {
            var service=RatingService(paths,session,http,()=>DateTimeOffset.UtcNow);
            var read=service.GetDislikeStatsAsync(url,caller.Token);
            await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));caller.Cancel();
            var failure=await CaptureAsync(()=>read.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(failure is OperationCanceledException && stalled.CanceledReads==1,"Caller canceled RYD read became an internal timeout or kept reading.");
        }
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared=new RatingBodyStream(Encoding.UTF8.GetBytes(VotesJson("fixture0001")),release.Task);
        using(var handler=new RatingHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(shared)})))
        using(var http=RatingHttpClient(handler))
        using(var waitingCaller=new CancellationTokenSource())
        {
            var service=RatingService(paths,session,http,()=>DateTimeOffset.UtcNow);
            var first=service.GetDislikeStatsAsync(url,CancellationToken.None);
            await shared.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued=service.GetDislikeStatsAsync(url,waitingCaller.Token);waitingCaller.Cancel();
            await ExpectAsync<OperationCanceledException>(()=>queued,"A queued RYD consumer ignored cancellation.");
            Assert(!first.IsCompleted && handler.Requests==1 && shared.CanceledReads==0,"Queued cancellation canceled the active public request.");
            release.TrySetResult();
            Assert(await first.WaitAsync(TimeSpan.FromSeconds(3))!=null && await service.GetDislikeStatsAsync(url,CancellationToken.None)!=null && handler.Requests==1,"Queued/cache consumers duplicated the active RYD GET.");
        }
    }

    private static YouTubeService RatingService(RuntimePaths paths,SessionService session,HttpClient http,Func<DateTimeOffset> clock,TimeSpan? timeout=null)=>
        (YouTubeService)typeof(YouTubeService).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single().Invoke([paths,session,http,timeout??TimeSpan.FromSeconds(5),clock]);
    private static HttpClient RatingHttpClient(HttpMessageHandler handler)=>new(handler){Timeout=Timeout.InfiniteTimeSpan};
    private static string RequestId(HttpRequestMessage request)
    {
        Assert(request.Method==HttpMethod.Get && request.Content==null && request.RequestUri?.Scheme=="https" && request.RequestUri.Host=="returnyoutubedislikeapi.com" && request.RequestUri.AbsolutePath=="/votes",
            "Public dislikes used an unexpected host/path/method/body.");
        Assert(request.Headers.Count()==1 && request.Headers.Accept.Single().MediaType=="application/json" && request.Headers.Authorization==null && request.Headers.Referrer==null && !request.Headers.Contains("Cookie"),
            "Public dislikes transmitted browser/account/authentication headers.");
        var query=request.RequestUri!.Query;
        Assert(query.StartsWith("?videoId=",StringComparison.Ordinal) && query.Length==20 && !query.Contains('&'),"Private URL/account context was forwarded to RYD.");
        return query[9..];
    }
    private static string VotesJson(string id,bool deleted=false)=>JsonSerializer.Serialize(new{id,likes=123,dislikes=456,deleted});
    private static HttpResponseMessage VotesResponse(string id,bool deleted=false)=>new(HttpStatusCode.OK){Content=new StringContent(VotesJson(id,deleted),Encoding.UTF8,"application/json")};
    private sealed class RatingHttpHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        public int Requests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {RequestId(request);Requests++;return send(request,cancellationToken);}
    }
    private sealed class RatingBodyStream(byte[] bytes,Task? release=null):Stream
    {
        private int _position;
        public int BytesRead {get;private set;}
        public int CanceledReads {get;private set;}
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            Started.TrySetResult();
            if(release!=null)try{await release.WaitAsync(cancellationToken);}catch(OperationCanceledException){CanceledReads++;throw;}
            cancellationToken.ThrowIfCancellationRequested();
            var read=Math.Min(buffer.Length,bytes.Length-_position);
            bytes.AsMemory(_position,read).CopyTo(buffer);_position+=read;BytesRead+=read;return read;
        }
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush(){}
    }
    private static void ExpectInvalid(Action action,string message)
    {var rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Assert(rejected,message);}
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {try{await action();return null;}catch(Exception ex){return ex;}}
    private static async Task ExpectAsync<T>(Func<Task> action,string message)where T:Exception
    {Assert(await CaptureAsync(action) is T,message);}

    private static async Task WatchRetryContractsAsync()
    {
        var retry=typeof(YouTubeService).GetMethod("RetryMarkWatchedAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        async Task<bool> Run(Func<Task<CommandResult>> request,CancellationToken token=default)
            =>await (Task<bool>)retry.Invoke(null,[request,token,(Func<int,CancellationToken,Task>)((_,_)=>Task.CompletedTask)])!;
        var attempts=0;
        Assert(await Run(()=>++attempts<3?Task.FromException<CommandResult>(new IOException("fixture timeout",new TimeoutException())):
            Task.FromResult(new CommandResult(0,"[youtube] Marking watched",""))) && attempts==3,"Internal timeouts did not retry exactly twice before success.");
        attempts=0;
        Assert(!await Run(()=>{attempts++;return Task.FromException<CommandResult>(new IOException("fixture timeout",new TimeoutException()));}) && attempts==3,
            "Exhausted watch timeouts stayed pending or retried beyond the limit.");
        using var canceled=new CancellationTokenSource();attempts=0;var cancellationPropagated=false;
        try{await Run(()=>{attempts++;canceled.Cancel();return Task.FromCanceled<CommandResult>(canceled.Token);},canceled.Token);}
        catch(OperationCanceledException){cancellationPropagated=true;}
        Assert(cancellationPropagated && attempts==1,"User cancellation retried a history request.");
        attempts=0;var errorPropagated=false;
        try{await Run(()=>{attempts++;return Task.FromException<CommandResult>(new IOException("fixture failure"));});}
        catch(IOException){errorPropagated=true;}
        Assert(errorPropagated && attempts==1,"An ordinary failure was mistaken for an internal timeout.");
    }

    private static void ArgumentContracts(string root)
    {
        var channel=new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2};
        var merged=YouTubeAccountOptions.ExtractorArguments(channel,"comment_sort=new;max_comments=100,100,30,3");
        Assert(merged=="youtube:data_sync_id=123456||fixture_user;player_skip=webpage,configs;player_client=default,web_safari;comment_sort=new;max_comments=100,100,30,3",
            "Account options and comment limits were not merged into one extractor clause.");
        Assert(YouTubeAccountOptions.ExtractorArguments(null)=="","Incognito inherited account extractor options.");
        Assert(YouTubeAccountOptions.ExtractorArguments(null,"comment_sort=top")=="youtube:comment_sort=top","Public comment sorting was lost.");
        foreach(var invalid in new[]{channel with{AuthUser=-1},channel with{AuthUser=100},channel with{DataSyncId="123456||u;comment_sort=new"},
            channel with{PageId="999"},channel with{DataSyncId="123456||u||extra"},channel with{DataSyncId="alpha||user",PageId="alpha"}})
        {
            var rejected=false;
            try{YouTubeAccountOptions.ExtractorArguments(invalid);}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"An invalid selected account option was accepted.");
        }
        foreach(var extra in new[]{"data_sync_id=other","player_skip=webpage","session_index=0","player_client=web","comment_sort=new;comment_sort=top","comment_sort=new\n"})
        {
            var rejected=false;
            try{YouTubeAccountOptions.ExtractorArguments(channel,extra);}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"An extra option could overwrite account authority.");
        }
        YouTubeAccountOptions.Validate(new YouTubeChannel{Id="fixture-primary",DataSyncId="fixture_user||",AuthUser=1});
        var paths=new RuntimePaths(root);
        var selected=YouTubeAccountOptions.CreateCommand(paths,["--ignore-config","--no-plugin-dirs","--simulate"],channel);
        Assert(selected.Executable==paths.Python && selected.Arguments[1]==Path.Combine(root,"worker","selected_ytdlp.py"),
            "Selected extraction bypassed the authoritative adapter.");
        Assert(!selected.Arguments.Any(arg=>arg.Contains("fixture-brand",StringComparison.Ordinal)),"Private identity leaked into command arguments.");
        using var request=JsonDocument.Parse(selected.StandardInput!);
        Assert(request.RootElement.GetProperty("accountContext").GetProperty("authUser").GetInt32()==2,"Private context lost AuthUser.");
        var guest=YouTubeAccountOptions.CreateCommand(paths,["--simulate"],null);
        Assert(guest.Executable==paths.YtDlp && guest.StandardInput is null,"Incognito retained private request context.");
    }

    private static async Task ProcessTimeoutAndCancellationAsync(string python,string root)
    {
        var script=Path.Combine(root,"owned-timeout.py");
        File.WriteAllText(script,"""
            import json, os, subprocess, sys, time
            child=subprocess.Popen([sys.executable,'-B','-c','import time,sys; f=open(sys.argv[1]); time.sleep(120)',sys.argv[1]])
            f=open(sys.argv[1])
            with open(sys.argv[2],'w') as marker: json.dump([os.getpid(),child.pid],marker)
            print('ready',flush=True)
            time.sleep(120)
            """,new UTF8Encoding(false));
        foreach(var canceled in new[]{false,true})
        {
            var cookie=Path.Combine(root,"fixture-cookie-"+canceled+".txt");
            var marker=Path.Combine(root,"pids-"+canceled+".json");
            File.WriteAllText(cookie,"offline-fixture");
            using var caller=new CancellationTokenSource();
            var run=ProcessRunner.RunAsync(python,["-B",script,cookie,marker],root,canceled?TimeSpan.FromSeconds(20):TimeSpan.FromSeconds(1.5),caller.Token);
            if(canceled)
            {
                var ready=Stopwatch.StartNew();
                while(!File.Exists(marker) && ready.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                Assert(File.Exists(marker),"Cancellation fixture did not start.");
                caller.Cancel();
            }
            Exception? failure=null;
            try{await run;}catch(Exception ex){failure=ex;}
            if(canceled)Assert(failure is OperationCanceledException && caller.IsCancellationRequested,"User cancellation became a request failure.");
            else Assert(failure is IOException{InnerException:TimeoutException} && !caller.IsCancellationRequested,"Internal timeout remained user cancellation.");
            Assert(File.Exists(marker),"Process timeout fixture never started.");
            var ids=JsonSerializer.Deserialize<int[]>(File.ReadAllText(marker))!;
            Assert(ids.All(id=>!IsAlive(id)),"ProcessRunner returned before its owned process tree exited.");
            using(var exclusive=new FileStream(cookie,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
            File.Delete(cookie);
            Assert(!File.Exists(cookie),"ProcessRunner retained a disposable cookie file handle.");
        }
    }

    private static async Task DownloadPublicationAsync(string root)
    {
        var create=typeof(DownloadService).GetMethod("CreateStageDirectory",BindingFlags.NonPublic|BindingFlags.Static)!;
        var cleanup=typeof(DownloadService).GetMethod("CleanupStageAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        var publish=typeof(DownloadService).GetMethod("PublishAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        var stage=(string)create.Invoke(null,null)!;
        try
        {
            Assert(!stage.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Download staging stayed in the destination/workspace.");
            Assert(new DirectoryInfo(Path.GetDirectoryName(stage)!).GetAccessControl().AreAccessRulesProtected,"Download staging inherited public access rules.");
            var completed=Path.Combine(stage,"video.mkv");var destination=Path.Combine(root,"video.mkv");
            var bytes=Enumerable.Range(0,256*1024).Select(i=>(byte)(i%251)).ToArray();
            File.WriteAllBytes(completed,bytes);
            File.WriteAllText(destination,"original destination");
            async Task Publish(CancellationToken token=default)
            {
                var snapshot=new FileInfo(destination);
                await (Task)publish.Invoke(null,[completed,destination,snapshot.Exists,snapshot.Exists?(DateTime?)snapshot.LastWriteTimeUtc:null,
                    snapshot.Exists?(long?)snapshot.Length:null,token])!;
            }
            // %TEMP% and the workspace may be on different drives. This executes
            // the same copy-then-rename path on either topology.
            await Publish();
            Assert(File.ReadAllBytes(destination).SequenceEqual(bytes),"Publication did not copy the whole completed file.");
            var backup=Directory.EnumerateFiles(root,"*.fkyt-backup-*.mkv").SingleOrDefault();
            Assert(backup is not null && File.ReadAllText(backup)=="original destination","Replacement did not preserve the previous destination in a recoverable backup.");
            Assert(File.Exists(completed),"Publication prematurely deleted its staging source.");
            Assert(!Directory.EnumerateFiles(root,".fkyt-publish-*.tmp").Any(),"Successful publication left a temporary destination file.");
            File.WriteAllText(destination,"locked original");
            using(var locked=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var rejected=false;
                try{await Publish();}catch(IOException){rejected=true;}
                Assert(rejected,"A locked destination unexpectedly replaced successfully.");
                Assert(File.ReadAllText(destination)=="locked original","A failed publication partially replaced the original file.");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.Cancel();var rejected=false;
                try{await Publish(cancellation.Token);}catch(OperationCanceledException){rejected=true;}
                Assert(rejected && File.ReadAllText(destination)=="locked original","Canceled publication changed the destination.");
            }
            var previous=new FileInfo(destination);var oldStamp=previous.LastWriteTimeUtc;var oldLength=previous.Length;
            File.WriteAllText(destination,"manually changed original with different size");
            var conflict=false;
            try{await (Task)publish.Invoke(null,[completed,destination,true,(DateTime?)oldStamp,(long?)oldLength,CancellationToken.None])!;}
            catch(IOException){conflict=true;}
            Assert(conflict && File.ReadAllText(destination).StartsWith("manually changed",StringComparison.Ordinal),"A concurrent destination edit was overwritten.");
            File.WriteAllText(destination,"edited-a");
            var matching=new FileInfo(destination);var matchingStamp=matching.LastWriteTimeUtc;var matchingLength=matching.Length;
            File.WriteAllText(destination,"edited-b");File.SetLastWriteTimeUtc(destination,matchingStamp);
            await (Task)publish.Invoke(null,[completed,destination,true,(DateTime?)matchingStamp,(long?)matchingLength,CancellationToken.None])!;
            Assert(Directory.EnumerateFiles(root,"*.fkyt-backup-*.mkv").Any(file=>File.ReadAllText(file)=="edited-b"),"Same-size/time concurrent destination contents were irrecoverably lost.");
            File.Delete(destination);
            await Publish();
            Assert(File.ReadAllBytes(destination).SequenceEqual(bytes),"New-file publication failed.");
            Assert(!Directory.EnumerateFiles(root,".fkyt-publish-*.tmp").Any(),"Failed or canceled publication left a final-copy temporary file.");
            var unrelated=Path.Combine(root,"job-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated,"keep.txt"),"fixture");
            await (Task)cleanup.Invoke(null,[unrelated])!;
            Assert(File.Exists(Path.Combine(unrelated,"keep.txt")),"Stage cleanup deleted a directory outside its private root.");
        }
        finally{await (Task)cleanup.Invoke(null,[stage])!;}
        Assert(!Directory.Exists(stage),"Owned download staging survived cleanup.");
    }

    public static async Task RunDownloadBoundariesAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","final-hardening-20261015","boundaries"));
        var root=Path.Combine(parent,"download-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var failures=new List<Exception>();
            try{await DownloadProgressBoundsAsync();}catch(Exception ex){failures.Add(ex);}
            try{await DownloadProcessLifetimeAsync(workspace,root);}catch(Exception ex){failures.Add(ex);}
            if(failures.Count>0)throw new AggregateException(failures);
        }
        finally
        {
            Assert(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Download fixture cleanup escaped its root.");
            Directory.Delete(root,true);
        }
    }

    public static async Task RunDownloadReviewAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","review-fixes-20261015"));
        var root=Path.Combine(parent,"download-review-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{await DownloadPublicationAsync(root);await DownloadProgressBoundsAsync();await RunDownloadCompletionReviewAsync(workspace);}
        finally
        {
            Assert(Path.GetDirectoryName(Path.GetFullPath(root))==parent && Path.GetFileName(root).StartsWith("download-review-",StringComparison.Ordinal),"Download review fixture escaped its parent.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    public static async Task RunDownloadCompletionReviewAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","review16-r2-20261015"));
        var root=Path.Combine(parent,"download-completion-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var failures=new List<Exception>();
            try{DownloadCompletionProgressLifetime();}
            catch(Exception error){failures.Add(new InvalidOperationException("completion progress: "+error.Message,error));}
            foreach(var mode in new[]{"normal-new-result","normal-overwrite-result"})
            {
                try{await DownloadProcessFixtureAsync(workspace,root,mode);}
                catch(Exception error){failures.Add(new InvalidOperationException(mode+": "+error.Message,error));}
            }
            if(failures.Count>0)throw new AggregateException(failures);
        }
        finally
        {
            Assert(Path.GetDirectoryName(Path.GetFullPath(root))==parent && Path.GetFileName(root).StartsWith("download-completion-fixture-",StringComparison.Ordinal),
                "Download completion fixture escaped its parent.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    private static void DownloadCompletionProgressLifetime()
    {
        var lifetimeType=typeof(MainWindow).GetNestedType("DownloadProgressLifetime",BindingFlags.NonPublic);
        Assert(lifetimeType is not null,"Download progress has no terminal-state guard for already queued callbacks.");
        var previous=SynchronizationContext.Current;var context=new QueuedDownloadProgressContext();var delivered=new List<object>();
        var active=new DownloadProgressUpdate(DownloadProgressKind.Downloading,20);
        var latest=new DownloadProgressUpdate(DownloadProgressKind.Downloading,99.99);
        var merging=new DownloadProgressUpdate(DownloadProgressKind.Merging);
        var remuxing=new DownloadProgressUpdate(DownloadProgressKind.Remuxing);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var progress=(IProgress<DownloadProgressUpdate>)Activator.CreateInstance(lifetimeType!,[new Action<DownloadProgressUpdate>(update=>delivered.Add(update))])!;
            progress.Report(active);
            Assert(delivered.Count==0 && context.Pending==1,"Download progress fixture did not hold its UI callback.");
            context.Drain();Assert(delivered.SequenceEqual([active]),"Live download progress was suppressed.");
            for(var i=0;i<10000;i++)progress.Report(new(DownloadProgressKind.Downloading,i/100d));
            Assert(context.Pending==1,"A busy UI accumulated one callback per download progress line instead of one pending update.");
            Task.Run(()=>Parallel.For(0,1000,i=>progress.Report(new(DownloadProgressKind.Downloading,i/10d)))).GetAwaiter().GetResult();
            progress.Report(latest);
            Assert(context.Pending==1,"Background progress producers accumulated extra callbacks.");
            context.Drain();Assert(delivered.SequenceEqual([active,latest]),"Coalesced progress did not deliver the latest value.");
            progress.Report(merging);context.Drain();
            Assert(Equals(delivered[^1],merging),"A later merge phase was not delivered.");
            progress.Report(remuxing);context.Drain();
            Assert(Equals(delivered[^1],remuxing),"A later remux phase was not delivered.");
            progress.Report(active);
            lifetimeType!.GetMethod("Complete")!.Invoke(progress,null);
            delivered.Add("final completion with retained backup");context.Drain();
            progress.Report(merging);
            Assert(context.Pending==0,"Completed progress scheduled another UI callback.");context.Drain();
            Assert(delivered.SequenceEqual([active,latest,merging,remuxing,"final completion with retained backup"]),
                "Queued or later download progress overwrote the terminal completion message.");
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }

    private sealed class QueuedDownloadProgressContext:SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback,object? State)> _pending=[];
        public int Pending=>_pending.Count;
        public override void Post(SendOrPostCallback callback,object? state)=>_pending.Enqueue((callback,state));
        public void Drain(){while(_pending.TryDequeue(out var pending))pending.Callback(pending.State);}
    }

    private static async Task DownloadProgressBoundsAsync()
    {
        var read=typeof(DownloadService).GetMethod("ReadProgressAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        async Task Read(string text,DownloadProgress progress,CancellationToken token=default)
        {
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader=new StreamReader(stream);
            await (Task)read.Invoke(null,[reader,progress,token])!;
        }
        var language=UiText.Language;
        try
        {
            foreach(var selected in new[]{"ko","en","ja"})
            {
                UiText.SetLanguage(selected);
                var normal=new DownloadProgress();
                await Read(new string('x',64*1024)+"\r\nFK_PROGRESS:60.0%\n[Merger] fixture\r[VideoRemuxer] fixture\nFK_PROGRESS:invalid\nFK_PROGRESS:-5%\nFK_PROGRESS:120%\n",normal);
                Assert(normal.Updates.SequenceEqual(new DownloadProgressUpdate[]{new(DownloadProgressKind.Downloading,60),
                    new(DownloadProgressKind.Merging),new(DownloadProgressKind.Remuxing),new(DownloadProgressKind.Downloading,0),new(DownloadProgressKind.Downloading,100)}),
                    "Bounded progress reader lost CR/LF, phase distinctions, percentage bounds or language-neutral payloads in "+selected+".");
            }
        }
        finally{UiText.SetLanguage(language);}
        await ExpectAsync<IOException>(()=>Read(new string('x',64*1024+1)+"\n",new DownloadProgress()),
            "Download output accepted a line above 65,536 characters.");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(()=>Read("FK_PROGRESS:20.0%\n",new DownloadProgress(),canceled.Token),
            "Canceled progress reader consumed output.");
    }

    private static async Task DownloadProcessLifetimeAsync(string workspace,string root)
    {
        var failures=new List<Exception>();
        foreach(var mode in new[]{"normal","normal-highest","oversized-stdout","oversized-stderr","cancel","callback-failure"})
        {
            try{await DownloadProcessFixtureAsync(workspace,root,mode);}
            catch(Exception ex){failures.Add(new InvalidOperationException("download "+mode,ex));}
        }
        if(failures.Count>0)throw new AggregateException(failures);
    }

    private static async Task DownloadProcessFixtureAsync(string workspace,string root,string mode)
    {
        var app=Path.Combine(root,"download-"+mode);var paths=new RuntimePaths(app);
        var runtime=Path.Combine(app,"runtime");Directory.CreateDirectory(runtime);
        var python=Path.Combine(runtime,"python");
        var junction=new ProcessStartInfo("pwsh.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+python.Replace("'","''")+"' -Value '"+
            Path.Combine(workspace,"runtime","python").Replace("'","''")+"' | Out-Null"})junction.ArgumentList.Add(argument);
        using(var process=Process.Start(junction)!)
        {
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();await Task.WhenAll(output,error);
            Assert(process.ExitCode==0,"Download fixture Python junction could not be created.");
        }
        using var caller=new CancellationTokenSource();
        Task? run=null;
        var marker=Path.Combine(app,"pids.json");var destination=Path.Combine(app,"fixture.mkv");
        if(mode=="normal-overwrite-result")File.WriteAllText(destination,"recoverable previous fixture");
        var cookie=Path.Combine(app,"offline-cookie.txt");File.WriteAllText(cookie,"synthetic cookie handle sentinel");
        try
        {
            var ffmpeg=Path.Combine(runtime,"ffmpeg");Directory.CreateDirectory(ffmpeg);
            File.WriteAllText(Path.Combine(ffmpeg,"ffmpeg.exe"),"not executed by synthetic worker");
            var worker=Path.Combine(app,"worker");Directory.CreateDirectory(worker);
            var script="""
                import json, os, subprocess, sys, time
                sys.stdin.readline()
                mode=MODE
                cookie=COOKIE
                marker=MARKER
                handle=open(cookie)
                ids=[os.getpid()]
                if not mode.startswith('normal'):
                    child=subprocess.Popen([sys.executable,'-B','-c','import sys,time; handle=open(sys.argv[1]); time.sleep(120)',cookie])
                    ids.append(child.pid)
                with open(marker+'.format','w',encoding='utf-8') as output: output.write(sys.argv[sys.argv.index('--format')+1])
                with open(marker,'w',encoding='utf-8') as output: json.dump(ids,output)
                if mode.startswith('normal'):
                    print('FK_PROGRESS:60.0%',flush=True)
                    print('[Merger] fixture',file=sys.stderr,flush=True)
                    output=sys.argv[sys.argv.index('--output')+1].replace('%(ext)s','mkv').replace('%%','%')
                    with open(output,'wb') as completed: completed.write(b'synthetic complete download')
                elif mode.startswith('oversized-'):
                    stream=sys.stdout if mode.endswith('stdout') else sys.stderr
                    stream.write('x'*(128*1024)+'\n'+'x'*(1024*1024))
                    stream.flush()
                    time.sleep(120)
                elif mode=='callback-failure':
                    print('FK_PROGRESS:20.0%',flush=True)
                    time.sleep(120)
                else:
                    time.sleep(120)
                """.Replace("MODE",JsonSerializer.Serialize(mode)).Replace("COOKIE",JsonSerializer.Serialize(cookie)).Replace("MARKER",JsonSerializer.Serialize(marker));
            File.WriteAllText(Path.Combine(worker,"selected_ytdlp.py"),script,new UTF8Encoding(false));
            var progress=new DownloadProgress(mode=="callback-failure");
            var service=new DownloadService(paths);
            run=service.DownloadAsync("https://www.youtube.com/watch?v=fixture0001",mode=="normal-highest"?0:720,destination,cookie,progress,caller.Token,
                new YouTubeChannel{Id="fixture-primary",DataSyncId="fixture_user||",AuthUser=1});
            var ready=Stopwatch.StartNew();
            while(!File.Exists(marker) && !run.IsCompleted && ready.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
            Assert(File.Exists(marker),"Synthetic download worker did not start.");
            Assert(File.ReadAllText(marker+".format")==(mode=="normal-highest"?"bv*+ba/b":"bv*[height<=720]+ba/b[height<=720]"),
                "Download format lowered the highest-quality sentinel or lost an explicit resolution bound.");
            if(mode=="cancel")caller.Cancel();
            Exception? failure=null;
            try{await run.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception ex){failure=ex;}
            if(mode.StartsWith("normal",StringComparison.Ordinal))
            {
                Assert(failure is null && File.ReadAllText(destination)=="synthetic complete download" &&
                    progress.Updates.Contains(new(DownloadProgressKind.Downloading,60)) && progress.Updates.Contains(new(DownloadProgressKind.Merging)),
                    "Normal download did not publish complete output and progress.");
                if(mode.EndsWith("-result",StringComparison.Ordinal))
                {
                    var result=run.GetType().GetProperty("Result")?.GetValue(run);
                    var resultType=result?.GetType();
                    var published=resultType?.GetProperty("Destination")?.GetValue(result) as string;
                    var backup=resultType?.GetProperty("BackupPath")?.GetValue(result) as string;
                    Assert(resultType?.Name=="DownloadResult" && published==Path.GetFullPath(destination),
                        "A completed download did not return its published destination and backup result.");
                    if(mode=="normal-overwrite-result")
                        Assert(backup is not null && File.Exists(backup) && File.ReadAllText(backup)=="recoverable previous fixture" &&
                            Directory.EnumerateFiles(app,"*.fkyt-backup-*.mkv").Single()==backup,
                            "The completion result lost the exact retained backup or changed previous bytes.");
                    else Assert(backup is null && !Directory.EnumerateFiles(app,"*.fkyt-backup-*.mkv").Any(),
                        "A new-file completion reported or created a replacement backup.");
                    var format=typeof(MainWindow).GetMethod("DownloadCompletionMessage",BindingFlags.NonPublic|BindingFlags.Static);
                    Assert(format is not null,"The UI has no final completion message composed from the published download result.");
                    var message=(string)format!.Invoke(null,[result])!;
                    Assert(message.Contains(Path.GetFileName(destination),StringComparison.Ordinal) &&
                        (backup is null?!message.Contains("기존 파일 보관",StringComparison.Ordinal):message.Contains("기존 파일 보관: "+Path.GetFileName(backup),StringComparison.Ordinal)),
                        "The final UI completion message lost the retained backup discovery path.");
                }
            }
            else
            {
                Assert(mode switch{
                    "cancel"=>failure is OperationCanceledException,
                    "callback-failure"=>failure is InvalidOperationException,
                    _=>failure is IOException},"Output failure or cancellation did not stop the download promptly: "+(failure?.GetType().Name??"accepted"));
                Assert(!File.Exists(destination),"Failed/canceled download published a destination.");
            }
            var ids=JsonSerializer.Deserialize<int[]>(File.ReadAllText(marker))!;
            Assert(ids.All(id=>!IsAlive(id)),"Download returned before its owned child/grandchild exited.");
            using(var exclusive=new FileStream(cookie,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        }
        finally
        {
            caller.Cancel();
            if(run is not null)try{await run.WaitAsync(TimeSpan.FromSeconds(12));}catch(Exception){}
            Directory.Delete(python);
        }
    }

    private sealed class DownloadProgress(bool fail=false):IProgress<DownloadProgressUpdate>
    {
        public List<DownloadProgressUpdate> Updates{get;}=[];
        public void Report(DownloadProgressUpdate value)
        {
            if(fail)throw new InvalidOperationException("synthetic progress callback failure");
            lock(Updates)Updates.Add(value);
        }
    }

    private static bool IsAlive(int id)
    {
        try{using var process=Process.GetProcessById(id);return !process.HasExited;}
        catch(ArgumentException){return false;}
    }
    private static void Assert(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
}
