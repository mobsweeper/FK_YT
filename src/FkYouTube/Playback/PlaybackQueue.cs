namespace FkYouTube.Playback;

public static class PlaybackQueue
{
    // Position, rather than video ID, preserves intentional playlist duplicates.
    public static int NextIndex(IReadOnlyList<VideoItem> items,int currentIndex,bool hideMembers)
    {
        // -1 is the explicit before-first position used by "play list".
        if(currentIndex < -1 || currentIndex>=items.Count)return -1;
        for(var i=currentIndex+1;i<items.Count;i++)
            if(items[i].Kind=="video" && (!hideMembers || !items[i].MembersOnly))return i;
        return -1;
    }
}
