"""Lazy WEB home recommendations and YouTube-provided filter chips.

Only home browse requests are made. Cards remain flat entries; player, watch
history, feedback, and individual video extraction are never called here.
Filter endpoint payloads are kept in memory behind disposable opaque handles.
"""

from __future__ import annotations

from collections import deque
from collections.abc import Mapping
import copy
import re
import secrets
import urllib.parse

from youtube_session import account_api_headers, configure_ytcfg, validate_account_context


HOME_ID = "FEwhat_to_watch"
HOME_QUERY = {"browseId": HOME_ID}
VIDEO_ID = re.compile(r"^[A-Za-z0-9_-]{11}$")
PLAYLIST_ID = re.compile(r"^[A-Za-z0-9_-]{1,256}$")
MAX_PAGES = 128
MAX_PAGE_ENTRIES = 256
MAX_EMPTY_PAGES = 3
MAX_FILTERS = 64
HOME_TARGET = "browse-feedFEwhat_to_watch"
CHIP_TARGETS = frozenset(("feed-filter-chip-bar", "feed-filter-chip-bar-renderer"))


class HomeFeedError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__({"auth": "Sign in to load home recommendations",
                          "unsupported": "This home filter is no longer available",
                          "unavailable": "Home recommendations unavailable"}[code])


def _mapping(value):
    return value if isinstance(value, Mapping) else {}


def _at(value, *keys):
    for key in keys:
        value = _mapping(value).get(key)
    return value


def _text(value):
    if isinstance(value, str):
        return value[:512]
    direct = _mapping(value).get("simpleText") or _mapping(value).get("content")
    if isinstance(direct, str):
        return direct[:512]
    runs = _mapping(value).get("runs")
    if isinstance(runs, list):
        return "".join(run.get("text", "") for run in runs[:64]
                       if isinstance(run, Mapping) and isinstance(run.get("text"), str))[:512]
    return ""


def _token(value, limit=16384):
    return (isinstance(value, str) and 0 < len(value) <= limit
            and all(32 < ord(character) < 127 for character in value))


def _reload_presentation(value):
    """Recognize the home chip's inert loading placeholder; never replay it."""
    if not isinstance(value, Mapping) or set(value) - {"clickTrackingParams", "showReloadUiCommand"}:
        return False
    tracking = value.get("clickTrackingParams")
    if tracking is not None and not _token(tracking, 8192):
        return False
    command = value.get("showReloadUiCommand")
    if (not isinstance(command, Mapping) or set(command) - {"targetId", "content"}
            or command.get("targetId") != HOME_TARGET):
        return False
    content = command.get("content")
    if not isinstance(content, Mapping) or set(content) != {"ghostGridViewModel"}:
        return False
    grid = content["ghostGridViewModel"]
    rows = _mapping(grid).get("numRows")
    return (isinstance(grid, Mapping) and set(grid) == {"numRows"}
            and isinstance(rows, int) and not isinstance(rows, bool) and 1 <= rows <= 32)


def _browse_command(value):
    """Allow only one inert home browse command, never an arbitrary API URL."""
    value = _mapping(value)
    if "innertubeCommand" in value:
        if set(value) - {"innertubeCommand"}:
            return None
        value = _mapping(value["innertubeCommand"])
    if set(value) - {"browseEndpoint", "continuationCommand", "commandMetadata", "clickTrackingParams"}:
        return None
    if "commandMetadata" in value and not isinstance(value["commandMetadata"], Mapping):
        return None
    metadata = _at(value, "commandMetadata", "webCommandMetadata")
    if metadata is not None:
        if not isinstance(metadata, Mapping):
            return None
        if metadata.get("apiUrl") not in (None, "/youtubei/v1/browse"):
            return None
        if metadata.get("sendPost") is not None and metadata.get("sendPost") is not True:
            return None
    if "browseEndpoint" in value and "continuationCommand" not in value:
        endpoint = _mapping(value["browseEndpoint"])
        if endpoint.get("browseId") != HOME_ID:
            return None
        # The navigation presentation fields never become request fields.
        if set(endpoint) - {"browseId", "params", "canonicalBaseUrl"}:
            return None
        query = {"browseId": HOME_ID}
        if "params" in endpoint:
            if not _token(endpoint["params"]):
                return None
            query["params"] = endpoint["params"]
    elif "continuationCommand" in value and "browseEndpoint" not in value:
        command = _mapping(value["continuationCommand"])
        if (set(command) - {"token", "request", "command", "invalidationGroupId"}
                or command.get("request") not in (None, "CONTINUATION_REQUEST_TYPE_BROWSE")
                or not _token(command.get("token"))):
            return None
        if "command" in command and not _reload_presentation(command["command"]):
            return None
        if "invalidationGroupId" in command and not _token(command["invalidationGroupId"], 256):
            return None
        # Loading placeholders and invalidation groups are browser UI fields.
        # Only the server continuation token is sent to the browse API.
        query = {"continuation": command["token"]}
    else:
        return None
    tracking = value.get("clickTrackingParams")
    if tracking is not None and not _token(tracking, 8192):
        return None
    return query, tracking


def _home_surfaces(response):
    """Scope roots to the selected home tab and known home continuation targets."""
    contents, headers, title = [], [], ""
    response = _mapping(response)
    top_header = response.get("header")
    if isinstance(top_header, Mapping):
        headers.append(top_header)
    for key in ("twoColumnBrowseResultsRenderer", "singleColumnBrowseResultsRenderer"):
        tabs = _at(response, "contents", key, "tabs")
        if not isinstance(tabs, list):
            continue
        for value in tabs[:16]:
            tab = _mapping(_mapping(value).get("tabRenderer"))
            if tab.get("selected") is not True:
                continue
            identifier = tab.get("tabIdentifier")
            if identifier is not None and identifier != HOME_ID:
                continue
            title = _text(tab.get("title")) or title
            for kind in ("richGridRenderer", "sectionListRenderer"):
                renderer = _mapping(_mapping(tab.get("content")).get(kind))
                if not isinstance(renderer.get("contents"), list):
                    continue
                contents.append(renderer)
                if isinstance(renderer.get("header"), Mapping):
                    headers.append(renderer["header"])
    for key in ("onResponseReceivedActions", "onResponseReceivedEndpoints", "onResponseReceivedCommands"):
        commands = response.get(key)
        if not isinstance(commands, list):
            continue
        for command in commands[:64]:
            for action_key in ("appendContinuationItemsAction", "reloadContinuationItemsCommand"):
                action = _mapping(_mapping(command).get(action_key))
                items = action.get("continuationItems")
                if not isinstance(items, list):
                    continue
                target = action.get("targetId")
                if target == HOME_TARGET:
                    contents.append({"contents": items})
                elif target in CHIP_TARGETS:
                    # Chip-only updates cannot introduce recommendation cards.
                    headers.extend(value for value in items[:MAX_FILTERS] if isinstance(value, Mapping))
    continuations = _mapping(response.get("continuationContents"))
    for key in ("richGridContinuation", "sectionListContinuation"):
        renderer = _mapping(continuations.get(key))
        if isinstance(renderer.get("contents"), list):
            contents.append(renderer)
            if isinstance(renderer.get("header"), Mapping):
                headers.append(renderer["header"])
    return contents, headers, title


def _chip_renderers(headers):
    """Only headers contain filters; card menus and recommendation text do not."""
    found, visited, has_container = [], 0, False

    def visit(value, depth=0, in_bar=False):
        nonlocal visited, has_container
        visited += 1
        if depth > 12 or visited > 2048:
            return
        if isinstance(value, list):
            for child in value[:128]:
                visit(child, depth + 1, in_bar)
            return
        if not isinstance(value, Mapping):
            return
        for key in ("feedFilterChipBarRenderer", "chipCloudRenderer", "chipBarViewModel", "feedFilterChipBarViewModel"):
            if isinstance(value.get(key), Mapping):
                has_container = True
                visit(value[key], depth + 1, True)
                return
        for key in ("chipCloudChipRenderer", "chipViewModel"):
            renderer = value.get(key)
            if isinstance(renderer, Mapping) and in_bar:
                found.append(renderer)
                return
        # Descend only header structure, never executable commands or menus.
        for key in ("contents", "chips", "chipCloud", "content", "header", "primaryChips",
                    "feedTabbedHeaderRenderer", "feedHeaderRenderer", "chipBar"):
            if key in value:
                visit(value[key], depth + 1, in_bar)

    for header in headers:
        visit(header)
    return found[:MAX_FILTERS], has_container


def _playlist_url(playlist_id, renderer):
    seed = _at(renderer, "navigationEndpoint", "watchEndpoint", "videoId")
    if not isinstance(seed, str) or not VIDEO_ID.fullmatch(seed):
        seed = _at(renderer, "rendererContext", "commandContext", "onTap", "innertubeCommand", "watchEndpoint", "videoId")
    if not isinstance(seed, str) or not VIDEO_ID.fullmatch(seed):
        seed = renderer.get("videoId")
    if playlist_id.startswith("RD") and isinstance(seed, str) and VIDEO_ID.fullmatch(seed):
        return "https://www.youtube.com/watch?" + urllib.parse.urlencode({"v": seed, "list": playlist_id})
    return "https://www.youtube.com/playlist?" + urllib.parse.urlencode({"list": playlist_id})


def _flat_card(value, extractor):
    for key in ("videoRenderer", "gridVideoRenderer", "reelItemRenderer"):
        renderer = value.get(key)
        if isinstance(renderer, Mapping):
            video_id = renderer.get("videoId")
            if not isinstance(video_id, str) or not VIDEO_ID.fullmatch(video_id):
                return None
            result = extractor._extract_video(dict(renderer))
            if isinstance(result, Mapping):
                return {**result, "_recommendation_renderer": renderer}
            return None
    for key in ("playlistRenderer", "gridPlaylistRenderer", "radioRenderer", "gridRadioRenderer"):
        renderer = value.get(key)
        if isinstance(renderer, Mapping):
            playlist_id = renderer.get("playlistId")
            if not isinstance(playlist_id, str) or not PLAYLIST_ID.fullmatch(playlist_id):
                return None
            return {"_type": "url", "ie_key": "YoutubeTab", "id": playlist_id,
                    "url": _playlist_url(playlist_id, renderer), "title": _text(renderer.get("title")),
                    "thumbnails": extractor._extract_thumbnails(dict(renderer), "thumbnail"),
                    "_recommendation_renderer": renderer}
    renderer = value.get("lockupViewModel")
    if isinstance(renderer, Mapping):
        content_id, kind = renderer.get("contentId"), renderer.get("contentType")
        if not isinstance(content_id, str):
            return None
        if kind == "LOCKUP_CONTENT_TYPE_VIDEO":
            if not VIDEO_ID.fullmatch(content_id):
                return None
        elif kind in ("LOCKUP_CONTENT_TYPE_PLAYLIST", "LOCKUP_CONTENT_TYPE_PODCAST"):
            if not PLAYLIST_ID.fullmatch(content_id):
                return None
        else:
            return None
        result = extractor._extract_lockup_view_model(dict(renderer))
        if isinstance(result, Mapping):
            result = {**result, "_recommendation_renderer": renderer}
            if kind != "LOCKUP_CONTENT_TYPE_VIDEO":
                result["url"] = _playlist_url(content_id, renderer)
            return result
    return None


def _parse_cards(surfaces, extractor):
    entries, continuation, visited = [], None, 0

    def visit(value, depth=0, cursor=False):
        nonlocal visited, continuation
        visited += 1
        if depth > 14 or visited > 4096:
            raise HomeFeedError("unavailable")
        if isinstance(value, list):
            if len(value) > 4096:
                raise HomeFeedError("unavailable")
            # Continue looking for a trailing cursor within a separate traversal
            # budget. A bounded parse must not report truncation as real EOF.
            for child in value:
                visit(child, depth + 1, cursor)
            return
        if not isinstance(value, Mapping):
            return
        item = value.get("continuationItemRenderer")
        if isinstance(item, Mapping):
            if cursor and continuation is None:
                continuation = _browse_command(item.get("continuationEndpoint") or _at(item, "button", "buttonRenderer", "command"))
            return
        item = value.get("continuationItemViewModel")
        if isinstance(item, Mapping):
            if cursor and continuation is None:
                continuation = _browse_command(_at(item, "continuationCommand", "innertubeCommand"))
            return
        entry = _flat_card(value, extractor)
        if entry is not None:
            if len(entries) >= MAX_PAGE_ENTRIES:
                raise HomeFeedError("unavailable")
            entries.append(entry)
            return
        rich = value.get("richItemRenderer")
        if isinstance(rich, Mapping):
            visit(rich.get("content"), depth + 1)
        section = value.get("itemSectionRenderer")
        if isinstance(section, Mapping) and section.get("targetId") in (None, HOME_TARGET):
            visit(section.get("contents"), depth + 1, cursor)
        for key in ("richGridRenderer", "gridRenderer", "expandedShelfContentsRenderer"):
            container = value.get(key)
            if isinstance(container, Mapping):
                visit(container.get("contents") or container.get("items"), depth + 1)
        for key in ("shelfRenderer", "richSectionRenderer", "richShelfRenderer"):
            container = value.get(key)
            if isinstance(container, Mapping):
                visit(container.get("content") or container.get("contents"), depth + 1)

    for surface in surfaces:
        visit(surface.get("contents"), cursor=True)
        if continuation is None:
            for value in surface.get("continuations", [])[:8] if isinstance(surface.get("continuations"), list) else []:
                command = _mapping(_mapping(value).get("nextContinuationData"))
                if _token(command.get("continuation")):
                    continuation = ({"continuation": command["continuation"]},
                                    command.get("clickTrackingParams") if _token(command.get("clickTrackingParams"), 8192) else None)
                    break
    return entries, continuation


class HomeFeed:
    """One selected channel, one cursor, and only server-provided chip labels."""

    def __init__(self, ydl, account_context=None, *, extractor_factory=None,
                 max_pages=MAX_PAGES, max_empty_pages=MAX_EMPTY_PAGES):
        self.account_context = validate_account_context(account_context)
        if extractor_factory is None:
            from yt_dlp.extractor.youtube import YoutubeTabIE
            extractor_factory = YoutubeTabIE
        self.extractor = extractor_factory(ydl)
        self.max_pages = max(1, min(int(max_pages), MAX_PAGES))
        self.max_empty_pages = max(1, min(int(max_empty_pages), MAX_EMPTY_PAGES))
        self.queue = deque()
        self.seen_ids, self.seen_tokens = set(), set()
        self.cursor = None
        self.config = self.context = self.headers = None
        self.started = self.closed = False
        self.page_count = self.empty_pages = 0
        self.metadata = {"title": "YouTube 홈 추천", "filterKind": "home", "filters": []}
        self._filters = {}

    def __iter__(self):
        return self

    def close(self):
        self.closed = True
        self.queue.clear()
        self.seen_ids.clear()
        self.seen_tokens.clear()
        self._filters.clear()
        self.metadata["filters"] = []
        self.cursor = self.config = self.context = self.headers = None
        self.account_context = self.extractor = None

    def _api(self, command):
        query, tracking = command
        context = copy.deepcopy(self.context)
        if tracking:
            context["clickTracking"] = {"clickTrackingParams": tracking}
        response = self.extractor._call_api("browse", copy.deepcopy(query), "home",
                                            headers=self.headers, context=context)
        if not isinstance(response, Mapping) or not response or response.get("error"):
            raise HomeFeedError("unavailable")
        if _at(response, "responseContext", "mainAppWebResponseContext", "loggedOut") is True:
            raise HomeFeedError("auth")
        return response

    def _parse(self, response):
        surfaces, headers, title = _home_surfaces(response)
        if not surfaces:
            raise HomeFeedError("unavailable")
        entries, cursor = _parse_cards(surfaces, self.extractor)
        chips, has_chips = _chip_renderers(headers)
        return entries, cursor, chips, has_chips, title

    def _replace_filters(self, chips, *, command, selected_label=None, has_chips=True):
        previous = {}
        duplicates = set()
        for value in self._filters.values():
            label = value["label"]
            if label in previous:
                duplicates.add(label)
            previous[label] = value["command"]
        for label in duplicates:
            previous.pop(label, None)
        values = []
        if has_chips:
            selected_count = sum(chip.get("isSelected", chip.get("selected")) is True for chip in chips)
            for chip in chips:
                label = _text(chip.get("text")).strip()
                if not label:
                    continue
                selected = chip.get("isSelected", chip.get("selected")) is True
                endpoint = chip.get("navigationEndpoint", chip.get("tapCommand"))
                parsed = _browse_command(endpoint)
                # A currently selected chip sometimes omits its endpoint. Its
                # known active feed request is a safe refresh/return handle.
                if endpoint is None:
                    parsed = previous.get(label)
                    if parsed is None and selected and selected_count == 1:
                        parsed = command
                if parsed is not None:
                    values.append({"label": label, "selected": selected, "command": parsed})
        else:
            values = [{**value, "selected": value["label"] == selected_label}
                      for value in self._filters.values()]
        self._filters = {secrets.token_urlsafe(24): value for value in values[:MAX_FILTERS]}
        self.metadata["filters"] = [{"id": handle, "label": value["label"], "selected": value["selected"]}
                                    for handle, value in self._filters.items()]

    def _accept(self, parsed, *, command, selected_label=None, reset=False):
        entries, cursor, chips, has_chips, title = parsed
        if reset:
            self.queue.clear()
            self.seen_ids.clear()
            self.seen_tokens.clear()
            self.page_count = self.empty_pages = 0
        added = 0
        for entry in entries:
            key = (entry.get("ie_key"), entry.get("id"))
            if key in self.seen_ids:
                continue
            self.seen_ids.add(key)
            self.queue.append(entry)
            added += 1
        self.cursor = cursor
        if title:
            self.metadata["title"] = title
        # Paging must not revoke handles still displayed by the current UI.
        # Only initialization and explicit selection publish a new chip set.
        if reset:
            self._replace_filters(chips, command=command, selected_label=selected_label, has_chips=has_chips)
        return added

    def initialize(self):
        if self.closed:
            raise HomeFeedError("unsupported")
        if self.started:
            return self.metadata
        self.extractor.initialize()
        if not self.extractor.is_authenticated:
            raise HomeFeedError("auth")
        config = self.extractor._download_ytcfg("web", "home")
        if not isinstance(config, Mapping) or not config:
            raise HomeFeedError("unavailable")
        if config.get("LOGGED_IN") is False:
            raise HomeFeedError("auth")
        self.config = configure_ytcfg(dict(config), self.account_context)
        self.headers = account_api_headers(self.extractor, self.config, self.account_context)
        self.context = self.extractor._extract_context(self.config)
        command = (dict(HOME_QUERY), None)
        parsed = self._parse(self._api(command))
        self._accept(parsed, command=command, reset=True)
        self.started = True
        return self.metadata

    def select_filter(self, handle):
        if self.closed or not self.started or not isinstance(handle, str) or handle not in self._filters:
            raise HomeFeedError("unsupported")
        value = self._filters[handle]
        command = value["command"]
        # Parse before replacing cursor/handles, so failed requests remain retryable.
        parsed = self._parse(self._api(command))
        self._accept(parsed, command=command, selected_label=value["label"], reset=True)
        return self.metadata

    def _page(self):
        command = self.cursor
        token = command[0].get("continuation") if command else None
        if not token or token in self.seen_tokens:
            self.cursor = None
            return
        if self.page_count >= self.max_pages:
            raise HomeFeedError("unavailable")
        self.seen_tokens.add(token)
        self.page_count += 1
        parsed = self._parse(self._api(command))
        added = self._accept(parsed, command=command)
        if added:
            self.empty_pages = 0
        else:
            self.empty_pages += 1
            if self.cursor and self.empty_pages >= self.max_empty_pages:
                raise HomeFeedError("unavailable")

    def __next__(self):
        if self.closed:
            raise StopIteration
        self.initialize()
        while not self.queue:
            if not self.cursor:
                raise StopIteration
            self._page()
        return self.queue.popleft()
