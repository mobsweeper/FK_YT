"""Lazy channel videos in the order supplied by YouTube's browse API.

Sort and continuation tokens stay in this process. Only read-only browse and
resolve_url requests are performed; no individual video is ever extracted.
"""

from __future__ import annotations

from collections import deque
from collections.abc import Mapping
import copy
import re
import secrets
import urllib.parse

from home_feed import _chip_renderers, _flat_card, _text, _token
from channel_metadata import thumbnail_candidate
from youtube_session import account_api_headers, configure_ytcfg, validate_account_context


CHANNEL_ID = re.compile(r"^UC[A-Za-z0-9_-]{22}$")
MAX_PAGES = 256
MAX_PAGE_ENTRIES = 256
MAX_EMPTY_PAGES = 3
CHIP_TARGETS = frozenset(("feed-filter-chip-bar", "feed-filter-chip-bar-renderer"))


class ChannelFeedError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__({"auth": "Channel account session expired",
                          "unsupported": "Unsupported channel or sort",
                          "unavailable": "Channel videos unavailable"}[code])


def _mapping(value):
    return value if isinstance(value, Mapping) else {}


def _at(value, *keys):
    for key in keys:
        value = _mapping(value).get(key)
    return value


def channel_videos_url(value):
    """Canonicalize only channel entrances and their videos tab."""
    if not isinstance(value, str) or not value or len(value) > 4096:
        raise ChannelFeedError("unsupported")
    value = value.strip()
    try:
        if not urllib.parse.urlsplit(value).scheme:
            value = "https:" + value if value.startswith("//") else "https://" + value
        parsed = urllib.parse.urlsplit(value)
        if (parsed.scheme not in ("https", "http") or parsed.username or parsed.password
                or parsed.hostname not in ("youtube.com", "www.youtube.com", "m.youtube.com")
                or parsed.port not in (None, 80, 443)):
            raise ChannelFeedError("unsupported")
        parts = parsed.path.rstrip("/").split("/")[1:]
        if len(parts) in (1, 2) and parts[0].startswith("@") and len(parts[0]) > 1:
            owner, tab = parts[:1], parts[1:]
        elif len(parts) in (2, 3) and parts[0] in ("channel", "c", "user") and parts[1]:
            owner, tab = parts[:2], parts[2:]
            if parts[0] == "channel" and not CHANNEL_ID.fullmatch(parts[1]):
                raise ChannelFeedError("unsupported")
        else:
            raise ChannelFeedError("unsupported")
        if tab and tab != ["videos"]:
            raise ChannelFeedError("unsupported")
        # Encoded separators/control characters must not redefine the owner.
        for part in owner:
            decoded = urllib.parse.unquote(part)
            if any(character in decoded for character in "/\\?#") or any(ord(c) <= 32 for c in decoded):
                raise ChannelFeedError("unsupported")
        return "https://www.youtube.com/" + "/".join(owner) + "/videos"
    except (ValueError, IndexError):
        raise ChannelFeedError("unsupported") from None


def _presentation(value, targets):
    """A loading placeholder is inert and is omitted from the outgoing query."""
    value = _mapping(value)
    if set(value) - {"clickTrackingParams", "showReloadUiCommand"}:
        return False
    command = _mapping(value.get("showReloadUiCommand"))
    if set(command) - {"targetId", "content"} or command.get("targetId") not in targets:
        return False
    if "content" not in command:
        return value.get("clickTrackingParams") is None or _token(value["clickTrackingParams"], 8192)
    content = _mapping(command.get("content"))
    grid = _mapping(content.get("ghostGridViewModel"))
    rows = grid.get("numRows")
    return (set(content) == {"ghostGridViewModel"} and set(grid) == {"numRows"}
            and isinstance(rows, int) and not isinstance(rows, bool) and 1 <= rows <= 32
            and (value.get("clickTrackingParams") is None or _token(value["clickTrackingParams"], 8192)))


def _browse_command(value, channel_id, targets, *, allow_browse=True):
    """Validate a channel-scoped browse action; never replay arbitrary commands."""
    value = _mapping(value)
    if "innertubeCommand" in value:
        if set(value) != {"innertubeCommand"}:
            return None
        value = _mapping(value["innertubeCommand"])
    if set(value) - {"browseEndpoint", "continuationCommand", "commandMetadata", "clickTrackingParams"}:
        return None
    metadata = _at(value, "commandMetadata", "webCommandMetadata")
    if "commandMetadata" in value and not isinstance(value["commandMetadata"], Mapping):
        return None
    if metadata is not None and (not isinstance(metadata, Mapping)
            or metadata.get("apiUrl") not in (None, "/youtubei/v1/browse")
            or metadata.get("sendPost") not in (None, True)):
        return None
    tracking = value.get("clickTrackingParams")
    if tracking is not None and not _token(tracking, 8192):
        return None
    if allow_browse and "browseEndpoint" in value and "continuationCommand" not in value:
        endpoint = _mapping(value["browseEndpoint"])
        if (endpoint.get("browseId") != channel_id
                or set(endpoint) - {"browseId", "params", "canonicalBaseUrl"}):
            return None
        query = {"browseId": channel_id}
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
        if "command" in command and not _presentation(command["command"], targets):
            return None
        if "invalidationGroupId" in command and not _token(command["invalidationGroupId"], 256):
            return None
        query = {"continuation": command["token"]}
    else:
        return None
    return query, tracking


def _surfaces(response, targets, *, selected_targets=None):
    """Keep only the selected videos tab or matching channel grid updates."""
    grids, headers = [], []
    for renderer_key in ("twoColumnBrowseResultsRenderer", "singleColumnBrowseResultsRenderer"):
        tabs = _at(response, "contents", renderer_key, "tabs")
        if not isinstance(tabs, list):
            continue
        for value in tabs[:16]:
            tab = _mapping(_mapping(value).get("tabRenderer"))
            if tab.get("selected") is not True:
                continue
            identifier = tab.get("tabIdentifier")
            endpoint_url = _at(tab, "endpoint", "commandMetadata", "webCommandMetadata", "url")
            if identifier not in (None, "videos"):
                continue
            if isinstance(endpoint_url, str) and urllib.parse.urlsplit(endpoint_url).path.rstrip("/").rsplit("/", 1)[-1] != "videos":
                continue
            content = _mapping(tab.get("content"))
            for key in ("richGridRenderer", "sectionListRenderer"):
                grid = _mapping(content.get(key))
                if isinstance(grid.get("contents"), list):
                    grids.append(grid)
                    # The current channel videos API issues a per-response UUID
                    # target. Its value must come from this selected videos grid,
                    # never from a command or an unrelated continuation action.
                    if selected_targets is not None and key == "richGridRenderer" and _token(grid.get("targetId"), 256):
                        selected_targets.add(grid["targetId"])
                    if isinstance(grid.get("header"), Mapping):
                        headers.append(grid["header"])
    for key in ("onResponseReceivedActions", "onResponseReceivedEndpoints", "onResponseReceivedCommands"):
        commands = _mapping(response).get(key)
        if not isinstance(commands, list):
            continue
        for command in commands[:64]:
            for action_key in ("appendContinuationItemsAction", "reloadContinuationItemsCommand"):
                action = _mapping(_mapping(command).get(action_key))
                items = action.get("continuationItems")
                if not isinstance(items, list):
                    continue
                if action.get("targetId") in targets:
                    grids.append({"contents": items})
                elif action.get("targetId") in CHIP_TARGETS:
                    headers.extend(value for value in items[:16] if isinstance(value, Mapping))
    for key in ("richGridContinuation", "sectionListContinuation"):
        grid = _at(response, "continuationContents", key)
        if isinstance(grid, Mapping) and isinstance(grid.get("contents"), list):
            if grid.get("targetId") in (None, *targets):
                grids.append(grid)
                if isinstance(grid.get("header"), Mapping):
                    headers.append(grid["header"])
    return grids, headers


class ChannelFeed:
    """One closeable raw flat-entry cursor; every sort starts a fresh server cursor."""

    def __init__(self, ydl, url, account_context=None, *, max_pages=MAX_PAGES,
                 max_empty_pages=MAX_EMPTY_PAGES):
        self.url = channel_videos_url(url)
        self.account_context = validate_account_context(account_context)
        self.extractor = ydl.get_info_extractor("YoutubeTab")
        self.config = self.context = self.headers = None
        self.started = self.closed = False
        self.channel = {}
        self.filters = []
        self.handles = {}
        self.bootstrap = None
        self.targets = set()
        self.queue = deque()
        self.seen_ids, self.seen_tokens = set(), set()
        self.token = None
        self.page_count = self.empty_pages = 0
        self.max_pages = max(1, min(int(max_pages), MAX_PAGES))
        self.max_empty_pages = max(1, min(int(max_empty_pages), MAX_EMPTY_PAGES))

    def __iter__(self):
        return self

    def close(self):
        self.closed = True
        self.queue.clear()
        self.seen_ids.clear()
        self.seen_tokens.clear()
        self.handles.clear()
        self.filters.clear()
        self.token = self.bootstrap = None
        self.extractor = self.config = self.headers = self.context = None
        self.account_context = None

    @property
    def metadata(self):
        return {"title": self.channel.get("title", ""), "filterKind": "channel-sort",
                "channel": copy.deepcopy(self.channel), "filters": copy.deepcopy(self.filters)}

    def _api(self, endpoint, query, tracking=None):
        context = copy.deepcopy(self.context)
        if tracking:
            context["clickTracking"] = {"clickTrackingParams": tracking}
        response = self.extractor._call_api(endpoint, query, self.url,
                                            headers=self.headers, context=context)
        if not isinstance(response, Mapping) or not response or response.get("error"):
            raise ChannelFeedError("unavailable")
        if self.account_context is not None and _at(response, "responseContext", "mainAppWebResponseContext", "loggedOut") is True:
            raise ChannelFeedError("auth")
        visitor = self.extractor._extract_visitor_data(response)
        if visitor:
            self.config["VISITOR_DATA"] = visitor
            configured_context = self.config.get("INNERTUBE_CONTEXT")
            if isinstance(configured_context, dict):
                configured_context.setdefault("client", {})["visitorData"] = visitor
            self.headers = account_api_headers(self.extractor, self.config, self.account_context, visitor_data=visitor)
            self.context = self.extractor._extract_context(self.config)
        return response

    def _channel_metadata(self, response):
        raw = _mapping(_at(response, "metadata", "channelMetadataRenderer"))
        item = self.extractor._extract_metadata_from_tabs(self.bootstrap["browseId"], response)
        channel_id = item.get("channel_id")
        if not isinstance(channel_id, str) or not CHANNEL_ID.fullmatch(channel_id) or channel_id != self.bootstrap["browseId"]:
            raise ChannelFeedError("unavailable")
        title = item.get("channel") or raw.get("title")
        if not isinstance(title, str) or not title:
            raise ChannelFeedError("unavailable")
        avatars = _at(raw, "avatar", "thumbnails")
        # Banners and video thumbnails must never substitute for the owner avatar.
        if not isinstance(avatars, list):
            avatars = _at(response, "header", "c4TabbedHeaderRenderer", "avatar", "thumbnails")
        if not isinstance(avatars, list):
            avatars = _at(response, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel",
                          "image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources")
        candidates = [candidate for value in (avatars or [])[:64] if isinstance(value, Mapping)
                      and (candidate := thumbnail_candidate(value.get("url"))) is not None]
        urls = [candidate[0] for candidate in candidates if candidate[1]]
        if not urls:
            urls = [candidate[0] for candidate in candidates]
        self.channel = {"id": channel_id, "title": title[:512],
                        "url": "https://www.youtube.com/channel/" + channel_id + "/videos",
                        "thumbnail": urls[-1] if urls else ""}
        if isinstance(item.get("description"), str) and item["description"]:
            self.channel["description"] = item["description"][:8192]
        self.targets = {"browse-feed" + channel_id, "browse-feed" + channel_id + "videos"}

    def _filters(self, headers, *, command=None, selected_label=None):
        chips, has_chips = _chip_renderers(headers)
        previous = {}
        duplicate_labels = set()
        for value in self.filters:
            if value["label"] in previous:
                duplicate_labels.add(value["label"])
            previous[value["label"]] = self.handles[value["id"]]
        for label in duplicate_labels:
            previous.pop(label, None)
        filters, handles = [], {}
        if not has_chips:
            for value in self.filters:
                handle = secrets.token_urlsafe(24)
                handles[handle] = self.handles[value["id"]]
                filters.append({"id": handle, "label": value["label"],
                                "selected": value["label"] == selected_label})
            self.filters, self.handles = filters, handles
            return
        selected_count = sum(chip.get("isSelected") is True or chip.get("selected") is True for chip in chips)
        for chip in chips[:16]:
            label = _text(chip.get("text") or chip.get("title"))
            selected = chip.get("isSelected") is True or chip.get("selected") is True
            endpoint = chip.get("navigationEndpoint", chip.get("tapCommand", chip.get("onTap")))
            if endpoint is None:
                endpoint = _at(chip, "rendererContext", "commandContext", "onTap")
            parsed = _browse_command(endpoint, self.channel["id"], self.targets)
            # Selected Latest frequently has no click command. Retain the actual
            # resolved videos endpoint so it remains selectable after a sort.
            if endpoint is None:
                parsed = previous.get(label)
                if parsed is None and selected and selected_count == 1:
                    parsed = command
            if not label or parsed is None:
                continue
            handle = secrets.token_urlsafe(24)
            handles[handle] = parsed
            filters.append({"id": handle, "label": label, "selected": selected})
        self.filters, self.handles = filters, handles

    def _parse(self, response, *, update_filters=True, command=None, selected_label=None):
        selected_targets = set()
        grids, headers = _surfaces(response, self.targets, selected_targets=selected_targets)
        if not grids:
            raise ChannelFeedError("unavailable")
        if selected_targets:
            self.targets = {"browse-feed" + self.channel["id"], "browse-feed" + self.channel["id"] + "videos", *selected_targets}
        if update_filters:
            self._filters(headers, command=command, selected_label=selected_label)
        self.token = None
        added, visited = 0, 0

        def visit(value, depth=0):
            nonlocal added, visited
            visited += 1
            if visited > 4096 or depth > 12:
                raise ChannelFeedError("unavailable")
            if isinstance(value, list):
                if len(value) > 4096:
                    raise ChannelFeedError("unavailable")
                # The continuation follows the final card. Card and traversal
                # budgets are separate; truncation must never look like EOF.
                for child in value:
                    visit(child, depth + 1)
                return
            if not isinstance(value, Mapping):
                return
            continuation = value.get("continuationItemRenderer")
            if isinstance(continuation, Mapping):
                command = _browse_command(continuation.get("continuationEndpoint"), self.channel["id"],
                                          self.targets, allow_browse=False)
                if command and self.token is None:
                    self.token = command[0]["continuation"]
                return
            entry = _flat_card(value, self.extractor)
            if isinstance(entry, Mapping) and entry.get("ie_key") == "Youtube":
                video_id = entry.get("id")
                if video_id in self.seen_ids:
                    return
                if added >= MAX_PAGE_ENTRIES:
                    raise ChannelFeedError("unavailable")
                self.seen_ids.add(video_id)
                entry = dict(entry)
                entry.setdefault("channel", self.channel["title"])
                entry.setdefault("channel_id", self.channel["id"])
                if not entry.get("channel"):
                    entry["channel"] = self.channel["title"]
                if not entry.get("channel_id"):
                    entry["channel_id"] = self.channel["id"]
                if entry["channel_id"] == self.channel["id"]:
                    entry["channel_url"] = self.channel["url"]
                    entry["channel_thumbnail"] = self.channel["thumbnail"]
                self.queue.append(entry)
                added += 1
                return
            for key in ("richItemRenderer", "itemSectionRenderer", "gridRenderer", "richGridRenderer", "sectionListRenderer"):
                renderer = _mapping(value.get(key))
                for contents_key in ("content", "contents", "items"):
                    if contents_key in renderer:
                        visit(renderer[contents_key], depth + 1)

        for grid in grids:
            visit(grid.get("contents"))
        return added

    def initialize(self):
        if self.closed:
            raise ChannelFeedError("unavailable")
        if self.started:
            return self.metadata
        self.extractor.initialize()
        if self.account_context is not None and not self.extractor.is_authenticated:
            raise ChannelFeedError("auth")
        config = self.extractor._download_ytcfg("web", self.url)
        if not isinstance(config, Mapping):
            raise ChannelFeedError("unavailable")
        if self.account_context is not None and config.get("LOGGED_IN") is False:
            raise ChannelFeedError("auth")
        self.config = configure_ytcfg(dict(config), self.account_context)
        self.headers = account_api_headers(self.extractor, self.config, self.account_context)
        self.context = self.extractor._extract_context(self.config)
        resolved = self._api("navigation/resolve_url", {"url": self.url})
        endpoint = _mapping(_mapping(resolved.get("endpoint")).get("browseEndpoint"))
        channel_id = endpoint.get("browseId")
        if not isinstance(channel_id, str) or not CHANNEL_ID.fullmatch(channel_id):
            raise ChannelFeedError("unsupported")
        command = _browse_command(resolved.get("endpoint"), channel_id, set())
        if command is None:
            raise ChannelFeedError("unsupported")
        self.bootstrap = command[0]
        response = self._api("browse", *command)
        self._channel_metadata(response)
        self._parse(response, command=command)
        self.started = True
        return self.metadata

    def select_filter(self, handle):
        self.initialize()
        if not isinstance(handle, str) or len(handle) > 128 or handle not in self.handles:
            raise ChannelFeedError("unsupported")
        query, tracking = self.handles[handle]
        response = self._api("browse", dict(query), tracking)
        # Validate before discarding the visible old cursor on a failed response.
        if not _surfaces(response, self.targets)[0]:
            raise ChannelFeedError("unavailable")
        selected_label = next(value["label"] for value in self.filters if value["id"] == handle)
        self.queue.clear()
        self.seen_ids.clear()
        self.seen_tokens.clear()
        self.page_count = self.empty_pages = 0
        self._parse(response, command=(query, tracking), selected_label=selected_label)
        return self.metadata

    def __next__(self):
        if self.closed:
            raise StopIteration
        self.initialize()
        while not self.queue:
            if not self.token or self.token in self.seen_tokens:
                self.token = None
                raise StopIteration
            if self.page_count >= self.max_pages:
                raise ChannelFeedError("unavailable")
            token = self.token
            self.seen_tokens.add(token)
            self.page_count += 1
            added = self._parse(self._api("browse", {"continuation": token}), update_filters=False)
            self.empty_pages = 0 if added else self.empty_pages + 1
            if self.token and self.empty_pages >= self.max_empty_pages:
                raise ChannelFeedError("unavailable")
        return self.queue.popleft()
