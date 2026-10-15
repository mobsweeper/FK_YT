using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FkYouTube;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;
using Microsoft.Data.Sqlite;

internal static class UiLanguageRegression
{
    public static async Task RunUiAsync(string workspace)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var artifacts = Path.GetFullPath(Path.Combine(workspace, "artifacts", "ui-language-20261015"));
        try
        {
            UiText.SetLanguage("ko");
            Assert(new AppSettings().UiLanguage == "ko", "A new profile did not default to Korean.");
            foreach (var invalid in new string?[] { null, "", "fr", "en-US", "../ja", "invalid" })
            {
                UiText.SetLanguage(invalid);
                Assert(UiText.Language == "ko" && UiText.NormalizeLanguage(invalid) == "ko",
                    "An unsupported language did not normalize to Korean.");
            }
            Assert(UiText.NormalizeLanguage(" EN ") == "en" && UiText.NormalizeLanguage("JA") == "ja",
                "Language normalization did not accept trimmed case-insensitive supported codes.");
            VerifyCachedSubtitleLabels();
            await VerifyDownloadLiveRefresh(workspace);
            await VerifyPersistenceAndStartup(workspace);
            await VerifyActualSelectorAndLiveRefresh(workspace, artifacts);
            Assert(ReferenceEquals(culture, CultureInfo.CurrentCulture) && ReferenceEquals(uiCulture, CultureInfo.CurrentUICulture) &&
                   ReferenceEquals(defaultCulture, CultureInfo.DefaultThreadCurrentCulture) &&
                   ReferenceEquals(defaultUiCulture, CultureInfo.DefaultThreadCurrentUICulture),
                "UI language changes modified thread or process culture.");
        }
        finally { UiText.SetLanguage("ko"); }
        Console.WriteLine("UI-LANGUAGE screenshots=" + artifacts + " isolated-data=true network=false real-playback=false");
    }

    private static async Task VerifyDownloadLiveRefresh(string workspace)
    {
        var root = NewRoot(workspace);
        var artifacts = Path.Combine(workspace, "artifacts", "qualityreview-r3-20261016", "download-language");
        var python = Path.Combine(root, "runtime", "python");
        MainWindow? window = null;
        Task? download = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            var start = new ProcessStartInfo("pwsh.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + python.Replace("'", "''") +
                "' -Value '" + Path.Combine(workspace, "runtime", "python").Replace("'", "''") + "' | Out-Null" })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); await Task.WhenAll(output, error);
                Assert(process.ExitCode == 0, "The offline download-language Python junction could not be created.");
            }
            var ffmpeg = Path.Combine(root, "runtime", "ffmpeg"); Directory.CreateDirectory(ffmpeg);
            File.WriteAllText(Path.Combine(ffmpeg, "ffmpeg.exe"), "not executed by the offline fixture");
            var worker = Path.Combine(root, "worker"); Directory.CreateDirectory(worker);
            var merge = Path.Combine(root, "merge.signal");
            var remux = Path.Combine(root, "remux.signal");
            File.WriteAllText(Path.Combine(worker, "selected_ytdlp.py"), """
                import os, sys, time
                sys.stdin.readline()
                assert '--cookies' not in sys.argv
                print('FK_PROGRESS:62.5%', flush=True)
                while not os.path.exists(MERGE): time.sleep(.01)
                print('[Merger] offline fixture', flush=True)
                while not os.path.exists(REMUX): time.sleep(.01)
                print('[VideoRemuxer] offline fixture', flush=True)
                while True: time.sleep(.1)
                """.Replace("MERGE", JsonSerializer.Serialize(merge)).Replace("REMUX", JsonSerializer.Serialize(remux)), new UTF8Encoding(false));
            var paths = new RuntimePaths(root);
            // Persist a current profile before taking the preservation baseline.
            // A missing file starts at quality version 0; the first settings save
            // migrates it to 1 independently of download progress or translation.
            new DataStore(paths).SaveSettings(new AppSettings { UiLanguage = "ko" });
            window = new MainWindow(paths, ["--demo"])
            { ShowActivated = false, Left = 40, Top = 40, Width = 1380, Height = 900 };
            Get<DispatcherTimer>(window, "_saveTimer").Stop(); window.Show();
            await LayoutIdle(window);
            SelectLanguage(window, "ko");
            // Route only synthetic account metadata through the bundled Python adapter.
            // The isolated guest session has no cookie or encrypted session material.
            typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(Get<SessionService>(window, "_session"),
                new YouTubeChannel { Id = "offline-language", DataSyncId = "fixture_user||", AuthUser = 1 });
            var video = new VideoItem { Id = "fixture0001", Title = "원본 download title", Url = "https://www.youtube.com/watch?v=fixture0001" };
            var destination = Path.Combine(root, "fixture.mkv");
            download = (Task)Invoke(window, "DownloadCurrentAsync", video, 720, destination)!;
            Set(window, "_downloadTask", download);
            await WaitForText("다운로드 62.5%");
            var before = StableState.Capture(window);
            await CheckLanguages("progress", language => language switch
            {
                "en" => "Download 62.5% · progress per video/audio stage",
                "ja" => "ダウンロード 62.5% · 動画・音声の段階別進行状況",
                _ => "다운로드 62.5% · 영상/음성 단계별 진행"
            });
            File.WriteAllText(merge, "continue");
            await WaitForText("영상·음성 파일을 합치는 중…");
            await CheckLanguages("merge", MergeText);
            Invoke(window, "SetDownloadUiText", new Func<string>(() => "remux pending"));
            File.WriteAllText(remux, "continue");
            // The worker waits after each phase, so no later event can hide a stale translation.
            await WaitForText("영상·음성 파일을 합치는 중…");
            await CheckLanguages("remux", MergeText);
            Get<CancellationTokenSource?>(window, "_downloadCts")?.Cancel();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(!File.Exists(destination), "The canceled offline language fixture published an incomplete file.");
            await CheckLanguages("canceled", language => language switch
            {
                "en" => "Download canceled", "ja" => "ダウンロードをキャンセルしました", _ => "다운로드 취소됨"
            });
            Console.WriteLine("PASS UI-LANGUAGE download percent merge remux and cancellation refresh without new events; offline=true cookies=false");

            async Task WaitForText(string expected)
            {
                var timer = Stopwatch.StartNew();
                while (!Get<TextBlock>(window, "DownloadState").Text.StartsWith(expected, StringComparison.Ordinal) &&
                       !download.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5))
                { await Task.Delay(20); await LayoutIdle(window); }
                Assert(Get<TextBlock>(window, "DownloadState").Text.StartsWith(expected, StringComparison.Ordinal),
                    "The offline worker did not reach the expected progress phase: " + expected);
            }
            async Task CheckLanguages(string phase, Func<string, string> expected)
            {
                foreach (var language in new[] { "en", "ja", "ko" })
                {
                    SelectLanguage(window, language); await LayoutIdle(window);
                    Capture(window, artifacts, "download-" + phase + "-" + language);
                    Assert(Get<TextBlock>(window, "DownloadState").Text == expected(language),
                        "Download " + phase + " stayed in the previous language without another progress event: " + language);
                    var after = StableState.Capture(window);
                    Assert(before == after, "Download language refresh changed playback/account/request/preference state: " +
                        StableState.DescribeDifferences(before, after));
                }
            }
            static string MergeText(string language) => language switch
            {
                "en" => "Merging video and audio files…", "ja" => "動画と音声ファイルを結合中…", _ => "영상·음성 파일을 합치는 중…"
            };
        }
        finally
        {
            if (window is not null)
            {
                Get<CancellationTokenSource?>(window, "_downloadCts")?.Cancel();
                if (download is not null) await download.WaitAsync(TimeSpan.FromSeconds(12));
                await DisposeWindow(window);
            }
            if (Directory.Exists(python)) Directory.Delete(python);
            DeleteRoot(workspace, root); UiText.SetLanguage("ko");
        }
    }

    private static void VerifyCachedSubtitleLabels()
    {
        const string sourceName = "좋아요 원본 자막 이름";
        UiText.SetLanguage("ko");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "fixture0001",
            subtitles = new Dictionary<string, object> { ["ja"] = new[] { new { ext = "vtt", url = "https://www.youtube.com/api/timedtext?v=fixture0001&lang=ja", name = sourceName } } },
            automatic_captions = new Dictionary<string, object> { ["en"] = new[] { new { ext = "vtt", url = "https://www.youtube.com/api/timedtext?v=fixture0001&lang=en&kind=asr", name = sourceName } } }
        }));
        var catalog = YouTubeService.ParseSubtitleCatalog(doc.RootElement, "fixture0001");
        var original = catalog.Select(item => (item.Language, item.Url, item.Automatic, item.Label)).ToArray();
        var supplied = new AvailableSubtitle("fr", "Fixture prebuilt label", "https://www.youtube.com/api/timedtext?v=fixture0001&lang=fr", false);
        foreach (var language in new[] { "ko", "en", "ja" })
        {
            UiText.SetLanguage(language);
            foreach (var item in catalog)
            {
                var suffix = item.Automatic ? (language switch { "en" => "Automatic captions/translations", "ja" => "自動字幕・翻訳", _ => "자동 자막·번역" })
                    : (language switch { "en" => "Manual captions", "ja" => "手動字幕", _ => "수동 자막" });
                Assert(item.DisplayLabel.Contains(sourceName, StringComparison.Ordinal) && item.DisplayLabel.EndsWith(suffix, StringComparison.Ordinal),
                    "A cached subtitle label failed to translate its app-owned suffix or preserve the remote subtitle name.");
            }
            Assert(original.SequenceEqual(catalog.Select(item => (item.Language, item.Url, item.Automatic, item.Label))) &&
                   supplied.DisplayLabel == supplied.Label,
                "Refreshing cached subtitle display labels changed catalog data or replaced a supplied fixture label.");
        }
        UiText.SetLanguage("ko");
        Console.WriteLine("PASS UI-LANGUAGE cached subtitle display labels preserve source names URLs languages and supplied labels");
    }

    private static async Task VerifyPersistenceAndStartup(string workspace)
    {
        var root = NewRoot(workspace);
        try
        {
            var paths = new RuntimePaths(root);
            var store = new DataStore(paths);
            Assert(store.LoadSettings().UiLanguage == "ko", "Missing settings did not default to Korean.");
            var settingsPath = Path.Combine(paths.Data, "settings.json");
            File.WriteAllText(settingsPath, "{\"maxHeight\":720}");
            Assert(store.LoadSettings().UiLanguage == "ko", "Legacy settings without a language did not use Korean.");
            foreach (var invalidJson in new[] { "{\"uiLanguage\":\"fr\"}", "{\"uiLanguage\":null}" })
            {
                File.WriteAllText(settingsPath, invalidJson);
                Assert(store.LoadSettings().UiLanguage == "ko", "Invalid saved language did not load as Korean.");
            }
            foreach (var language in new[] { "en", "ja" })
            {
                var settings = new AppSettings { UiLanguage = language, AccountKey = "synthetic-language-profile", Volume = 37, Speed = 1.25 };
                store.SaveSettings(settings);
                Assert(new DataStore(paths).LoadSettings().UiLanguage == language,
                    "The saved language did not survive a new DataStore instance.");
                UiText.SetLanguage("ko");
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(paths, ["--demo"]);
                    Assert(UiText.Language == language && SelectedLanguage(window) == language,
                        "A new app window did not restore the saved language before initialization.");
                    Assert(Get<Button>(window, "HomeButton").Content?.ToString() == (language == "en" ? "Home" : "ホーム"),
                        "Startup XAML was created in a different language than the saved preference.");
                    Assert(Get<AppSettings>(window, "_settings").Volume == 37 && Get<AppSettings>(window, "_settings").Speed == 1.25,
                        "Loading the UI language changed playback preferences.");
                }
                finally { if (window is not null) await DisposeWindow(window); }
            }
        }
        finally { DeleteRoot(workspace, root); UiText.SetLanguage("ko"); }
        Console.WriteLine("PASS UI-LANGUAGE default legacy invalid and en/ja restart persistence");
    }

    private static async Task VerifyActualSelectorAndLiveRefresh(string workspace, string artifacts)
    {
        var root = NewRoot(workspace);
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new RuntimePaths(root), ["--demo"])
            { ShowActivated = false, Left = 40, Top = 40, Width = 1380, Height = 900 };
            Get<DispatcherTimer>(window, "_saveTimer").Stop();
            window.Show();
            await LayoutIdle(window);
            var selector = Get<ComboBox>(window, "UiLanguageCombo");
            Assert(selector.Items.Cast<ComboBoxItem>().Select(item => (item.Tag?.ToString() ?? "", item.Content?.ToString() ?? ""))
                   .SequenceEqual(new[] { ("en", "English"), ("ko", "한국어"), ("ja", "日本語") }),
                "The actual selector did not contain the three native language labels and stable language tags.");
            var settingsButton = Get<Button>(window, "SettingsButton");
            Assert(ReferenceEquals(selector.Parent, settingsButton.Parent) &&
                   selector.TranslatePoint(new Point(), window).Y >= settingsButton.TranslatePoint(new Point(0, settingsButton.ActualHeight), window).Y,
                "The language selector was not placed below the left Settings button.");

            const string remoteTitle = "홈 추천 · 재생목록 · 구독 — 원본 영상 제목";
            const string remoteChannel = "시청 기록 원본 채널";
            const string remoteDescription = "설정 · 로그인 · 관심 없음: 서버가 보낸 원본 설명";
            const string remoteComment = "좋아요 / 싫어요 / 준비됨 — 원본 댓글";
            const string remoteCategory = "홈 추천 원본 카테고리";
            var item = new VideoItem { Id = "fixture0001", Kind = "playlist", Title = remoteTitle, Channel = remoteChannel,
                ViewCount = 12345, Url = "https://www.youtube.com/playlist?list=PLfixture" };
            var historyItem = new VideoItem { Id = "fixture0002", Title = "시청 기록 원본 제목", Channel = remoteChannel,
                LocalDetail = "fixture legacy detail", LocalDetailFactory = () => UiText.T("로컬 기록 · 저장됨", "Local history · Saved", "ローカル履歴・保存済み") };
            var historyJson = JsonSerializer.Serialize(historyItem, JsonDefaults.Options);
            Assert(!historyJson.Contains("localDetailFactory") && !historyJson.Contains("localDetail"),
                "An in-memory local detail or translation delegate leaked into saved video metadata.");
            Invoke(window, "ClearDisplayedItems");
            Invoke(window, "AppendItem", item);
            Invoke(window, "AppendItem", historyItem);
            Set(window, "_current", item);
            Get<TextBlock>(window, "PlayingTitle").Text = remoteTitle;
            Invoke(window, "SetVideoDescription", remoteDescription, false);
            Get<TextBox>(window, "SearchBox").Text = "검색 / Search / 検索 user input";
            var metadata = new FeedNavigationMetadata { Title = remoteTitle,
                Filters = [new FeedFilter { Id = "fixture-category", Label = remoteCategory, Selected = true }],
                Channel = new ChannelPageInfo { Title = remoteChannel, Description = remoteDescription } };
            Set(window, "_feedNavigationMetadata", metadata);
            Invoke(window, "ApplyBrowseMetadata", metadata);
            Get<ListBox>(window, "CommentsList").ItemsSource = new[] { new CommentItem("원본 작성자", remoteComment, 17, null) };
            var settings = Get<AppSettings>(window, "_settings");
            settings.AccountKey = "synthetic-ui-language-account";
            settings.SelectedChannelId = "synthetic-channel";
            settings.MaxHeight = 720; settings.Volume = 37; settings.Speed = 1.25;
            // This fixture models an explicit current preference, rather than
            // measuring the first-save migration as part of a language change.
            settings.QualityPreferenceVersion = 1;
            var player = Get<MpvPlayerService>(window, "_player");
            Set(player, "_subtitleLanguage", "fr"); Set(player, "_subtitleOff", true);
            Set(player, "_subtitleSelectionGeneration", 19L); Set(player, "_subtitleSelectedId", 7L);
            var snapshot = new PlaybackSnapshot { MediaGeneration = 19, VideoUrl = item.Url, Position = 22, Duration = 120,
                Paused = true, Loaded = true, Speed = 1.25, Volume = 37, HardwareDecoder = "fixture-decoder" };
            Set(window, "_snapshot", snapshot); Set(player, "_snapshot", snapshot); Set(player, "_mediaGeneration", 19L);
            Set(window, "_expectedMediaGeneration", 19L); Set(window, "_videoGeneration", 11);
            Set(window, "_feedGeneration", 13); Set(window, "_viewGeneration", 17); Set(window, "_autoplayEpoch", 23);
            Set(window, "_pendingSeek", 54d);
            var before = StableState.Capture(window);
            var sidebarFailures = new List<string>();
            foreach (var language in new[] { "ko", "en", "ja" })
            {
                SelectLanguage(window, language);
                await LayoutIdle(window);
                Assert(UiText.Language == language && new DataStore(new RuntimePaths(root)).LoadSettings().UiLanguage == language,
                    "A real selector event did not immediately apply and persist the language.");
                var home = language switch { "en" => "Home", "ja" => "ホーム", _ => "홈 추천" };
                var playlist = language switch { "en" => "Playlist", "ja" => "再生リスト", _ => "재생목록" };
                var views = language switch { "en" => "12,345 views", "ja" => "12,345 回視聴", _ => "조회 12,345회" };
                var toolTip = language switch { "en" => "Search or enter a YouTube video, channel or playlist URL",
                    "ja" => "検索語または YouTube の動画・チャンネル・再生リストの URL", _ => "검색어 또는 YouTube 영상·채널·재생목록 주소" };
                Assert(Get<Button>(window, "HomeButton").Content?.ToString() == home &&
                       Get<TextBox>(window, "SearchBox").ToolTip?.ToString() == toolTip &&
                       AutomationProperties.GetName(selector) == (language switch { "en" => "Display language", "ja" => "表示言語", _ => "표시 언어" }),
                    "The visible static labels, tooltip or accessibility name did not refresh live.");
                var cardTexts = Descendants<TextBlock>(Get<ListBox>(window, "VideoList"))
                    .Where(text => ReferenceEquals(text.DataContext, item)).Select(text => text.Text).ToArray();
                Assert(cardTexts.Contains(playlist) && cardTexts.Contains(views) && cardTexts.Contains(remoteTitle),
                    "The existing realized card did not refresh its localized computed labels while retaining its source title.");
                Assert(Descendants<TextBlock>(Get<ListBox>(window, "VideoList")).Any(text => ReferenceEquals(text.DataContext, historyItem) &&
                       text.Text == (language switch { "en" => "Local history · Saved", "ja" => "ローカル履歴・保存済み", _ => "로컬 기록 · 저장됨" })),
                    "An already displayed local history detail stayed in the previous language.");
                var channelButton = Descendants<Button>(Get<ListBox>(window, "VideoList"))
                    .First(button => button.Style == window.FindResource("ChannelLinkButtonStyle"));
                Assert(channelButton.ToolTip?.ToString() == (language switch { "en" => "View channel videos", "ja" => "チャンネルの動画を見る", _ => "채널 영상 보기" }),
                    "The localized style tooltip did not update with the language binding.");
                Assert(Get<TextBlock>(window, "PageTitle").Text == remoteTitle && Get<TextBlock>(window, "ChannelHeaderTitle").Text == remoteChannel &&
                       Get<TextBlock>(window, "ChannelHeaderDescription").Text == remoteDescription &&
                       Get<TextBox>(window, "DescriptionText").Text == remoteDescription && Get<TextBlock>(window, "PlayingTitle").Text == remoteTitle &&
                       Get<TextBox>(window, "SearchBox").Text == "검색 / Search / 検索 user input" &&
                       ((CommentItem)Get<ListBox>(window, "CommentsList").Items[0]).Text == remoteComment &&
                       ((FeedFilter)Get<ItemsControl>(window, "BrowseFilterItems").Items[0]).Label == remoteCategory &&
                       ReferenceEquals(Get<List<VideoItem>>(window, "_items")[0], item),
                    "A language change altered remote content, user input or the current data object.");
                Assert(before == StableState.Capture(window), "Main UI refresh changed playback/account/request/preference state.");
                Capture(window, artifacts, "ui-" + language);
                sidebarFailures.AddRange(CheckSidebarAccess(window).Select(error => language + " normal: " + error));
                window.Width = 1080; window.Height = 640;
                await LayoutIdle(window);
                selector.BringIntoView();
                await LayoutIdle(window);
                Capture(window, artifacts, "ui-" + language + "-min");
                sidebarFailures.AddRange(CheckSidebarAccess(window).Select(error => language + " minimum: " + error));
                window.Width = 1380; window.Height = 900;
                await LayoutIdle(window);
                foreach (var scroll in Ancestors(selector).OfType<ScrollViewer>()) scroll.ScrollToTop();
                await LayoutIdle(window);
            }
            Assert(sidebarFailures.Count == 0, "The sidebar clipped or overlapped the selector/account block: " + string.Join("; ", sidebarFailures));
            Console.WriteLine("PASS UI-LANGUAGE actual selector placement live labels tooltips realized cards and source preservation");

            Invoke(window, "ShowPanel", "player"); Set(window, "_playerStarted", true);
            Invoke(window, "Popout_Click", window, new RoutedEventArgs());
            await LayoutIdle(window);
            var popup = Get<Window?>(window, "_popoutWindow");
            Assert(popup is not null, "The offline popout fixture failed to create its real controls.");
            var host = Get<MpvHost>(window, "VideoHost");
            var hostHandle = host.Handle;
            var popoutBefore = StableState.Capture(window);
            foreach (var language in new[] { "en", "ja", "ko" })
            {
                SelectLanguage(window, language);
                await LayoutIdle(window);
                var share = language switch { "en" => "Share", "ja" => "共有", _ => "공유" };
                Assert(Descendants<Button>(popup!).Any(button => button.Content?.ToString() == share) &&
                       Get<Button>(window, "_popoutPause").Content?.ToString() == (language switch { "en" => "Play", "ja" => "再生", _ => "재생" }) &&
                       popup!.Title == "FK_YT · " + remoteTitle && ReferenceEquals(Get<Window>(window, "_popoutWindow"), popup) &&
                       host.Handle == hostHandle && ReferenceEquals(host.Parent, Get<Border>(window, "_popoutSurface")),
                    "The existing popout did not refresh localized controls while preserving its title, window and video host.");
                Assert(Get<Button>(window, "PopoutLaunchButton").Content?.ToString() ==
                       (language switch { "en" => "Return", "ja" => "戻す", _ => "복귀" }),
                    "The main popout button did not refresh its Return label in " + language + ".");
                Assert(popoutBefore == StableState.Capture(window), "Popout language refresh changed playback/account/request/preference state.");
                Console.WriteLine("PASS UI-LANGUAGE popout Return label language=" + language);
            }
            Get<Button>(window, "PopoutLaunchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await LayoutIdle(window);
            Assert(Get<Window?>(window, "_popoutWindow") is null && host.Handle == hostHandle &&
                   ReferenceEquals(host.Parent, Get<Border>(window, "VideoSurface")) && popoutBefore == StableState.Capture(window),
                "The main Return button did not return the same host while preserving playback state.");
            foreach (var language in new[] { "en", "ja", "ko" })
            {
                SelectLanguage(window, language);
                await LayoutIdle(window);
                Assert(Get<Button>(window, "PopoutLaunchButton").Content?.ToString() ==
                       (language switch { "en" => "Pop out", "ja" => "別ウィンドウ", _ => "팝아웃" }),
                    "The closed popout button did not refresh its launch label in " + language + ".");
            }
            Set(window, "_playerStarted", false);
            Console.WriteLine("PASS UI-LANGUAGE main and popout refresh preserve media generations account subtitle preference and request arguments");

            SelectLanguage(window, "en");
            var savedBytes = File.ReadAllBytes(Path.Combine(root, "data", "settings.json"));
            Set(window, "_accountBusyDepth", 1);
            try { SelectLanguage(window, "ja"); }
            finally { Set(window, "_accountBusyDepth", 0); }
            Assert(UiText.Language == "en" && settings.UiLanguage == "en" && SelectedLanguage(window) == "en" &&
                   savedBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "data", "settings.json"))),
                "An account transition admitted or saved a language change.");
            Set(window, "_ready", false);
            try { SelectLanguage(window, "ja"); }
            finally { Set(window, "_ready", true); }
            Assert(UiText.Language == "en" && settings.UiLanguage == "en" && SelectedLanguage(window) == "en",
                "An initialization event changed the saved language.");
            var settingsPath = Path.Combine(root, "data", "settings.json");
            var heldPath = Path.Combine(root, "held-settings.json");
            File.Move(settingsPath, heldPath); Directory.CreateDirectory(settingsPath);
            try
            {
                SelectLanguage(window, "ja");
                await LayoutIdle(window);
                Assert(UiText.Language == "en" && settings.UiLanguage == "en" && SelectedLanguage(window) == "en" &&
                       Get<Button>(window, "HomeButton").Content?.ToString() == "Home" && savedBytes.SequenceEqual(File.ReadAllBytes(heldPath)),
                    "A failed language save did not roll back the selector/settings/UI or preserve the prior settings.");
            }
            finally { Directory.Delete(settingsPath); File.Move(heldPath, settingsPath); }
            Console.WriteLine("PASS UI-LANGUAGE account initialization barriers and failed-save rollback preserve prior preference");
        }
        finally
        {
            if (window is not null) await DisposeWindow(window);
            DeleteRoot(workspace, root);
            UiText.SetLanguage("ko");
        }
    }

    private sealed record StableState(string Settings, int VideoGeneration, int FeedGeneration, int ViewGeneration, int AutoplayEpoch,
        long ExpectedGeneration, long PlayerGeneration, PlaybackSnapshot? Snapshot, string? SubtitleLanguage, bool SubtitleOff,
        long SubtitleGeneration, long? SubtitleId, double? PendingSeek, string RequestArguments, string? SelectedChannel,
        string? SessionAccount, CancellationTokenSource AccountCts, CancellationTokenSource HistoryCts, object? PlayerSession, int ProcessCount,
        string Culture, string UiCulture)
    {
        public static string DescribeDifferences(StableState before, StableState after)
        {
            var changes = new List<string>();
            foreach (var property in typeof(StableState).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var previous = property.GetValue(before); var current = property.GetValue(after);
                if (Equals(previous, current)) continue;
                if (property.Name == nameof(Settings))
                {
                    var left = JsonNode.Parse((string)previous!)!.AsObject();
                    var right = JsonNode.Parse((string)current!)!.AsObject();
                    foreach (var key in left.Select(item => item.Key).Union(right.Select(item => item.Key)))
                        if (!JsonNode.DeepEquals(left[key], right[key]))
                            changes.Add("Settings." + key + ": " + SafeSetting(left[key]) + " -> " + SafeSetting(right[key]));
                }
                else changes.Add(property.Name + ": " + SafeValue(previous) + " -> " + SafeValue(current));
            }
            return string.Join("; ", changes);

            static string SafeSetting(JsonNode? node) => node?.GetValueKind() switch
            {
                null or JsonValueKind.Null => "null",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node!.ToJsonString(),
                _ => "<changed value redacted>"
            };
            static string SafeValue(object? value) => value switch
            {
                null => "null",
                bool or int or long or double => JsonSerializer.Serialize(value),
                _ => "<changed value redacted>"
            };
        }

        public static StableState Capture(MainWindow window)
        {
            var settings = JsonNode.Parse(JsonSerializer.Serialize(Get<AppSettings>(window, "_settings"), JsonDefaults.Options))!.AsObject();
            settings.Remove("uiLanguage");
            var player = Get<MpvPlayerService>(window, "_player");
            var session = Get<SessionService>(window, "_session");
            var youtube = Get<YouTubeService>(window, "_youtube");
            var args = (List<string>)Invoke(youtube, "BaseArguments", null,
                new YouTubeChannel { Id = "synthetic-channel", PageId = "123456", DataSyncId = "123456||fixture", AuthUser = 2 }, "comment_sort=top")!;
            return new(settings.ToJsonString(), Get<int>(window, "_videoGeneration"), Get<int>(window, "_feedGeneration"),
                Get<int>(window, "_viewGeneration"), Get<int>(window, "_autoplayEpoch"), Get<long>(window, "_expectedMediaGeneration"),
                Get<long>(player, "_mediaGeneration"), Get<PlaybackSnapshot?>(window, "_snapshot"), player.PreferredSubtitleLanguage,
                Get<bool>(player, "_subtitleOff"), Get<long>(player, "_subtitleSelectionGeneration"), Get<long?>(player, "_subtitleSelectedId"),
                Get<double?>(window, "_pendingSeek"), JsonSerializer.Serialize(args), session.SelectedChannel?.Id,
                session.IsConnected ? "connected" : "guest", Get<CancellationTokenSource>(window, "_accountCts"),
                Get<CancellationTokenSource>(window, "_historyCts"), Get<object?>(player, "_session"), player.OwnedProcessIds.Count,
                CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name);
        }
    }

    private static string NewRoot(string workspace) => Path.GetFullPath(Path.Combine(workspace, "artifacts", "ui-language-fixture-" + Guid.NewGuid().ToString("N")));
    private static void DeleteRoot(string workspace, string root)
    {
        SqliteConnection.ClearAllPools();
        var artifacts = Path.GetFullPath(Path.Combine(workspace, "artifacts")) + Path.DirectorySeparatorChar;
        Assert(Path.GetFullPath(root).StartsWith(artifacts, StringComparison.OrdinalIgnoreCase), "Fixture cleanup escaped the artifacts directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    private static async Task DisposeWindow(MainWindow window)
    {
        Get<DispatcherTimer>(window, "_saveTimer").Stop(); Set(window, "_closing", true); Set(window, "_allowClose", true);
        Invoke(window, "ClosePopoutForShutdown"); Invoke(window, "DisposePlayerViewModes");
        await Get<FeedService>(window, "_feed").DisposeAsync(); await Get<MpvPlayerService>(window, "_player").DisposeAsync();
        Get<CancellationTokenSource>(window, "_accountCts").Dispose(); Get<CancellationTokenSource>(window, "_historyCts").Dispose();
        ThumbnailImage.Cache?.Dispose(); ThumbnailImage.Cache = null; window.Close(); SqliteConnection.ClearAllPools();
    }
    private static string SelectedLanguage(MainWindow window) => ((ComboBoxItem)Get<ComboBox>(window, "UiLanguageCombo").SelectedItem).Tag?.ToString() ?? "";
    private static void SelectLanguage(MainWindow window, string language)
    {
        var selector = Get<ComboBox>(window, "UiLanguageCombo");
        selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == language);
    }
    private static async Task LayoutIdle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }
    private static IEnumerable<string> CheckSidebarAccess(MainWindow window)
    {
        var selector = Get<ComboBox>(window, "UiLanguageCombo");
        var sidebar = Get<Border>(window, "Sidebar");
        var bottom = (FrameworkElement)Get<TextBlock>(window, "AccountLabel").Parent;
        var selectorBounds = new Rect(selector.TranslatePoint(new Point(), window), new Size(selector.ActualWidth, selector.ActualHeight));
        var sidebarBounds = new Rect(sidebar.TranslatePoint(new Point(), window), new Size(sidebar.ActualWidth, sidebar.ActualHeight));
        var bottomBounds = new Rect(bottom.TranslatePoint(new Point(), window), new Size(bottom.ActualWidth, bottom.ActualHeight));
        if (!sidebarBounds.Contains(selectorBounds)) yield return "selector bounds escaped the sidebar";
        if (!sidebarBounds.Contains(bottomBounds)) yield return "account block bounds escaped the sidebar";
        if (selectorBounds.Bottom > bottomBounds.Top + .5 && selectorBounds.Top < bottomBounds.Bottom - .5)
            yield return "selector overlapped the account block";
        if (window.InputHitTest(new Point(selectorBounds.Left + selectorBounds.Width / 2, selectorBounds.Top + selectorBounds.Height / 2)) is not DependencyObject hit ||
            !ReferenceEquals(hit, selector) && !Ancestors(hit).Contains(selector))
            yield return "selector center did not hit its real control";
    }
    private static void Capture(Window window, string directory, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(window);
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
