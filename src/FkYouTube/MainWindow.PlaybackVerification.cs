using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FkYouTube;

public partial class MainWindow
{
    private async Task RunPlaybackUpgradeProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();string? failure=null;var stage="start";string? playbackError=null;
        void Error(Playback.PlaybackFailure failure)=>playbackError=failure.Message;
        _player.PlaybackError+=Error;
        _settings.Incognito=true;_settings.SyncHistory=false;_settings.Volume=0;_settings.MaxHeight=144;_settings.AutoPlayNext=true;
        Header.IsEnabled=Sidebar.IsEnabled=false;
        var first=new VideoItem{Id="aqz-KE-bpKQ",Title="공개 영상 통합 검사 · 기록 없음",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ"};
        var second=first with{Title="다음 영상 통합 검사 · 기록 없음"};
        ClearDisplayedItems();AppendItem(first);AppendItem(second);_hasFeed=false;_hasMore=false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(210));var ct=deadline.Token;
        async Task WaitForCurrentAsync(VideoItem target)
        {
            var wait=Stopwatch.StartNew();
            while(_current?.Id!=target.Id || _current.Title!=target.Title || _snapshot?.Loaded!=true || _videoCts!=null)
            {
                if(_snapshot?.Ended==true || wait.Elapsed.TotalSeconds>60)
                    throw new IOException($"Playback did not load. expected={_expectedMediaGeneration}; service={JsonSerializer.Serialize(_player.Snapshot)}; UI={JsonSerializer.Serialize(_snapshot)}; error={playbackError}");
                await Task.Delay(100,ct);
            }
        }
        async Task ClickPauseAndWaitAsync(System.Windows.Controls.Border surface,bool paused)
        {
            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                {RoutedEvent=PreviewMouseLeftButtonDownEvent});
            var wait=Stopwatch.StartNew();
            while(await _player.GetPropertyAsync<bool>("pause",ct)!=paused)
            {
                if(wait.Elapsed.TotalSeconds>3)throw new InvalidOperationException("Video surface click did not toggle native mpv pause.");
                await Task.Delay(50,ct);
            }
        }
        try
        {
            stage="initial-playback";await PlayVideoAsync(first,explicitStart:0);
            await WaitForCurrentAsync(first);
            // This probe is guest-only and stores diagnostics outside the release/data.
            // Keep native extraction failures distinct from WPF assertion failures.
            await _player.CommandAsync(ct,"set_property","log-file",Path.Combine(Path.GetFullPath(directory),"guest-mpv.log"));
            checks.Add(new{name="quality-not-extracted-until-requested",passed=_qualityCts==null && _qualityKey.Length==0 && QualityLoadButton.Visibility==Visibility.Visible});
            var safetyWait=Stopwatch.StartNew();
            while(!_player.Snapshot.CanReleaseCookies && safetyWait.Elapsed.TotalSeconds<5)await Task.Delay(50,ct);
            checks.Add(new{name="confirmed-vod-permits-early-cookie-release",passed=_player.Snapshot.CanReleaseCookies});
            QualityLoadButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            while(_qualityCts!=null)await Task.Delay(50,ct);
            checks.Add(new{name="on-demand-quality-shows-real-formats",passed=_resolutions.Count>0 && QualityCombo.Visibility==Visibility.Visible,heights=_resolutions.ToArray()});
            checks.Add(new{name="quality-button-opens-loaded-dropdown",passed=QualityCombo.IsDropDownOpen && QualityLoadButton.IsEnabled});
            QualityCombo.IsDropDownOpen=false;
            await _relatedTask;
            var relatedBefore=_relatedItems.ToArray();var relatedTaskBefore=_relatedTask;
            var ownerBefore=_current?.ChannelUrl;var avatarBefore=_current?.ChannelThumbnail;
            var resolutionsBefore=_resolutions.ToArray();
            var requestedHeight=_resolutions.First(h=>h>144);
            stage="quality-reload";QualityCombo.SelectedItem=requestedHeight;
            await WaitForCurrentAsync(first);
            checks.Add(new{name="quality-change-keeps-formats-and-selection",passed=_resolutions.SequenceEqual(resolutionsBefore) && QualityCombo.Visibility==Visibility.Visible && QualityCombo.SelectedItem is int chosen && chosen==requestedHeight});
            checks.Add(new{name="quality-change-keeps-related-without-new-task",passed=ReferenceEquals(_relatedTask,relatedTaskBefore) && _relatedItems.SequenceEqual(relatedBefore)});
            checks.Add(new{name="related-owner-survives-quality-reload",passed=!string.IsNullOrEmpty(ownerBefore) && _current?.ChannelUrl==ownerBefore && _current?.ChannelThumbnail==avatarBefore});
            await _player.CommandAsync(ct,"set_property","pause",false);
            await ClickPauseAndWaitAsync(VideoSurface,true);
            await ClickPauseAndWaitAsync(VideoSurface,false);
            checks.Add(new{name="video-surface-click-toggles-native-mpv",passed=true});
            var mediaGeneration=_expectedMediaGeneration;var hwnd=VideoHost.Handle;
            checks.Add(new{name="comments-not-requested-on-play",passed=!_commentsRequested && _commentsCts==null && CommentsList.ItemsSource==null});
            checks.Add(new{name="next-list-matches-autoplay-order",passed=_upNext.Count==1 && ReferenceEquals(_upNext[0],second)});
            await _player.CommandAsync(ct,"set_property","pause",true);
            stage="popout";Popout_Click(this,new RoutedEventArgs());
            while(_popoutWindow==null || _popoutSurface?.Child!=VideoHost){await Task.Delay(50,ct);}
            _popoutWindow.Width=640;_popoutWindow.Height=420;_popoutWindow.UpdateLayout();await Task.Delay(500,ct);
            checks.Add(new{name="native-popout-resize-retains-playing-window",passed=VideoHost.Handle==hwnd && _expectedMediaGeneration==mediaGeneration && _player.Snapshot.Loaded && _popoutSurface.Child==VideoHost});
            await ClickPauseAndWaitAsync(_popoutSurface,false);
            await ClickPauseAndWaitAsync(_popoutSurface,true);
            checks.Add(new{name="popout-video-click-toggles-native-mpv",passed=true});
            _popoutWindow.Close();await Task.Delay(300,ct);
            checks.Add(new{name="native-popout-close-restores-video",passed=VideoSurface.Child==VideoHost && VideoHost.Handle==hwnd && _player.Snapshot.Loaded});
            VolumeSlider.ApplyTemplate();UpdateLayout();
            var track=VolumeSlider.Template.FindName("PART_Track",VolumeSlider) as Track;
            var sixty=track?.ValueFromPoint(new Point((track.ActualWidth-track.Thumb.ActualWidth)*.6+track.Thumb.ActualWidth/2,track.ActualHeight/2))??0;
            checks.Add(new{name="volume-click-maps-directly-to-60",passed=VolumeSlider.IsMoveToPointEnabled && Math.Abs(sixty-60)<.1,mapped=sixty});
            stage="preparation";await _player.CommandAsync(ct,"seek",Math.Max(0,_snapshot!.Duration-12),"absolute+exact");
            var preparation=Stopwatch.StartNew();
            while(_preparedItem==null && preparation.Elapsed.TotalSeconds<75){await Task.Delay(100,ct);}
            checks.Add(new{name="next-video-prepared-through-real-service",passed=ReferenceEquals(_preparedItem,second),seconds=preparation.Elapsed.TotalSeconds});
            if(_preparedItem==null)throw new InvalidOperationException("Next preparation failed: "+PrefetchState.Text);
            await Task.Delay(3000,ct);
            long eof=0;void Observe(Playback.PlaybackSnapshot s){if(s.MediaGeneration==mediaGeneration && s.EndReason=="eof")Interlocked.CompareExchange(ref eof,Stopwatch.GetTimestamp(),0);}
            _player.SnapshotChanged+=Observe;
            try
            {
                stage="autoplay";await _player.CommandAsync(ct,"seek",Math.Max(0,_snapshot!.Duration-1),"absolute+exact");
                await _player.CommandAsync(ct,"set_property","pause",false);
                while(_current?.Id!=second.Id || _current.Title!=second.Title || _snapshot?.Loaded!=true || _snapshot.Position<.2 || _videoCts!=null){await Task.Delay(50,ct);}
                var elapsed=eof==0?(double?)null:Stopwatch.GetElapsedTime(eof).TotalMilliseconds;
                checks.Add(new{name="prepared-autoplay-handoff",passed=_playingIndex==1 && _snapshot.Position<10 && _expectedMediaGeneration==mediaGeneration+1,eofToObservedProgressMs=elapsed,position=_snapshot.Position});
            }
            finally{_player.SnapshotChanged-=Observe;}
            var nativePlaylist=await _player.GetPropertyAsync<JsonElement>("playlist",ct);
            checks.Add(new{name="adopted-playlist-removes-past-entry",passed=nativePlaylist.ValueKind==JsonValueKind.Array && nativePlaylist.GetArrayLength()==1});
            checks.Add(new{name="comments-remain-lazy-after-autoplay",passed=!_commentsRequested && _commentsCts==null && CommentsList.ItemsSource==null});
            await _relatedTask;
            checks.Add(new{name="related-service-items-visible",passed=_relatedItems.Count>0,count=_relatedItems.Count});
            checks.Add(new{name="incognito-no-history",passed=_store.GetHistory(_settings.AccountKey).Count==0 && _playCookie?.Path==null});
            await _player.CommandAsync(ct,"set_property","pause",true);await _player.CommandAsync(ct,"seek",40,"absolute+exact");await Task.Delay(500,ct);
            var position=await _player.GetPropertyAsync<double>("time-pos",ct);checks.Add(new{name="seek-after-prepared-autoplay",passed=Math.Abs(position-40)<.5,position});
            AutoPlayCheck.IsChecked=false;_settings.AutoPlayNext=false;
            stage="previous";PreviousVideo_Click(this,new RoutedEventArgs());
            await WaitForCurrentAsync(first);
            checks.Add(new{name="previous-button-preserves-list-order",passed=_playingIndex==0 && !PreviousVideoButton.IsEnabled});
            stage="manual-next";NextVideo_Click(this,new RoutedEventArgs());
            await WaitForCurrentAsync(second);
            checks.Add(new{name="manual-next-works-with-autoplay-off",passed=_playingIndex==1 && !_settings.AutoPlayNext});
            await _relatedTask;
            SelectPlayerSide("related");UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var dpi=VisualTreeHelper.GetDpi(this);var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);bitmap.Render(this);
            using var stream=File.Create(Path.Combine(directory,"player-layout.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
        }
        catch(Exception ex){failure=stage+": "+ex.GetType().Name+": "+ex.Message;}
        finally{_player.PlaybackError-=Error;await StopPlaybackAsync();}
        File.WriteAllText(Path.Combine(directory,"playback-upgrade.json"),JsonSerializer.Serialize(new{failure,checks,scope="Guest real mpv/YouTube service playback and WPF host reparent. Surface pause uses routed click events and real mpv state; volume uses template coordinate mapping. Neither uses a physical mouse. Transition time ends at first observed >0.2 s position, not first displayed frame."},new JsonSerializerOptions{WriteIndented=true}));
    }
}
