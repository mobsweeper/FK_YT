using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FkYouTube;
using FkYouTube.Controls;
using FkYouTube.Playback;
using FkYouTube.Services;
using System.Net;
using System.Net.Http;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class PlayerInteractionRegression
{
    public static async Task RunUiAsync(string workspace,string? suite=null,CancellationToken stopToken=default)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "artifacts", "interaction-fixture-" + Guid.NewGuid().ToString("N")));
        var window = new MainWindow(new RuntimePaths(root), ["--demo"])
        {
            ShowActivated = false, Left = 50, Top = 50, Width = 1200, Height = 800
        };
        var failures = new List<Exception>();
        var viewArtifacts = Path.GetFullPath(Path.Combine(workspace, "artifacts", "player-view-modes-20261015"));
        nint surrogate = 0;
        try
        {
            window.Show();
            Invoke(window, "ShowPanel", "player");
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var host = Get<MpvHost>(window, "VideoHost");
            Assert(host.Handle != 0 && host.InputDisabled && !host.IsEnabled && !host.Focusable,
                "The real video host no longer protects WPF keyboard focus.");
            surrogate = CreateWindowExW(0, "STATIC", "foreign child input fixture", 0x40000000 | 0x10000000,
                0, 0, (int)host.ActualWidth, (int)host.ActualHeight, host.Handle, 0, 0, 0);
            Assert(surrogate != 0 && IsWindowEnabled(surrogate), "The surrogate foreign child was not created enabled.");
            var video = new VideoItem { Id = "aqz-KE-bpKQ", Title = "Interaction fixture",
                Url = "https://youtu.be/aqz-KE-bpKQ?si=tracking&list=PLtest&t=31#extra" };
            Set(window, "_current", video);
            Set(window, "_playerStarted", true);
            var nativeHandle = host.Handle;

            await Check("main video raw input and keyboard focus", async () =>
            {
                var surface = Get<Border>(window, "VideoSurface");
                var status = Get<TextBlock>(window, "StatusText");
                Get<TextBox>(window, "SearchBox").Focus();
                status.Text = "before-video-click";
                AssertNativeTarget(window, surface);
                Click(window, surface, new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(status.Text == "플레이어 연결이 끊겼습니다. 영상을 다시 여세요.",
                    "WPF raw input over the disabled host did not reach the actual pause command route.");
                Assert(host.InputDisabled, "Video click enabled the native host.");
                Assert(Get<Button>(window, "PlayPauseButton").IsKeyboardFocused,
                    "Clicking video left keyboard focus in the search field.");
            });
            await Check("main volume track click and held drag", async () =>
            {
                var slider = Get<Slider>(window, "VolumeSlider");
                await TrackClickThenDrag(window, slider);
            });
            foreach (var volumeState in new[] { "success", "current-error", "video", "account" })
                await Check("main volume queue " + volumeState,
                    () => VerifyVolumeCommandBurst(window, Get<Slider>(window, "VolumeSlider"), "main", volumeState));
            await Check("main focused controls keep their keyboard defaults", async () =>
            {
                var autoplay = Get<CheckBox>(window, "AutoPlayCheck");
                var checkedBefore = autoplay.IsChecked;
                PressKey(window, autoplay, Key.Space);
                Assert(autoplay.IsChecked != checkedBefore, "The main preview shortcut consumed a checkbox's Space key.");
                autoplay.IsChecked = checkedBefore;
                var volume = Get<Slider>(window, "VolumeSlider");
                volume.SetCurrentValue(Slider.ValueProperty, 60d);
                PressKey(window, volume, Key.Right);
                Assert(Math.Abs(volume.Value - (60 + volume.SmallChange)) < .001,
                    "The main preview shortcut consumed the volume slider's Right key.");
                var copied = new List<string>();
                var originalCopy = Get<Action<string>>(window, "_copyShareText");
                Set(window, "_copyShareText", new Action<string>(copied.Add));
                try
                {
                    PressKey(window, Get<Button>(window, "ShareButton"), Key.Space);
                    Assert(copied.SequenceEqual(["https://www.youtube.com/watch?v=aqz-KE-bpKQ"]),
                        "Space on the main share button did not invoke its default click exactly once.");
                }
                finally { Set(window, "_copyShareText", originalCopy); }
                var play = Get<Button>(window, "PlayPauseButton");
                var clicks = 0;
                RoutedEventHandler count = (_, _) => clicks++;
                play.Click += count;
                try { PressKey(window, play, Key.Space); Assert(clicks == 1, "Playback Space was consumed or clicked more than once."); }
                finally { play.Click -= count; }
                await WaitForVolumeDrain(window);
                VerifyKeyboardSeek(window, window, Get<Slider>(window, "SeekSlider"), play, "main");
            });
            await Check("main subtitle popup commits a ready anchored menu", async () =>
                await VerifySubtitlePopupAnchor(window, window, Get<Button>(window, "SubtitleButton"), viewArtifacts, "main"));
            foreach (var captionFailure in new[] { "stopped", "retained", "stale-generation", "orphaned-loaded" })
                await Check("prepared subtitle failure advice " + captionFailure,
                    () => VerifyPreparedCaptionFailure(window, workspace, captionFailure));
            foreach (var subtitleState in new[] { "detached", "connected", "stale-operation", "stale-ui", "stale-media" })
                foreach (var canceled in new[] { false, true })
                    await Check("manual subtitle recovery advice " + subtitleState + (canceled ? " cancellation" : " failure"),
                        () => VerifyManualSubtitleFailure(window, workspace, subtitleState, canceled));
            foreach (var catalogFailure in new[] { false, true })
                await Check("subtitle controls hold and dismissed catalog " + (catalogFailure ? "failure" : "success"),
                    () => VerifySubtitleControlsHold(window, workspace, catalogFailure));
            await Check("visible subtitle catalog failure keeps its moved-window anchor",
                ()=>VerifySubtitleControlsHold(window,workspace,true,keepOpen:true));
            foreach(var mode in new[]{"budget","first-rejection-fallback","preferred-fallback","existing-unrelated","already-desired","manual-off","unconfirmed-no-retry","changed-rejection"})
                await Check("prepared caption request policy "+mode,()=>VerifyPreparedCaptionPolicy(window,workspace,mode));
            foreach(var mode in new[]{"orig-order","rejected-base-orig","loaded-orig","case-orig","preferred-orig-base","preferred-orig-loaded-base","regional-en","regional-base-en","regional-base-en-exact","regional-zh"})
                await Check("prepared caption request policy "+mode,()=>VerifyPreparedCaptionOriginalLanguage(window,workspace,mode));
            foreach(var off in new[]{false,true})
                await Check("manual subtitle intent cancels prepared captions "+off,()=>VerifyPreparedCaptionManualIntent(window,workspace,off));
            foreach(var off in new[]{false,true})
                await Check("in-flight prefetch survives same-language or off intent "+off,()=>VerifyExistingPrefetchDuringSubtitleIntent(window,workspace,off));
            foreach (var outcome in new[] { "success", "failure", "canceled-connected", "canceled-detached", "failure-read-pending" })
                await Check("subtitle selection survives menu reopen " + outcome,
                    () => VerifySubtitleSelectionReopen(window, workspace, outcome));
            foreach (var transition in new[] { "video", "account" })
                await Check("subtitle read and selection cancel on " + transition,
                    () => VerifySubtitleOperationTransition(window, workspace, transition));
            await Check("delayed playback failure keeps its media and account context",
                () => VerifyPlaybackFailureContext(window));
            await Check("share button clean URL", () =>
            {
                var share = window.FindName("ShareButton") as Button;
                Assert(share is not null, "The main player has no share button.");
                var copied = new List<string>();
                Set(window, "_copyShareText", new Action<string>(copied.Add));
                share!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(copied.SequenceEqual(["https://www.youtube.com/watch?v=aqz-KE-bpKQ"]),
                    "The share route retained tracking, playlist, time or fragment parameters.");
                Set(window, "_current", video with { Id = "bad", Url = "https://example.com/watch?v=aqz-KE-bpKQ" });
                share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(copied.Count == 1, "An unsupported share URL reached the clipboard sink.");
                Set(window, "_current", video);
                return Task.CompletedTask;
            });
            await Check("sidebar preference and theater survive fullscreen", async () =>
            {
                var comments = Get<Border>(window, "CommentsPanel");
                var commentColumn = Get<ColumnDefinition>(window, "CommentColumn");
                var sidebar = Get<Border>(window, "Sidebar");
                var header = Get<Grid>(window, "Header");
                var controls = Get<Border>(window, "PlaybackControlsTray");
                var normalVideoWidth = Get<Grid>(window, "VideoColumn").ActualWidth;
                Assert(comments.IsVisible && commentColumn.ActualWidth > 0 && controls.IsVisible,
                    "The normal player did not start with its sidebar and playback controls visible.");
                CaptureView(window, viewArtifacts, "normal");

                Get<Button>(window, "HideSidePanelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Assert(Get<bool>(window, "_sidePanelHidden") && comments.Visibility == Visibility.Collapsed &&
                       commentColumn.ActualWidth == 0 && Get<Button>(window, "ShowSidePanelButton").IsVisible,
                    "Hiding the side panel did not collapse its column and expose its restore button.");
                Assert(Get<Grid>(window, "VideoColumn").ActualWidth > normalVideoWidth,
                    "Hiding the side panel did not give the video more horizontal space.");
                CaptureView(window, viewArtifacts, "sidebar-hidden");

                Get<Button>(window, "TheaterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Assert(Get<bool>(window, "_theaterMode") && sidebar.Visibility == Visibility.Collapsed &&
                       header.Visibility == Visibility.Collapsed && comments.Visibility == Visibility.Collapsed &&
                       Get<StackPanel>(window, "PlayingChannelPanel").Visibility == Visibility.Collapsed &&
                       Get<StackPanel>(window, "RatingPanel").Visibility == Visibility.Collapsed,
                    "Theater did not hide the navigation, header, side panel and video metadata.");
                Assert(controls.IsVisible && host.Handle == nativeHandle,
                    "Theater hid playback controls or recreated the native video handle.");
                CaptureView(window, viewArtifacts, "theater");

                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Assert(Get<bool>(window, "_fullscreen") && window.WindowStyle == WindowStyle.None &&
                       window.WindowState == WindowState.Maximized && host.Handle == nativeHandle,
                    "Fullscreen did not retain the active native video in a borderless maximized window.");
                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Assert(!Get<bool>(window, "_fullscreen") && Get<bool>(window, "_theaterMode") &&
                       Get<bool>(window, "_sidePanelHidden") && sidebar.Visibility == Visibility.Collapsed &&
                       header.Visibility == Visibility.Collapsed && comments.Visibility == Visibility.Collapsed,
                    "Leaving fullscreen lost the theater mode or the user's hidden side panel preference.");

                Get<Button>(window, "TheaterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Assert(!Get<bool>(window, "_theaterMode") && sidebar.IsVisible && header.IsVisible &&
                       comments.Visibility == Visibility.Collapsed && controls.IsVisible,
                    "Leaving theater did not restore normal chrome while preserving a hidden side panel.");
                Get<Button>(window, "ShowSidePanelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Assert(!Get<bool>(window, "_sidePanelHidden") && comments.IsVisible && commentColumn.ActualWidth > 0,
                    "The side panel restore button did not restore the panel and its width.");
                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Assert(!Get<bool>(window, "_fullscreen") && !Get<bool>(window, "_theaterMode") &&
                       sidebar.IsVisible && header.IsVisible && comments.IsVisible && host.Handle == nativeHandle,
                    "Fullscreen from normal mode failed to restore the visible side panel or native video handle.");
                Get<Button>(window, "TheaterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Get<Button>(window, "ShowSidePanelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(window);
                Assert(!Get<bool>(window, "_theaterMode") && !Get<bool>(window, "_sidePanelHidden") &&
                       comments.IsVisible && header.IsVisible,
                    "Explicitly reopening the side panel did not leave theater and restore the requested panel.");
            });
            await Check("fullscreen controls hide after three seconds and wake at bottom", async () =>
            {
                var hide = Get<PlaybackControlsAutoHide>(window, "_controlsAutoHide");
                var tray = Get<Border>(window, "PlaybackControlsTray");
                Assert(!hide.Enabled, "Normal windowed playback unexpectedly enabled automatic hiding.");
                AgeControls(hide, 20);
                TickControls(hide);
                Assert(tray.Child.IsVisible && tray.ActualHeight > 0,
                    "Normal windowed playback hid its controls while idle.");
                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Assert(hide.Enabled, "Fullscreen did not enable automatic playback control hiding.");
                await VerifyAutoHide(window, window, Get<Grid>(window, "VideoColumn"),
                    Get<Border>(window, "VideoSurface"), tray, Get<Border>(window, "PlaybackWakeZone"),
                    hide, Get<Slider>(window, "VolumeSlider"), Get<Button>(window, "SubtitleButton"),
                    viewArtifacts, "fullscreen");
                Invoke(window, "ToggleFullscreen");
                await LayoutIdle(window);
                Assert(!hide.Enabled && tray.Child.IsVisible && tray.Child.IsHitTestVisible &&
                       double.IsNaN(tray.Height) && host.Handle == nativeHandle,
                    "Leaving fullscreen failed to immediately restore controls or retain the video handle.");
            });
            await Check("fullscreen theater action always exits to theater", async () =>
            {
                foreach (var priorTheater in new[] { false, true })
                {
                    Set(window, "_theaterMode", priorTheater);
                    Invoke(window, "ApplyPlayerViewLayout");
                    Invoke(window, "ToggleFullscreen");
                    await LayoutIdle(window);
                    Get<Button>(window, "TheaterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await LayoutIdle(window);
                    Assert(!Get<bool>(window, "_fullscreen") && Get<bool>(window, "_theaterMode") &&
                           Get<Border>(window, "Sidebar").Visibility == Visibility.Collapsed && host.Handle == nativeHandle,
                        "Theater from fullscreen restored normal mode when the prior theater flag was " + priorTheater + ".");
                    Set(window, "_theaterMode", false);
                    Invoke(window, "ApplyPlayerViewLayout");
                }
            });
            await Check("independent subtitle popup follows only its actual owner", () =>
                VerifyIndependentSubtitlePopupOwner(window, host, nativeHandle));
            await Check("popout native video, share and volume interaction", async () =>
            {
                Invoke(window, "Popout_Click", window, new RoutedEventArgs());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (GetNullable<Window>(window, "_popoutWindow") is null ||
                       GetNullable<Border>(window, "_popoutSurface")?.Child != host)
                    await Task.Delay(10, deadline.Token);
                var popup = Get<Window>(window, "_popoutWindow");
                popup.UpdateLayout();
                await popup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var surface = Get<Border>(window, "_popoutSurface");
                var status = Get<TextBlock>(window, "StatusText");
                status.Text = "before-popout-click";
                AssertNativeTarget(popup, surface);
                Click(popup, surface, new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                Assert(status.Text == "플레이어 연결이 끊겼습니다. 영상을 다시 여세요.",
                    "Popout raw video input did not reach the shared pause route.");
                Assert(Get<Button>(window, "_popoutPause").IsKeyboardFocused,
                    "Popout video input did not restore playback keyboard focus.");
                var sliders = Descendants(popup).OfType<Slider>().ToArray();
                var volume = sliders.Single(slider => slider.Maximum == 100);
                foreach (var volumeState in new[] { "success", "current-error", "video", "account" })
                    await Check("popout volume queue " + volumeState,
                        () => VerifyVolumeCommandBurst(window, volume, "popout", volumeState));
                await TrackClickThenDrag(popup, volume);
                Assert(Math.Abs(Get<Slider>(window, "VolumeSlider").Value - volume.Value) < .1,
                    "Popout volume stopped synchronizing to the main volume setting.");
                var share = Descendants(popup).OfType<Button>().SingleOrDefault(button => Equals(button.Content, "공유"));
                Assert(share is not null, "The popout has no share button.");
                var copied = new List<string>();
                Set(window, "_copyShareText", new Action<string>(copied.Add));
                share!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(copied.SequenceEqual(["https://www.youtube.com/watch?v=aqz-KE-bpKQ"]),
                    "Popout share did not use the same clean video URL.");
                var pin = Descendants(popup).OfType<CheckBox>().Single(check => Equals(check.Content, "항상 위"));
                var pinnedBefore = popup.Topmost;
                PressKey(popup, pin, Key.Space);
                Assert(popup.Topmost != pinnedBefore, "The popout preview shortcut consumed the pin checkbox's Space key.");
                pin.IsChecked = pinnedBefore;
                PressKey(popup, share, Key.Space);
                Assert(copied.Count == 2, "Space on the popout share button was consumed or clicked more than once.");
                volume.SetCurrentValue(Slider.ValueProperty, 60d);
                PressKey(popup, volume, Key.Right);
                Assert(Math.Abs(volume.Value - (60 + volume.SmallChange)) < .001,
                    "The popout preview shortcut consumed the volume slider's Right key.");
                await WaitForVolumeDrain(window);
                VerifyKeyboardSeek(window, popup, Get<Slider>(window, "_popoutSeek"),
                    Get<Button>(window, "_popoutPause"), "popout");
                Assert(host.Handle == nativeHandle, "Opening popout recreated the native video handle.");
                var layout = (Grid)popup.Content;
                var tray = layout.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 1);
                var wake = layout.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 2);
                var hide = Get<PlaybackControlsAutoHide>(window, "_popoutControlsAutoHide");
                var subtitles = Descendants(popup).OfType<Button>().Single(button => Equals(button.Content, "자막 ▾"));
                Assert(hide.Enabled, "Popout did not enable automatic playback control hiding.");
                await VerifySubtitlePopupAnchor(window, popup, subtitles, viewArtifacts, "popout");
                await VerifyAutoHide(window, popup, layout, surface, tray, wake, hide, volume, subtitles,
                    viewArtifacts, "popout");
                var retainedCurrent = Get<VideoItem>(window, "_current");
                var retainedSnapshot = GetNullable<PlaybackSnapshot>(window, "_snapshot");
                var retainedGeneration = Get<int>(window, "_videoGeneration");
                CancelEventHandler cancelClose = (_, args) => args.Cancel = true;
                popup.Closing += cancelClose;
                try
                {
                    var stop = (Task)Invoke(window, "StopPlaybackAsync")!;
                    await stop;
                    await LayoutIdle(popup);
                    Assert(Equals(stop.GetType().GetProperty("Result")?.GetValue(stop), false) &&
                           ReferenceEquals(GetNullable<Window>(window, "_popoutWindow"), popup) &&
                           ReferenceEquals(GetNullable<VideoItem>(window, "_current"), retainedCurrent) &&
                           ReferenceEquals(GetNullable<PlaybackSnapshot>(window, "_snapshot"), retainedSnapshot) &&
                           Get<bool>(window, "_playerStarted") && Get<int>(window, "_videoGeneration") == retainedGeneration,
                        "A cancelled popout close still cleared playback or advanced its generation.");
                    Assert(ReferenceEquals(host.Parent, surface) && host.Handle == nativeHandle &&
                           Get<Button>(window, "PopoutLaunchButton").Content?.ToString() == "복귀",
                        "A cancelled popout close lost its video surface or Return button state.");
                    Invoke(window, "Back_Click", window, new RoutedEventArgs());
                    await LayoutIdle(window);
                    Assert(Get<Grid>(window, "PlayerPanel").IsVisible && ReferenceEquals(GetNullable<VideoItem>(window, "_current"), retainedCurrent),
                        "A cancelled popout close still navigated away from the player.");
                    Console.WriteLine("UIREVIEW popout-close-cancel=retained scope=controlled-Closing-cancellation-not-native-reattachment-fault");
                }
                finally { popup.Closing -= cancelClose; }
                popup.Close();
                Assert(Get<Border>(window, "VideoSurface").Child == host && host.InputDisabled &&
                       host.Handle == nativeHandle && !hide.Enabled && !ControlsField<DispatcherTimer>(hide, "_timer").IsEnabled,
                    "Popout close failed to restore the same disabled native host or stop its hide timer.");
            });
            await Check("description popup follows owner lifecycle and handles Escape in text", async () =>
            {
                var popup=Get<Popup>(window,"DescriptionPopup");
                var text=Get<TextBox>(window,"DescriptionText");
                Invoke(window,"SetVideoDescription","Selectable fixture description",false);
                foreach(var lifecycle in new[]{"OnDeactivated","OnLocationChanged","OnStateChanged"})
                {
                    popup.IsOpen=true;
                    Assert(popup.IsOpen,"Description popup fixture did not open.");
                    typeof(Window).GetMethod(lifecycle,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[EventArgs.Empty]);
                    Assert(!popup.IsOpen,"Description popup survived owner "+lifecycle);
                }
                popup.IsOpen=true;window.Width+=8;window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(!popup.IsOpen,"Description popup survived owner resize.");
                popup.IsOpen=true;popup.Child.UpdateLayout();
                text.Focus();text.Select(0,10);
                Assert(popup.IsOpen && text.SelectedText=="Selectable","Description text selection dismissed the popup.");
                var source=PresentationSource.FromVisual(text) ?? PresentationSource.FromVisual(window)!;
                var escape=new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                text.RaiseEvent(escape);
                Assert(escape.Handled && !popup.IsOpen,"Escape in description text did not close its popup.");
            });
            await Check("invalid RYD and subtitle data reaches feature error UI", async () =>
            {
                var original=Get<YouTubeService>(window,"_youtube");
                using var client=new HttpClient(new InvalidDislikeHandler());
                var service=(YouTubeService)Activator.CreateInstance(typeof(YouTubeService),BindingFlags.Instance|BindingFlags.NonPublic,
                    null,[new RuntimePaths(root),Get<SessionService>(window,"_session"),client,TimeSpan.FromSeconds(2),(Func<DateTimeOffset>)(()=>DateTimeOffset.UtcNow)],null)!;
                Set(window,"_youtube",service);Set(window,"_demo",false);
                try
                {
                    await (Task)Invoke(window,"LoadDislikeStatsAsync")!;
                    Assert(Get<TextBlock>(window,"DislikeStatsState").Text.Contains("불러오지 못했습니다"),"Malformed RYD response left its loading indicator.");
                    Func<CancellationToken,Task> fail=_=>Task.FromException(new InvalidDataException("synthetic invalid subtitle"));
                    var player = Get<MpvPlayerService>(window,"_player");
                    await (Task)Invoke(window,"SelectSubtitleFromMenuAsync",fail,"Fixture",Get<int>(window,"_videoGeneration"),player.PreferredSubtitleLanguage,player.Snapshot.MediaGeneration)!;
                    Assert(!Get<TextBlock>(window,"StatusText").Text.Contains("불러오는 중"),"Malformed subtitle left its loading indicator.");
                    Assert(typeof(MainWindow).GetMethod("TrackRatingTask",BindingFlags.Instance|BindingFlags.NonPublic)!.ReturnType==typeof(Task),"Rating tracking still has an async-void second exception observer.");
                    var tracked=Invoke(window,"TrackRatingTask",Task.FromException(new InvalidDataException("single observer")));
                    var observed=0;try{await (Task)tracked!;}catch(InvalidDataException){observed++;}
                    Assert(observed==1 && Get<HashSet<Task>>(window,"_ratingTasks").Count==0,"Failed tracked rating was not removed or observed exactly once.");
                }
                finally{Set(window,"_youtube",original);Set(window,"_demo",true);}
            });
            await Check("independent popout minimize return and automated cleanup", () =>
                VerifyIndependentPopoutLifecycle(window, host, nativeHandle));

        }
        finally
        {
            if (Mouse.Captured is not null) Mouse.Capture(null);
            if (surrogate != 0) DestroyWindow(surrogate);
            GetNullable<Window>(window, "_popoutWindow")?.Close();
            GetNullable<PlaybackControlsAutoHide>(window, "_controlsAutoHide")?.Dispose();
            Set(window, "_playerStarted", false);
            Set(window, "_current", null);
            Get<DispatcherTimer>(window, "_saveTimer").Stop();
            Get<CancellationTokenSource>(window, "_accountCts").Dispose();
            Get<CancellationTokenSource>(window, "_historyCts").Dispose();
            ThumbnailImage.Cache?.Dispose(); ThumbnailImage.Cache = null;
            Set(window, "_allowClose", true);
            window.Close();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        Console.WriteLine("INTERACTION scope=real-WPF-controls WPF-raw-input-with-explicit-source synchronous-captured-movement-before-native-message-dispatch Win32-disabled-subtree-hit-test enabled-surrogate-child clipboard-sink view-mode-state native-handle-preservation deterministic-idle no-physical-mouse no-real-player");

        async Task Check(string label, Func<Task> check)
        {
            var group=label.StartsWith("prepared subtitle failure advice",StringComparison.Ordinal) ||
                label.StartsWith("prepared caption request policy",StringComparison.Ordinal) ||
                label.StartsWith("manual subtitle intent cancels prepared captions",StringComparison.Ordinal) ||
                label.StartsWith("in-flight prefetch survives",StringComparison.Ordinal)?"prepared-subtitles":
                label.StartsWith("manual subtitle recovery advice",StringComparison.Ordinal) ||
                label.StartsWith("subtitle controls hold",StringComparison.Ordinal) ||
                label.StartsWith("visible subtitle catalog failure",StringComparison.Ordinal) ||
                label.StartsWith("subtitle selection survives",StringComparison.Ordinal) ||
                label.StartsWith("subtitle read and selection",StringComparison.Ordinal) ||
                label=="main subtitle popup commits a ready anchored menu"?"subtitle-selection":"controls";
            if(suite is not null && group!=suite)return;
            stopToken.ThrowIfCancellationRequested();
            // Nested checks (the popout volume cases) borrow an existing popout.
            // Only the check that created a window owns its teardown.
            var popoutAtStart = GetNullable<Window>(window, "_popoutWindow");
            try { await check();stopToken.ThrowIfCancellationRequested(); Console.WriteLine("INTERACTION passed=" + label); }
            catch(OperationCanceledException)when(stopToken.IsCancellationRequested){throw;}
            catch (Exception error) { failures.Add(new InvalidOperationException(label + ": " + error.Message, error)); }
            finally
            {
                Cleanup("mouse capture", () => { if (Mouse.Captured is not null) Mouse.Capture(null); });
                Cleanup("popout teardown", () =>
                {
                    if (GetNullable<Window>(window, "_popoutWindow") is { } lingeringPopout &&
                        !ReferenceEquals(lingeringPopout, popoutAtStart))
                    {
                        Cleanup("popout playback-stop route", () =>
                            Assert(Equals(Invoke(window, "ClosePopoutForPlaybackStop"), true),
                                "The fixture popout refused to close."));
                        if (GetNullable<Window>(window, "_popoutWindow") is not null)
                            Cleanup("popout fallback close", lingeringPopout.Close);
                        Cleanup("popout isolation", () =>
                            Assert(GetNullable<Window>(window, "_popoutWindow") is null,
                                "A fixture popout remains open and can contaminate the next check."));
                    }
                });
                Cleanup("view layout", () =>
                {
                    if (Get<bool>(window, "_fullscreen")) Invoke(window, "ToggleFullscreen");
                    Set(window, "_theaterMode", false);
                    Set(window, "_sidePanelHidden", false);
                    Invoke(window, "ApplyPlayerViewLayout");
                });
            }

            void Cleanup(string action, Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error)
                {
                    var failure = new InvalidOperationException(label + " cleanup " + action + ": " + error.Message, error);
                    failures.Add(failure);
                    Console.WriteLine("INTERACTION cleanup-failed=" + failure.Message);
                }
            }
        }
    }

    private static async Task VerifyIndependentSubtitlePopupOwner(MainWindow main, MpvHost host, nint nativeHandle)
    {
        var failures=new List<string>();
        var savedBounds=new Rect(main.Left,main.Top,main.Width,main.Height);
        var savedState=main.WindowState;
        Invoke(main,"Popout_Click",main,new RoutedEventArgs());
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(GetNullable<Window>(main,"_popoutWindow") is null || GetNullable<Border>(main,"_popoutSurface")?.Child!=host)
            await Task.Delay(10,deadline.Token);
        var popup=Get<Window>(main,"_popoutWindow");
        await LayoutIdle(popup);
        var anchor=Descendants(popup).OfType<Button>().Single(button=>Equals(button.Content,"자막 ▾"));
        var popupBounds=new Rect(popup.Left,popup.Top,popup.Width,popup.Height);
        var show=typeof(MainWindow).GetMethod("ShowSubtitleMenuAtAnchor",BindingFlags.Static|BindingFlags.NonPublic)!;
        try
        {
            foreach(var route in new[]{"deactivated","location","state","size"})
            {
                var menu=await OpenMenu(anchor,popup);
                ChangeOwner(main,route);
                await LayoutIdle(main);await LayoutIdle(popup);
                if(!menu.IsOpen || !ReferenceEquals(GetNullable<Window>(main,"_subtitlePopupOwner"),popup) ||
                    !ReferenceEquals(menu.PlacementTarget,anchor))
                    failures.Add("Main "+route+" closed an independent popout subtitle menu.");
                Invoke(main,"CloseSubtitleMenu");
                RestoreMain();await LayoutIdle(main);
            }
            foreach(var route in new[]{"deactivated","location","state","size","anchor-hidden","anchor-unloaded","global-close"})
            {
                var menu=await OpenMenu(anchor,popup);
                if(route=="anchor-hidden")anchor.Visibility=Visibility.Collapsed;
                else if(route=="anchor-unloaded")anchor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                else if(route=="global-close")Invoke(main,"CloseSubtitlePopup");
                else ChangeOwner(popup,route);
                await LayoutIdle(popup);
                if(menu.IsOpen || menu.PlacementTarget is not null || GetNullable<ContextMenu>(main,"_subtitleMenu") is not null)
                    failures.Add("Actual popout "+route+" retained its subtitle menu or anchor.");
                Invoke(main,"CloseSubtitleMenu");anchor.Visibility=Visibility.Visible;
                popup.WindowState=WindowState.Normal;popup.Left=popupBounds.Left;popup.Top=popupBounds.Top;
                popup.Width=popupBounds.Width;popup.Height=popupBounds.Height;await LayoutIdle(popup);
            }
            var closingMenu=await OpenMenu(anchor,popup);
            popup.Close();await LayoutIdle(main);
            if(closingMenu.IsOpen || closingMenu.PlacementTarget is not null || GetNullable<ContextMenu>(main,"_subtitleMenu") is not null)
                failures.Add("Closing the actual popout owner retained its subtitle menu or anchor.");
            Assert(GetNullable<Window>(main,"_popoutWindow") is null && Get<Border>(main,"VideoSurface").Child==host && host.Handle==nativeHandle,
                "The subtitle owner fixture did not restore the original native host.");
            var mainMenu=await OpenMenu(Get<Button>(main,"SubtitleButton"),main);
            ChangeOwner(main,"deactivated");await LayoutIdle(main);
            if(mainMenu.IsOpen || mainMenu.PlacementTarget is not null || GetNullable<ContextMenu>(main,"_subtitleMenu") is not null)
                failures.Add("A main-owned subtitle menu survived main deactivation.");
            Assert(failures.Count==0,string.Join("; ",failures));
            Console.WriteLine("UIREVIEW subtitle-owner main-lifecycle-isolated=True actual-owner-lifecycle-closes=True anchor-hidden-unloaded-closes=True global-cleanup-closes=True scope=offline-WPF-events-no-physical-pointer");
        }
        finally
        {Invoke(main,"CloseSubtitleMenu");RestoreMain();await LayoutIdle(main);}

        async Task<ContextMenu> OpenMenu(Button button,Window owner)
        {
            var menu=new ContextMenu{PlacementTarget=button,Placement=PlacementMode.Bottom,StaysOpen=true};
            menu.Items.Add(new MenuItem{Header="Offline independent owner fixture"});
            Set(main,"_subtitleMenu",menu);Invoke(main,"AttachSubtitlePopupOwner",menu);show.Invoke(null,[menu]);
            await LayoutIdle(owner);
            Assert(menu.IsOpen && ReferenceEquals(GetNullable<Window>(main,"_subtitlePopupOwner"),owner),
                "The subtitle owner fixture did not open on its actual owner.");
            return menu;
        }
        static void ChangeOwner(Window owner,string route)
        {
            switch(route)
            {
                case "deactivated":typeof(Window).GetMethod("OnDeactivated",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[EventArgs.Empty]);break;
                case "location":owner.Left+=13;break;
                case "state":owner.WindowState=WindowState.Minimized;break;
                case "size":owner.Width+=17;break;
            }
        }
        void RestoreMain()
        {
            main.WindowState=WindowState.Normal;main.Left=savedBounds.Left;main.Top=savedBounds.Top;
            main.Width=savedBounds.Width;main.Height=savedBounds.Height;main.WindowState=savedState;
        }
    }

    private static async Task VerifyIndependentPopoutLifecycle(MainWindow main, MpvHost host, nint nativeHandle)
    {
        var failures = new List<string>();
        var player = Get<MpvPlayerService>(main, "_player");
        var sessionField = typeof(MpvPlayerService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var current = Get<VideoItem>(main, "_current");
        var snapshot = GetNullable<PlaybackSnapshot>(main, "_snapshot");
        var generation = Get<int>(main, "_videoGeneration");
        var savedState = main.WindowState;
        var savedBounds = new Rect(main.Left, main.Top, main.Width, main.Height);
        var savedShutdown = Get<bool>(main, "_popoutShutdown");
        using var commands = new PlayerCommandFixture(player);
        var session = sessionField.GetValue(player);
        var routeIndex = 0;
        foreach (var priorState in new[] { WindowState.Normal, WindowState.Maximized })
        foreach (var pinned in new[] { false, true })
        {
            Window? popup = null;
            try
            {
                main.WindowState = priorState;
                await LayoutIdle(main);
                Get<Button>(main, "PopoutLaunchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await LayoutIdle(main);
                popup = Get<Window>(main, "_popoutWindow");
                var popupHandle = new WindowInteropHelper(popup).Handle;
                var pin = Descendants(popup).OfType<CheckBox>().Single();
                pin.IsChecked = pinned;
                main.WindowState = WindowState.Minimized;
                await LayoutIdle(popup);
                var owner = GetWindow(popupHandle, 4); // GW_OWNER: managed Owner alone is not native proof.
                var popupVisible = IsWindowVisible(popupHandle);
                var videoVisible = IsWindowVisible(nativeHandle);
                var minimized = IsIconic(popupHandle);
                Console.WriteLine($"UIREVIEW popout-independent prior={priorState} pin={pinned} nativeOwner={owner} popupVisible={popupVisible} videoVisible={videoVisible} popupMinimized={minimized}");
                if (popup.Owner is not null || owner != 0 || !popupVisible || !videoVisible || minimized || popup.Topmost != pinned || main.Topmost)
                    failures.Add($"{priorState}/pin={pinned}: main minimize affected native popout visibility, ownership or independent pin state");
                if (Get<Button>(main, "PopoutLaunchButton").Content?.ToString() != "복귀")
                    failures.Add($"{priorState}/pin={pinned}: main button did not become Return");
                await (Task)Invoke(main, "PlayerCommandAsync", (object)new object[] { "cycle", "pause" })!;
                var route = new[] { "main-button", "popout-button", "escape", "window-close" }[routeIndex++];
                switch (route)
                {
                    case "main-button": Get<Button>(main, "PopoutLaunchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                    case "popout-button": Descendants(popup).OfType<Button>().Single(button => Equals(button.Content, "원래 창으로")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                    case "escape": PressKey(popup, Get<Button>(main, "_popoutPause"), Key.Escape); break;
                    default: popup.Close(); break;
                }
                await LayoutIdle(main);
                if (GetNullable<Window>(main, "_popoutWindow") is not null || !ReferenceEquals(host.Parent, Get<Border>(main, "VideoSurface")) ||
                    main.WindowState != priorState || !main.IsActive || Get<Button>(main, "PopoutLaunchButton").Content?.ToString() != "팝아웃")
                    failures.Add($"{route}: explicit return did not close popout, restore/activate the prior main state and reset its button");
                if (host.Handle != nativeHandle || !ReferenceEquals(sessionField.GetValue(player), session) ||
                    !ReferenceEquals(GetNullable<VideoItem>(main, "_current"), current) ||
                    !ReferenceEquals(GetNullable<PlaybackSnapshot>(main, "_snapshot"), snapshot) || Get<int>(main, "_videoGeneration") != generation)
                    failures.Add(route + ": returning changed the native video, IPC session, media or generation");
                Console.WriteLine($"UIREVIEW popout-return route={route} expectedMain={priorState} actualMain={main.WindowState} active={main.IsActive} sameHost={host.Handle == nativeHandle} sameSession={ReferenceEquals(sessionField.GetValue(player), session)}");
            }
            finally
            {
                if (GetNullable<Window>(main, "_popoutWindow") is not null) popup?.Close();
                main.WindowState = WindowState.Normal;
                await LayoutIdle(main);
            }
        }
        Get<Button>(main, "PopoutLaunchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await LayoutIdle(main);
        var canceledPopup = Get<Window>(main, "_popoutWindow");
        main.WindowState = WindowState.Minimized;
        await LayoutIdle(canceledPopup);
        CancelEventHandler cancelClose = (_, args) => args.Cancel = true;
        canceledPopup.Closing += cancelClose;
        try
        {
            canceledPopup.Close();
            await LayoutIdle(canceledPopup);
            var retained = ReferenceEquals(GetNullable<Window>(main, "_popoutWindow"), canceledPopup) &&
                ReferenceEquals(host.Parent, Get<Border>(main, "_popoutSurface")) && host.Handle == nativeHandle &&
                Get<Button>(main, "PopoutLaunchButton").Content?.ToString() == "복귀" && main.WindowState == WindowState.Minimized;
            if (!retained) failures.Add("cancel-close: closing cancellation lost popout video/Return state or restored main");
            Console.WriteLine($"UIREVIEW popout-close-cancel retainedSurfaceAndReturn={retained} sameHost={host.Handle == nativeHandle} mainMinimized={main.WindowState == WindowState.Minimized}");
        }
        finally
        {
            canceledPopup.Closing -= cancelClose;
            Invoke(main, "ClosePopoutForPlaybackStop");
            main.WindowState = WindowState.Normal;
            await LayoutIdle(main);
        }
        foreach (var transition in new[] { "playback-stop", "account-transition", "shutdown" })
        {
            Get<Button>(main, "PopoutLaunchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await LayoutIdle(main);
            main.WindowState = WindowState.Minimized;
            await LayoutIdle(main);
            if (transition == "account-transition") Invoke(main, "SetAccountBusy", true);
            try
            {
                // StopPlaybackAsync and ResetAccountAsync share this close boundary.
                // Shutdown uses its distinct explicit close now that native ownership is gone.
                Invoke(main, transition == "shutdown" ? "ClosePopoutForShutdown" : "ClosePopoutForPlaybackStop");
                await LayoutIdle(main);
                if (GetNullable<Window>(main, "_popoutWindow") is not null || main.WindowState != WindowState.Minimized || main.IsActive ||
                    host.Handle != nativeHandle || !ReferenceEquals(host.Parent, Get<Border>(main, "VideoSurface")) ||
                    !ReferenceEquals(sessionField.GetValue(player), session) || Get<Button>(main, "PopoutLaunchButton").Content?.ToString() != "팝아웃")
                    failures.Add(transition + ": automatic close raised main, retained popout or changed video/session/button state");
                Console.WriteLine($"UIREVIEW popout-automatic transition={transition} mainMinimized={main.WindowState == WindowState.Minimized} active={main.IsActive} popoutClosed={GetNullable<Window>(main, "_popoutWindow") is null}");
            }
            finally
            {
                if (transition == "account-transition") Invoke(main, "SetAccountBusy", false);
                main.WindowState = WindowState.Normal;
                await LayoutIdle(main);
            }
        }
        Set(main, "_popoutShutdown", savedShutdown);
        main.WindowState = WindowState.Normal;
        main.Left = savedBounds.Left; main.Top = savedBounds.Top;
        main.Width = savedBounds.Width; main.Height = savedBounds.Height;
        main.WindowState = savedState;
        await LayoutIdle(main);
        Assert(commands.Commands.Count == 4 && commands.Commands.All(command => command.GetRawText() == "[\"cycle\",\"pause\"]"),
            "Popout transfers restarted playback or broke the existing IPC command session.");
        Assert(failures.Count == 0, string.Join("; ", failures));
    }

    private static async Task VerifyAutoHide(MainWindow main, Window owner, FrameworkElement area,
        FrameworkElement video, Border tray, Border wake, PlaybackControlsAutoHide hide, Slider volume,
        Button subtitleButton, string artifacts, string prefix)
    {
        var center = new Point(video.ActualWidth / 2, video.ActualHeight / 3);
        SendMouse(owner, video, center, 0x0200, 0);
        hide.Reveal();
        await LayoutIdle(owner);
        var videoHeight = video.ActualHeight;
        var shownHeight = tray.ActualHeight;
        Assert(shownHeight > 0 && tray.Child.IsVisible, "Playback tray did not start visible.");
        AgeControls(hide, 2.8);
        TickControls(hide);
        Assert(!ControlsField<bool>(hide, "_hidden") && tray.Child.IsVisible,
            "Playback controls hid before the three-second idle interval.");
        var heightAnimatedDuringHide = false;
        var host = Get<MpvHost>(main, "VideoHost");
        var hideResizes = await CountHostResizes(host, async () =>
        {
            AgeControls(hide, 3.2);
            var beforeTick = AutoHideDiagnostic(main, owner, area, tray, hide);
            TickControls(hide);
            var afterTick = AutoHideDiagnostic(main, owner, area, tray, hide);
            if (!ControlsField<bool>(hide, "_hidden"))
                Console.WriteLine($"UIREVIEW auto-hide-blocked prefix={prefix} before={beforeTick} after={afterTick}");
            heightAnimatedDuringHide = DependencyPropertyHelper.GetValueSource(tray, FrameworkElement.HeightProperty).IsAnimated;
            await WaitForTray(owner, tray, hidden: true,
                () => $"phase=initial prefix={prefix} beforeTick={beforeTick} afterTick={afterTick} current={AutoHideDiagnostic(main, owner, area, tray, hide)}");
        });
        Console.WriteLine($"UIREVIEW auto-hide prefix={prefix} hostResizes={hideResizes} trayHeightAnimated={heightAnimatedDuringHide} scope=WPF-native-host-size-not-GPU-allocation");
        Assert(!heightAnimatedDuringHide && hideResizes <= 1,
            "Hiding playback controls animated layout height or repeatedly resized the native video host.");
        Assert(tray.ActualHeight < 1 && !tray.Child.IsHitTestVisible && video.ActualHeight > videoHeight + shownHeight - 2,
            "Hidden playback controls still reserved space or accepted pointer input.");
        CaptureView(owner, artifacts, prefix + "-hidden");

        // Moving over video must not cover it with controls. The transparent
        // bottom strip receives real WPF raw input despite the native video host.
        var beforeUpperVideoInput = AutoHideDiagnostic(main, owner, area, tray, hide);
        SendMouse(owner, video, new Point(center.X + 20, center.Y), 0x0200, 0);
        var afterUpperVideoInput = AutoHideDiagnostic(main, owner, area, tray, hide);
        await LayoutIdle(owner);
        if (!ControlsField<bool>(hide, "_hidden") || tray.Child.IsVisible)
            Console.WriteLine($"UIREVIEW auto-hide-unexpected-reveal phase=upper-video prefix={prefix} beforeInput={beforeUpperVideoInput} afterInput={afterUpperVideoInput} current={AutoHideDiagnostic(main, owner, area, tray, hide)}");
        Assert(ControlsField<bool>(hide, "_hidden") && !tray.Child.IsVisible,
            "Moving through the upper video unexpectedly woke the playback controls.");
        Assert(wake.IsVisible && wake.ActualHeight > 0, "A hidden tray had no bottom pointer wake area.");
        var revealResizes = await CountHostResizes(host, async () =>
        {
            SendMouse(owner, wake, new Point(wake.ActualWidth / 2, wake.ActualHeight / 2), 0x0200, 0);
            await WaitForTray(owner, tray, hidden: false);
        });
        Console.WriteLine($"UIREVIEW auto-reveal prefix={prefix} hostResizes={revealResizes} slideAnimating={ControlsField<TranslateTransform>(hide, "_slide").HasAnimatedProperties} scope=WPF-native-host-size-not-GPU-allocation");
        Assert(revealResizes <= 1, "Showing playback controls repeatedly resized the native video host.");
        Assert(!ControlsField<bool>(hide, "_hidden") && tray.Child.IsHitTestVisible,
            "Moving into the bottom wake area did not reveal usable playback controls.");
        CaptureView(owner, artifacts, prefix + "-shown");

        volume.ApplyTemplate();
        // Make this a track press, independent of an earlier keyboard test's
        // value. A press on the existing thumb uses native button-state handling.
        volume.SetCurrentValue(Slider.ValueProperty, 10d);
        await LayoutIdle(owner);
        var track = (Track)volume.Template.FindName("PART_Track", volume);
        var press = ValuePoint(track, .6);
        var beforePressValue = volume.Value;
        try
        {
            SendMouse(owner, track, press, 0x0200, 0);
            SendMouse(owner, track, press, 0x0201, 1);
            Assert(Mouse.Captured == volume, "The volume track fixture did not capture its slider.");
            Assert(GetCapture() == ((HwndSource)PresentationSource.FromVisual(owner)).Handle,
                "The volume track fixture did not acquire native capture for its owner.");
            SendMouse(owner, video, center, 0x0200, 1);
            Assert(Mouse.Captured == volume, "The volume track fixture lost slider capture before the idle check.");
            Console.WriteLine($"UIREVIEW captured-track-drag prefix={prefix} initialValue={beforePressValue:F2} captured={Mouse.Captured?.GetType().Name} scope=synthetic-track-drag-not-native-thumb-button-state");
            AgeControls(hide, 20);
            TickControls(hide);
            Assert(!ControlsField<bool>(hide, "_hidden") && tray.Child.IsVisible,
                "Automatic hiding interrupted a captured volume drag outside the tray.");
            SendMouse(owner, track, press, 0x0202, 0);
        }
        finally
        {
            SendMouse(owner, track, press, 0x0202, 0);
            if (Mouse.Captured is not null) Mouse.Capture(null);
        }
        SendMouse(owner, video, center, 0x0200, 0);
        TickControls(hide);
        Assert(!ControlsField<bool>(hide, "_hidden"), "Ending a drag hid controls without a fresh idle interval.");

        var previousMenu = GetNullable<ContextMenu>(main, "_subtitleMenu");
        var previousCts = GetNullable<CancellationTokenSource>(main, "_subtitleMenuReadCts");
        using var pendingCatalog = new CancellationTokenSource();
        var menu = new ContextMenu { PlacementTarget = subtitleButton, StaysOpen = true };
        menu.Items.Add(new MenuItem { Header = "Offline subtitle fixture" });
        try
        {
            Set(main, "_subtitleMenu", menu);
            Set(main, "_subtitleMenuReadCts", pendingCatalog);
            menu.IsOpen = true;
            await LayoutIdle(owner);
            Assert(menu.IsOpen, "The subtitle menu fixture did not open.");
            AgeControls(hide, 20);
            TickControls(hide);
            Assert(!ControlsField<bool>(hide, "_hidden") && tray.Child.IsVisible,
                "Automatic hiding interrupted an open subtitle menu.");
            await CloseFixtureMenuAsync(owner, menu);
            SendMouse(owner, video, center, 0x0200, 0);
            TickControls(hide);
            AgeControls(hide, 3.2);
            var beforeDismissedTick = AutoHideDiagnostic(main, owner, area, tray, hide);
            TickControls(hide);
            var afterDismissedTick = AutoHideDiagnostic(main, owner, area, tray, hide);
            if (!ControlsField<bool>(hide, "_hidden"))
                Console.WriteLine($"UIREVIEW auto-hide-blocked-after-menu prefix={prefix} before={beforeDismissedTick} after={afterDismissedTick}");
            await WaitForTray(owner, tray, hidden: true,
                () => $"phase=after-menu prefix={prefix} beforeTick={beforeDismissedTick} afterTick={afterDismissedTick} current={AutoHideDiagnostic(main, owner, area, tray, hide)}");
            Assert(!pendingCatalog.IsCancellationRequested && !menu.IsOpen,
                "Idle hiding canceled the pending catalog or reopened the dismissed subtitle menu.");
            hide.Reveal();
            await WaitForTray(owner, tray, hidden: false);
            Console.WriteLine($"UIREVIEW dismissed-subtitle-catalog prefix={prefix} pending=True controls-hidden=True menu-closed=True scope=actual-auto-hide-closure-offline-state-fixture");
        }
        finally
        {
            try { await CloseFixtureMenuAsync(owner, menu); }
            finally { Set(main, "_subtitleMenu", previousMenu); Set(main, "_subtitleMenuReadCts", previousCts); }
        }
        SendMouse(owner, video, center, 0x0200, 0);
        TickControls(hide);
        Assert(!ControlsField<bool>(hide, "_hidden"), "Closing a menu hid controls without a fresh idle interval.");
        AgeControls(hide, 3.2);
        var beforeFinalTick = AutoHideDiagnostic(main, owner, area, tray, hide);
        TickControls(hide);
        var afterFinalTick = AutoHideDiagnostic(main, owner, area, tray, hide);
        if (!ControlsField<bool>(hide, "_hidden"))
            Console.WriteLine($"UIREVIEW auto-hide-blocked-final prefix={prefix} before={beforeFinalTick} after={afterFinalTick}");
        await WaitForTray(owner, tray, hidden: true,
            () => $"phase=final prefix={prefix} beforeTick={beforeFinalTick} afterTick={afterFinalTick} current={AutoHideDiagnostic(main, owner, area, tray, hide)}");
        hide.Reveal();
        await WaitForTray(owner, tray, hidden: false);
        Assert(area.ActualWidth > 0, "The playback pointer area was detached while hiding controls.");
    }

    private static async Task WaitForTray(Window owner, Border tray, bool hidden, Func<string>? diagnostic = null)
    {
        var deadline = Stopwatch.StartNew();
        var slide = ((TransformGroup)tray.Child.RenderTransform).Children.OfType<TranslateTransform>().Last();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            await LayoutIdle(owner);
            if (hidden ? tray.Child.Visibility == Visibility.Hidden && tray.ActualHeight < 1 :
                tray.Child.IsVisible && tray.ActualHeight > 0 && double.IsNaN(tray.Height) &&
                !slide.HasAnimatedProperties && Math.Abs(slide.Y) < .001) return;
            await Task.Delay(15);
        }
        var extra = diagnostic is null ? "" : " diag=" + diagnostic();
        Assert(false, (hidden ? "Playback tray hide animation did not complete." : "Playback tray reveal animation did not complete.") +
            $" height={tray.ActualHeight:F2} visibility={tray.Child.Visibility} slideY={slide.Y:F2} slideAnimating={slide.HasAnimatedProperties} captured={Mouse.Captured?.GetType().Name ?? "none"} leftButton={Mouse.LeftButton} focused={Keyboard.FocusedElement?.GetType().Name ?? "none"}{extra}");
    }

    private static async Task VerifyPreparedCaptionFailure(MainWindow main, string workspace, string mode)
    {
        // Reuse the owned local IPC fixture: it never opens media or a network
        // connection. Its Python child only keeps a job alive for ownership proof.
        var fixtureType = typeof(PlayerRootRegression).GetNestedType("SubtitleFixture", BindingFlags.NonPublic)!;
        var create = (Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null, [workspace])!;
        await create;
        var fixture = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player = (MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var previousPlayer = Get<MpvPlayerService>(main, "_player");
        var previousExpected = Get<long>(main, "_expectedMediaGeneration");
        var previousNotice = Get<string?>(main, "_subtitleLoadError");
        var status = Get<TextBlock>(main, "StatusText"); var previousStatus = status.Text;
        Task? loading = null;
        try
        {
            ((System.Collections.IList)fixtureType.GetField("_tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!).Clear();
            fixtureType.GetProperty("Sid")!.SetValue(fixture, "no");
            Set(main, "_player", player); Set(main, "_expectedMediaGeneration", 7L);
            var caption = new PreparedCaption("ko", mode == "retained" ? "http://fixture.invalid/invalid" : "https://www.youtube.com/api/timedtext?v=fixture0001&lang=ko");
            if (mode == "stopped")
            {
                fixtureType.GetProperty("SkipSubAdd")!.SetValue(fixture, true);
                // A definite unchanged rejection now correctly retains playback.
                // Omit the terminal acknowledgement status instead: the caller
                // cannot prove whether the native subtitle mutation completed.
                fixtureType.GetProperty("OmitSubAddReplyStatus")!.SetValue(fixture, true);
            }
            if (mode == "orphaned-loaded")
            {
                // A historical Loaded bit cannot establish that an owned player
                // still exists, even when the media generation matches.
                await player.StopAsync();
                typeof(MpvPlayerService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(player, new PlaybackSnapshot { MediaGeneration = 7, Loaded = true });
            }
            if (mode == "stale-generation") fixtureType.GetMethod("BlockTrackRead")!.Invoke(fixture, null);
            status.Text = "caption-newer-state";
            loading = (Task)Invoke(main, "LoadPreparedCaptionsAsync", new[] { caption }, Get<int>(main, "_videoGeneration"))!;
            if (mode == "stale-generation")
            {
                await ((TaskCompletionSource)fixtureType.GetProperty("TrackReadStarted")!.GetValue(fixture)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixtureType.GetMethod("SetMedia")!.Invoke(fixture, [99L, true, null]);
                fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null);
            }
            await loading.WaitAsync(TimeSpan.FromSeconds(8));
            if (mode == "stale-generation")
                Assert(status.Text == "caption-newer-state" && Get<string?>(main, "_subtitleLoadError") is null,
                    "A prepared-caption failure from an obsolete service generation overwrote newer playback state.");
            else if (mode == "retained")
                Assert(player.OwnedProcessIds.Count > 0 && player.Snapshot.MediaGeneration == 7 && !player.Snapshot.Ended &&
                       status.Text.Contains("자막 메뉴에서 다시 선택"),
                    "A rejected caption that retained the exact owned player did not offer a subtitle-only retry.");
            else
                Assert(player.OwnedProcessIds.Count == 0 && status.Text.Contains("영상을 다시 재생") &&
                       !status.Text.Contains("자막 메뉴에서 다시 선택"),
                    "Prepared-caption failure advised subtitle-only retry after its owned player was unavailable: " + mode);
            Console.WriteLine("UIREVIEW prepared-caption-advice mode=" + mode + " scope=owned-offline-IPC-no-media-no-network");
        }
        finally
        {
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null);
            if (loading is not null) try { await loading.WaitAsync(TimeSpan.FromSeconds(8)); } catch { }
            Set(main, "_player", previousPlayer); Set(main, "_expectedMediaGeneration", previousExpected);
            Set(main, "_subtitleLoadError", previousNotice); status.Text = previousStatus;
            await ((IAsyncDisposable)fixture).DisposeAsync();
        }
    }

    private static async Task VerifyPreparedCaptionPolicy(MainWindow main,string workspace,string mode)
    {
        var fixtureType=typeof(PlayerRootRegression).GetNestedType("SubtitleFixture",BindingFlags.NonPublic)!;
        var create=(Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null,[workspace])!;await create;
        var fixture=create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player=(MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved=SaveSubtitleOperationState(main);var status=Get<TextBlock>(main,"StatusText");var priorStatus=status.Text;
        try
        {
            Set(main,"_player",player);Set(main,"_expectedMediaGeneration",7L);
            Set(main,"_subtitleLoadError",null);Set(main,"_subtitleMenuCts",null);
            if(mode is "budget" or "first-rejection-fallback" or "preferred-fallback" or "unconfirmed-no-retry" or "changed-rejection")
                fixtureType.GetMethod("ClearNativeSubtitles")!.Invoke(fixture,null);
            if(mode is "preferred-fallback" or "existing-unrelated")
                typeof(MpvPlayerService).GetField("_subtitleLanguage",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(player,mode=="existing-unrelated"?"ja":"ko");
            if(mode=="manual-off")await player.SelectSubtitleAsync(null,7);
            if(mode=="budget")fixtureType.GetProperty("RejectSubAdd")!.SetValue(fixture,true);
            if(mode=="unconfirmed-no-retry")fixtureType.GetProperty("SkipSubAdd")!.SetValue(fixture,true);
            if(mode=="changed-rejection")
            {fixtureType.GetProperty("RejectSubAdd")!.SetValue(fixture,true);fixtureType.GetProperty("AddTrackOnRejectedSubAdd")!.SetValue(fixture,true);}
            if(mode is "first-rejection-fallback" or "preferred-fallback")
                ((HashSet<string>)fixtureType.GetProperty("RejectedSubAddLanguages")!.GetValue(fixture)!).Add("ko");
            var captions=new[]{"ko","en","ja","fr","de"}.Select(language=>new PreparedCaption(language,
                "https://www.youtube.com/api/timedtext?v=fixture0001&lang="+language)).ToArray();
            await (Task)Invoke(main,"LoadPreparedCaptionsAsync",captions,Get<int>(main,"_videoGeneration"))!;
            var requests=(int)fixtureType.GetProperty("SubAdds")!.GetValue(fixture)!;
            var notice=Get<string?>(main,"_subtitleLoadError");
            var expected=mode switch{"budget"=>3,"first-rejection-fallback" or "preferred-fallback"=>2,"existing-unrelated" or "unconfirmed-no-retry" or "changed-rejection"=>1,_=>0};
            Assert(requests==expected,$"Prepared caption policy {mode} used {requests} native requests, expected {expected}.");
            Assert(player.HasActiveMedia(7)==(mode!="changed-rejection"),"The prepared caption failure boundary retained or stopped the wrong media: "+mode);
            if(mode=="preferred-fallback")
                Assert(notice?.Contains("선호 언어")==true && player.PreferredSubtitleLanguage=="ko",
                    "A successful fallback hid the failed requested language or committed it as the preference.");
            else if(mode is "budget" or "unconfirmed-no-retry")Assert(notice?.Contains("자막 메뉴에서 다시 선택")==true,"The exhausted or unconfirmed request hid its failure.");
            else if(mode=="changed-rejection")Assert(notice?.Contains("영상을 다시 재생")==true,"Changed native state did not request media recovery.");
            else Assert(notice is null,"A satisfied optional caption policy left a failure notice: "+mode);
            if(mode=="manual-off")Assert(player.SubtitlesOff,"Prepared captions changed the explicit subtitle-off preference.");
            Console.WriteLine($"UIREVIEW prepared-policy mode={mode} requests={requests} scope=offline-owned-IPC-no-network-no-media");
        }
        finally
        {RestoreSubtitleOperationState(main,saved);status.Text=priorStatus;await ((IAsyncDisposable)fixture).DisposeAsync();}
    }

    private static async Task VerifyPreparedCaptionOriginalLanguage(MainWindow main,string workspace,string mode)
    {
        var fixtureType=typeof(PlayerRootRegression).GetNestedType("SubtitleFixture",BindingFlags.NonPublic)!;
        var create=(Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null,[workspace])!;await create;
        var fixture=create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player=(MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved=SaveSubtitleOperationState(main);var status=Get<TextBlock>(main,"StatusText");var priorStatus=status.Text;
        try
        {
            Set(main,"_player",player);Set(main,"_expectedMediaGeneration",7L);
            Set(main,"_subtitleLoadError",null);Set(main,"_subtitleMenuCts",null);
            fixtureType.GetMethod("ClearNativeSubtitles")!.Invoke(fixture,null);
            var wanted=mode switch{"preferred-orig-base" or "preferred-orig-loaded-base"=>"KO-ORIG","regional-en"=>"en-US","regional-base-en" or "regional-base-en-exact"=>"en","regional-zh"=>"zh-Hans",_=>"ko"};
            typeof(MpvPlayerService).GetField("_subtitleLanguage",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(player,wanted);
            var loaded=mode switch{"loaded-orig"=>"ko-orig","preferred-orig-loaded-base"=>"ko","regional-en"=>"en","regional-base-en" or "regional-base-en-exact"=>"en-US","regional-zh"=>"zh-Hant-orig",_=>null};
            if(loaded is not null)
            {
                var trackType=typeof(PlayerRootRegression).GetNestedType("FixtureSubtitle",BindingFlags.NonPublic)!;
                var tracks=(System.Collections.IList)fixtureType.GetField("_tracks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture)!;
                tracks.Add(Activator.CreateInstance(trackType,[31L,"Existing original fixture",loaded]));
                fixtureType.GetProperty("Sid")!.SetValue(fixture,31L);
            }
            if(mode=="rejected-base-orig")
                ((HashSet<string>)fixtureType.GetProperty("RejectedSubAddLanguages")!.GetValue(fixture)!).Add("ko");
            string[] languages=mode switch
            {
                "rejected-base-orig"=>["en-orig","ko","ko-orig"],
                "case-orig"=>["en-orig","KO-oRiG"],
                "preferred-orig-base" or "preferred-orig-loaded-base"=>["en-orig","ko"],
                "regional-en"=>["en-orig","en-US-orig"],
                "regional-base-en"=>["en-orig"],
                "regional-base-en-exact"=>["en"],
                "regional-zh"=>["zh-Hant-orig","zh-Hans-orig"],
                _=>["en-orig","ko-orig"]
            };
            var captions=languages.Select(language=>new PreparedCaption(language,
                "https://www.youtube.com/api/timedtext?v=fixture0001&lang="+language)).ToArray();
            await (Task)Invoke(main,"LoadPreparedCaptionsAsync",captions,Get<int>(main,"_videoGeneration"))!;
            var commands=(JsonElement[])fixtureType.GetProperty("RecordedCommands")!.GetValue(fixture)!;
            var added=commands.Where(command=>command[0].GetString()=="sub-add").Select(command=>command[4].GetString()!).ToArray();
            string[] expected=mode switch
            {
                "loaded-orig" or "preferred-orig-loaded-base"=>[],"rejected-base-orig"=>["ko","ko-orig"],"case-orig"=>["KO-oRiG"],
                "preferred-orig-base"=>["ko"],"regional-en"=>["en-US-orig"],"regional-base-en"=>["en-orig"],"regional-base-en-exact"=>["en"],"regional-zh"=>["zh-Hans-orig"],_=>["ko-orig"]
            };
            Assert(added.SequenceEqual(expected),$"Prepared original-language policy {mode} requested [{string.Join(',',added)}], expected [{string.Join(',',expected)}].");
            Assert(Get<string?>(main,"_subtitleLoadError") is null,
                "A satisfied original-language preference left false failure advice: "+mode);
            Assert(player.PreferredSubtitleLanguage==wanted && player.HasActiveMedia(7),
                "Prepared original-language matching changed the explicit preference or active media: "+mode);
            var selected=(await player.GetSubtitlesAsync()).Tracks.SingleOrDefault(track=>track.Selected)?.Language;
            var expectedSelected=mode switch{"loaded-orig"=>"ko-orig","preferred-orig-loaded-base"=>"ko",_=>expected[^1]};
            Assert(string.Equals(selected,expectedSelected,StringComparison.OrdinalIgnoreCase),
                $"Prepared original-language policy {mode} selected [{selected??"none"}], expected [{expectedSelected}].");
            Console.WriteLine($"UIREVIEW prepared-original-language mode={mode} requests={added.Length} requested={string.Join(',',added)} selected={selected} no-false-warning=True scope=offline-owned-IPC-no-network-no-media");
        }
        finally
        {RestoreSubtitleOperationState(main,saved);status.Text=priorStatus;await ((IAsyncDisposable)fixture).DisposeAsync();}
    }

    private static async Task VerifyPreparedCaptionManualIntent(MainWindow main,string workspace,bool turnOff)
    {
        var fixtureType=typeof(PlayerRootRegression).GetNestedType("SubtitleFixture",BindingFlags.NonPublic)!;
        var create=(Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null,[workspace])!;await create;
        var fixture=create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player=(MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved=SaveSubtitleOperationState(main);var status=Get<TextBlock>(main,"StatusText");var priorStatus=status.Text;
        Task? loading=null;Task? selecting=null;
        try
        {
            Set(main,"_player",player);Set(main,"_expectedMediaGeneration",7L);Set(main,"_subtitleMenuCts",null);
            fixtureType.GetMethod("ClearNativeSubtitles")!.Invoke(fixture,null);
            fixtureType.GetMethod("BlockSubAdd")!.Invoke(fixture,null);
            var captions=new[]{"ko","ja"}.Select(language=>new PreparedCaption(language,
                "https://www.youtube.com/api/timedtext?v=fixture0001&lang="+language)).ToArray();
            loading=(Task)Invoke(main,"LoadPreparedCaptionsAsync",captions,Get<int>(main,"_videoGeneration"))!;
            await ((TaskCompletionSource)fixtureType.GetProperty("SubAddStarted")!.GetValue(fixture)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var captionCts=Get<CancellationTokenSource>(main,"_captionCts");
            var english=new AvailableSubtitle("en","Manual English","https://www.youtube.com/api/timedtext?v=fixture0001&lang=en",false);
            Func<CancellationToken,Task> select=token=>turnOff?player.SelectSubtitleAsync(null,7,token):player.SelectExternalSubtitleAsync(english,7,token);
            selecting=(Task)Invoke(main,"SelectSubtitleFromMenuAsync",select,turnOff?null:english.DisplayLabel,
                Get<int>(main,"_videoGeneration"),turnOff?null:"en",7L)!;
            Assert(captionCts.IsCancellationRequested && !selecting.IsCompleted,
                "Manual subtitle intent did not cancel the prepared operation while preserving native serialization.");
            fixtureType.GetMethod("ReleaseSubAdd")!.Invoke(fixture,null);
            await Task.WhenAll(loading,selecting).WaitAsync(TimeSpan.FromSeconds(8));
            var requests=(int)fixtureType.GetProperty("SubAdds")!.GetValue(fixture)!;
            Assert(requests==(turnOff?1:2) && Get<string?>(main,"_subtitleLoadError") is null,
                "A canceled prepared operation made a later request or overwrote the current manual result.");
            Assert(turnOff?player.SubtitlesOff:player.PreferredSubtitleLanguage=="en",
                "The latest manual subtitle choice was lost after the older native request completed.");
            Console.WriteLine($"UIREVIEW prepared-manual-interleave off={turnOff} requests={requests} scope=offline-owned-IPC-terminal-reply-and-rollback");
        }
        finally
        {
            fixtureType.GetMethod("ReleaseSubAdd")!.Invoke(fixture,null);
            if(loading is not null)try{await loading.WaitAsync(TimeSpan.FromSeconds(8));}catch{}
            if(selecting is not null)try{await selecting.WaitAsync(TimeSpan.FromSeconds(8));}catch{}
            RestoreSubtitleOperationState(main,saved);status.Text=priorStatus;await ((IAsyncDisposable)fixture).DisposeAsync();
        }
    }

    private static async Task VerifyExistingPrefetchDuringSubtitleIntent(MainWindow main,string workspace,bool turnOff)
    {
        var fixtureType=typeof(PlayerRootRegression).GetNestedType("SubtitleFixture",BindingFlags.NonPublic)!;
        var create=(Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null,[workspace])!;await create;
        var fixture=create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player=(MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved=SaveSubtitleOperationState(main);var settings=Get<AppSettings>(main,"_settings");
        var status=Get<TextBlock>(main,"StatusText");var priorStatus=status.Text;
        var wasAuto=settings.AutoPlayNext;var wasPrivate=settings.Incognito;
        var next=Get<System.Collections.ObjectModel.ObservableCollection<VideoItem>>(main,"_upNext");var priorNext=next.ToArray();
        var root=Path.Combine(workspace,"artifacts","subtitle-prefetch-overlap-"+Guid.NewGuid().ToString("N"));
        var paths=new RuntimePaths(root);var releaseSelection=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? prefetch=null;Task? selecting=null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Python)!);
            foreach(var file in Directory.EnumerateFiles(Path.Combine(workspace,"runtime","python")))
                File.Copy(file,Path.Combine(Path.GetDirectoryName(paths.Python)!,Path.GetFileName(file)));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.YtDlp)!);File.WriteAllText(paths.YtDlp,"never execute");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Deno)!);File.WriteAllText(paths.Deno,"never execute");
            Directory.CreateDirectory(Path.Combine(root,"worker"));
            File.WriteAllText(Path.Combine(root,"worker","selected_ytdlp.py"),"""
                import json,pathlib,sys,time
                sys.stdin.readline()
                pathlib.Path('started').write_text('offline')
                deadline=time.monotonic()+10
                while not pathlib.Path('release').exists():
                    if time.monotonic()>deadline:sys.exit(2)
                    time.sleep(.01)
                print(json.dumps({'url':'https://media.fixture.invalid/next','is_live':False}))
                """,new UTF8Encoding(false));
            var session=new SessionService(paths);
            typeof(SessionService).GetProperty("SelectedChannel")!.SetValue(session,new YouTubeChannel{Id="fixture",AuthUser=2,DataSyncId="123456||fixture",PageId="123456"});
            Set(main,"_youtube",new YouTubeService(paths,session));Set(main,"_player",player);Set(main,"_expectedMediaGeneration",7L);
            Set(main,"_snapshot",new PlaybackSnapshot{MediaGeneration=7,Loaded=true,Duration=120,Position=100});
            Set(main,"_subtitleMenuCts",null);Set(main,"_prefetchAttempt",null);Set(main,"_preparedItem",null);
            await player.SelectSubtitleAsync(1,7);
            var candidate=new VideoItem{Id="fixture0002",Url="https://www.youtube.com/watch?v=fixture0002",Title="Offline next"};
            next.Clear();next.Add(candidate);settings.AutoPlayNext=true;settings.Incognito=false;
            prefetch=(Task)Invoke(main,"EnsurePrefetchAsync")!;
            var deadline=Stopwatch.StartNew();while(!File.Exists(Path.Combine(root,"started")) && deadline.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
            Assert(File.Exists(Path.Combine(root,"started")),"The offline prefetch did not reach its controlled resolver.");
            Func<CancellationToken,Task> select=async token=>
            {await releaseSelection.Task.WaitAsync(token);await player.SelectSubtitleAsync(turnOff?null:1,7,token);};
            selecting=(Task)Invoke(main,"SelectSubtitleFromMenuAsync",select,turnOff?null:"Korean",Get<int>(main,"_videoGeneration"),turnOff?null:"ko",7L)!;
            File.WriteAllText(Path.Combine(root,"release"),"release");await prefetch.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(!selecting.IsCompleted && ReferenceEquals(Get<VideoItem?>(main,"_preparedItem"),candidate),
                "An unchanged-language pending intent discarded the existing prefetch and left its attempted candidate stuck.");
            releaseSelection.TrySetResult();await selecting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ReferenceEquals(Get<VideoItem?>(main,"_preparedItem"),candidate) && player.SubtitlesOff==turnOff,
                "Completing the latest unchanged-language/off selection lost prepared media or its off state.");
            Console.WriteLine($"UIREVIEW prefetch-overlap off={turnOff} scope=offline-worker-and-owned-IPC-no-network-no-media");
        }
        finally
        {
            Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"release"),"release");releaseSelection.TrySetResult();
            if(prefetch is not null)try{await prefetch.WaitAsync(TimeSpan.FromSeconds(8));}catch{}
            if(selecting is not null)try{await selecting.WaitAsync(TimeSpan.FromSeconds(8));}catch{}
            settings.AutoPlayNext=wasAuto;settings.Incognito=wasPrivate;next.Clear();foreach(var item in priorNext)next.Add(item);
            RestoreSubtitleOperationState(main,saved);status.Text=priorStatus;await ((IAsyncDisposable)fixture).DisposeAsync();
            await PlaybackStateRegression.DeleteReleasedFixtureAsync(root,"subtitle-prefetch-overlap-");
        }
    }

    private static async Task VerifyManualSubtitleFailure(MainWindow main, string workspace, string state, bool canceled)
    {
        var fixtureType = typeof(PlayerRootRegression).GetNestedType("SubtitleFixture", BindingFlags.NonPublic)!;
        var create = (Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null, [workspace])!;
        await create;
        var fixture = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player = (MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var sessionField = typeof(MpvPlayerService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nativeSession = sessionField.GetValue(player)!;
        var retained = typeof(MpvPlayerService).GetField("_retainedCookieSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
        var previousPlayer = Get<MpvPlayerService>(main, "_player");
        var previousGeneration = Get<int>(main, "_videoGeneration");
        var previousMenuCts = GetNullable<CancellationTokenSource>(main, "_subtitleMenuCts");
        var previousHold = GetNullable<CancellationTokenSource>(main, "_subtitleControlsHoldCts");
        var previousCatalog = Get<IReadOnlyList<AvailableSubtitle>>(main, "_subtitleCatalog");
        var previousKey = Get<string?>(main, "_subtitleCatalogKey");
        var previousExpiry = Get<DateTimeOffset>(main, "_subtitleCatalogExpires");
        var previousNotice = Get<string?>(main, "_subtitleLoadError");
        var status = Get<TextBlock>(main, "StatusText"); var previousStatus = status.Text;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? selecting = null;
        try
        {
            Set(main, "_player", player);
            AvailableSubtitle[] catalog = [new("ko", "Fixture", "https://www.youtube.com/api/timedtext?v=fixture0001", false)];
            Set(main, "_subtitleCatalog", catalog); Set(main, "_subtitleCatalogKey", "manual-fixture");
            Func<CancellationToken, Task> select = _ => completion.Task;
            selecting = (Task)Invoke(main, "SelectSubtitleFromMenuAsync", select, "Fixture", previousGeneration, player.PreferredSubtitleLanguage, 7L)!;
            Assert(!selecting.IsCompleted && player.HasActiveMedia(7), "Manual subtitle fixture did not hold its exact connected media operation.");
            var selectionCts = Get<CancellationTokenSource>(main, "_subtitleMenuCts");
            if (state == "detached")
            {
                // Model the post-cleanup ownership state with a real live owned PID.
                // This tests UI advice, not native termination failure or cookie cleanup.
                retained.GetType().GetMethod("Add")!.Invoke(retained, [nativeSession]);
                sessionField.SetValue(player, null);
                typeof(MpvPlayerService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(player, new PlaybackSnapshot { MediaGeneration = 7, Loaded = false, Ended = true });
                Assert(player.OwnedProcessIds.Count > 0 && !player.HasActiveMedia(7),
                    "Detached subtitle fixture did not retain ownership after losing its active connection.");
            }
            if (state == "stale-operation")
            {
                Func<CancellationToken, Task> successor = _ => Task.CompletedTask;
                await (Task)Invoke(main, "SelectSubtitleFromMenuAsync", successor, null, previousGeneration, null, 7L)!;
            }
            if (state == "stale-ui") Set(main, "_videoGeneration", previousGeneration + 1);
            if (state == "stale-media") fixtureType.GetMethod("SetMedia")!.Invoke(fixture, [99L, true, null]);
            var stale = state.StartsWith("stale-", StringComparison.Ordinal);
            status.Text = "manual-subtitle-newer-state";
            Set(main, "_subtitleLoadError", stale ? "manual-subtitle-newer-notice" : null);
            if (canceled)
            {
                selectionCts.Cancel();
                completion.TrySetCanceled(selectionCts.Token);
            }
            else completion.TrySetException(new InvalidDataException("synthetic manual subtitle failure"));
            await selecting.WaitAsync(TimeSpan.FromSeconds(5));
            if (stale)
                Assert(status.Text == "manual-subtitle-newer-state" && Get<string?>(main, "_subtitleLoadError") == "manual-subtitle-newer-notice" &&
                       ReferenceEquals(Get<IReadOnlyList<AvailableSubtitle>>(main, "_subtitleCatalog"), catalog),
                    "An obsolete manual subtitle operation overwrote newer status, notice, or catalog: " + state);
            else if (state == "detached")
                Assert(player.OwnedProcessIds.Count > 0 && status.Text.Contains("영상을 다시 재생") &&
                       status.Text.Contains("재생기에 연결할 수 없습니다") && !status.Text.Contains("정리했습니다") &&
                       !status.Text.Contains("자막 목록을 다시 열어") && Get<string?>(main, "_subtitleLoadError") == status.Text,
                    "Manual subtitle recovery treated a detached retained PID as an active player: " + (canceled ? "cancellation" : "failure"));
            else if (canceled)
                Assert(status.Text == "manual-subtitle-newer-state" && Get<string?>(main, "_subtitleLoadError") is null,
                    "Canceling a subtitle operation on its active media incorrectly requested playback recovery.");
            else
                Assert(player.HasActiveMedia(7) && status.Text.Contains("자막 목록을 다시 열어") &&
                       !status.Text.Contains("영상을 다시 재생") && Get<IReadOnlyList<AvailableSubtitle>>(main, "_subtitleCatalog").Count == 0,
                    "Manual subtitle failure on the exact active media did not preserve its subtitle-only retry.");
            Console.WriteLine($"UIREVIEW manual-subtitle-advice state={state} canceled={canceled} scope=actual-async-UI-entrypoint-owned-offline-IPC-synthetic-detached-state-no-media-no-network");
        }
        finally
        {
            completion.TrySetCanceled();
            if (selecting is not null) try { await selecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            sessionField.SetValue(player, nativeSession);
            retained.GetType().GetMethod("Remove")!.Invoke(retained, [nativeSession]);
            Set(main, "_player", previousPlayer); Set(main, "_videoGeneration", previousGeneration);
            Set(main, "_subtitleMenuCts", previousMenuCts); Set(main, "_subtitleControlsHoldCts", previousHold);
            Set(main, "_subtitleCatalog", previousCatalog); Set(main, "_subtitleCatalogKey", previousKey); Set(main, "_subtitleCatalogExpires", previousExpiry);
            Set(main, "_subtitleLoadError", previousNotice); status.Text = previousStatus;
            await ((IAsyncDisposable)fixture).DisposeAsync();
        }
    }

    private static async Task VerifySubtitleControlsHold(MainWindow main, string workspace, bool catalogFailure,bool keepOpen=false)
    {
        var fixtureType = typeof(PlayerRootRegression).GetNestedType("SubtitleFixture", BindingFlags.NonPublic)!;
        var create = (Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null, [workspace])!;
        await create;
        var fixture = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var previousPlayer = Get<MpvPlayerService>(main, "_player");
        var previousYoutube = Get<YouTubeService>(main, "_youtube");
        var previousMenu = GetNullable<ContextMenu>(main, "_subtitleMenu");
        var previousCatalog = Get<IReadOnlyList<AvailableSubtitle>>(main, "_subtitleCatalog");
        var previousKey = Get<string?>(main, "_subtitleCatalogKey");
        var previousExpiry = Get<DateTimeOffset>(main, "_subtitleCatalogExpires");
        var catalogRoot = Path.GetFullPath(Path.Combine(workspace, "artifacts", "subtitle-controls-fixture-" + Guid.NewGuid().ToString("N")));
        var paths = new RuntimePaths(catalogRoot);
        var catalogRelease = Path.Combine(catalogRoot, "catalog-release");
        var status = Get<TextBlock>(main, "StatusText"); var previousStatus = status.Text;
        var heldSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? opening = null; Task? selecting = null; ContextMenu? menu = null;
        var hold = ControlsField<Func<bool>>(Get<PlaybackControlsAutoHide>(main, "_controlsAutoHide"), "_interactionBusy");
        var player = (MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var previousLeft=main.Left;var previousTop=main.Top;
        try
        {
            Set(main, "_player", player);
            if(keepOpen){main.Left=previousLeft+137;main.Top=previousTop+83;await LayoutIdle(main);}
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Python)!);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(workspace, "runtime", "python")))
                File.Copy(file, Path.Combine(Path.GetDirectoryName(paths.Python)!, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(catalogRoot, "worker"));
            File.WriteAllText(Path.Combine(catalogRoot, "worker", "selected_ytdlp.py"), """
                import json, pathlib, sys, time
                request=json.loads(sys.stdin.readline())
                assert request['accountContext'] is None
                assert '--cookies' not in sys.argv
                pathlib.Path('catalog-started').write_text('offline fixture')
                deadline=time.monotonic()+15
                while not pathlib.Path('catalog-release').exists():
                    if time.monotonic()>deadline: sys.exit(2)
                    time.sleep(.01)
                if pathlib.Path('catalog-fail').exists(): sys.exit(1)
                video_id=sys.argv[-1].split('v=')[1].split('&')[0]
                print(json.dumps({'id':video_id,'subtitles':{},'automatic_captions':{}}))
                """, new UTF8Encoding(false));
            if (catalogFailure) File.WriteAllText(Path.Combine(catalogRoot, "catalog-fail"), "fail");
            Set(main, "_youtube", new YouTubeService(paths, new SessionService(paths)));
            Set(main, "_subtitleCatalogKey", null);
            fixtureType.GetMethod("BlockTrackRead")!.Invoke(fixture, null);
            opening = (Task)Invoke(main, "OpenSubtitleMenuAsync", Get<Button>(main, "SubtitleButton"))!;
            await ((TaskCompletionSource)fixtureType.GetProperty("TrackReadStarted")!.GetValue(fixture)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(hold() && GetNullable<ContextMenu>(main, "_subtitleMenu")?.IsOpen == false,
                "Controls were not retained during the initial subtitle-track read before its menu opened.");
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null);
            var catalogWait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(catalogRoot, "catalog-started")) && catalogWait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Assert(File.Exists(Path.Combine(catalogRoot, "catalog-started")) && !opening.IsCompleted,
                "The offline subtitle catalog fixture did not hold the actual catalog request.");
            menu = Get<ContextMenu>(main, "_subtitleMenu");
            Assert(menu.IsOpen && hold(), "The opened subtitle menu stopped retaining its controls.");
            if(!keepOpen)
            {
                await CloseFixtureMenuAsync(main, menu);
                Assert(!hold() && !opening.IsCompleted,
                    "A dismissed subtitle menu retained controls during its pending catalog request.");
            }
            File.WriteAllText(catalogRelease, "release");
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            if(keepOpen)
            {
                await WaitForSubtitlePopupReady(main,main,menu);AssertSubtitlePopupAdjacent(menu,Get<Button>(main,"SubtitleButton"));
                Assert(menu.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()?.Contains("전체 언어 조회 실패")==true),
                    "The visible catalog failure did not show its actual failure footer.");
                await CloseFixtureMenuAsync(main,menu);
            }
            Assert(!menu.IsOpen && !hold(), "Late subtitle catalog completion reopened a dismissed menu or retained controls.");
            Func<CancellationToken, Task> select = token => heldSelection.Task.WaitAsync(token);
            selecting = (Task)Invoke(main, "SelectSubtitleFromMenuAsync", select, null, Get<int>(main, "_videoGeneration"), null, player.Snapshot.MediaGeneration)!;
            Assert(!selecting.IsCompleted && hold(), "Explicit subtitle selection stopped retaining its controls.");
            heldSelection.SetResult(); await selecting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!hold(), "Completed subtitle selection retained its controls hold.");
            Console.WriteLine($"UIREVIEW subtitle-controls-hold initial-read=True selection=True dismissed-catalog-released=True catalog-failure={catalogFailure} visible-failure-anchor={keepOpen} scope=actual-async-UI-entrypoints-offline-IPC-and-worker");
        }
        finally
        {
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null); heldSelection.TrySetResult();
            File.WriteAllText(catalogRelease, "release");
            if (opening is not null) try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (selecting is not null) try { await selecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            menu ??= GetNullable<ContextMenu>(main, "_subtitleMenu");
            if (menu is not null && !ReferenceEquals(menu, previousMenu)) await CloseFixtureMenuAsync(main, menu);
            Invoke(main,"CloseSubtitleMenu");main.Left=previousLeft;main.Top=previousTop;
            Set(main, "_player", previousPlayer); Set(main, "_youtube", previousYoutube); Set(main, "_subtitleMenu", previousMenu);
            Set(main, "_subtitleCatalog", previousCatalog);
            Set(main, "_subtitleCatalogKey", previousKey); Set(main, "_subtitleCatalogExpires", previousExpiry); status.Text = previousStatus;
            await ((IAsyncDisposable)fixture).DisposeAsync();
            Assert(Path.GetFileName(catalogRoot).StartsWith("subtitle-controls-fixture-", StringComparison.Ordinal) &&
                   Path.GetFileName(Path.GetDirectoryName(catalogRoot)) == "artifacts", "Subtitle controls cleanup escaped its fixture directory.");
            await PlaybackStateRegression.DeleteReleasedFixtureAsync(catalogRoot, "subtitle-controls-fixture-");
        }
    }

    private static async Task VerifySubtitleSelectionReopen(MainWindow main, string workspace, string outcome)
    {
        var fixtureType = typeof(PlayerRootRegression).GetNestedType("SubtitleFixture", BindingFlags.NonPublic)!;
        var create = (Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null, [workspace])!;
        await create;
        var fixture = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player = (MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved = SaveSubtitleOperationState(main);
        var settings = Get<AppSettings>(main, "_settings"); var previousAutoPlay = settings.AutoPlayNext;
        var next = Get<System.Collections.ObjectModel.ObservableCollection<VideoItem>>(main, "_upNext");
        var previousNext = next.ToArray();
        var status = Get<TextBlock>(main, "StatusText"); var previousStatus = status.Text;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noRuntimeRoot = Path.Combine(workspace, "artifacts", "absent-subtitle-prefetch-" + Guid.NewGuid().ToString("N"));
        var selectionToken = CancellationToken.None;
        Task? selecting = null; Task? opening = null; ContextMenu? menu = null;
        var sessionField = typeof(MpvPlayerService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nativeSession = sessionField.GetValue(player)!;
        var retained = typeof(MpvPlayerService).GetField("_retainedCookieSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
        var hold = ControlsField<Func<bool>>(Get<PlaybackControlsAutoHide>(main, "_controlsAutoHide"), "_interactionBusy");
        try
        {
            Set(main, "_player", player); Set(main, "_expectedMediaGeneration", 7L);
            // Native defaults select the Korean track without establishing an
            // explicit preference. Confirm a real explicit baseline before
            // testing whether a failed English choice preserves that preference.
            await player.SelectSubtitleAsync(1, 7);
            Assert(player.PreferredSubtitleLanguage == "ko", "The reopened-selection fixture did not establish its explicit Korean preference.");
            Set(main, "_snapshot", new PlaybackSnapshot { MediaGeneration = 7, Loaded = true, Paused = true, Duration = 120, Position = 100 });
            // An absent fixture runtime makes a resumed prefetch fail locally before
            // a process/network request. Its attempt identity proves immediate retry.
            var noRuntimePaths = new RuntimePaths(noRuntimeRoot);
            Set(main, "_youtube", new YouTubeService(noRuntimePaths, new SessionService(noRuntimePaths)));
            var candidate = new VideoItem { Id = "fixture0002", Url = "https://www.youtube.com/watch?v=fixture0002", Title = "Offline next" };
            next.Clear(); next.Add(candidate); settings.AutoPlayNext = true;
            Set(main, "_prefetchAttempt", candidate);
            var caption = new AvailableSubtitle("en", "Fixture English", "https://www.youtube.com/api/timedtext?v=fixture0001&lang=en", false);
            Set(main, "_subtitleCatalog", new[] { caption });
            var account = typeof(MainWindow).GetProperty("RatingAccountStamp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            Set(main, "_subtitleCatalogKey", Get<VideoItem>(main, "_current").Id + ":" + account);
            Set(main, "_subtitleCatalogExpires", DateTimeOffset.UtcNow.AddMinutes(5));
            Func<CancellationToken, Task> select = async token =>
            {
                selectionToken = token; entered.TrySetResult(); await release.Task;
                if (outcome == "success") await player.SelectExternalSubtitleAsync(caption, 7, token);
                else if (outcome.StartsWith("canceled-", StringComparison.Ordinal)) throw new OperationCanceledException(token);
                else throw new InvalidDataException("synthetic reopened subtitle selection failure");
            };
            selecting = (Task)Invoke(main, "SelectSubtitleFromMenuAsync", select, caption.DisplayLabel,
                Get<int>(main, "_videoGeneration"), caption.Language, 7L)!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var selectionCts = Get<CancellationTokenSource>(main, "_subtitleMenuCts");
            Assert(Get<VideoItem?>(main, "_prefetchAttempt") is null,
                "Changing the subtitle language did not invalidate the old prefetch before selection.");
            await (Task)Invoke(main,"EnsurePrefetchAsync")!;
            Assert(Get<VideoItem?>(main,"_prefetchAttempt") is null,
                "A playback snapshot restarted old-language prefetch while explicit selection was pending.");
            fixtureType.GetMethod("BlockTrackRead")!.Invoke(fixture, null);
            opening = (Task)Invoke(main, "OpenSubtitleMenuAsync", Get<Button>(main, "SubtitleButton"))!;
            await ((TaskCompletionSource)fixtureType.GetProperty("TrackReadStarted")!.GetValue(fixture)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!selectionToken.IsCancellationRequested && !selecting.IsCompleted && hold(),
                "Reopening the subtitle popup canceled or released ownership of the pending explicit selection.");
            if (outcome == "failure-read-pending")
            {
                release.TrySetResult(); await selecting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(hold() && !opening.IsCompleted,
                    "Completing an explicit selection released controls still owned by the popup's initial track read.");
            }
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null);
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            menu = Get<ContextMenu>(main, "_subtitleMenu");
            await CloseFixtureMenuAsync(main, menu);
            if (outcome != "failure-read-pending")
                Assert(hold() && !selecting.IsCompleted,
                    "Dismissing a reopened subtitle popup released controls still owned by its pending explicit selection.");
            if (outcome == "canceled-detached")
            {
                retained.GetType().GetMethod("Add")!.Invoke(retained, [nativeSession]); sessionField.SetValue(player, null);
                typeof(MpvPlayerService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(player, new PlaybackSnapshot { MediaGeneration = 7, Loaded = false, Ended = true });
            }
            if (outcome.StartsWith("canceled-", StringComparison.Ordinal))
            { status.Text = "reopened-cancel-newer-status"; selectionCts.Cancel(); }
            release.TrySetResult(); await selecting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!hold(), "A completed subtitle operation left its controls retained after the popup closed.");
            if (outcome == "success")
                Assert(player.PreferredSubtitleLanguage == "en" && (await player.GetSubtitlesAsync()).Tracks.Any(track => track.Selected && track.Language == "en") &&
                       status.Text.Contains(caption.DisplayLabel) && !status.Text.Contains("불러오는 중") && Get<string?>(main, "_subtitleLoadError") is null,
                    "A successful external selection after reopening lost its native choice, preference or completed UI status.");
            else if (outcome.StartsWith("failure", StringComparison.Ordinal))
            {
                var notice = Get<string?>(main, "_subtitleLoadError");
                var catalogCount = Get<IReadOnlyList<AvailableSubtitle>>(main, "_subtitleCatalog").Count;
                Assert(notice?.Contains("자막 목록을 다시 열어") == true,
                    "A failed selection after reopening suppressed the retry notice. actual=" + (notice ?? "<null>"));
                Assert(catalogCount == 0, "A failed selection after reopening retained its failed catalog. actualCount=" + catalogCount);
                Assert(player.PreferredSubtitleLanguage == "ko",
                    "A failed selection after reopening changed its explicit Korean preference. actual=" + (player.PreferredSubtitleLanguage ?? "<null>"));
            }
            else if (outcome == "canceled-detached")
                Assert(Get<string?>(main, "_subtitleLoadError")?.Contains("재생기에 연결할 수 없습니다") == true,
                    "Cancellation after reopening suppressed recovery advice for the detached owned player.");
            else Assert(status.Text == "reopened-cancel-newer-status" && Get<string?>(main, "_subtitleLoadError") is null,
                "Cancellation after reopening overwrote a newer status while its exact media remained active.");
            if (!outcome.StartsWith("canceled-", StringComparison.Ordinal))
                Assert(ReferenceEquals(Get<VideoItem?>(main, "_prefetchAttempt"), candidate),
                    "Finishing the current language-changing selection after reopening did not immediately resume invalidated prefetch.");
            Console.WriteLine($"UIREVIEW subtitle-reopen outcome={outcome} popup-read-and-selection-independent=True scope=actual-async-UI-and-offline-IPC-no-network-no-real-media");
        }
        finally
        {
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null); release.TrySetResult();
            if (opening is not null) try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (selecting is not null) try { await selecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            menu ??= GetNullable<ContextMenu>(main, "_subtitleMenu");
            if (menu is not null && !ReferenceEquals(menu, saved["_subtitleMenu"])) await CloseFixtureMenuAsync(main, menu);
            sessionField.SetValue(player, nativeSession); retained.GetType().GetMethod("Remove")!.Invoke(retained, [nativeSession]);
            settings.AutoPlayNext = previousAutoPlay; next.Clear(); foreach (var item in previousNext) next.Add(item);
            RestoreSubtitleOperationState(main, saved); status.Text = previousStatus;
            await ((IAsyncDisposable)fixture).DisposeAsync();
            await PlaybackStateRegression.DeleteReleasedFixtureAsync(noRuntimeRoot, "absent-subtitle-prefetch-");
        }
    }

    private static async Task VerifySubtitleOperationTransition(MainWindow main, string workspace, string transition)
    {
        var fixtureType = typeof(PlayerRootRegression).GetNestedType("SubtitleFixture", BindingFlags.NonPublic)!;
        var create = (Task)fixtureType.GetMethod("CreateAsync")!.Invoke(null, [workspace])!; await create;
        var fixture = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var player = (MpvPlayerService)fixtureType.GetProperty("Player")!.GetValue(fixture)!;
        var saved = SaveSubtitleOperationState(main);
        var generation = Get<int>(main, "_videoGeneration");
        var status = Get<TextBlock>(main, "StatusText"); var previousStatus = status.Text;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken selectionToken = default; Task? selecting = null; Task? opening = null; var accountBusy = false;
        var hold = ControlsField<Func<bool>>(Get<PlaybackControlsAutoHide>(main, "_controlsAutoHide"), "_interactionBusy");
        try
        {
            Set(main, "_player", player); Set(main, "_expectedMediaGeneration", 7L);
            Func<CancellationToken, Task> select = async token =>
            { selectionToken = token; entered.TrySetResult(); await release.Task; throw new InvalidDataException("obsolete subtitle completion"); };
            selecting = (Task)Invoke(main, "SelectSubtitleFromMenuAsync", select, "Fixture", generation, player.PreferredSubtitleLanguage, 7L)!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixtureType.GetMethod("BlockTrackRead")!.Invoke(fixture, null);
            opening = (Task)Invoke(main, "OpenSubtitleMenuAsync", Get<Button>(main, "SubtitleButton"))!;
            await ((TaskCompletionSource)fixtureType.GetProperty("TrackReadStarted")!.GetValue(fixture)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The fallback also runs this behavioral test against the pre-split code.
            var readField = typeof(MainWindow).GetField("_subtitleMenuReadCts", BindingFlags.Instance | BindingFlags.NonPublic) ??
                typeof(MainWindow).GetField("_subtitleMenuCts", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var readToken = ((CancellationTokenSource)readField.GetValue(main)!).Token;
            if (transition == "account") { Invoke(main, "SetAccountBusy", true); accountBusy = true; }
            else { Set(main, "_videoGeneration", generation + 1); Invoke(main, "ResetPlaybackExtras", true, true); }
            Assert(selectionToken.IsCancellationRequested && readToken.IsCancellationRequested && !hold(),
                "The " + transition + " transition did not cancel both subtitle owners and release their controls.");
            status.Text = "subtitle-transition-newer-state"; Set(main, "_subtitleLoadError", "subtitle-transition-newer-notice");
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null); release.TrySetResult();
            await Task.WhenAll(selecting, opening).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(status.Text == "subtitle-transition-newer-state" && Get<string?>(main, "_subtitleLoadError") == "subtitle-transition-newer-notice" &&
                   GetNullable<ContextMenu>(main, "_subtitleMenu")?.IsOpen != true && !hold(),
                "An obsolete subtitle read/selection completion reopened its menu or overwrote the " + transition + " transition.");
            Console.WriteLine($"UIREVIEW subtitle-transition transition={transition} both-canceled=True stale-completion-ignored=True scope=actual-UI-transition-offline-IPC-no-account-or-network");
        }
        finally
        {
            fixtureType.GetMethod("ReleaseTrackRead")!.Invoke(fixture, null); release.TrySetResult();
            if (opening is not null) try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (selecting is not null) try { await selecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (accountBusy) Invoke(main, "SetAccountBusy", false);
            Set(main, "_videoGeneration", generation); RestoreSubtitleOperationState(main, saved); status.Text = previousStatus;
            await ((IAsyncDisposable)fixture).DisposeAsync();
        }
    }

    private static Dictionary<string, object?> SaveSubtitleOperationState(MainWindow main)
    {
        var fields = new[] { "_player", "_youtube", "_snapshot", "_expectedMediaGeneration", "_subtitleMenu", "_subtitleMenuCts",
            "_subtitleControlsHoldCts", "_subtitleMenuReadCts", "_subtitleMenuControlsHoldCts", "_subtitleCatalog", "_subtitleCatalogKey",
            "_subtitleCatalogExpires", "_subtitleLoadError", "_subtitleLoadErrorUiText", "_subtitleToolTipText", "_prefetchAttempt", "_prefetchCts",
            "_preparedItem", "_preparedResolutions", "_preparedCaptions", "_prefetchUiText", "_captionCts", "_subtitleSelectionRevision", "_subtitlePreferenceRevision" };
        return fields.Select(name => (Name: name, Field: typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)))
            .Where(item => item.Field is not null).ToDictionary(item => item.Name, item => item.Field!.GetValue(main));
    }

    private static void RestoreSubtitleOperationState(MainWindow main, Dictionary<string, object?> saved)
    { foreach (var (name, value) in saved) Set(main, name, value); }

    private static async Task VerifyPlaybackFailureContext(MainWindow main)
    {
        var player = Get<MpvPlayerService>(main, "_player");
        var snapshotField = typeof(MpvPlayerService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var savedSnapshot = snapshotField.GetValue(player);
        var savedCurrent = Get<VideoItem>(main, "_current"); var savedExpected = Get<long>(main, "_expectedMediaGeneration");
        var savedAccountDepth = Get<int>(main, "_accountBusyDepth"); var savedClosing = Get<bool>(main, "_closing");
        var status = Get<TextBlock>(main, "StatusText"); var savedStatus = status.Text;
        var savedStatusFactory = Get<Func<string>?>(main, "_statusTextFactory");
        var first = new VideoItem { Id = "fixture0001", Url = "https://www.youtube.com/watch?v=fixture0001", Title = "First fixture" };
        var second = new VideoItem { Id = "fixture0002", Url = "https://www.youtube.com/watch?v=fixture0002", Title = "Second fixture" };
        try
        {
            Set(main, "_current", first); Set(main, "_expectedMediaGeneration", 7L);
            snapshotField.SetValue(player, new PlaybackSnapshot { MediaGeneration = 7, VideoUrl = first.Url });
            var failure = new PlaybackFailure(7, first.Url, "current playback fixture failure");
            Invoke(main, "OnPlaybackFailure", failure);
            Assert(status.Text == failure.Message, "The current playback failure did not reach its status UI.");
            status.Text = "newer playback status";
            var queuedVideoFailure = main.Dispatcher.InvokeAsync(() => Invoke(main, "OnPlaybackFailure", failure), DispatcherPriority.Background);
            Set(main, "_current", second); Set(main, "_expectedMediaGeneration", 8L);
            snapshotField.SetValue(player, new PlaybackSnapshot { MediaGeneration = 8, VideoUrl = second.Url });
            await queuedVideoFailure;
            Assert(status.Text == "newer playback status", "A delayed old-video failure overwrote the new video's status.");
            Set(main, "_current", first); Set(main, "_expectedMediaGeneration", 7L);
            Invoke(main, "OnPlaybackFailure", failure);
            Assert(status.Text == "newer playback status", "A delayed failure ignored the player's newer service generation.");
            snapshotField.SetValue(player, new PlaybackSnapshot { MediaGeneration = 7, VideoUrl = first.Url });
            Invoke(main, "OnPlaybackFailure", failure with { VideoUrl = second.Url });
            Assert(status.Text == "newer playback status", "A failure for a different URL overwrote the current video's status.");
            var queuedAccountFailure = main.Dispatcher.InvokeAsync(() => Invoke(main, "OnPlaybackFailure", failure), DispatcherPriority.Background);
            Set(main, "_accountBusyDepth", savedAccountDepth + 1);
            await queuedAccountFailure;
            Assert(status.Text == "newer playback status", "A delayed failure overwrote status while an account transition blocked playback work.");
            Set(main, "_accountBusyDepth", savedAccountDepth); Set(main, "_closing", true);
            Invoke(main, "OnPlaybackFailure", failure);
            Assert(status.Text == "newer playback status", "A delayed playback failure overwrote shutdown status.");
            Console.WriteLine("UIREVIEW contextual-playback-failure current-shown=True old-video-url-service-generation-account-shutdown-ignored=True scope=real-dispatcher-callback-offline-state-fixture");
        }
        finally
        {
            snapshotField.SetValue(player, savedSnapshot); Set(main, "_current", savedCurrent); Set(main, "_expectedMediaGeneration", savedExpected);
            Set(main, "_accountBusyDepth", savedAccountDepth); Set(main, "_closing", savedClosing);
            Set(main, "_statusTextFactory", savedStatusFactory); status.Text = savedStatus;
        }
    }

    private static void AgeControls(PlaybackControlsAutoHide hide, double seconds) =>
        typeof(PlaybackControlsAutoHide).GetField("_lastInteractionAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(hide, ControlsField<Stopwatch>(hide, "_clock").Elapsed.TotalSeconds - seconds);

    private static string AutoHideDiagnostic(MainWindow main, Window owner, FrameworkElement area,
        Border tray, PlaybackControlsAutoHide hide)
    {
        var pointer = Mouse.GetPosition(area);
        var inTray = area.TranslatePoint(pointer, tray);
        var pointerInsideArea = pointer.X >= 0 && pointer.X <= area.ActualWidth &&
            pointer.Y >= 0 && pointer.Y <= area.ActualHeight;
        var wakeDistance = (double)typeof(PlaybackControlsAutoHide)
            .GetField("WakeDistance", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var wakeThresholdY = Math.Max(0, area.ActualHeight - wakeDistance);
        return JsonSerializer.Serialize(new
        {
            age = ControlsField<Stopwatch>(hide, "_clock").Elapsed.TotalSeconds - ControlsField<double>(hide, "_lastInteractionAt"),
            enabled = hide.Enabled, hidden = ControlsField<bool>(hide, "_hidden"),
            timerEnabled = ControlsField<DispatcherTimer>(hide, "_timer").IsEnabled,
            ownerActive = owner.IsActive, ownerKeyboardFocusWithin = owner.IsKeyboardFocusWithin,
            focused = Keyboard.FocusedElement?.GetType().Name,
            focusedName = (Keyboard.FocusedElement as FrameworkElement)?.Name,
            trayVisible = tray.IsVisible, trayHeight = tray.ActualHeight,
            areaWidth = area.ActualWidth, areaHeight = area.ActualHeight,
            pointerInsideArea, wakeThresholdY,
            pointerInWakeBand = pointerInsideArea && pointer.Y >= wakeThresholdY,
            areaMouseOver = area.IsMouseOver, pointer = pointer.ToString(),
            lastPointer = ControlsField<Point?>(hide, "_lastPointer")?.ToString(),
            pointerInTray = inTray.X >= 0 && inTray.X <= tray.ActualWidth && inTray.Y >= 0 && inTray.Y <= tray.ActualHeight,
            activeSourceMatches = Mouse.PrimaryDevice.ActiveSource == PresentationSource.FromVisual(owner),
            capture = Mouse.Captured?.GetType().Name,
            left = Mouse.LeftButton.ToString(), right = Mouse.RightButton.ToString(), middle = Mouse.MiddleButton.ToString(),
            x1 = Mouse.XButton1.ToString(), x2 = Mouse.XButton2.ToString(),
            pressedKeys = ControlsField<HashSet<Key>>(hide, "_pressedKeys").Select(key => key.ToString()).ToArray(),
            suppliedInteractionBusy = ControlsField<Func<bool>?>(hide, "_interactionBusy")?.Invoke(),
            popoutSeekDragging = Get<bool>(main, "_popoutSeekDragging"),
            subtitleMenuOpen = GetNullable<ContextMenu>(main, "_subtitleMenu")?.IsOpen,
            subtitleControlsHold = GetNullable<CancellationTokenSource>(main, "_subtitleControlsHoldCts") is { IsCancellationRequested: false },
            subtitleMenuControlsHold = GetNullable<CancellationTokenSource>(main, "_subtitleMenuControlsHoldCts") is { IsCancellationRequested: false }
        });
    }

    private static void TickControls(PlaybackControlsAutoHide hide) =>
        typeof(PlaybackControlsAutoHide).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hide, [null, EventArgs.Empty]);

    private static T ControlsField<T>(PlaybackControlsAutoHide hide, string field) =>
        (T)typeof(PlaybackControlsAutoHide).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hide)!;

    private static async Task LayoutIdle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task<int> CountHostResizes(MpvHost host, Func<Task> transition)
    {
        var changes = 0;
        SizeChangedEventHandler count = (_, _) => changes++;
        host.SizeChanged += count;
        try
        {
            await transition();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            return changes;
        }
        finally { host.SizeChanged -= count; }
    }

    private static async Task VerifySubtitlePopupAnchor(MainWindow main, Window owner, Button anchor,
        string artifacts, string prefix)
    {
        var originalMenu = GetNullable<ContextMenu>(main, "_subtitleMenu");
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            MaxHeight = 360, MaxWidth = 520, Style = (Style)main.FindResource("SubtitleMenuStyle") };
        var show = typeof(MainWindow).GetMethod("ShowSubtitleMenuAtAnchor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var generation = Get<int>(main, "_videoGeneration");
        var originalLeft=owner.Left;
        var selection = new SubtitleSelection(10, [new SubtitleTrack(1, "한국어 · 불러온 자막", "ko", false)], true);
        try
        {
            Set(main, "_subtitleMenu", menu);
            Invoke(main,"AttachSubtitlePopupOwner",menu);
            Invoke(main, "PopulateSubtitleMenu", menu, selection, generation);
            show.Invoke(null, [menu]);
            await LayoutIdle(owner);
            AssertSubtitlePopupAdjacent(menu, anchor);
            var ready = new ContextMenu();
            Invoke(main, "PopulateSubtitleMenu", ready, selection, generation);
            AvailableSubtitle[] catalog = Enumerable.Range(0, 24).Select(index =>
            {
                var language = new[] { "de", "ja", "ko", "en" }[index % 4];
                return new AvailableSubtitle(language + "-" + index, "합성 언어 " + language + " " + index,
                    "https://www.youtube.com/api/timedtext?v=aqz-KE-bpKQ&lang=" + language, index >= 12);
            }).ToArray();
            Invoke(main, "AppendSubtitleCatalog", ready, catalog, selection.MediaGeneration, generation);
            var items = ready.Items.Cast<object>().ToArray();
            ready.Items.Clear();
            Invoke(main, "ReplaceSubtitleMenuContents", menu, items);
            await WaitForSubtitlePopupReady(main,owner,menu);
            AssertSubtitlePopupAdjacent(menu, anchor);
            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()!).ToArray();
            Assert(headers[0].Contains("자막 끄기") && Array.IndexOf(headers, "공식 자막 · 제작자 제공") <
                   Array.IndexOf(headers, "기계번역 · 자동 생성") && menu.Items.OfType<MenuItem>().Any(item => item.Items.Count == 1),
                "Atomic subtitle updates changed the off/manual/automatic grouping or loaded-track fallback.");
            Directory.CreateDirectory(artifacts);
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(menu.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(menu.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            using (var file = File.Create(Path.Combine(artifacts, prefix + "-subtitle-atomic.png")))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
            }
            owner.Left=originalLeft+41;await LayoutIdle(owner);
            Assert(!menu.IsOpen && menu.PlacementTarget is null && GetNullable<ContextMenu>(main,"_subtitleMenu") is null,
                "Moving the subtitle popup's actual owner retained a detached popup or its stale anchor: "+prefix);
            owner.Left=originalLeft;await LayoutIdle(owner);
            menu.PlacementTarget=anchor;Set(main,"_subtitleMenu",menu);Invoke(main,"AttachSubtitlePopupOwner",menu);
            show.Invoke(null,[menu]);await LayoutIdle(owner);AssertSubtitlePopupAdjacent(menu,anchor);
            await CloseFixtureMenuAsync(owner, menu);
            items = menu.Items.Cast<object>().ToArray();
            Invoke(main, "ReplaceSubtitleMenuContents", menu, items);
            Assert(!menu.IsOpen, "A late subtitle update reopened a dismissed popup.");
            var disconnected = new ContextMenu { PlacementTarget = new Button() };
            show.Invoke(null, [disconnected]);
            Assert(!disconnected.IsOpen, "A subtitle popup opened with an unattached anchor.");
            Console.WriteLine($"UIREVIEW subtitle-anchor prefix={prefix} atomic-update=True dismissed-stays-closed=True scope=offline-WPF-popup-not-reported-origin-reproduction");
        }
        finally
        {
            try { await CloseFixtureMenuAsync(owner, menu); }
            finally {Invoke(main,"CloseSubtitleMenu");owner.Left=originalLeft;Set(main, "_subtitleMenu", originalMenu); }
        }
    }

    private static async Task WaitForSubtitlePopupReady(MainWindow main,Window owner,ContextMenu menu)
    {
        var deadline=Stopwatch.StartNew();
        do
        {
            await LayoutIdle(owner);
            if(menu.IsOpen && menu.IsVisible && GetNullable<ContextMenu>(main,"_subtitleMenuReopening") is null)return;
            await Task.Delay(15);
        }while(deadline.Elapsed<TimeSpan.FromSeconds(2));
        Assert(false,"The rebuilt subtitle popup did not finish its actual close and anchored reopen.");
    }

    private static async Task CloseFixtureMenuAsync(Window owner, ContextMenu menu)
    {
        var retainedCapture = FixtureMenuOwnsCapture(menu);
        menu.IsOpen = false;
        var deadline = Stopwatch.StartNew();
        do
        {
            // Closing a Popup can defer its window/capture teardown. This fixture
            // owns the menu, so release only its capture and drain closing/focus
            // callbacks before the following idle-hide scenario starts.
            if (FixtureMenuOwnsCapture(menu)) Mouse.Capture(null);
            await LayoutIdle(owner);
            if (!menu.IsOpen && !menu.IsVisible && !FixtureMenuOwnsCapture(menu))
            {
                if (retainedCapture) Console.WriteLine("UIREVIEW menu-teardown fixtureCaptureReleased=True visible=False");
                return;
            }
            await Task.Delay(15);
        } while (deadline.Elapsed < TimeSpan.FromSeconds(2));
        Assert(false, $"Fixture subtitle menu did not finish closing: open={menu.IsOpen} visible={menu.IsVisible} ownedCapture={FixtureMenuOwnsCapture(menu)}.");
    }

    private static bool FixtureMenuOwnsCapture(ContextMenu menu)
    {
        for (var current = Mouse.Captured as DependencyObject; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, menu)) return true;
        return false;
    }

    private static void AssertSubtitlePopupAdjacent(ContextMenu menu, Button anchor)
    {
        menu.UpdateLayout();
        var top = anchor.PointToScreen(new Point());
        var bottom = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var menuTop = menu.PointToScreen(new Point());
        var menuBottom = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
        Assert(menu.IsVisible && Math.Min(Math.Abs(menuBottom.Y - top.Y), Math.Abs(menuTop.Y - bottom.Y)) <= 20 &&
               menuTop.X <= bottom.X && menuBottom.X >= top.X,
            $"Subtitle popup was not attached to its button: anchor={top}..{bottom}, menu={menuTop}..{menuBottom}.");
    }

    private static void VerifyKeyboardSeek(MainWindow main, Window owner, Slider seek, Button pause, string prefix)
    {
        var priorSnapshot = GetNullable<PlaybackSnapshot>(main, "_snapshot");
        var priorPauseIntent=GetNullable<object>(main,"_playPauseIntent");
        var priorPaused=Get<bool>(main,"_pausedPlaybackIntent");
        var priorPending = typeof(MainWindow).GetField("_pendingSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        var priorPendingAt = Get<double>(main, "_pendingSeekAt");
        var priorMaximum = seek.Maximum; var priorValue = seek.Value; var priorEnabled = seek.IsEnabled;
        using var commands = new PlayerCommandFixture(Get<MpvPlayerService>(main, "_player"));
        try
        {
            // This fixture installs a different native-session stand-in without
            // LoadAsync. Pair its initial unpaused state with a fresh intent and
            // restore the former session's intent when removing the stand-in.
            Set(main,"_playPauseIntent",null);Set(main,"_pausedPlaybackIntent",false);
            Set(main, "_snapshot", new PlaybackSnapshot { Loaded = true, Duration = 120, Position = 60 });
            seek.IsEnabled = true; seek.Maximum = 120; seek.Value = 60;
            Assert(commands.Commands.Count == 0, "A programmatic playback position update submitted a seek.");
            foreach (var (key, expected) in new[]
            {
                (Key.Right, 65d), (Key.Left, 60d), (Key.Up, 65d), (Key.Down, 60d),
                (Key.PageUp, 90d), (Key.PageDown, 60d), (Key.Home, 0d), (Key.End, 120d)
            })
            {
                var before = commands.Commands.Count;
                PressKey(owner, seek, key);
                Assert(Math.Abs(seek.Value - expected) < .001 && commands.Commands.Count == before + 1,
                    $"{prefix} seek {key} did not submit its expected keyboard target exactly once. expected={expected} actual={seek.Value} before={before} after={commands.Commands.Count} commands={string.Join(';', commands.Commands.Select(command => command.GetRawText()))}");
                var command = commands.Commands[^1];
                Assert(command.GetArrayLength() == 3 && command[0].GetString() == "seek" &&
                       Math.Abs(command[1].GetDouble() - expected) < .001 && command[2].GetString() == "absolute+exact" &&
                       Get<double?>(main, "_pendingSeek") == expected,
                    $"{prefix} seek {key} did not reach the serialized player command or pending snapshot guard.");
                Assert(seek.IsKeyboardFocused || seek.IsKeyboardFocusWithin,
                    $"{prefix} seek navigation removed keyboard access to the focused slider.");
            }
            var atEnd = commands.Commands.Count;
            PressKey(owner, seek, Key.Right);
            Assert(seek.Value == 120 && commands.Commands.Count == atEnd,
                $"{prefix} seek moved beyond the duration or submitted an unchanged endpoint.");
            seek.Value = 118;
            PressKey(owner, seek, Key.PageUp);
            Assert(seek.Value == 120 && commands.Commands[^1][1].GetDouble() == 120,
                $"{prefix} page seek was not clamped to the duration.");
            var beforePause = commands.Commands.Count;
            var pauseIntent=GetNullable<object>(main,"_playPauseIntent");
            var expectedPaused=!(pauseIntent is not null?(bool)pauseIntent.GetType().GetProperty("Paused")!.GetValue(pauseIntent)!:
                GetNullable<PlaybackSnapshot>(main,"_snapshot")?.Paused??Get<bool>(main,"_pausedPlaybackIntent"));
            var pauseGeneration=Get<int>(main,"_videoGeneration");
            var clicks = 0; RoutedEventHandler clicked = (_, _) => clicks++;
            pause.Click += clicked;
            try { PressKey(owner, pause, Key.Space); }
            finally { pause.Click -= clicked; }
            Assert(clicks == 1 && commands.Commands.Count == beforePause + 1 &&
                   commands.Commands[^1].GetRawText() == "[\"set_property\",\"pause\","+(expectedPaused?"true":"false")+"]" &&
                   Get<int>(main,"_videoGeneration")==pauseGeneration,
                $"{prefix} focused playback Space did not submit exactly one pause command.");
            Console.WriteLine($"UIREVIEW keyboard-seek prefix={prefix} arrows=5s page=30s home-end=True clamp=True pause-once=True scope=real-WPF-routes-serialized-mpv-command-in-memory-ack-no-real-player");
        }
        finally
        {
            Set(main,"_playPauseIntent",priorPauseIntent);Set(main,"_pausedPlaybackIntent",priorPaused);
            Set(main, "_snapshot", priorSnapshot); Set(main, "_pendingSeek", priorPending); Set(main, "_pendingSeekAt", priorPendingAt);
            seek.Maximum = priorMaximum; seek.Value = priorValue; seek.IsEnabled = priorEnabled;
        }
    }

    private static async Task VerifyVolumeCommandBurst(MainWindow main, Slider input, string prefix, string transition)
    {
        await WaitForVolumeDrain(main);
        var player = Get<MpvPlayerService>(main, "_player");
        var snapshotField = typeof(MpvPlayerService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var savedPlayerSnapshot = snapshotField.GetValue(player);
        var savedSnapshot = GetNullable<PlaybackSnapshot>(main, "_snapshot");
        var savedCurrent = Get<VideoItem>(main, "_current");
        var savedGeneration = Get<int>(main, "_videoGeneration"); var savedExpected = Get<long>(main, "_expectedMediaGeneration");
        var savedReady = Get<bool>(main, "_ready"); var settings = Get<AppSettings>(main, "_settings");
        var savedVolume = settings.Volume; var slider = Get<Slider>(main, "VolumeSlider"); var savedSlider = slider.Value;
        var status = Get<TextBlock>(main, "StatusText"); var savedStatus = status.Text;
        var savedStatusFactory = Get<Func<string>?>(main, "_statusTextFactory");
        using var commands = new PlayerCommandFixture(player, holdFirst: true);
        var accountBusy = false;
        try
        {
            Set(main, "_ready", false); input.SetCurrentValue(Slider.ValueProperty, 10d);
            await LayoutIdle(main); Set(main, "_ready", savedReady);
            Set(main, "_expectedMediaGeneration", 7L);
            var active = new PlaybackSnapshot { MediaGeneration = 7, VideoUrl = savedCurrent.Url, Loaded = true };
            snapshotField.SetValue(player, active); Set(main, "_snapshot", active);
            input.SetCurrentValue(Slider.ValueProperty, 21d);
            await commands.FirstCommandWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.SetCurrentValue(Slider.ValueProperty, 40d);
            input.SetCurrentValue(Slider.ValueProperty, 60d);
            input.SetCurrentValue(Slider.ValueProperty, 88d);
            await LayoutIdle(main);
            Assert(Math.Abs(settings.Volume - 88) < .001 && Math.Abs(slider.Value - 88) < .001,
                prefix + " volume coalescing delayed the in-memory setting or main/popout synchronization.");
            Assert(commands.Commands.Count == 1 && commands.Commands[0].GetRawText() == "[\"set_property\",\"volume\",21]",
                prefix + " volume input queued intermediate IPC commands while the first acknowledgement was pending.");
            if (transition == "video")
            {
                var newer = savedCurrent with { Id = "fixture0002", Url = "https://www.youtube.com/watch?v=fixture0002" };
                Set(main, "_current", newer); Set(main, "_videoGeneration", savedGeneration + 1); Set(main, "_expectedMediaGeneration", 8L);
                snapshotField.SetValue(player, new PlaybackSnapshot { MediaGeneration = 8, VideoUrl = newer.Url, Loaded = true });
            }
            if (transition == "account") { Invoke(main, "SetAccountBusy", true); accountBusy = true; }
            status.Text = "newer volume fixture status";
            commands.ReleaseFirst(transition == "success" ? null : new IOException("synthetic delayed volume failure"));
            await WaitForVolumeDrain(main);
            var stale = transition is "video" or "account";
            Assert(commands.Commands.Count == (stale ? 1 : 2) &&
                   (stale || commands.Commands[^1].GetRawText() == "[\"set_property\",\"volume\",88]"),
                prefix + " volume drain did not send exactly the latest pending value, or sent it into a newer video/account.");
            if (stale)
                Assert(status.Text == "newer volume fixture status",
                    prefix + " delayed volume failure overwrote the newer " + transition + " status.");
            if (transition == "current-error")
                Assert(status.Text != "newer volume fixture status" && !status.Text.Contains("synthetic delayed volume failure", StringComparison.Ordinal),
                    prefix + " current volume failure was suppressed or exposed its raw fixture error instead of user-facing advice.");
            Assert(Math.Abs(settings.Volume - 88) < .001,
                prefix + " volume completion or transition overwrote the latest in-memory preference.");
            if (accountBusy) { Invoke(main, "SetAccountBusy", false); accountBusy = false; }
            var beforeFresh = commands.Commands.Count;
            input.SetCurrentValue(Slider.ValueProperty, 93d); await WaitForVolumeDrain(main);
            Assert(commands.Commands.Count == beforeFresh + 1 && commands.Commands[^1].GetRawText() == "[\"set_property\",\"volume\",93]" &&
                   Math.Abs(settings.Volume - 93) < .001,
                prefix + " volume drain did not accept a fresh input after completing its previous operation.");
            Console.WriteLine($"UIREVIEW volume-queue prefix={prefix} state={transition} first-plus-latest-only=True stale-pending-dropped={stale} scope=actual-slider-routes-serialized-IPC-blocked-first-ack-no-real-player");
        }
        finally
        {
            commands.ReleaseFirst(); await WaitForVolumeDrain(main);
            if (accountBusy) Invoke(main, "SetAccountBusy", false);
            snapshotField.SetValue(player, savedPlayerSnapshot); Set(main, "_snapshot", savedSnapshot); Set(main, "_current", savedCurrent);
            Set(main, "_videoGeneration", savedGeneration); Set(main, "_expectedMediaGeneration", savedExpected);
            Set(main, "_ready", false); slider.SetCurrentValue(Slider.ValueProperty, savedSlider);
            await LayoutIdle(main); settings.Volume = savedVolume; Set(main, "_ready", savedReady);
            Set(main, "_statusTextFactory", savedStatusFactory); status.Text = savedStatus;
        }
    }

    private static async Task WaitForVolumeDrain(MainWindow main)
    {
        // The original async-void route has no drain task. Pump its posted
        // continuations as well, so the same behavioral fixture can establish RED.
        var field = typeof(MainWindow).GetField("_volumeTask", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(main) is Task task) await task.WaitAsync(TimeSpan.FromSeconds(3));
        await LayoutIdle(main); await LayoutIdle(main);
    }

    // Exercise PlayerCommandAsync -> MpvPlayerService.SendAsync without a process,
    // connected pipe, network, or media. Acknowledge the actual serialized request.
    private sealed class PlayerCommandFixture : IDisposable
    {
        private readonly MpvPlayerService _player;
        private readonly FieldInfo _sessionField = typeof(MpvPlayerService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly object? _previousSession;
        private readonly object _session;
        private readonly NamedPipeClientStream _pipe = new(".", "fkyt-ui-unconnected-" + Guid.NewGuid().ToString("N"), PipeDirection.InOut);
        private readonly RecordingCommandWriter _writer;
        private readonly CancellationTokenSource _cancellation;
        private TaskCompletionSource<JsonElement?>? _firstCompletion;
        public List<JsonElement> Commands { get; } = [];
        public TaskCompletionSource FirstCommandWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PlayerCommandFixture(MpvPlayerService player, bool holdFirst = false)
        {
            _player = player; _previousSession = _sessionField.GetValue(player);
            var type = typeof(MpvPlayerService).GetNestedType("Session", BindingFlags.NonPublic)!;
            _session = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [null, _pipe], null)!;
            _cancellation = (CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(_session)!;
            var pending = (ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>>)type.GetProperty("Pending")!.GetValue(_session)!;
            _writer = new RecordingCommandWriter(message =>
            {
                using var document = JsonDocument.Parse(message);
                Commands.Add(document.RootElement.GetProperty("command").Clone());
                var id = document.RootElement.GetProperty("request_id").GetInt64();
                Assert(pending.TryRemove(id, out var completion), "Recorded player request did not have a pending completion.");
                if (holdFirst && Commands.Count == 1) _firstCompletion = completion;
                else completion!.TrySetResult(null);
                FirstCommandWritten.TrySetResult();
            });
            type.GetField("Writer")!.SetValue(_session, _writer);
            _sessionField.SetValue(player, _session);
        }

        public void ReleaseFirst(Exception? error = null)
        {
            if (_firstCompletion is not { } completion) return;
            _firstCompletion = null;
            if (error is null) completion.TrySetResult(null); else completion.TrySetException(error);
        }

        public void Dispose()
        {
            ReleaseFirst();
            _sessionField.SetValue(_player, _previousSession);
            _writer.Dispose(); _pipe.Dispose(); _cancellation.Dispose();
            ((SemaphoreSlim)_session.GetType().GetProperty("WriteGate")!.GetValue(_session)!).Dispose();
        }
    }

    private sealed class RecordingCommandWriter(Action<string> record) : StreamWriter(new MemoryStream(), new UTF8Encoding(false))
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); record(buffer.ToString()); return Task.CompletedTask;
        }
        // WriteLineAsync records the complete frame directly and does not buffer
        // bytes. Its synthetic acknowledgement must not depend on a redundant
        // asynchronous MemoryStream flush before the next keyboard assertion.
        public override Task FlushAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private static void PressKey(Window window, UIElement target, Key key)
    {
        target.Focus();
        var source = PresentationSource.FromVisual(target) ?? PresentationSource.FromVisual(window)!;
        foreach (var (preview, bubble) in new[]
        {
            (Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent),
            (Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent)
        })
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = preview };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = bubble };
                target.RaiseEvent(args);
            }
        }
    }

    private static void CaptureView(Window window, string directory, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static async Task TrackClickThenDrag(Window window, Slider slider)
    {
        slider.ApplyTemplate(); window.UpdateLayout();
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        slider.SetCurrentValue(Slider.ValueProperty, 10d); window.UpdateLayout();
        var start = ValuePoint(track, .6);
        var end = ValuePoint(track, .85);
        // Raw reports carry integral device pixels. A nearest-pixel coordinate can
        // differ from an exact slider fraction by at most half a device pixel.
        var valueTolerance = (slider.Maximum - slider.Minimum) /
            ((track.ActualWidth - track.Thumb.ActualWidth) * VisualTreeHelper.GetDpi(track).DpiScaleX) / 2 + .00001;
        MouseButtonEventHandler observe = (_, args) => Console.WriteLine(
            $"INTERACTION volume-input source={args.OriginalSource.GetType().Name} trackPoint={args.GetPosition(track)} " +
            $"plannedPoint={start} value={slider.Value:F2} captured={Mouse.Captured?.GetType().Name ?? "none"} " +
            $"track={track.ActualWidth:F2}x{track.ActualHeight:F2} thumb={track.Thumb.ActualWidth:F2}");
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, observe, true);
        try
        {
            SendMouse(window, track, start, 0x0200, 0);
            SendMouse(window, track, start, 0x0201, 1);
            Assert(Math.Abs(slider.Value - 60) <= valueTolerance,
                "Track press did not immediately move volume to the clicked value within half a device pixel.");
            Assert(Mouse.Captured == slider || Mouse.Captured == track.Thumb,
                "Track press did not capture a continuous volume drag.");
            Assert(GetCapture() == ((HwndSource)PresentationSource.FromVisual(window)).Handle,
                "Track press did not acquire native capture for its owner.");
            SendMouse(window, track, end, 0x0200, 1);
            // ProcessInput routes the injected move synchronously. Validate it before
            // pumping native messages: SetCapture can queue a move at the unchanged
            // desktop pointer, which is unrelated to this fixture's planned coordinates.
            Assert(Math.Abs(slider.Value - 85) <= valueTolerance,
                "Held volume movement after the track press did not update continuously within half a device pixel.");
            SendMouse(window, track, end, 0x0202, 0);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Assert(Mouse.Captured is null, "Volume mouse capture survived release.");
            Assert(GetCapture() == 0, "Native volume mouse capture survived release.");
            Console.WriteLine($"INTERACTION volume={slider.Value:F2} dragReleased=True nativeCaptureVerified=True scope=synthetic-raw-WPF-input-not-physical-pointer");
        }
        finally
        {
            SendMouse(window, track, end, 0x0202, 0);
            if (Mouse.Captured is not null) Mouse.Capture(null);
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, observe);
        }
    }

    private static Point ValuePoint(Track track, double fraction) => new(
        track.Thumb.ActualWidth / 2 + (track.ActualWidth - track.Thumb.ActualWidth) * fraction,
        track.ActualHeight / 2);

    private sealed class InvalidDislikeHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"id\":\"different01\",\"likes\":1,\"dislikes\":2}")});
    }

    private static void AssertNativeTarget(Window window, FrameworkElement surface)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window);
        var point = surface.TranslatePoint(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2), window);
        var device = source.CompositionTarget.TransformToDevice.Transform(point);
        var selected = ChildWindowFromPointEx(source.Handle, new NativePoint { X = (int)device.X, Y = (int)device.Y }, 1 | 2 | 4);
        Assert(selected == source.Handle, "Win32's input hit test did not skip the disabled video subtree.");
    }

    private static void Click(Window window, FrameworkElement target, Point point)
    {
        SendMouse(window, target, point, 0x0200, 0);
        SendMouse(window, target, point, 0x0201, 1);
        SendMouse(window, target, point, 0x0202, 0);
    }

    private static void SendMouse(Window window, FrameworkElement target, Point point, uint message, nuint state)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window);
        var device = source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(point, window));
        // SendMessage alone does not activate WPF's mouse PresentationSource and is
        // intermittently rejected while another source is active. Feed the same raw
        // report into the real InputManager, specifying this fixture's source and
        // coordinates without changing the desktop pointer or foreground window.
        var assembly = typeof(InputManager).Assembly;
        var reportType = assembly.GetType("System.Windows.Input.RawMouseInputReport", throwOnError: true)!;
        var modeType = assembly.GetType("System.Windows.Input.InputMode", throwOnError: true)!;
        var actionsType = assembly.GetType("System.Windows.Input.RawMouseActions", throwOnError: true)!;
        var eventType = assembly.GetType("System.Windows.Input.InputReportEventArgs", throwOnError: true)!;
        // Real HwndMouseInputProvider.ReportInput marks its provider active before
        // emitting Activate. Direct InputManager injection bypasses that transition.
        // Mirror only that activation state: capture itself must still be acquired by
        // production code through native SetCapture and verified below. Otherwise an
        // inactive provider queries the real desktop during CaptureMouse, which can
        // fail with access denied in this isolated test session despite valid raw input.
        if (Mouse.PrimaryDevice.ActiveSource != source)
        {
            var provider = typeof(PresentationSource).GetMethod("GetInputProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source, [typeof(MouseDevice)])!;
            var active = provider.GetType().GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
            active.SetValue(provider, true);
            ProcessReport(0x2, 0, 0);
            Assert(Mouse.PrimaryDevice.ActiveSource == source && (bool)active.GetValue(provider)!,
                "Synthetic mouse activation did not synchronize its device and HWND provider.");
        }
        ProcessReport(0x10 | (message == 0x0201 ? 0x40 : message == 0x0202 ? 0x80 : 0),
            (int)Math.Round(device.X), (int)Math.Round(device.Y));

        void ProcessReport(int flags, int x, int y)
        {
            var report = Activator.CreateInstance(reportType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [Enum.Parse(modeType, "Foreground"), Environment.TickCount, source,
                    Enum.ToObject(actionsType, flags), x, y, 0, nint.Zero], culture: null)!;
            var args = (InputEventArgs)Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [Mouse.PrimaryDevice, report], culture: null)!;
            args.RoutedEvent = (RoutedEvent)typeof(InputManager).GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            InputManager.Current.ProcessInput(args);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static object? Invoke(object target, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Get<T>(object target, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T? GetNullable<T>(object target, string field) where T : class => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target) as T;
    private static void Set(object target, string field, object? value) => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent, NativePoint point, uint flags);
}
