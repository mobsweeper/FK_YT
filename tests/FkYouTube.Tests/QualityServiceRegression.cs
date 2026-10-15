using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text;
using FkYouTube;
using FkYouTube.Services;

public static class QualityServiceRegression
{
    public static async Task RunAsync(string workspace)
    {
        await RunSelectedAccountResponseErrorsAsync(workspace);
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","quality-service-regression"));
        var root=Path.GetFullPath(Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N")));
        if(!root.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quality fixture escaped its temporary parent.");
        Directory.CreateDirectory(root);
        try
        {
            await HighestAvailableFormatAsync(workspace,root);
            PreparedHlsParserContracts();
            var paths=new RuntimePaths(root);
            var service=new YouTubeService(paths,new SessionService(paths));
            var builder=typeof(YouTubeService).GetMethod("ResolutionArguments",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var account=new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2};
            foreach(var channel in new YouTubeChannel?[]{null,account})
            {
                var arguments=(IReadOnlyList<string>)builder.Invoke(service,[null,channel,"https://www.youtube.com/watch?v=aqz-KE-bpKQ"])!;
                var request=JsonSerializer.Serialize(new{arguments,accountContext=channel},JsonDefaults.Options);
                var result=await ProcessRunner.RunAsync(Path.Combine(workspace,"runtime","python","python.exe"),
                    ["-B",Path.Combine(workspace,"worker","test_quality_query.py"),"--fixture"],root,TimeSpan.FromSeconds(30),standardInput:request);
                Check(result.ExitCode==0,"Offline quality fixture did not execute: "+result.Error);
                using var response=JsonDocument.Parse(result.Output);
                var data=response.RootElement;
                Check(data.GetProperty("exitCode").GetInt32()==0,"Resolution query lost valid format metadata after stream probe failure.");
                Check(data.GetProperty("streamProbeAttempts").GetInt32()==0,"Metadata query attempted to download or test a media stream.");
                Check(data.GetProperty("accountPreserved").GetBoolean(),"Resolution query lost selected account authority.");
                Check(data.GetProperty("skipDownload").GetBoolean() && !data.GetProperty("markWatched").GetBoolean(),
                    "Resolution query enabled downloads or remote history writes.");
                Check(YouTubeService.ParseResolutions(data).SequenceEqual(new[]{720,1080}),"Resolution query did not expose all extracted format heights.");
            }
        }
        finally { Directory.Delete(root,true); }
    }

    public static async Task RunSelectedAccountResponseErrorsAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","selected-account-response-regression"));
        var root=Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N"));var paths=new RuntimePaths(root);
        try
        {
            var python=Path.Combine(root,"runtime","python");Directory.CreateDirectory(python);
            foreach(var source in Directory.EnumerateFiles(Path.Combine(workspace,"runtime","python")))File.Copy(source,Path.Combine(python,Path.GetFileName(source)));
            Directory.CreateDirectory(Path.Combine(root,"worker"));
            var session=new SessionService(paths);
            typeof(SessionService).GetField("<SelectedChannel>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,
                new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"});
            var service=new YouTubeService(paths,session);
            File.WriteAllText(Path.Combine(root,"worker","selected_ytdlp.py"),
                "import sys,json\nr=json.loads(sys.stdin.readline())\nassert r['accountContext']['authUser']==2\nprint('{\"https://private.fixture.invalid/?token=private-upstream-secret\":')\n");
            Exception? failure=null;
            try{await service.ResolveNextAsync("https://www.youtube.com/watch?v=fixture0001",0,CancellationToken.None);}
            catch(Exception error){failure=error;}
            Check(failure is InvalidDataException && failure is not YouTubeAccessException &&
                !failure.ToString().Contains("private-upstream-secret") && !failure.ToString().Contains("private.fixture.invalid") &&
                failure.InnerException is null,
                "Successful malformed extractor JSON escaped the safe metadata failure contract: "+failure?.GetType().Name);
            Check(session.SelectedChannel is {AuthUser:2,Id:"fixture"} && !Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),
                "Malformed extractor JSON changed selected authority or retained a temporary lease.");
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Selected-account response fixture escaped its root.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    private static async Task HighestAvailableFormatAsync(string workspace,string root)
    {
        var formatBuilder=typeof(YouTubeService).GetMethod("VideoFormat",BindingFlags.NonPublic|BindingFlags.Static)!;
        var script=Path.Combine(root,"format-selection.py");
        File.WriteAllText(script,"""
            import contextlib, copy, io, json, sys
            import yt_dlp
            request=json.loads(sys.stdin.readline())
            formats=[{'format_id':'audio','url':'https://media.fixture.invalid/audio','protocol':'https','ext':'m4a','vcodec':'none','acodec':'mp4a','abr':128},
              {'format_id':'dash-360','url':'https://media.fixture.invalid/video','protocol':'https','ext':'mp4','vcodec':'avc1','acodec':'none','height':360,'width':640}]
            for height in (720,1080):
              formats.append({'format_id':'hls-'+str(height),'url':'https://manifest.googlevideo.com/api/manifest/hls_playlist/fixture/'+str(height)+'/index.m3u8',
                'manifest_url':'https://manifest.googlevideo.com/api/manifest/hls_variant/fixture/file/index.m3u8',
                'protocol':'m3u8_native','ext':'mp4','vcodec':'avc1','acodec':'mp4a','height':height,'width':height*16//9})
            if request['highDash']:
              formats.append({'format_id':'dash-2160','url':'https://media.fixture.invalid/2160','protocol':'https','ext':'mp4','vcodec':'avc1','acodec':'none','height':2160,'width':3840})
            fixture={'id':'fixture0001','title':'Offline quality selection fixture','extractor':'Youtube',
              'is_live':False,'live_status':'not_live','formats':formats}
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
              with yt_dlp.YoutubeDL({'format':request['format'],'simulate':True,'skip_download':True,'check_formats':False,'quiet':True,'no_warnings':True}) as ydl:
                ydl._check_formats=lambda *a,**kw: (_ for _ in ()).throw(AssertionError('Unexpected media probe'))
                data=ydl.process_ie_result(copy.deepcopy(fixture),download=False)
            print(json.dumps(data))
            """,new UTF8Encoding(false));
        foreach(var sample in new[]{(MaxHeight:0,HighDash:false,Height:1080,Format:"hls-1080"),
            (MaxHeight:720,HighDash:false,Height:720,Format:"hls-720"),(MaxHeight:0,HighDash:true,Height:2160,Format:"dash-2160+audio")})
        {
            var format=(string)formatBuilder.Invoke(null,[sample.MaxHeight])!;
            var result=await ProcessRunner.RunAsync(Path.Combine(workspace,"runtime","python","python.exe"),["-B",script],root,TimeSpan.FromSeconds(30),
                standardInput:JsonSerializer.Serialize(new{format,highDash=sample.HighDash}));
            Check(result.ExitCode==0,"Pinned yt-dlp format selection fixture failed: "+result.Error);
            using var data=JsonDocument.Parse(result.Output);
            Check(data.RootElement.GetProperty("height").GetInt32()==sample.Height && data.RootElement.GetProperty("format_id").GetString()==sample.Format,
                "Highest-available selection preferred lower video-only media, exceeded the height cap, or lowered higher DASH media: "+format);
            var prepared=YouTubeService.ParsePreparedVideo(data.RootElement);
            Check(prepared.Resolutions.SequenceEqual(sample.HighDash?new[]{360,720,1080,2160}:new[]{360,720,1080}) &&
                prepared.MediaUrl.StartsWith(sample.HighDash?"edl://":"https://manifest.googlevideo.com/",StringComparison.Ordinal),
                "The selected highest format could not cross the prepared-media boundary.");
        }
    }

    private static void PreparedHlsParserContracts()
    {
        const string selectedUrl="https://manifest.googlevideo.com/api/manifest/hls_playlist/fixture/1080/index.m3u8?sig=A%2fb%2BC%3D";
        Dictionary<string,object?> Variant()=>new()
        {
            ["url"]=selectedUrl,["manifest_url"]="https://manifest.googlevideo.com/api/manifest/hls_variant/fixture/file/index.m3u8",
            ["protocol"]="m3u8_native",["vcodec"]="avc1.640028",["acodec"]="mp4a.40.2",["height"]=1080,
            ["is_live"]=false,["live_status"]="not_live",["has_drm"]=false,
            ["http_headers"]=new Dictionary<string,string>{{"User-Agent","fixture-agent"},{"Accept","*/*"},{"Accept-Language","en-US,en;q=0.5"},{"Sec-Fetch-Mode","navigate"}}
        };
        PreparedVideo Parse(Dictionary<string,object?> value)
        {using var json=JsonDocument.Parse(JsonSerializer.Serialize(value));return YouTubeService.ParsePreparedVideo(json.RootElement);}
        foreach(var protocol in new[]{"m3u8_native","m3u8"})
        {
            var value=Variant();value["protocol"]=protocol;var parsed=Parse(value);
            Check(parsed.MediaUrl==selectedUrl && parsed.Headers["User-Agent"]=="fixture-agent" && parsed.HttpChunkSize is null,
                "A selected muxed HTTPS VOD HLS variant was changed or rejected.");
        }
        var single=Variant();single["requested_formats"]=new[]{Variant()};
        Check(Parse(single).MediaUrl==selectedUrl,"A single selected muxed HLS format was treated as a mixed stream.");
        var invalid=new List<Dictionary<string,object?>>();
        foreach(var change in new (string Key,object? Value)[]{("is_live",true),("is_live",null),("live_status","is_live"),("live_status","is_upcoming"),
            ("live_status","was_live"),("live_status",null),("was_live",true),("has_drm",true),("has_drm","maybe"),
            ("vcodec","none"),("vcodec",null),("acodec","none"),("acodec",null),("fragments",Array.Empty<object>()),
            ("fragment_base_url","https://media.fixture.invalid/"),("hls_aes",new{uri="https://media.fixture.invalid/key"}),
            ("extra_param_to_segment_url","token=private"),("downloader_options",new{http_chunk_size=10485760})})
        {var value=Variant();value[change.Key]=change.Value;invalid.Add(value);}
        foreach(var missing in new[]{"is_live","live_status","vcodec","acodec","manifest_url"}){var value=Variant();value.Remove(missing);invalid.Add(value);}
        foreach(var url in new[]{"http://manifest.googlevideo.com/api/manifest/hls_playlist/fixture/index.m3u8",selectedUrl+"#fragment",
            selectedUrl+"\n","https://user@manifest.googlevideo.com/api/manifest/hls_playlist/fixture/index.m3u8",
            "https://manifest.googlevideo.com:8443/api/manifest/hls_playlist/fixture/index.m3u8",
            "https://manifest.googlevideo.com/api/manifest/hls_variant/fixture/file/index.m3u8","https://media.fixture.invalid/master.m3u8"})
        {var value=Variant();value["url"]=url;invalid.Add(value);}
        foreach(var header in new[]{"Cookie","Authorization","Proxy-Authorization","X-Forwarded-For","Host","Unknown-Required","Bad Header"})
        {var value=Variant();value["http_headers"]=new Dictionary<string,string>{{header,"fixture-only"}};invalid.Add(value);}
        foreach(var header in new[]{"User-Agent","Accept"})
        {var value=Variant();value["http_headers"]=new Dictionary<string,string>{{header,"bad\r\nvalue"}};invalid.Add(value);}
        var mixed=Variant();mixed["requested_formats"]=new object[]{Variant(),new{url="https://media.fixture.invalid/audio",protocol="https",vcodec="none",acodec="mp4a"}};invalid.Add(mixed);
        var twoHls=Variant();twoHls["requested_formats"]=new[]{Variant(),Variant()};invalid.Add(twoHls);
        var rootDrm=Variant();rootDrm["has_drm"]=true;rootDrm["requested_formats"]=new[]{Variant()};invalid.Add(rootDrm);
        var rootCookie=Variant();rootCookie["http_headers"]=new Dictionary<string,string>{{"Cookie","fixture-only"}};rootCookie["requested_formats"]=new[]{Variant()};invalid.Add(rootCookie);
        foreach(var value in invalid)
        {
            var rejected=false;try{Parse(value);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Unsafe, non-VOD, split, DRM, or unbounded HLS metadata crossed the prepared-media boundary.");
        }
    }

    public static async Task RunSubtitleCatalogAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","subtitle-catalog-regression"));
        var root=Path.GetFullPath(Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N")));
        Check(root.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Subtitle catalog fixture escaped its parent.");
        Directory.CreateDirectory(root);
        try
        {
            SubtitleCatalogParserContracts();
            var paths=new RuntimePaths(root);var session=new SessionService(paths);var service=new YouTubeService(paths,session);
            var builder=typeof(YouTubeService).GetMethod("SubtitleCatalogArguments",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var account=new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2};
            var script=Path.Combine(root,"catalog-options.py");
            File.WriteAllText(script,"""
                import contextlib, copy, io, json, sys
                import yt_dlp
                from youtube_session import create_youtube_dl
                r=json.loads(sys.stdin.readline()); parsed=yt_dlp.parse_options(r['arguments'])
                fixture={'id':'fixture0001','title':'Offline caption fixture','extractor':'Youtube',
                  'formats':[{'format_id':'fixture','url':'https://fixture.invalid/audio','ext':'m4a','vcodec':'none','acodec':'mp4a'}],
                  'subtitles':{'ja':[{'ext':'vtt','url':'https://www.youtube.com/api/timedtext?v=fixture0001&lang=ja'}]},
                  'automatic_captions':{lang:[{'ext':'vtt','url':'https://www.youtube.com/api/timedtext?v=fixture0001&tlang='+lang}]
                    for lang in ('es','de','zh-Hans','fr-ja')}}
                writes=[]; probes=[]
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                  with create_youtube_dl(parsed.ydl_opts,r.get('accountContext')) as ydl:
                    ydl._write_subtitles=lambda *a,**kw: writes.append(1)
                    ydl._check_formats=lambda *a,**kw: probes.append(1)
                    data=ydl.process_ie_result(copy.deepcopy(fixture),download=True)
                    opts=ydl.params
                print(json.dumps({'simulate':opts.get('simulate'),'autoDictionary':opts.get('writeautomaticsub'),
                  'writeManual':bool(opts.get('writesubtitles')),'requestedCaptions':len(data.get('requested_subtitles') or {}),
                  'captionWrites':len(writes),'streamProbes':len(probes),'languages':list(data['automatic_captions'])}))
                """,new UTF8Encoding(false));
            foreach(var channel in new YouTubeChannel?[]{null,account})
            {
                var args=(IReadOnlyList<string>)builder.Invoke(service,[null,channel,"https://www.youtube.com/watch?v=fixture0001"])!;
                var required=new[]{"--ignore-config","--no-plugin-dirs","--no-cache-dir","--skip-download","--no-mark-watched","--simulate","--no-write-subs","--write-auto-subs","--no-check-formats","--ignore-no-formats-error","--dump-single-json"};
                Check(required.All(args.Contains) && !args.Contains("--no-simulate") && !args.Contains("--list-subs") && !args.Contains("--no-write-auto-subs") && args[args.ToList().IndexOf("--sub-langs")+1]=="^$",
                    "Catalog metadata flags enabled downloads or failed to construct translated-caption metadata.");
                var command=YouTubeAccountOptions.CreateCommand(paths,args,channel);
                Check(channel is null?command.Executable==paths.YtDlp && command.StandardInput==null:command.Executable==paths.Python && command.StandardInput!=null,
                    "Catalog request lost selected-account routing or guest isolation.");
                if(channel!=null)
                {
                    using var authority=JsonDocument.Parse(command.StandardInput!);
                    Check(authority.RootElement.GetProperty("accountContext").GetProperty("authUser").GetInt32()==2 &&
                        authority.RootElement.GetProperty("accountContext").GetProperty("dataSyncId").GetString()==account.DataSyncId,"Catalog adapter changed selected account authority.");
                }
                var result=await ProcessRunner.RunAsync(Path.Combine(workspace,"runtime","python","python.exe"),["-B",script],root,TimeSpan.FromSeconds(30),
                    standardInput:JsonSerializer.Serialize(new{arguments=args,accountContext=channel},JsonDefaults.Options));
                Check(result.ExitCode==0,"Pinned catalog simulation fixture failed: "+result.Error);
                using var doc=JsonDocument.Parse(result.Output);var data=doc.RootElement;
                Check(data.GetProperty("simulate").GetBoolean() && data.GetProperty("autoDictionary").GetBoolean() && !data.GetProperty("writeManual").GetBoolean() &&
                    data.GetProperty("requestedCaptions").GetInt32()==0 && data.GetProperty("captionWrites").GetInt32()==0 && data.GetProperty("streamProbes").GetInt32()==0 && data.GetProperty("languages").GetArrayLength()==4,
                    "Pinned yt-dlp catalog processing wrote/requested captions or removed translated-language metadata.");
            }
            await SelectedSubtitleCatalogFixtureAsync(workspace,paths,session,service,account);
            await RunPreparedCaptionResolutionAsync(workspace);
        }
        finally{Directory.Delete(root,true);}
    }

    public static async Task RunPreparedCaptionResolutionAsync(string workspace)
    {
        var parent=Path.GetFullPath(Path.Combine(workspace,"artifacts","prepared-captions-regression"));
        var root=Path.Combine(parent,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var paths=new RuntimePaths(root);var runtime=Path.GetDirectoryName(paths.Python)!;Directory.CreateDirectory(runtime);
            foreach(var file in Directory.EnumerateFiles(Path.Combine(workspace,"runtime","python")))File.Copy(file,Path.Combine(runtime,Path.GetFileName(file)));
            var session=new SessionService(paths);var service=new YouTubeService(paths,session);
            var account=new YouTubeChannel{Id="fixture-brand",PageId="123456",DataSyncId="123456||fixture_user",AuthUser=2};
            typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(session,account);
            var worker=Path.Combine(root,"worker");Directory.CreateDirectory(worker);
            var importPaths=JsonSerializer.Serialize(new[]{Path.Combine(workspace,"runtime","python","Lib","site-packages"),Path.Combine(workspace,"worker")});
            File.WriteAllText(Path.Combine(worker,"selected_ytdlp.py"),"import sys\nsys.path[:0]="+importPaths+"\n"+"""
                import contextlib, copy, io, json, pathlib
                import yt_dlp
                from youtube_session import create_youtube_dl
                request=json.loads(sys.stdin.readline());args=sys.argv[1:]; parsed=yt_dlp.parse_options(args)
                mode=pathlib.Path('caption-mode.txt').read_text(); manual=mode in ('manual','both'); automatic=mode!='manual'
                auto_language={'automatic-original':'ja-orig','regional-original':'zh-Hans-orig'}.get(mode,'ja')
                caption=lambda kind: [{'ext':'vtt','url':'https://www.youtube.com/api/timedtext?v=fixture0001&kind='+kind}]
                fixture={'id':'fixture0001','title':'Offline prepared caption fixture','extractor':'Youtube','protocol':'https',
                  'url':'https://media.fixture.invalid/stream','formats':[{'format_id':'fixture','url':'https://media.fixture.invalid/stream','ext':'mp4','vcodec':'avc1','acodec':'mp4a','height':720}],
                  'subtitles':{'ja':caption('manual')} if manual else {},'automatic_captions':{auto_language:caption('automatic')} if automatic else {}}
                writes=[];probes=[]
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                  with create_youtube_dl(parsed.ydl_opts,request['accountContext']) as ydl:
                    ydl._write_subtitles=lambda *a,**kw:writes.append(1)
                    ydl._check_formats=lambda *a,**kw:probes.append(1)
                    data=ydl.process_ie_result(copy.deepcopy(fixture),download=True)
                pathlib.Path('caption-contract.json').write_text(json.dumps({'arguments':args,'authUser':request['accountContext']['authUser'],
                  'captionWrites':len(writes),'streamProbes':len(probes),'subPattern':parsed.ydl_opts['subtitleslangs']}))
                print(json.dumps(data))
                """,new UTF8Encoding(false));
            foreach(var sample in new[]{(Mode:"manual",Preference:"ja",Language:"ja"),(Mode:"automatic",Preference:"ja",Language:"ja"),
                (Mode:"both",Preference:"ja",Language:"ja"),(Mode:"automatic-original",Preference:"ja",Language:"ja-orig"),
                (Mode:"automatic-original",Preference:"JA-ORIG",Language:"ja-orig"),(Mode:"regional-original",Preference:"zh-Hans",Language:"zh-Hans-orig")})
            {
                File.WriteAllText(Path.Combine(root,"caption-mode.txt"),sample.Mode);
                var prepared=await service.ResolveNextAsync("https://www.youtube.com/watch?v=fixture0001",720,CancellationToken.None,subtitleLanguage:sample.Preference);
                Check(prepared.Captions.Count==1 && prepared.Captions[0].Language==sample.Language && prepared.Captions[0].Url.Contains(sample.Mode is "manual" or "both"?"kind=manual":"kind=automatic",StringComparison.Ordinal),
                    "Prepared extraction omitted the preferred original caption or lost manual-over-automatic priority: "+sample.Mode+"/"+sample.Preference);
                using var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"caption-contract.json")));var data=contract.RootElement;
                Check(data.GetProperty("authUser").GetInt32()==2 && data.GetProperty("captionWrites").GetInt32()==0 && data.GetProperty("streamProbes").GetInt32()==0,
                    "Prepared caption resolution changed selected account context, wrote captions, or probed streams.");
                var arguments=data.GetProperty("arguments").EnumerateArray().Select(item=>item.GetString()!).ToArray();
                Check(arguments.Contains("--simulate") && arguments.Contains("--skip-download") && arguments.Contains("--no-mark-watched") &&
                    arguments.Contains("--write-subs") && arguments.Contains("--write-auto-subs") && arguments[Array.IndexOf(arguments,"--sub-langs")+1]==YouTubeService.PreferredSubtitlePattern(sample.Preference),
                    "Prepared caption flags lost selection preferences or simulation/history protections.");
            }
            File.WriteAllText(Path.Combine(root,"caption-mode.txt"),"both");
            var uncapped=await service.ResolveNextAsync("https://www.youtube.com/watch?v=fixture0001",0,CancellationToken.None,subtitleLanguage:"ja");
            using(var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"caption-contract.json"))))
            {
                var arguments=contract.RootElement.GetProperty("arguments").EnumerateArray().Select(item=>item.GetString()!).ToArray();
                Check(arguments[Array.IndexOf(arguments,"--format")+1]=="bv*+ba/b" && uncapped.Captions.Count==1,
                    "Uncapped best quality was converted to a one-pixel height filter during prepared extraction.");
            }
        }
        finally{Directory.Delete(root,true);}
    }

    private static void SubtitleCatalogParserContracts()
    {
        var metadata=new
        {
            id="fixture0001",
            subtitles=new Dictionary<string,object>{["ja"]=new[]{new{ext="srt",url="https://www.youtube.com/api/timedtext?v=fixture0001&fmt=srt",name="Japanese"},new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001&fmt=vtt",name="Japanese"}},
                ["en"]=new[]{new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001&lang=en",name="English"}}},
            automatic_captions=new Dictionary<string,object>{["ja"]=new[]{new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001&tlang=ja",name="Japanese auto"}},
                ["es"]=new[]{new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001&tlang=es",name="Spanish translated"}},
                ["zh-Hans-en"]=new[]{new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001&tlang=zh-Hans",name="Chinese from English"}}}
        };
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        var catalog=YouTubeService.ParseSubtitleCatalog(doc.RootElement,"fixture0001");
        Check(catalog.Count==5 && catalog.Count(item=>item.Language=="ja")==2 && catalog.Single(item=>item.Language=="ja" && !item.Automatic).Url.EndsWith("fmt=vtt") &&
            catalog.Any(item=>item.Language=="es" && item.Automatic) && catalog.Any(item=>item.Language=="zh-Hans-en" && item.Label.Contains("Chinese from English")),
            "All-language catalog lost manual/automatic distinction, translated keys, display names, or VTT preference.");
        foreach(var json in new[]{"[]","null","{}","{\"id\":\"fixture0002\"}","{\"id\":\"fixture0001\",\"subtitles\":[]}","{\"id\":\"fixture0001\",\"automatic_captions\":42}"})
        {
            using var invalid=JsonDocument.Parse(json);var rejected=false;
            try{YouTubeService.ParseSubtitleCatalog(invalid.RootElement,"fixture0001");}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Malformed or mismatched subtitle catalog was accepted.");
        }
        foreach(var url in new[]{"http://www.youtube.com/api/timedtext?v=fixture0001","https://other.invalid/api/timedtext?v=fixture0001","https://www.youtube.com/watch?v=fixture0001",
            "https://user@www.youtube.com/api/timedtext","https://www.youtube.com:8443/api/timedtext","https://www.youtube.com/api/timedtext\n","https://www.youtube.com/api/timedtext#fragment","https://www.youtube.com\\api/timedtext"})
            Check(!YouTubeService.TrySubtitleCatalogUrl(url,out var canonical) && canonical=="","Unsafe subtitle URL was accepted.");
        foreach(var language in new[]{"en\n","en,all","live_chat","../../en","(?i:en)","x",new string('a',65)})Check(!YouTubeService.IsValidSubtitleLanguage(language),"Unsafe subtitle language key was accepted.");
        var entries=new Dictionary<string,object>();
        for(var index=0;index<1100;index++)entries["zz-x"+index]=new[]{new{ext="vtt",url="https://www.youtube.com/api/timedtext?v=fixture0001",name="unknown"}};
        using(var bounded=JsonDocument.Parse(JsonSerializer.Serialize(new{id="fixture0001",subtitles=entries,automatic_captions=entries})))
            Check(YouTubeService.ParseSubtitleCatalog(bounded.RootElement,"fixture0001").Count==1024,"Subtitle catalog was not bounded to 1024 rows.");
        using(var unsafeRows=JsonDocument.Parse("{\"id\":\"fixture0001\",\"subtitles\":{\"en,all\":[{\"ext\":\"vtt\",\"url\":\"https://www.youtube.com/api/timedtext\"}],\"en\":[{\"ext\":\"vtt\",\"url\":\"https://other.invalid/private\"}],\"ja\":[{\"ext\":\"json3\",\"url\":\"https://www.youtube.com/api/timedtext\"}],\"es\":[{\"ext\":\"vtt\",\"url\":\"https://www.youtube.com/api/timedtext\",\"name\":\"bad\\nname\"}]}}"))
        {
            var safe=YouTubeService.ParseSubtitleCatalog(unsafeRows.RootElement,"fixture0001");
            Check(safe.Count==1 && safe[0].Language=="es" && !safe[0].Label.Any(char.IsControl),"Unsafe catalog rows/names were retained.");
        }
        Check(YouTubeService.PreferredSubtitlePattern(null)=="ko,en,ko-orig,en-orig","No-preference extraction lost its bounded Korean/English fallback.");
        Check(YouTubeService.PreferredSubtitlePattern("zh-hans")=="(?i:zh-hans(?:-orig)?),ko,en,ko-orig,en-orig","Next-file preference lost locale casing, its original-caption variant, or Korean/English fallback.");
        foreach(var sample in new[]{(Preference:"ja",Matches:new[]{"ja","ja-orig","JA-ORIG"},Rejects:new[]{"ja-JP","ja-JP-orig","ja-orig-extra","japanese"}),
            (Preference:"JA-ORIG",Matches:new[]{"ja","ja-orig","JA-ORIG"},Rejects:new[]{"ja-JP","ja-JP-orig","ja-orig-orig"}),
            (Preference:"zh-Hans",Matches:new[]{"zh-Hans","ZH-hANS-ORIG"},Rejects:new[]{"zh","zh-Hant","zh-Hant-orig","zh-Hans-en"}),
            (Preference:"en-US-orig",Matches:new[]{"en-US","EN-us-ORIG"},Rejects:new[]{"en","en-orig","en-GB","en-US-orig-orig"})})
        {
            var selected=YouTubeService.PreferredSubtitlePattern(sample.Preference).Split(',')[0];
            Check(sample.Matches.All(language=>System.Text.RegularExpressions.Regex.IsMatch(language,"\\A(?:"+selected+")\\z")) &&
                sample.Rejects.All(language=>!System.Text.RegularExpressions.Regex.IsMatch(language,"\\A(?:"+selected+")\\z")),
                "Preferred subtitle pattern lost original-caption equivalence or merged regional languages: "+sample.Preference);
        }
        var invalidPreference=false;try{YouTubeService.PreferredSubtitlePattern("en,all");}catch(InvalidDataException){invalidPreference=true;}
        Check(invalidPreference,"A selected language could inject an all-language extraction pattern.");
        var subtitles=new Dictionary<string,object>();foreach(var language in new[]{"ja","es","de","fr","ko","en","ko-orig","en-orig","zh"})subtitles[language]=new{url="https://www.youtube.com/api/timedtext?v=fixture0001"};
        using var prepared=JsonDocument.Parse(JsonSerializer.Serialize(new{url="https://media.fixture.invalid/stream",protocol="https",requested_subtitles=subtitles}));
        Check(YouTubeService.ParsePreparedVideo(prepared.RootElement).Captions.Count==8,"Prepared captions lost the selected non-Korean/English language or exceeded their bound.");
    }

    private static async Task SelectedSubtitleCatalogFixtureAsync(string workspace,RuntimePaths paths,SessionService session,YouTubeService service,YouTubeChannel account)
    {
        var runtime=Path.GetDirectoryName(paths.Python)!;Directory.CreateDirectory(runtime);
        foreach(var source in Directory.EnumerateFiles(Path.Combine(workspace,"runtime","python")))File.Copy(source,Path.Combine(runtime,Path.GetFileName(source)));
        var worker=Path.Combine(paths.Root,"worker");Directory.CreateDirectory(worker);
        File.WriteAllText(Path.Combine(worker,"selected_ytdlp.py"),"""
            import json, pathlib, sys, urllib.parse
            request=json.loads(sys.stdin.readline());context=request['accountContext']; args=sys.argv[1:]
            cookie=args[args.index('--cookies')+1] if '--cookies' in args else None
            contract={'authUser':context['authUser'] if context else None,'id':context['id'] if context else None,'cookieExists':bool(cookie and pathlib.Path(cookie).is_file()),
              'simulate':'--simulate' in args,'subPattern':args[args.index('--sub-langs')+1],'operation':request.get('operation')}
            pathlib.Path('catalog-contract.json').write_text(json.dumps(contract))
            mode=pathlib.Path('catalog-mode.txt').read_text() if pathlib.Path('catalog-mode.txt').exists() else 'good'
            video=urllib.parse.parse_qs(urllib.parse.urlsplit(args[-1]).query)['v'][0]
            print(json.dumps({'id':video if mode=='good' else 'fixture0002','subtitles':{'ja':[{'ext':'vtt','name':'Japanese','url':'https://www.youtube.com/api/timedtext?v='+video}]},
              'automatic_captions':{'es':[{'ext':'vtt','name':'Spanish translated','url':'https://www.youtube.com/api/timedtext?v='+video+'&tlang=es'}]}}))
            """,new UTF8Encoding(false));
        var fake=Path.Combine(paths.Root,"fixture-cookie.txt");File.WriteAllText(fake,"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\toffline-only\n");
        session.ImportFile(fake);typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(session,account);
        const string url="https://www.youtube.com/watch?v=fixture0001";
        var catalog=await service.GetSubtitleCatalogAsync(url,CancellationToken.None);
        using(var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.Root,"catalog-contract.json"))))
        {
            Check(catalog.Count==2 && contract.RootElement.GetProperty("authUser").GetInt32()==2 && contract.RootElement.GetProperty("id").GetString()==account.Id &&
                contract.RootElement.GetProperty("cookieExists").GetBoolean() && contract.RootElement.GetProperty("simulate").GetBoolean() && contract.RootElement.GetProperty("subPattern").GetString()=="^$" && contract.RootElement.GetProperty("operation").GetString()=="subtitle_catalog",
                "Actual catalog service discarded selected session identity or selected every caption for download.");
        }
        File.WriteAllText(Path.Combine(paths.Root,"catalog-mode.txt"),"mismatch");
        var mismatch=false;try{await service.GetSubtitleCatalogAsync(url,CancellationToken.None);}catch(InvalidDataException){mismatch=true;}
        Check(mismatch,"Actual catalog service accepted metadata from another video.");
        typeof(SessionService).GetField("_selectedChannelId",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(session,"missing-private-session");
        var privateRejected=false;try{await service.GetSubtitleCatalogAsync(url,CancellationToken.None);}catch(InvalidDataException){privateRejected=true;}
        Check(privateRejected,"An unavailable selected session silently fell back to another account.");
        File.WriteAllText(Path.Combine(paths.Root,"catalog-mode.txt"),"good");
        Check((await service.GetSubtitleCatalogAsync(url,CancellationToken.None,true)).Count==2,"Incognito could not read public caption metadata.");
        using(var guestContract=JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.Root,"catalog-contract.json"))))
            Check(guestContract.RootElement.GetProperty("authUser").ValueKind==JsonValueKind.Null && guestContract.RootElement.GetProperty("id").ValueKind==JsonValueKind.Null && !guestContract.RootElement.GetProperty("cookieExists").GetBoolean(),
                "Incognito inherited the selected private account or cookie acquisition path.");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var canceledBeforeCookies=false;try{await service.GetSubtitleCatalogAsync(url,canceled.Token);}catch(OperationCanceledException){canceledBeforeCookies=true;}
        Check(canceledBeforeCookies && !Directory.EnumerateFiles(Path.Combine(paths.Data,"temp")).Any(),"Canceled/failed catalog requests retained plaintext cookie leases.");
    }

    private static void Check(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
}
