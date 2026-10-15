using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FkYouTube.Playback;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private async Task RunReviewProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();
        ShowDemo();await ShowLocalHistoryAsync();
        checks.Add(new{name="empty-history-clears-item-count",passed=_items.Count==0 && ItemCount.Text=="",text=ItemCount.Text});
        ShowDemo();await ResetAccountAsync();
        checks.Add(new{name="account-reset-clears-item-count",passed=_items.Count==0 && ItemCount.Text=="",text=ItemCount.Text});
        ShowPanel("player");PlayerPanel.IsEnabled=true;SeekSlider.IsEnabled=true;SeekSlider.Maximum=635;UpdateLayout();SeekSlider.ApplyTemplate();
        if(SeekSlider.Template.FindName("PART_Track",SeekSlider) is System.Windows.Controls.Primitives.Track track)
        {
            try
            {
                SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=PreviewMouseLeftButtonDownEvent,Handled=true});
                var expected=Math.Clamp(track.ValueFromPoint(Mouse.GetPosition(track)),0,SeekSlider.Maximum);
                SeekSlider.Value=expected<SeekSlider.Maximum/2?SeekSlider.Maximum:0;
                track.UpdateLayout();expected=Math.Clamp(track.ValueFromPoint(Mouse.GetPosition(track)),0,SeekSlider.Maximum);
                SeekSlider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=PreviewMouseMoveEvent});
                checks.Add(new{name="track-press-move-follows-pointer",passed=Math.Abs(SeekSlider.Value-expected)<.01,expected,actual=SeekSlider.Value});
                SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=PreviewMouseLeftButtonUpEvent,Handled=true});
                checks.Add(new{name="track-release-clears-drag-and-capture",passed=!_seekDragging && Mouse.Captured!=SeekSlider});
            }
            finally{if(Mouse.Captured==SeekSlider)SeekSlider.ReleaseMouseCapture();}
        }
        else checks.Add(new{name="seek-track-template",passed=false});
        File.WriteAllText(Path.Combine(directory,"review-smoke.json"),JsonSerializer.Serialize(new{checks,scope="Offline WPF state transitions and synthetic routed pointer events; no account/network or physical mouse/DPI validation."},new JsonSerializerOptions{WriteIndented=true}));
    }
    private async Task RunSeekProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();string? failure=null;
        var syncBefore=_settings.SyncHistory;var volumeBefore=_settings.Volume;_settings.SyncHistory=false;_settings.Volume=0;
        Header.IsEnabled=Sidebar.IsEnabled=PlayerPanel.IsEnabled=false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await PlayVideoAsync(new(){Id="aqz-KE-bpKQ",Title="자동 재생바 검사 · 시청 기록 전송 없음",Url="https://www.youtube.com/watch?v=aqz-KE-bpKQ"},false,0);
            while(_snapshot?.Loaded!=true){deadline.Token.ThrowIfCancellationRequested();await Task.Delay(100,deadline.Token);}
            await _player.CommandAsync("set_property","volume",0);
            await _player.CommandAsync("set_property","pause",true);
            PlayerPanel.IsEnabled=true;
            UpdateLayout();SeekSlider.ApplyTemplate();
            if(SeekSlider.Template.FindName("PART_Track",SeekSlider) is not System.Windows.Controls.Primitives.Track track)throw new InvalidOperationException("재생바 Track 템플릿을 찾지 못했습니다.");
            var mapped=track.ValueFromPoint(new System.Windows.Point(track.ActualWidth/2,track.ActualHeight/2));
            if(!SeekSlider.IsMoveToPointEnabled || Math.Abs(mapped-SeekSlider.Maximum/2)>2)throw new InvalidOperationException("재생바 위치와 영상 시간의 대응이 올바르지 않습니다.");
            checks.Add(new{name="track-click-time-mapping",passed=true,maximum=SeekSlider.Maximum,midpoint=mapped});
            foreach(var target in new[]{Math.Min(200,SeekSlider.Maximum*.6),Math.Min(40,SeekSlider.Maximum*.2)})
            {
                SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=PreviewMouseLeftButtonDownEvent,Handled=true});
                if(!_seekDragging)throw new InvalidOperationException("Slider가 먼저 처리한 눌림 이벤트가 탐색 보호 핸들러에 전달되지 않았습니다.");
                var captured=Mouse.Captured==SeekSlider;
                checks.Add(new{name="track-press-captures-outside-release",passed=captured});
                if(!captured)throw new InvalidOperationException("Track 클릭 후 외부 놓임을 받을 마우스 캡처가 없습니다.");
                SeekSlider.Value=target;
                await Task.Delay(350,deadline.Token);
                OnSnapshot(_snapshot!);
                var held=Math.Abs(SeekSlider.Value-target)<.01;
                checks.Add(new{name="handled-track-press-preserves-target",passed=held,target,actual=SeekSlider.Value});
                if(!held)throw new InvalidOperationException("재생바를 누르는 동안 진행 이벤트가 목표 위치를 덮어썼습니다.");
                SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=PreviewMouseLeftButtonUpEvent,Handled=true});
                if(_seekDragging)throw new InvalidOperationException("Slider가 먼저 처리한 놓임 이벤트가 탐색 핸들러에 전달되지 않았습니다.");
                var released=Mouse.Captured!=SeekSlider;
                checks.Add(new{name="track-release-clears-capture",passed=released});
                if(!released)throw new InvalidOperationException("탐색 후 마우스 캡처가 남았습니다.");
                var timer=Stopwatch.StartNew();double actual=0;
                do{actual=await _player.GetPropertyAsync<double>("time-pos",deadline.Token);if(Math.Abs(actual-target)<.35)break;await Task.Delay(100,deadline.Token);}while(timer.Elapsed.TotalSeconds<8);
                var passed=Math.Abs(actual-target)<.35;checks.Add(new{name="normal-ui-seek-handler",passed,target,actual});
                if(!passed)throw new InvalidOperationException("일반 재생바 핸들러의 이동 결과가 요청 시간과 다릅니다.");
            }
        }
        catch(Exception ex){failure=ex.Message;}
        finally{await StopPlaybackAsync();_settings.SyncHistory=syncBefore;_settings.Volume=volumeBefore;}
        File.WriteAllText(Path.Combine(directory,"seek-smoke.json"),JsonSerializer.Serialize(new{passed=failure==null,failure,checks,markWatched=false,scope="WPF handled routed mouse events, held track target stability and native mpv position; not physical mouse or monitor-DPI validation."},new JsonSerializerOptions{WriteIndented=true}));
    }
    // This explicit diagnostic mode uses the same UI playback and watch tracker as
    // normal viewing. It never calls MarkWatched directly or bypasses its threshold.
    private async Task RunAccountPlaybackProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string videoId="aqz-KE-bpKQ";
        var errors=new List<string>();
        var metrics=new List<object>();
        bool? presentBefore=null,presentAfter=null;
        var started=Stopwatch.StartNew();
        double? firstPlaybackSeconds=null;
        var observed=new HashSet<int>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        PlaybackSnapshot? lastPlaying=null;
        string? failure=null;
        async Task<bool> FindInRemoteHistoryAsync()
        {
            await using var feed=new FeedService(_paths);var found=false;
            var batch=await feed.ReadAsync(true,"history",1,_session.Acquire(),item=>found|=item.Id==videoId,deadline.Token);
            if(!found && batch.HasMore)await feed.ReadAsync(false,"history",1,null,item=>found|=item.Id==videoId,deadline.Token);
            return found;
        }
        try
        {
            if(!_session.IsConnected)throw new InvalidOperationException("검증할 로그인 세션이 연결되지 않았습니다.");
            if(!_settings.SyncHistory)throw new InvalidOperationException("시청 기록 전송 설정이 꺼져 있습니다.");
            presentBefore=await FindInRemoteHistoryAsync();
            var playStarted=started.Elapsed.TotalSeconds;
            await PlayVideoAsync(new(){Id=videoId,Title="Big Buck Bunny · 공개 재생 검증",Url="https://www.youtube.com/watch?v="+videoId},false,0);
            var previousCpu=new Dictionary<int,(double Cpu,double Time)>();
            while(started.Elapsed.TotalSeconds-playStarted<110)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var snapshot=_snapshot;
                if(snapshot?.Loaded==true && snapshot.Position>0)
                {lastPlaying=snapshot;firstPlaybackSeconds??=started.Elapsed.TotalSeconds-playStarted;}
                var ids=ProcessSupport.OwnedProcess.GetAllOwnedProcessIds().Append(Environment.ProcessId).Distinct().ToArray();
                long working=0,privateBytes=0;double cpu=0;var now=started.Elapsed.TotalSeconds;
                foreach(var pid in ids)
                {
                    try
                    {
                        using var process=Process.GetProcessById(pid);var total=process.TotalProcessorTime.TotalSeconds;
                        if(previousCpu.TryGetValue(pid,out var old) && now>old.Time)cpu+=Math.Max(0,total-old.Cpu)/(now-old.Time)/Environment.ProcessorCount*100;
                        previousCpu[pid]=(total,now);working+=process.WorkingSet64;privateBytes+=process.PrivateMemorySize64;observed.Add(pid);
                    }
                    catch(ArgumentException){}catch(InvalidOperationException){}
                }
                metrics.Add(new{elapsedSeconds=now,workingSetBytes=working,privateBytes,cpuPercentOfMachine=cpu,processCount=ids.Length,position=snapshot?.Position,phase=_watch.Qualified?"history-sync":"playback-and-extraction"});
                if(_watch.Qualified && _historyTasks.Count==0)break;
                if(snapshot?.Ended==true && !_watch.Qualified)throw new InvalidOperationException("실제 재생 기준을 충족하기 전에 재생이 종료되었습니다.");
                await Task.Delay(500,deadline.Token);
            }
            if(!_watch.Qualified)throw new InvalidOperationException("검증 시간 내 시청 기록 전송 기준에 도달하지 못했습니다.");
            SavePosition();
            await Task.WhenAll(_historyTasks.ToArray()).WaitAsync(deadline.Token);
            for(var attempt=0;attempt<3;attempt++)
            {
                presentAfter=await FindInRemoteHistoryAsync();
                if(presentAfter==true){_store.SetSyncStatus(_settings.AccountKey,videoId,"confirmed");break;}
                if(attempt<2)await Task.Delay(5000,deadline.Token);
            }
        }
        catch(Exception ex){failure=ex is OperationCanceledException?"계정 재생 검증 시간 초과":ex.Message;}
        finally{await StopPlaybackAsync();}
        var record=_store.GetHistory(_settings.AccountKey).FirstOrDefault(h=>h.Video.Id==videoId);
        File.WriteAllText(Path.Combine(directory,"account-playback-smoke.json"),JsonSerializer.Serialize(new
        {
            testVideoId=videoId,authenticated=true,passed=failure==null && _watch.Qualified && presentAfter==true,
            failure,presentBefore,presentAfter,watchQualified=_watch.Qualified,
            actualWatchSeconds=_watch.WatchedSeconds,watchedMediaSeconds=_watch.WatchedMediaSeconds,
            historyStatus=record?.SyncStatus,localResumePosition=record?.Position,firstPlaybackSeconds,
            playingSnapshot=lastPlaying,metrics,observedProcessIds=observed,
            limits=new[]{"CPU/memory includes the app and live Job Object members; shared pages may be counted more than once.","A short public test video is not a browser comparison or a sustained 1080p benchmark.","Hardware decoder state is observed, while GPU engine utilization and native mouse/DPI input are not measured."}
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
