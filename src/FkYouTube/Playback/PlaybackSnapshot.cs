namespace FkYouTube.Playback;

public sealed record PlaybackSnapshot
{
    public long MediaGeneration { get; init; }
    public string? VideoUrl { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public bool Paused { get; init; }
    public bool Buffering { get; init; }
    public bool Seeking { get; init; }
    public double Speed { get; init; } = 1;
    public double Volume { get; init; } = 100;
    public string HardwareDecoder { get; init; } = "unavailable";
    public bool Loaded { get; init; }
    public bool CanReleaseCookies { get; init; }
    public bool Ended { get; init; }
    public string? EndReason { get; init; }
    public string? ErrorMessage { get; init; }
    public bool AuthenticationRequired { get; init; }
}
