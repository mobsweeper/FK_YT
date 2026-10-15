namespace FkYouTube.Services;

// Wall time is supplied by a monotonic clock; position jumps are not watching.
public sealed class WatchProgressTracker
{
    private double? _lastClock,_lastPosition;
    private bool _lastActive;
    public double WatchedSeconds { get; private set; }
    public double WatchedMediaSeconds { get; private set; }
    public bool Qualified { get; private set; }
    public void Reset() { _lastClock=_lastPosition=null;_lastActive=false;WatchedSeconds=WatchedMediaSeconds=0;Qualified=false; }
    public bool Observe(double clock,double position,double duration,double speed,bool paused,bool buffering,bool seeking,bool loaded)
    {
        var active=loaded && !paused && !buffering && !seeking && speed>0;
        // Other observed properties can arrive between time-pos updates. Keep the
        // time anchor until position advances, otherwise those events lose watch time.
        if(active && _lastActive && _lastPosition==position && _lastClock.HasValue && clock-_lastClock.Value<=3)return false;
        if(_lastClock.HasValue && _lastPosition.HasValue && active && _lastActive)
        {
            var wall=clock-_lastClock.Value;var media=position-_lastPosition.Value;
            if(wall>0 && wall<=3 && media>0 && media<=wall*speed+0.75)
            { WatchedSeconds+=Math.Min(wall,media/speed);WatchedMediaSeconds+=Math.Min(media,wall*speed); }
        }
        _lastClock=clock;_lastPosition=position;_lastActive=active;
        bool now=WatchedSeconds>=30 || (duration>0 && duration<30 && WatchedMediaSeconds>=duration*0.8);
        if(now && !Qualified) { Qualified=true;return true; }
        return false;
    }
}
