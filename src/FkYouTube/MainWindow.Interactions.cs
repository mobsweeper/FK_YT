using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FkYouTube.Services;
using FkYouTube.Playback;

namespace FkYouTube;

public partial class MainWindow
{
    private Action<string> _copyShareText = Clipboard.SetText;
    private CancellationTokenSource? _ratingReadCts, _ratingSendCts, _dislikeStatsCts;
    private readonly HashSet<Task> _ratingTasks = [];
    private VideoRating? _videoRating;
    private DislikeStats? _dislikeStats;
    private int _ratingRevision;
    private bool _ratingBusy, _ratingsAccountBusy;
    // Explicit selection owns its result even when the popup is reopened.
    private CancellationTokenSource? _subtitleMenuCts;
    private CancellationTokenSource? _subtitleControlsHoldCts;
    private CancellationTokenSource? _subtitleMenuReadCts, _subtitleMenuControlsHoldCts;
    private ContextMenu? _subtitleMenu;
    private Window? _subtitlePopupOwner;
    private FrameworkElement? _subtitlePopupAnchor;
    private bool _replacingSubtitleMenu;
    private ContextMenu? _subtitleMenuReopening;
    private int _subtitlePopupRevision;
    private int _subtitleSelectionRevision,_subtitlePreferenceRevision;
    private bool SubtitlePreferencePending=>_subtitleMenuCts is {IsCancellationRequested:false};
    private readonly HashSet<Task> _subtitleTasks = [];
    private string? _currentDescription;
    private bool _descriptionFailed;
    private IReadOnlyList<AvailableSubtitle> _subtitleCatalog = [];
    private string? _subtitleCatalogKey;
    private DateTimeOffset _subtitleCatalogExpires;

    private readonly List<Action> _subtitleTextRefresh = [];
    private static readonly object SubtitleCatalogLoadingMarker = new();
    private Func<string>? _ratingUiText, _dislikeStatsUiText, _subtitleToolTipText, _authenticationUiText, _subtitleLoadErrorUiText;

    private void SetRatingUiText(Func<string> text)
    { _ratingUiText = text; RatingState.Text = text(); }
    private void SetDislikeStatsUiText(Func<string> text)
    { _dislikeStatsUiText = text; DislikeStatsState.Text = text(); }
    private void SetSubtitleLoadError(Func<string> text)
    { _subtitleLoadErrorUiText = text; _subtitleLoadError = text(); SetStatus(text); }

    private void RefreshInteractionUiText()
    {
        SetVideoDescription(_currentDescription, _descriptionFailed);
        if (_ratingUiText is {} ratingText) RatingState.Text = ratingText();
        if (_dislikeStatsUiText is {} statsText) DislikeStatsState.Text = statsText();
        if (_subtitleToolTipText is {} subtitleText) SubtitleButton.ToolTip = subtitleText();
        if (_authenticationUiText is {} authenticationText) AuthenticationNoticeText.Text = authenticationText();
        if (_subtitleLoadError is not null && _subtitleLoadErrorUiText is {} errorText) _subtitleLoadError = errorText();
        foreach (var refresh in _subtitleTextRefresh.ToArray()) refresh();
        UpdateRatingControls();
    }

    private MenuItem LocalizedSubtitleMenuItem(Func<string> header, bool enabled = true, Func<string>? tooltip = null)
    {
        var item = new MenuItem { IsEnabled = enabled };
        void Refresh()
        {
            item.Header = header();
            if (tooltip is not null) item.ToolTip = tooltip();
        }
        Refresh();
        _subtitleTextRefresh.Add(Refresh);
        return item;
    }

    private void ResetSubtitleCatalog()
    { _subtitleCatalog = []; _subtitleCatalogKey = null; _subtitleCatalogExpires = default; }

    private void ResetVideoDescription()
    {
        CloseDescriptionPopup();
        SetVideoDescription(null);
    }

    private void SetVideoDescription(string? text, bool failed = false)
    {
        _currentDescription = text;
        _descriptionFailed = failed;
        if (DescriptionText is null) return;
        DescriptionText.Text = failed ? UiText.T("영상 설명을 불러오지 못했습니다. 새로고침 후 다시 확인해 주세요.","Could not load the video description. Refresh and try again.","動画の説明を読み込めませんでした。更新してからもう一度確認してください。") :
            text is null ? UiText.T("설명을 불러오는 중입니다…","Loading description…","説明を読み込み中…") :
            string.IsNullOrWhiteSpace(text) ? UiText.T("이 영상에는 설명이 없습니다.","This video has no description.","この動画には説明がありません。") : text;
    }

    private void CloseDescriptionPopup()
    {
        if (DescriptionPopup is not null) DescriptionPopup.IsOpen = false;
    }

    private void DescriptionPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DescriptionPopup?.IsOpen != true) return;
        e.Handled = true;
        CloseDescriptionPopup();
        PlayingTitleButton.Focus();
    }

    private static bool IsInteractionFailure(Exception error) => error is IOException or InvalidDataException
        or InvalidOperationException or TimeoutException or JsonException or Win32Exception or System.Net.Http.HttpRequestException;

    private void PlayingTitle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || _ratingsAccountBusy || _current is null) return;
        DescriptionPopup.IsOpen = !DescriptionPopup.IsOpen;
    }

    private void DescriptionClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseDescriptionPopup();
        PlayingTitleButton.Focus();
    }

    private void ShowAuthenticationNotice(string message, bool canCancel)=>ShowAuthenticationNotice(()=>message,canCancel);

    private void ShowAuthenticationNotice(Func<string> message, bool canCancel)
    {
        _authenticationUiText = message;
        AuthenticationNoticeText.Text = message();
        AuthenticationSkipCancelButton.IsEnabled = canCancel && !_closing;
        AuthenticationNoticePanel.Visibility = Visibility.Visible;
    }

    private void HideAuthenticationNotice()
    {
        AuthenticationNoticePanel.Visibility = Visibility.Collapsed;
        _authenticationUiText = null;
        AuthenticationNoticeText.Text = "";
        AuthenticationSkipCancelButton.IsEnabled = false;
    }

    private void AuthenticationSkipCancel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || AuthenticationNoticePanel.Visibility != Visibility.Visible || !AuthenticationSkipCancelButton.IsEnabled) return;
        CancelAuthenticationSkip();
    }

    private async Task TrackSubtitleTaskAsync(Task task)
    {
        _subtitleTasks.Add(task);
        try { await task; }
        finally { _subtitleTasks.Remove(task); }
    }

    private void CloseSubtitleMenu()
    {
        _subtitlePopupRevision++;
        _subtitleMenuReopening=null;
        DetachSubtitlePopupOwner();
        _subtitleMenuReadCts?.Cancel();
        _subtitleMenuReadCts = null;
        _subtitleMenuControlsHoldCts = null;
        if (_subtitleMenu is { } menu) {menu.IsOpen = false;menu.PlacementTarget=null;}
        _subtitleMenu = null;
        _subtitleTextRefresh.Clear();
    }

    private void CloseSubtitlePopup()=>CloseSubtitleMenu();

    private void CloseMainSubtitlePopup()
    {
        if(_subtitlePopupOwner is null || ReferenceEquals(_subtitlePopupOwner,this))CloseSubtitleMenu();
    }

    private void AttachSubtitlePopupOwner(ContextMenu menu)
    {
        DetachSubtitlePopupOwner();
        if(menu.PlacementTarget is not FrameworkElement anchor || Window.GetWindow(anchor) is not {} owner)return;
        _subtitlePopupOwner=owner;_subtitlePopupAnchor=anchor;
        owner.Deactivated+=SubtitlePopupOwnerChanged;owner.LocationChanged+=SubtitlePopupOwnerChanged;
        owner.StateChanged+=SubtitlePopupOwnerChanged;owner.Closed+=SubtitlePopupOwnerChanged;
        owner.SizeChanged+=SubtitlePopupOwnerSizeChanged;
        anchor.Unloaded+=SubtitlePopupAnchorUnloaded;anchor.IsVisibleChanged+=SubtitlePopupAnchorVisibilityChanged;
    }

    private void DetachSubtitlePopupOwner()
    {
        if(_subtitlePopupOwner is {} owner)
        {
            owner.Deactivated-=SubtitlePopupOwnerChanged;owner.LocationChanged-=SubtitlePopupOwnerChanged;
            owner.StateChanged-=SubtitlePopupOwnerChanged;owner.Closed-=SubtitlePopupOwnerChanged;
            owner.SizeChanged-=SubtitlePopupOwnerSizeChanged;
        }
        if(_subtitlePopupAnchor is {} anchor)
        {anchor.Unloaded-=SubtitlePopupAnchorUnloaded;anchor.IsVisibleChanged-=SubtitlePopupAnchorVisibilityChanged;}
        _subtitlePopupOwner=null;_subtitlePopupAnchor=null;
    }

    private void SubtitlePopupOwnerChanged(object? sender,EventArgs e)=>CloseSubtitleMenu();
    private void SubtitlePopupOwnerSizeChanged(object sender,SizeChangedEventArgs e)=>CloseSubtitleMenu();
    private void SubtitlePopupAnchorUnloaded(object sender,RoutedEventArgs e)=>CloseSubtitleMenu();
    private void SubtitlePopupAnchorVisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)
    {if(e.NewValue is false)CloseSubtitleMenu();}

    private void CancelSubtitleOperations()
    {
        _subtitleSelectionRevision++;
        _captionCts?.Cancel();
        CloseSubtitleMenu();
        _subtitleMenuCts?.Cancel();
        _subtitleMenuCts = null;
        _subtitleControlsHoldCts = null;
    }

    private bool SubtitleControlsBusy => _subtitleMenu?.IsOpen == true || _subtitleMenuReopening is not null ||
        _subtitleControlsHoldCts is { IsCancellationRequested: false } ||
        _subtitleMenuControlsHoldCts is { IsCancellationRequested: false };

    private async void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _ratingsAccountBusy || !_playerStarted || _current is null || sender is not Button anchor) return;
        await TrackSubtitleTaskAsync(OpenSubtitleMenuAsync(anchor));
    }

    private async Task OpenSubtitleMenuAsync(Button anchor)
    {
        CloseSubtitleMenu();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _subtitleMenuReadCts = cts;
        _subtitleMenuControlsHoldCts = cts;
        var generation = _videoGeneration; var video = _current!; var account = RatingAccountStamp;
        var key = video.Id + ":" + account;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MaxHeight = 360,
            MaxWidth = 520, Style = (Style)FindResource("SubtitleMenuStyle") };
        _subtitleMenu = menu;
        AttachSubtitlePopupOwner(menu);
        menu.Closed += (_, _) =>
        {
            if (!_replacingSubtitleMenu && !ReferenceEquals(menu,_subtitleMenuReopening) && ReferenceEquals(menu, _subtitleMenu)) _subtitleTextRefresh.Clear();
        };
        bool Current() => !_closing && !_ratingsAccountBusy && !cts.IsCancellationRequested && generation == _videoGeneration &&
            _current?.Id == video.Id && account == RatingAccountStamp && ReferenceEquals(menu, _subtitleMenu);
        var loadedTracks = false;
        try
        {
            var selection = await _player.GetSubtitlesAsync(cts.Token);
            if (!Current()) return;
            PopulateSubtitleMenu(menu, selection, generation);
            loadedTracks = true;
            var needsCatalog = _subtitleCatalogKey != key || DateTimeOffset.UtcNow >= _subtitleCatalogExpires;
            if (needsCatalog)
            {
                menu.Items.Add(new Separator());
                var loading = LocalizedSubtitleMenuItem(() => UiText.T("영상의 모든 자막 언어를 불러오는 중…","Loading all subtitle languages…","動画のすべての字幕言語を読み込み中…"), false);
                loading.Tag = SubtitleCatalogLoadingMarker;
                menu.Items.Add(loading);
            }
            else AppendSubtitleCatalog(menu, _subtitleCatalog, selection.MediaGeneration, generation);
            ShowSubtitleMenuAtAnchor(menu);
            // The popup itself now keeps its controls available. Catalog work
            // may continue after dismissal without keeping the tray on screen.
            if (ReferenceEquals(_subtitleMenuControlsHoldCts, cts)) _subtitleMenuControlsHoldCts = null;
            if (needsCatalog)
            {
                var catalog = await _youtube.GetSubtitleCatalogAsync(video.Url, cts.Token, _settings.Incognito);
                if (!Current()) return;
                _subtitleCatalog = catalog; _subtitleCatalogKey = key;
                _subtitleCatalogExpires = DateTimeOffset.UtcNow.AddMinutes(5);
                selection = await _player.GetSubtitlesAsync(cts.Token);
                if (!Current()) return;
                // Prepare the complete grouping away from the visible popup, then
                // commit and reopen once at the current button position.
                var ready = new ContextMenu();
                PopulateSubtitleMenu(ready, selection, generation);
                AppendSubtitleCatalog(ready, _subtitleCatalog, selection.MediaGeneration, generation);
                var items = ready.Items.Cast<object>().ToArray();
                ready.Items.Clear();
                ReplaceSubtitleMenuContents(menu, items);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsInteractionFailure(ex))
        {
            if (Current())
            {
                var items = loadedTracks ? menu.Items.Cast<object>().ToList() : new List<object>();
                if (items.Count > 0 && items[^1] is MenuItem { IsEnabled: false } loading &&
                    ReferenceEquals(loading.Tag, SubtitleCatalogLoadingMarker)) items.RemoveAt(items.Count - 1);
                items.Add(LocalizedSubtitleMenuItem(() => loadedTracks ? UiText.T("전체 언어 조회 실패 · 자막 메뉴를 다시 열어 주세요.","Could not load all languages · Reopen the subtitles menu.","すべての言語を取得できませんでした · 字幕メニューを開き直してください。") : UiText.T("재생이 시작된 뒤 다시 눌러 주세요.","Try again after playback starts.","再生が始まってからもう一度押してください。"), false));
                ReplaceSubtitleMenuContents(menu, items);
                if (!loadedTracks) ShowSubtitleMenuAtAnchor(menu);
            }
        }
        finally
        {
            if (ReferenceEquals(_subtitleMenuControlsHoldCts, cts)) _subtitleMenuControlsHoldCts = null;
            if (ReferenceEquals(_subtitleMenuReadCts, cts)) _subtitleMenuReadCts = null;
        }
    }

    private static void ShowSubtitleMenuAtAnchor(ContextMenu menu)
    {
        if (menu.PlacementTarget is not FrameworkElement anchor || !anchor.IsLoaded || !anchor.IsVisible ||
            Window.GetWindow(anchor) is not {IsVisible:true} owner || owner.WindowState==WindowState.Minimized ||
            PresentationSource.FromVisual(anchor) is not HwndSource anchorSource || anchorSource.IsDisposed ||
            !ReferenceEquals(PresentationSource.FromVisual(owner),anchorSource)) return;
        anchor.UpdateLayout();
        if(anchor.ActualWidth<=0 || anchor.ActualHeight<=0)return;
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementRectangle=new Rect(new Point(0,0),anchor.RenderSize);
        menu.HorizontalOffset=0;menu.VerticalOffset=0;
        menu.IsOpen = true;
    }

    private void ReplaceSubtitleMenuContents(ContextMenu menu, IReadOnlyList<object> items)
    {
        var reopen = menu.IsOpen;
        var pending=ReferenceEquals(menu,_subtitleMenuReopening);
        var revision=pending?_subtitlePopupRevision:++_subtitlePopupRevision;
        var anchor=menu.PlacementTarget;
        _replacingSubtitleMenu = true;
        try
        {
            if(reopen)
            {
                _subtitleMenuReopening=menu;
                RoutedEventHandler? closed=null;
                closed=(_,_)=>
                {
                    menu.Closed-=closed;
                    // Wait for the actual Popup close, including its animation,
                    // before layout and native placement of the rebuilt contents.
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,new Action(()=>
                    {
                        if(!_closing && revision==_subtitlePopupRevision && ReferenceEquals(menu,_subtitleMenu) &&
                            ReferenceEquals(menu,_subtitleMenuReopening) && ReferenceEquals(menu.PlacementTarget,anchor))
                            ShowSubtitleMenuAtAnchor(menu);
                        if(ReferenceEquals(menu,_subtitleMenuReopening))_subtitleMenuReopening=null;
                        if(!menu.IsOpen && ReferenceEquals(menu,_subtitleMenu))_subtitleTextRefresh.Clear();
                    }));
                };
                menu.Closed+=closed;menu.IsOpen=false;
            }
            menu.Items.Clear();
            foreach (var item in items) menu.Items.Add(item);
        }
        finally
        {
            _replacingSubtitleMenu = false;
            if (!reopen && !pending) _subtitleTextRefresh.Clear();
        }
    }

    private void AppendSubtitleCatalog(ContextMenu menu, IReadOnlyList<AvailableSubtitle> catalog, long mediaGeneration, int generation)
    {
        // mpv's loaded tracks have no reliable manual/automatic flag. Keep their
        // fast selection and current check in a fallback submenu, without calling
        // an unknown track an official caption or repeating it above the catalog.
        var loaded = menu.Items.OfType<MenuItem>().Where(item => item.Tag is long).ToArray();
        while (menu.Items.Count > 1) menu.Items.RemoveAt(1);
        foreach (var automatic in new[] { false, true })
        {
            var captions = catalog.Where(item => item.Automatic == automatic)
                .OrderBy(item => SubtitleLanguagePriority(item.Language))
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            menu.Items.Add(new Separator());
            menu.Items.Add(LocalizedSubtitleMenuItem(() => automatic ? UiText.T("기계번역 · 자동 생성","Machine translated · Auto-generated","機械翻訳 · 自動生成") : UiText.T("공식 자막 · 제작자 제공","Official subtitles · Creator-provided","公式字幕 · 投稿者提供"), false));
            if (captions.Length == 0)
                menu.Items.Add(LocalizedSubtitleMenuItem(() => UiText.T("제공되는 자막이 없습니다.","No subtitles available.","利用できる字幕がありません。"), false));
            foreach (var caption in captions)
            {
                var option = LocalizedSubtitleMenuItem(() => caption.DisplayLabel, tooltip: () => caption.DisplayLabel + (automatic ? UiText.T(" · 자동 생성/기계번역"," · Auto-generated/machine translated"," · 自動生成/機械翻訳") : UiText.T(" · 제작자 제공 자막"," · Creator-provided subtitles"," · 投稿者提供の字幕")));
                option.Tag = caption.Language;
                option.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (_closing || _ratingsAccountBusy || !_playerStarted || generation != _videoGeneration) return;
                    await TrackSubtitleTaskAsync(SelectSubtitleFromMenuAsync(
                        token => _player.SelectExternalSubtitleAsync(caption, mediaGeneration, token), caption.DisplayLabel, generation, caption.Language, mediaGeneration));
                };
                menu.Items.Add(option);
            }
        }
        if (loaded.Length > 0)
        {
            menu.Items.Add(new Separator());
            var fallback = LocalizedSubtitleMenuItem(() => UiText.T("불러온 자막","Loaded subtitles","読み込み済みの字幕"), tooltip: () => UiText.T("이미 재생기에 불러온 자막 · 현재 선택 표시","Subtitles already loaded in the player · Current selection shown","再生機に読み込み済みの字幕 · 現在の選択を表示"));
            foreach (var item in loaded) fallback.Items.Add(item);
            menu.Items.Add(fallback);
        }
    }

    private static int SubtitleLanguagePriority(string? language) => language?.Split('-')[0].ToLowerInvariant() switch
    { "en" or "eng" => 0, "ko" or "kor" => 1, "ja" or "jpn" => 2, _ => 3 };

    private async Task SelectSubtitleFromMenuAsync(Func<CancellationToken, Task> select, string? label, int generation, string? language, long mediaGeneration)
    {
        if(_closing || generation!=_videoGeneration || mediaGeneration!=_player.Snapshot.MediaGeneration)return;
        _subtitleLoadError=null;_subtitleLoadErrorUiText=null;
        CancelSubtitleOperations();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _subtitleMenuCts = cts;
        _subtitleControlsHoldCts = cts;
        var account = RatingAccountStamp;
        bool Current() => !_closing && !_ratingsAccountBusy && ReferenceEquals(_subtitleMenuCts, cts) &&
            generation == _videoGeneration && mediaGeneration == _player.Snapshot.MediaGeneration && account == RatingAccountStamp;
        var changesLanguage = label is not null && !string.Equals(_player.PreferredSubtitleLanguage, language, StringComparison.OrdinalIgnoreCase);
        if(changesLanguage)_subtitlePreferenceRevision++;
        try
        {
            SetStatus(()=>label is null ? UiText.T("자막을 끄는 중…","Turning subtitles off…","字幕をオフにしています…") : UiText.T("선택한 자막을 불러오는 중…","Loading selected subtitles…","選択した字幕を読み込み中…"));
            // Invalidate prepared captions before selection can change the preference.
            // EOF may start the next video as soon as the player's selection task completes.
            if (changesLanguage)
            {
                _prefetchCts?.Cancel(); _prefetchAttempt = null; _preparedItem = null;
                _preparedResolutions = []; _preparedCaptions = []; SetPrefetchUiText(()=>"");
                await ClearPrefetchSafelyAsync();
                if (!Current() || cts.IsCancellationRequested) return;
            }
            await select(cts.Token);
            if (!Current() || cts.IsCancellationRequested) return;
            _subtitleToolTipText = () => label is null ? UiText.T("자막 꺼짐","Subtitles off","字幕オフ") : UiText.F("선택한 자막: {0}","Selected subtitles: {0}","選択した字幕: {0}",label);
            SubtitleButton.ToolTip = _subtitleToolTipText();
            SetStatus(()=>label is null ? UiText.T("자막을 껐습니다.","Subtitles turned off.","字幕をオフにしました。") : UiText.F("자막: {0}","Subtitles: {0}","字幕: {0}",label));
        }
        catch (OperationCanceledException)
        {
            if (Current() && !_player.HasActiveMedia(mediaGeneration))
                SetSubtitleLoadError(()=>UiText.T("자막을 불러오지 못했고 재생기에 연결할 수 없습니다. 영상을 다시 재생해 주세요.","Could not load subtitles or connect to the player. Play the video again.","字幕を読み込めず、再生機に接続できません。動画を再生し直してください。"));
        }
        catch (Exception ex) when (IsInteractionFailure(ex))
        {
            if (Current())
            {
                ResetSubtitleCatalog();
                var mediaMissing = !_player.HasActiveMedia(mediaGeneration);
                SetSubtitleLoadError(()=>mediaMissing ? UiText.T("자막을 불러오지 못했고 재생기에 연결할 수 없습니다. 영상을 다시 재생해 주세요.","Could not load subtitles or connect to the player. Play the video again.","字幕を読み込めず、再生機に接続できません。動画を再生し直してください。") :
                    UiText.T("자막을 선택하지 못했습니다. 자막 목록을 다시 열어 주세요.","Could not select subtitles. Reopen the subtitles list.","字幕を選択できませんでした。字幕一覧を開き直してください。"));
            }
        }
        finally
        {
            var resumePrefetch = Current() && !cts.IsCancellationRequested;
            if (ReferenceEquals(_subtitleControlsHoldCts, cts)) _subtitleControlsHoldCts = null;
            if (ReferenceEquals(_subtitleMenuCts, cts)) _subtitleMenuCts = null;
            if (resumePrefetch) _ = EnsurePrefetchAsync();
        }
    }

    private void PopulateSubtitleMenu(ContextMenu menu, SubtitleSelection selection, int generation)
    {
        menu.Items.Clear();
        _subtitleTextRefresh.Clear();
        void Add(Func<string> labelText, long? id, bool selected)
        {
            var item = LocalizedSubtitleMenuItem(() => (selected ? "✓ " : "　 ") + labelText(), tooltip: labelText);
            item.Tag = id;
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                if (_closing || _ratingsAccountBusy || !_playerStarted || generation != _videoGeneration) return;
                await TrackSubtitleTaskAsync(SelectSubtitleFromMenuAsync(
                    token => _player.SelectSubtitleAsync(id, selection.MediaGeneration, token), id is null ? null : labelText(), generation,
                    selection.Tracks.FirstOrDefault(track => track.Id == id)?.Language, selection.MediaGeneration));
            };
            menu.Items.Add(item);
        }
        Add(()=>UiText.T("자막 끄기","Subtitles off","字幕をオフにする"), null, selection.Off);
        if (selection.Tracks.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var track in selection.Tracks.OrderBy(item => SubtitleLanguagePriority(item.Language))
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase))
                Add(()=>track.Label, track.Id, !selection.Off && track.Selected);
        }
        else menu.Items.Add(LocalizedSubtitleMenuItem(() => UiText.T("현재 불러온 자막이 없습니다.","No subtitles are currently loaded.","現在読み込まれている字幕はありません。"), false));
    }

    private string RatingAccountStamp => $"{_settings.AccountKey}:{_session.SelectedChannel?.Id}:{_settings.Incognito}";
    // A metadata read begun during a write may have observed the pre-write rating.
    private int RelatedRatingRevision => _ratingBusy ? -1 : _ratingRevision;

    private async Task TrackRatingTask(Task task)
    {
        _ratingTasks.Add(task);
        try { await task; }
        finally { _ratingTasks.Remove(task); }
    }

    private async void ObserveRatingTask(Task task)
    {
        try { await TrackRatingTask(task); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Trace.WriteLine("Background rating failure: " + error.GetType().Name);
            if (!_closing) SetStatus(()=>UiText.T("평가 정보를 확인하지 못했습니다. 다시 시도해 주세요.","Could not check rating information. Try again.","評価情報を確認できませんでした。もう一度お試しください。"));
        }
    }

    private void ResetVideoRatings()
    {
        _ratingRevision++;
        _ratingReadCts?.Cancel(); _ratingSendCts?.Cancel(); _dislikeStatsCts?.Cancel();
        _videoRating = null; _dislikeStats = null;
        SetRatingUiText(() => _settings.Incognito ? UiText.T("시크릿 모드에서는 평가할 수 없습니다.","Ratings are unavailable in Incognito mode.","シークレットモードでは評価できません。") : UiText.T("내 평가 확인 대기","Your rating has not been checked","自分の評価の確認待ち"));
        SetDislikeStatsUiText(() => _settings.Incognito ? UiText.T("수치 보기를 누르면 영상 ID로 RYD를 조회합니다.","Show counts queries RYD using the video ID.","「数値を表示」を押すと動画 ID で RYD に問い合わせます。") : UiText.T("RYD 수치 조회 대기","RYD counts have not been checked","RYD 数値の取得待ち"));
        UpdateRatingControls();
    }

    private void UpdateRatingControls()
    {
        if (LikeButton is null) return;
        var hasVideo = !_closing && _current is not null;
        var authenticated = !_settings.Incognito && _session.IsConnected && _session.SelectedChannel is not null;
        var selectedLike = _videoRating?.Value == "like";
        var selectedDislike = _videoRating?.Value == "dislike";
        LikeButton.Content = UiText.F("{0}👍 {1}","{0}👍 {1}","{0}👍 {1}",selectedLike ? "✓ " : "",
            _dislikeStats is null ? "—" : UiText.F("{0:N0}","{0:N0}","{0:N0}",_dislikeStats.Likes));
        DislikeButton.Content = UiText.F("{0}👎 {1}","{0}👎 {1}","{0}👎 {1}",selectedDislike ? "✓ " : "",
            _dislikeStats is null ? "—" : "≈ " + UiText.F("{0:N0}","{0:N0}","{0:N0}",_dislikeStats.Dislikes));
        foreach (var (button, selected) in new[] { (LikeButton, selectedLike), (DislikeButton, selectedDislike) })
        {
            if (selected) { button.Background = (Brush)FindResource("Accent"); button.Foreground = (Brush)FindResource("Bg"); }
            else { button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.ForegroundProperty); }
            button.IsEnabled = hasVideo && authenticated && !_ratingsAccountBusy && !_ratingBusy &&
                _ratingReadCts is null && _videoRating is { CanRate: true, Value: not "unknown" };
        }
        RatingRefreshButton.IsEnabled = hasVideo && authenticated && !_ratingsAccountBusy && !_ratingBusy && _ratingReadCts is null;
        DislikeStatsButton.IsEnabled = hasVideo && !_ratingsAccountBusy && _dislikeStatsCts is null;
        DislikeStatsButton.Content = _dislikeStats is null ? UiText.T("수치 보기","Show counts","数値を表示") : UiText.T("수치 확인","Check counts","数値を確認");
        if (!authenticated) SetRatingUiText(() => _settings.Incognito ? UiText.T("시크릿 모드에서는 평가할 수 없습니다.","Ratings are unavailable in Incognito mode.","シークレットモードでは評価できません。") : UiText.T("평가하려면 YouTube 채널을 연결하세요.","Connect a YouTube channel to rate videos.","評価するには YouTube チャンネルを接続してください。"));
    }

    private bool RatingResultIsCurrent(VideoItem item, int generation, string account, int revision) =>
        !_closing && !_ratingsAccountBusy && generation == _videoMetadataGeneration &&
        revision == _ratingRevision && account == RatingAccountStamp && _current?.Id == item.Id;

    private void ApplyVideoRating(VideoRating? rating, VideoItem item, int generation, string account, int revision)
    {
        if (!RatingResultIsCurrent(item, generation, account, revision) || rating is null ||
            !YouTubeService.TryVideoUrl(item.Url, out var id, out _) || rating.VideoId != id) return;
        _videoRating = rating;
        SetRatingUiText(() => rating.Value switch
        {
            "like" => UiText.T("내 평가: 좋아요 · 다시 누르면 취소","Your rating: Like · Click again to remove","自分の評価: 高評価 · もう一度押すと取り消し"),
            "dislike" => UiText.T("내 평가: 싫어요 · 다시 누르면 취소","Your rating: Dislike · Click again to remove","自分の評価: 低評価 · もう一度押すと取り消し"),
            "none" when rating.CanRate => UiText.T("선택한 YouTube 채널에 반영됩니다.","Applied to the selected YouTube channel.","選択した YouTube チャンネルに反映されます。"),
            _ => UiText.T("내 평가를 확인하지 못했습니다. ‘내 평가 확인’을 눌러 주세요.","Could not check your rating. Click “Check my rating”.","自分の評価を確認できませんでした。「自分の評価を確認」を押してください。")
        });
        UpdateRatingControls();
    }

    private async void RatingRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _ratingsAccountBusy || _ratingBusy || _current is null || _settings.Incognito ||
            !_session.IsConnected || _session.SelectedChannel is null) return;
        await TrackRatingTask(LoadCurrentRatingAsync());
    }

    private async Task LoadCurrentRatingAsync()
    {
        var item = _current!; var generation = _videoMetadataGeneration; var account = RatingAccountStamp;
        var revision = ++_ratingRevision;
        _ratingReadCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _ratingReadCts = cts; SetRatingUiText(() => UiText.T("내 평가를 확인하는 중…","Checking your rating…","自分の評価を確認中…")); UpdateRatingControls();
        try
        {
            var result = await _youtube.GetRatingAsync(item.Url, cts.Token, _settings.Incognito);
            if (!cts.IsCancellationRequested) ApplyVideoRating(result, item, generation, account, revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsInteractionFailure(ex))
        {
            if (RatingResultIsCurrent(item, generation, account, revision))
            { _videoRating = null; SetRatingUiText(() => UiText.T("내 평가를 확인하지 못했습니다. 로그인 연결을 확인한 뒤 다시 시도하세요.","Could not check your rating. Check your account connection and try again.","自分の評価を確認できませんでした。ログイン接続を確認して、もう一度お試しください。")); }
        }
        finally { if (ReferenceEquals(_ratingReadCts, cts)) _ratingReadCts = null; UpdateRatingControls(); }
    }

    private static string RatingToggleTarget(string current, string clicked) => current == clicked ? "none" : clicked;

    private async void Rating_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _ratingsAccountBusy || _ratingBusy || _ratingReadCts is not null || _current is null ||
            _settings.Incognito || !_session.IsConnected || _session.SelectedChannel is null ||
            _videoRating is not { CanRate: true, Value: not "unknown" } ||
            sender is not Button { Tag: string clicked } || clicked is not ("like" or "dislike")) return;
        var task = SetCurrentRatingAsync(RatingToggleTarget(_videoRating.Value, clicked));
        await TrackRatingTask(task);
    }

    private async Task SetCurrentRatingAsync(string target)
    {
        var item = _current!; var generation = _videoMetadataGeneration; var account = RatingAccountStamp;
        var revision = ++_ratingRevision;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _ratingSendCts = cts; _ratingBusy = true; SetRatingUiText(() => UiText.T("YouTube에 평가를 반영하는 중…","Applying your rating on YouTube…","YouTube に評価を反映中…")); UpdateRatingControls();
        try
        {
            var result = await _youtube.SetRatingAsync(item.Url, target, cts.Token, _settings.Incognito);
            if (!cts.IsCancellationRequested) ApplyVideoRating(result, item, generation, account, revision);
        }
        catch (OperationCanceledException)
        {
            if (RatingResultIsCurrent(item, generation, account, revision))
            { _videoRating = null; SetRatingUiText(() => UiText.T("평가 결과를 확인하지 못했습니다. ‘내 평가 확인’을 눌러 주세요.","Could not confirm the rating result. Click “Check my rating”.","評価結果を確認できませんでした。「自分の評価を確認」を押してください。")); }
        }
        catch (Exception ex) when (IsInteractionFailure(ex))
        {
            if (RatingResultIsCurrent(item, generation, account, revision))
            { _videoRating = null; SetRatingUiText(() => UiText.T("평가 결과를 확인하지 못했습니다. ‘내 평가 확인’을 눌러 주세요.","Could not confirm the rating result. Click “Check my rating”.","評価結果を確認できませんでした。「自分の評価を確認」を押してください。")); }
        }
        finally { if (ReferenceEquals(_ratingSendCts, cts)) _ratingSendCts = null; _ratingBusy = false; UpdateRatingControls(); }
    }

    private async void DislikeStats_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _ratingsAccountBusy || _current is null || _dislikeStatsCts is not null) return;
        await TrackRatingTask(LoadDislikeStatsAsync());
    }

    private async Task LoadDislikeStatsAsync()
    {
        if (_current is not { } item || _closing || _ratingsAccountBusy) return;
        var generation = _videoMetadataGeneration; var account = RatingAccountStamp;
        _dislikeStatsCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _dislikeStatsCts = cts; SetDislikeStatsUiText(() => UiText.T("RYD 수치를 불러오는 중…","Loading RYD counts…","RYD の数値を読み込み中…")); UpdateRatingControls();
        bool Current() => !_closing && !_ratingsAccountBusy && !cts.IsCancellationRequested &&
            generation == _videoMetadataGeneration && _current?.Id == item.Id && account == RatingAccountStamp;
        try
        {
            var result = await _youtube.GetDislikeStatsAsync(item.Url, cts.Token);
            if (!Current()) return;
            _dislikeStats = result;
            SetDislikeStatsUiText(() => result is null ? UiText.T("RYD에 수치가 없습니다.","RYD has no counts for this video.","RYD にこの動画の数値がありません。") : UiText.T("싫어요 ≈ RYD 추정치 · 수치는 즉시 갱신되지 않을 수 있습니다.","Dislikes ≈ RYD estimate · Counts may not update immediately.","低評価 ≈ RYD の推定値 · 数値はすぐに更新されない場合があります。"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsInteractionFailure(ex))
        { if (Current()) SetDislikeStatsUiText(() => UiText.T("RYD 수치를 불러오지 못했습니다. 잠시 후 수치 보기를 눌러 주세요.","Could not load RYD counts. Click Show counts again later.","RYD の数値を読み込めませんでした。しばらくしてから「数値を表示」を押してください。")); }
        finally { if (ReferenceEquals(_dislikeStatsCts, cts)) _dislikeStatsCts = null; UpdateRatingControls(); }
    }

    private void RydAttribution_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://returnyoutubedislike.com/") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { SetStatus(()=>UiText.T("Return YouTube Dislike 안내 페이지를 열지 못했습니다.","Could not open the Return YouTube Dislike information page.","Return YouTube Dislike の案内ページを開けませんでした。")); }
    }

    private void VideoSurface_Down(object sender, MouseButtonEventArgs e)
    {
        if (_closing || !_playerStarted || _current is null) return;
        // The disabled HWND keeps foreign-player focus away from keyboard shortcuts.
        // Win32 passes the press to the enabled WPF video surface in either window.
        e.Handled = true;
        if (ReferenceEquals(sender, _popoutSurface)) _popoutPause?.Focus();
        else PlayPauseButton.Focus();
        Pause_Click(sender, e);
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        CopyVideoShareLink(_current);
    }

    private void CardShare_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || sender is not MenuItem { DataContext: VideoItem { Kind: "video" } item }) return;
        CopyVideoShareLink(item);
    }

    private void CopyVideoShareLink(VideoItem? item)
    {
        if (item is not { Kind: "video" } || !YouTubeService.TryVideoUrl(item.Url, out _, out var cleanUrl))
        {
            SetStatus(()=>UiText.T("공유할 영상 주소를 확인할 수 없습니다.","Could not find a video URL to share.","共有する動画 URL を確認できません。"));
            return;
        }
        try
        {
            _copyShareText(cleanUrl);
            SetStatus(()=>UiText.T("영상 주소를 복사했습니다.","Video URL copied.","動画 URL をコピーしました。"));
        }
        catch (ExternalException)
        {
            SetStatus(()=>UiText.T("클립보드에 복사하지 못했습니다. 다시 시도해 주세요.","Could not copy to the clipboard. Try again.","クリップボードにコピーできませんでした。もう一度お試しください。"));
        }
    }
}
