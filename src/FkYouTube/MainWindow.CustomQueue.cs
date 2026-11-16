using System.Windows;
using System.Windows.Controls;

namespace FkYouTube;

public partial class MainWindow
{
    private enum CustomQueueState { Pending, Loading, Held }
    private sealed class CustomQueueEntry(VideoItem item)
    {
        public VideoItem Item { get; set; } = item;
        public CustomQueueState State { get; set; }
    }

    // Occurrences are separate from the browsing feed and from server recommendation actions.
    private readonly List<CustomQueueEntry> _customQueue = [];
    private readonly HashSet<string> _queueRemovedVideos = [];
    private CustomQueueEntry? _activeCustomQueueEntry;
    private bool _customQueueActive;
    private bool UsesPlaybackFeed => !_customQueueActive && HasPlaybackList;
    private bool UsesAttachedPlaybackFeed => !_customQueueActive && PlaybackFeedAttached;
    private static bool IsVideoQueueItem(VideoItem item) => item.Kind == "video" &&
        Services.YouTubeService.TryVideoUrl(item.Url, out _, out _);
    private bool IsCustomQueuePlayable(CustomQueueEntry entry) => entry.State == CustomQueueState.Pending &&
        (!_settings.HideMembersOnly || !entry.Item.MembersOnly);
    private CustomQueueEntry? NextCustomQueueEntry() => _customQueue.FirstOrDefault(IsCustomQueuePlayable);

    private IEnumerable<VideoItem> RunnableQueueItems()
    {
        if (_customQueueActive && NextCustomQueueEntry() is not null)
        {
            foreach (var entry in _customQueue.Where(IsCustomQueuePlayable)) yield return entry.Item;
            yield break;
        }
        if (UsesPlaybackFeed)
        {
            var found = false;
            foreach (var item in PlaybackItems.Skip(_playingIndex + 1).Where(IsPlayableQueueItem))
            { found = true; yield return item; }
            if (found || UsesAttachedPlaybackFeed && _hasFeed && !_feedExhausted) yield break;
        }
        foreach (var item in _relatedItems.Where(item => IsPlayableQueueItem(item) &&
                     item.Id != _current?.Id && !_autoplaySeen.Contains(item.Id))) yield return item;
    }

    private VideoItem? NextQueueCandidate() => RunnableQueueItems().FirstOrDefault();

    private void VideoQueueMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var item = menu.DataContext as VideoItem;
        var entry = item is null ? null : _customQueue.FirstOrDefault(v => ReferenceEquals(v.Item, item));
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            if (child.Tag is not string action || !action.StartsWith("queue_", StringComparison.Ordinal)) continue;
            child.IsEnabled = !AccountWorkBlocked && item is not null && IsVideoQueueItem(item) &&
                entry?.State != CustomQueueState.Loading;
            if (action == "queue_remove")
                child.IsEnabled &= entry is not null || item is not null && _upNext.Any(candidate => ReferenceEquals(candidate, item));
        }
    }

    private async void QueuePlayNext_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is MenuItem { DataContext: VideoItem item }) await AddCustomQueueAsync(item, playNext: true);
    }

    private async void QueueAppend_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is MenuItem { DataContext: VideoItem item }) await AddCustomQueueAsync(item, playNext: false);
    }

    private async Task AddCustomQueueAsync(VideoItem item, bool playNext)
    {
        if (AccountWorkBlocked || !IsVideoQueueItem(item)) return;
        var entry = _customQueue.FirstOrDefault(v => ReferenceEquals(v.Item, item));
        if (entry?.State == CustomQueueState.Loading) return;
        _queueRemovedVideos.Remove(item.Id);
        _customQueueActive = true;
        if (entry is null)
            entry = new(item with { LocalDetail = null, LocalDetailFactory = null });
        else
        {
            _customQueue.Remove(entry);
            entry.Item = entry.Item with { LocalDetail = null, LocalDetailFactory = null };
            entry.State = CustomQueueState.Pending;
        }
        if (playNext) _customQueue.Insert(0, entry); else _customQueue.Add(entry);
        UpdateUpNext();
        SetStatus(() => playNext
            ? UiText.T("바로 다음에 재생합니다. 지정한 목록이 끝나면 마지막 영상의 추천으로 이어집니다.",
                "Queued to play next. After your queue, recommendations follow the last video.",
                "次に再生します。指定した一覧の後は、最後の動画のおすすめに続きます。")
            : UiText.T("대기열 끝에 추가했습니다. 지정한 목록이 끝나면 마지막 영상의 추천으로 이어집니다.",
                "Added to the queue. After your queue, recommendations follow the last video.",
                "キューの最後に追加しました。一覧の後は、最後の動画のおすすめに続きます。"));
        if (_current is null && NextCustomQueueEntry() is { } first)
            await PlayVideoAsync(first.Item, explicitStart: 0);
        else if (_snapshot is { Ended: true } && _videoCts is null && (playNext || _settings.AutoPlayNext))
            await AutoPlayNextAsync(_videoGeneration, _autoplayEpoch, manual: true);
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (AccountWorkBlocked || sender is not MenuItem { DataContext: VideoItem item }) return;
        var entry = _customQueue.FirstOrDefault(v => ReferenceEquals(v.Item, item));
        if (entry?.State == CustomQueueState.Loading) return;
        if (entry is not null) _customQueue.Remove(entry);
        else if (_upNext.Any(candidate => ReferenceEquals(candidate, item))) _queueRemovedVideos.Add(item.Id);
        else return;
        UpdateUpNext();
        SetStatus(() => UiText.T("이 재생 대기열에서 제거했습니다. YouTube 추천 설정은 바꾸지 않았습니다.",
            "Removed from this queue. Your YouTube recommendation settings were not changed.",
            "この再生キューから削除しました。YouTube のおすすめ設定は変更していません。"));
    }

    private void ClearCustomQueue_Click(object sender, RoutedEventArgs e)
    {
        if (AccountWorkBlocked) return;
        _customQueue.RemoveAll(entry => entry.State != CustomQueueState.Loading);
        // Keep the custom sequence's recommendation tail, rather than reviving an old feed.
        UpdateUpNext();
    }

    private CustomQueueEntry? BeginCustomQueuePlayback(VideoItem item, bool preserveWatch)
    {
        var entry = preserveWatch ? _activeCustomQueueEntry :
            _customQueue.FirstOrDefault(v => ReferenceEquals(v.Item, item));
        if (!preserveWatch && _activeCustomQueueEntry is { State: CustomQueueState.Loading } previous &&
            !ReferenceEquals(previous, entry)) HoldCustomQueueEntry(previous);
        if (entry is not null && !_customQueue.Contains(entry)) entry = null;
        _activeCustomQueueEntry = entry;
        if (entry is not null) entry.State = CustomQueueState.Loading;
        return entry;
    }

    private void FinishCustomQueuePlayback(CustomQueueEntry? entry, int generation, bool accepted)
    {
        if (entry is null || generation != _videoGeneration || !_customQueue.Contains(entry)) return;
        if (accepted)
        {
            _customQueue.Remove(entry);
            if (ReferenceEquals(_activeCustomQueueEntry, entry)) _activeCustomQueueEntry = null;
        }
        else HoldCustomQueueEntry(entry);
        if (!_closing) UpdateUpNext();
    }

    private static void HoldCustomQueueEntry(CustomQueueEntry entry)
    {
        entry.State = CustomQueueState.Held;
        entry.Item = entry.Item with
        {
            LocalDetailFactory = () => UiText.T("재생 보류 · 우클릭으로 다시 대기열에 넣기",
                "Held · Right-click to queue again", "再生保留・右クリックでキューに戻す")
        };
    }

    private void ResetCustomQueue()
    {
        _customQueue.Clear(); _queueRemovedVideos.Clear();
        _activeCustomQueueEntry = null; _customQueueActive = false;
    }
}
