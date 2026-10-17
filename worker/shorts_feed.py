"""Lazy authenticated Shorts recommendations from YouTube's reel sequence API.

Only feed APIs are called here. A card is never resolved through YoutubeIE,
and playback/history/feedback endpoints are never executed by this iterator.
"""

from __future__ import annotations

from collections import deque
from collections.abc import Mapping
import re

from youtube_session import account_api_headers, configure_ytcfg, validate_account_context


SHORTS_URL = "https://www.youtube.com/shorts"
VIDEO_ID = re.compile(r"^[A-Za-z0-9_-]{11}$")
MAX_SEQUENCE_PAGES = 128
MAX_PAGE_ENTRIES = 128
MAX_EMPTY_PAGES = 3
# A deliberately small fixture contract: explicit ad cards only. Generic
# player ad inventories, paid-promotion disclosures and text are not evidence
# that the feed card itself is a served ad. Live response schemas may differ.
SERVED_AD_RENDERERS = ("adSlotRenderer", "adPlacementRenderer", "reelAdRenderer",
                       "shortsAdRenderer", "instreamVideoAdRenderer")


class ShortsFeedError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__({"auth": "Sign in with valid account cookies to load Shorts",
                          "unavailable": "Shorts recommendations unavailable"}[code])


def _mapping(value):
    return value if isinstance(value, Mapping) else {}


def _string(value):
    return value if isinstance(value, str) else ""


def _continuation(response):
    # Root sequenceContinuation is bootstrap-only. Nested tokens are comments,
    # engagement panels, or other surfaces, and must never seed this feed.
    direct = _string(response.get("sequenceContinuation"))
    if direct:
        return direct
    return _string(_mapping(_mapping(response.get("continuationEndpoint")).get("continuationCommand")).get("token"))


def _served_ad(scope):
    scope = _mapping(scope)
    return scope.get("isAd") is True or any(
        isinstance(scope.get(key), Mapping) and bool(scope[key])
        for key in SERVED_AD_RENDERERS)


def _current_reel_response(response, video_id, *, matched_prefetch=False):
    response = _mapping(response)
    if "videoId" in response and response.get("videoId") != video_id:
        return {}
    replacement_id = _mapping(_mapping(response.get("replacementEndpoint")).get("reelWatchEndpoint")).get("videoId")
    if replacement_id:
        return response if replacement_id == video_id else {}
    # A prefetched reel response without its own ID is usable only when its
    # associated playerResponse videoDetails identifies this exact card.
    return response if matched_prefetch else {}


def _flat_entry(command, *, item_response=None, card=None):
    endpoint = _mapping(_mapping(command).get("reelWatchEndpoint"))
    video_id = _string(endpoint.get("videoId"))
    if not VIDEO_ID.fullmatch(video_id):
        return None
    prefetch = _mapping(endpoint.get("unserializedPrefetchData"))
    player_response = _mapping(prefetch.get("playerResponse"))
    details = _mapping(player_response.get("videoDetails"))
    matched_prefetch = details.get("videoId") == video_id
    renderer = (_current_reel_response(prefetch.get("reelItemWatchResponse"), video_id,
                                       matched_prefetch=True) if matched_prefetch else {})
    current_response = _current_reel_response(item_response, video_id)
    card = _mapping(card)
    card_id = _mapping(_mapping(card.get("command")).get("reelWatchEndpoint")).get("videoId")
    # Do not recursively scan: neighboring cards, menus and engagement panels
    # can describe other content. Only these current-card locations qualify.
    scopes = [_mapping(command), endpoint, current_response]
    if card_id == video_id:
        scopes.append(card)
    if matched_prefetch:
        scopes.extend((prefetch, player_response, renderer))
    for response in (renderer, current_response):
        scopes.append(_mapping(_mapping(response.get("overlay")).get("reelPlayerOverlayRenderer")))
    if any(_served_ad(scope) for scope in scopes):
        return None
    # Metadata from a prefetched neighboring video must not label this card.
    if details.get("videoId") != video_id:
        details = {}
    thumbnails = _mapping(endpoint.get("thumbnail")).get("thumbnails")
    if not isinstance(thumbnails, (list, tuple)):
        thumbnails = _mapping(details.get("thumbnail")).get("thumbnails", [])
    if not isinstance(thumbnails, (list, tuple)):
        thumbnails = []
    thumbnails = [dict(value) for value in thumbnails[:64] if isinstance(value, Mapping)]
    result = {
        "_type": "url", "ie_key": "Youtube", "id": video_id,
        "url": f"https://www.youtube.com/shorts/{video_id}",
        "title": _string(details.get("title")) or "Shorts 영상",
        "channel": _string(details.get("author")),
        "channel_id": _string(details.get("channelId")),
        "thumbnails": thumbnails,
    }
    duration = details.get("lengthSeconds")
    if isinstance(duration, str) and len(duration) < 20 and duration.isascii() and duration.isdecimal():
        result["duration"] = int(duration)
    elif isinstance(duration, (int, float)) and not isinstance(duration, bool) and duration >= 0:
        result["duration"] = duration
    # Raw menus are passed to the worker's strict feedback classifier. They are
    # inert; the worker retains tokens internally and exposes opaque handles.
    renderer = renderer or current_response
    menu = _mapping(_mapping(renderer.get("overlay")).get("reelPlayerOverlayRenderer")).get("menu")
    if isinstance(menu, Mapping) and menu:
        result["_recommendation_renderer"] = {"menu": menu}
    return result


class ShortsFeed:
    """A closeable flat-entry iterator with one cursor and bounded API calls."""

    def __init__(self, ydl, account_context=None, *, extractor_factory=None,
                 max_pages=MAX_SEQUENCE_PAGES, max_empty_pages=MAX_EMPTY_PAGES):
        self.account_context = validate_account_context(account_context)
        if extractor_factory is None:
            from yt_dlp.extractor.youtube import YoutubeTabIE
            extractor_factory = YoutubeTabIE
        self.extractor = extractor_factory(ydl)
        self.max_pages = max(1, min(int(max_pages), MAX_SEQUENCE_PAGES))
        self.max_empty_pages = max(1, min(int(max_empty_pages), MAX_EMPTY_PAGES))
        self.queue = deque()
        self.seen_ids = set()
        self.seen_tokens = set()
        self.token = None
        self.config = None
        self.context = None
        self.headers = None
        self.started = False
        self.closed = False
        self.page_count = 0
        self.empty_pages = 0

    def __iter__(self):
        return self

    def close(self):
        self.closed = True
        self.queue.clear()
        self.seen_ids.clear()
        self.seen_tokens.clear()
        self.token = None
        self.config = self.context = self.headers = None
        self.account_context = None
        self.extractor = None

    def _api(self, endpoint, query):
        response = self.extractor._call_api(
            endpoint, query, "shorts", headers=self.headers, context=self.context)
        if not isinstance(response, Mapping) or not response:
            raise ShortsFeedError("unavailable")
        web_context = _mapping(_mapping(response.get("responseContext")).get("mainAppWebResponseContext"))
        if web_context.get("loggedOut") is True:
            raise ShortsFeedError("auth")
        if response.get("error"):
            raise ShortsFeedError("unavailable")
        return response

    def _enqueue(self, command, item_response=None, *, card=None):
        entry = _flat_entry(command, item_response=item_response, card=card)
        if entry is None or entry["id"] in self.seen_ids:
            return False
        self.seen_ids.add(entry["id"])
        self.queue.append(entry)
        return True

    def _start(self):
        self.started = True
        self.extractor.initialize()
        if not self.extractor.is_authenticated:
            raise ShortsFeedError("auth")
        # The Web page is read only to obtain client/session configuration. Its
        # recommendation items and continuation tokens are never used here.
        config = self.extractor._download_ytcfg("web", "shorts")
        if not isinstance(config, Mapping) or not config or config.get("LOGGED_IN") is False:
            raise ShortsFeedError("auth")
        self.config = configure_ytcfg(dict(config), self.account_context)
        self.headers = account_api_headers(self.extractor, self.config, self.account_context)
        self.context = self.extractor._extract_context(self.config)
        resolved = self._api("navigation/resolve_url", {"url": SHORTS_URL})
        endpoint = _mapping(_mapping(resolved.get("endpoint")).get("reelWatchEndpoint"))
        if not endpoint or endpoint.get("videoId"):
            # /shorts must resolve the feed entrance, never an arbitrary seed.
            raise ShortsFeedError("unavailable")
        query = {key: endpoint[key] for key in ("params", "inputType") if key in endpoint}
        query["disablePlayerResponse"] = True
        initial = self._api("reel/reel_item_watch", query)
        self.token = _continuation(initial)
        self._enqueue(initial.get("replacementEndpoint"), item_response=initial)
        entries = initial.get("entries")
        if isinstance(entries, list):
            for entry in entries[:MAX_PAGE_ENTRIES]:
                self._enqueue(_mapping(entry).get("command"), card=entry)
        if not self.queue and not self.token:
            raise ShortsFeedError("unavailable")

    def _page(self):
        if not self.token or self.token in self.seen_tokens:
            self.token = None
            return
        if self.page_count >= self.max_pages:
            raise ShortsFeedError("unavailable")
        token = self.token
        self.seen_tokens.add(token)
        self.page_count += 1
        response = self._api("reel/reel_watch_sequence", {"sequenceParams": token})
        self.token = _continuation(response)
        entries = response.get("entries")
        if not isinstance(entries, list):
            if self.token:
                raise ShortsFeedError("unavailable")
            entries = []
        added = 0
        for entry in entries[:MAX_PAGE_ENTRIES]:
            added += self._enqueue(_mapping(entry).get("command"), card=entry)
        if added:
            self.empty_pages = 0
        else:
            self.empty_pages += 1
            if self.token and (self.token in self.seen_tokens or self.empty_pages >= self.max_empty_pages):
                # An empty/ad-only page with a continuation is not end-of-feed.
                # An unusable/repeating cursor requires a refresh instead.
                raise ShortsFeedError("unavailable")

    def __next__(self):
        if self.closed:
            raise StopIteration
        if not self.started:
            self._start()
        while not self.queue:
            if not self.token:
                raise StopIteration
            self._page()
        return self.queue.popleft()
