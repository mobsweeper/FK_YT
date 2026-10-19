namespace FkYouTube.Playback;

public sealed record PlaybackSnapshot
{
    public long MediaGeneration { get; init; }
    // Orders actual state changes across the player's entire lifetime.
    public long Revision { get; init; }
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
    // Only ProcessRunner's allowlisted classification crosses into UI state.
    public string? ErrorCode { get; init; }
    public bool FailureClassificationPending { get; init; }
    public bool AuthenticationRequired { get; init; }
    public string LiveStatus { get; init; } = "unknown";
    public bool IsLive => LiveStatus == "is_live";
    public bool Seekable { get; init; }
    public bool PartiallySeekable { get; init; }
    // These are mpv's local demuxer-cache ranges, never a promise of server DVR.
    public IReadOnlyList<PlaybackSeekRange> SeekableRanges { get; init; } = [];
    public string SeekRangeSource => SeekableRanges.Count > 0 ? "cache" : "none";
    public double CacheDuration { get; init; }
    public double CacheBufferingState { get; init; }
}

public sealed record PlaybackSeekRange(double Start, double End);
