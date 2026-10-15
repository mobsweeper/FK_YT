"""A single-cursor, lazy yt-dlp feed worker. stdout is NDJSON only."""

from __future__ import annotations

import contextlib
import json
import math
import re
import secrets
import sys
import time
import urllib.parse
from collections.abc import Mapping
from pathlib import Path

from recommendation_actions import (ACTIONS, RecommendationError, extract_feedback_actions,
                                    install_recommendation_support, validate_feedback_actions)
from youtube_session import (DiscardOutput, SessionError, create_youtube_dl, selected_ydl_options,
                             validate_account_context)
from shorts_feed import ShortsFeed, ShortsFeedError
from channel_metadata import channel_url, enrich_entry


MAX_BATCH = 90
MAX_REDIRECTS = 4
MAX_LINE = 65536
VIDEO_ID = re.compile(r"^[A-Za-z0-9_-]{11}$")
VIDEO_MIX_ID = re.compile(r"^RD([A-Za-z0-9_-]{11})$")
CHANNEL_TABS = frozenset(("videos", "shorts", "streams", "playlists"))
FEED_URLS = {
    "home": "https://www.youtube.com/feed/recommended",
    "shorts": "https://www.youtube.com/shorts",
    "subscriptions": "https://www.youtube.com/feed/subscriptions",
    "playlists": "https://www.youtube.com/feed/playlists",
    "watchlater": "https://www.youtube.com/playlist?list=WL",
    "history": "https://www.youtube.com/feed/history",
}
SAFE_MESSAGES = {
    "auth": "로그인 연결 갱신 필요: Firefox 또는 쿠키 파일을 다시 연결하세요.",
    "network": "목록을 불러오는 중 연결에 실패했습니다. 다시 시도해 주세요.",
    "unsupported": "이 주소 또는 목록 형식은 지원하지 않습니다.",
    "unavailable": "목록을 사용할 수 없습니다. 새로고침해 주세요.",
    "internal": "목록 작업자를 사용할 수 없습니다. 다시 시작해 주세요.",
    "feedback": "YouTube 추천 설정을 적용하지 못했습니다. 목록을 새로고침한 뒤 다시 시도하세요.",
}


class FeedError(Exception):
    def __init__(self, code: str):
        self.code = code
        super().__init__(SAFE_MESSAGES[code])


class QuietLogger:
    """Never send upstream diagnostics, profile paths, or cookie data to logs."""

    def debug(self, _message):
        pass

    info = debug
    warning = debug
    error = debug

    def progress_bar(self):
        return None


def quiet_call(function, *args, **kwargs):
    # Upstream normally uses the logger; discard any incidental output as well.
    with contextlib.redirect_stdout(DiscardOutput()), contextlib.redirect_stderr(DiscardOutput()):
        return function(*args, **kwargs)


def classify_error(error: Exception) -> str:
    if getattr(error, "code", None) in SAFE_MESSAGES:
        return error.code
    if isinstance(error, (FeedError, RecommendationError, SessionError, ShortsFeedError)):
        return error.code
    if isinstance(error, (TimeoutError, ConnectionError)):
        return "network"
    # Exception text is used for categorization only, never emitted.
    message = str(error).lower()
    name = type(error).__name__.lower()
    if any(token in message for token in (
        "sign in", "sign-in", "login required", "log in", "authentication",
        "cookies", "cookie", "account is required", "confirm you're not a bot",
    )):
        return "auth"
    if any(token in message for token in (
        "private", "unavailable", "not available", "not found", "http error 404",
        "deleted", "does not exist", "unable to recognize tab",
    )):
        return "unavailable"
    if isinstance(error, (TimeoutError, ConnectionError, OSError)) or any(
        token in name + " " + message for token in (
            "timeout", "timed out", "network", "httperror", "http error",
            "connection", "urlerror", "sslerror", "temporary failure",
        )
    ):
        return "network"
    if "unsupported" in message:
        return "unsupported"
    return "internal"


def playlist_browse_url(playlist_id: str, video_id: str = "") -> str:
    # A generated Mix has a watch-side playlist panel; its ordinary playlist
    # page can return an unsupported-playlist alert. Keep the server's seed,
    # or recover it only for the known RD + 11-character video ID form.
    if playlist_id.startswith("RD"):
        seed = video_id if VIDEO_ID.fullmatch(video_id) else ""
        if not seed:
            match = VIDEO_MIX_ID.fullmatch(playlist_id)
            seed = match[1] if match else ""
        if seed:
            return "https://www.youtube.com/watch?" + urllib.parse.urlencode(
                {"v": seed, "list": playlist_id})
    return "https://www.youtube.com/playlist?" + urllib.parse.urlencode({"list": playlist_id})


def browse_url(url: str) -> str:
    """Accept list/feed/channel URLs, and reject individual videos before extraction."""
    if not isinstance(url, str) or len(url) > 4096:
        raise FeedError("unsupported")
    url = url.strip()
    try:
        parsed = urllib.parse.urlsplit(url)
        if not parsed.scheme:
            url = "https:" + url if url.startswith("//") else "https://" + url
            parsed = urllib.parse.urlsplit(url)
    except ValueError:
        raise FeedError("unsupported") from None
    if parsed.scheme not in ("https", "http") or parsed.username or parsed.password:
        raise FeedError("unsupported")
    if parsed.hostname not in ("youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com"):
        raise FeedError("unsupported")
    try:
        port = parsed.port
    except ValueError:
        raise FeedError("unsupported") from None
    if port not in (None, 80, 443):
        raise FeedError("unsupported")
    path = parsed.path.rstrip("/")
    query = urllib.parse.parse_qs(parsed.query)
    playlist_id = (query.get("list") or [""])[0]
    if path in ("/playlist", "/watch") and playlist_id:
        # This function is used only for an explicit browse request/redirect.
        # A video carrying a list parameter opens that list without extracting it.
        # Mixes need the watch URL's seed to obtain their inline playlist.
        return playlist_browse_url(playlist_id, (query.get("v") or [""])[0])
    if path.startswith("/feed/") and len(path.split("/")) == 3:
        return urllib.parse.urlunsplit(("https", "www.youtube.com", path, parsed.query, ""))
    components = path.split("/")
    channel_parts = None
    if len(components) >= 2 and components[1].startswith("@") and len(components[1]) > 1:
        channel_parts = 2
    elif len(components) >= 3 and components[1] in ("channel", "c", "user") and components[2]:
        channel_parts = 3
    if channel_parts is not None and (
            len(components) == channel_parts or
            len(components) == channel_parts + 1 and components[-1] in CHANNEL_TABS):
        # A bare channel URL can return a nested videos/shorts/streams playlist.
        # An explicit videos tab keeps the public raw result a single lazy feed.
        if len(components) == channel_parts:
            path += "/videos"
        return urllib.parse.urlunsplit(("https", "www.youtube.com", path, parsed.query, ""))
    raise FeedError("unsupported")


def resolve_source(source: str) -> tuple[str, bool]:
    if not isinstance(source, str) or not source or len(source) > 4096:
        raise FeedError("unsupported")
    if source in FEED_URLS:
        return FEED_URLS[source], source in ("home", "shorts", "subscriptions")
    if source.startswith("search:"):
        query = source[7:].strip()
        if not query or len(query) > 1024:
            raise FeedError("unsupported")
        return "ytsearchall:" + query, True
    return browse_url(source), False


def number_or_none(value, *, integer=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not math.isfinite(value) or value < 0:
        return None
    return int(value) if integer else value


def text_or_empty(value, limit=4096):
    return value[:limit] if isinstance(value, str) else ""


def _thumbnail_candidate(value):
    if (not isinstance(value, str) or not value or len(value) > 4096 or "\\" in value
            or any(character.isspace() or ord(character) < 32 for character in value)):
        return None
    try:
        parsed = urllib.parse.urlsplit(value)
        if (parsed.scheme not in ("https", "http") or parsed.username is not None
                or parsed.password is not None or not parsed.hostname
                or not parsed.hostname.endswith((".ytimg.com", ".ggpht.com", ".googleusercontent.com"))
                or parsed.port not in (None, 443 if parsed.scheme == "https" else 80)):
            return None
    except ValueError:
        return None
    if parsed.scheme == "https":
        return value, True
    # Match ThumbnailCache's HTTPS CDN policy. Only known legacy CDN URLs can
    # be upgraded; userinfo, unusual ports and arbitrary hosts never qualify.
    return urllib.parse.urlunsplit(("https", parsed.hostname, parsed.path, parsed.query, parsed.fragment)), False


def thumbnail_url(entry) -> str:
    direct = _thumbnail_candidate(entry.get("thumbnail"))
    if direct is not None and direct[1]:
        return direct[0]
    thumbnails = entry.get("thumbnails")
    if not isinstance(thumbnails, (list, tuple)):
        return direct[0] if direct else ""
    candidates = [(thumb, candidate) for thumb in thumbnails[:64] if isinstance(thumb, Mapping)
                  and (candidate := _thumbnail_candidate(thumb.get("url"))) is not None]
    secure = [candidate for candidate in candidates if candidate[1][1]]
    if secure:
        candidates = secure
    elif direct is not None:
        return direct[0]
    if not candidates:
        return ""
    # Prefer a card-sized image when the extractor gives several resolutions.
    sized = [candidate for candidate in candidates if number_or_none(candidate[0].get("width")) is not None]
    card_sized = [candidate for candidate in sized if candidate[0]["width"] <= 640]
    selected = max(card_sized or sized, key=lambda candidate: candidate[0]["width"]) if sized else candidates[-1]
    return selected[1][0]


def normalize_entry(entry):
    if not isinstance(entry, Mapping):
        return None
    entry = enrich_entry(entry, entry.get("_recommendation_renderer"))
    item_id = text_or_empty(entry.get("id"), 256)
    url = text_or_empty(entry.get("url") or entry.get("webpage_url"))
    key = text_or_empty(entry.get("ie_key"), 128).lower()
    try:
        parsed = urllib.parse.urlsplit(url)
    except ValueError:
        return None
    query = urllib.parse.parse_qs(parsed.query)
    path = parsed.path.rstrip("/")
    playlist_id = (query.get("list") or [""])[0]
    channel_id = text_or_empty(entry.get("channel_id"), 256)
    if key == "youtube" and VIDEO_ID.fullmatch(item_id):
        kind = "video"
        url = "https://www.youtube.com/watch?v=" + item_id
    elif playlist_id and (key in ("youtubetab", "youtubeplaylist")
                          or path == "/watch" and not (query.get("v") or [""])[0]):
        kind, item_id = "playlist", playlist_id
        url = playlist_browse_url(item_id, (query.get("v") or [""])[0])
    elif path in ("/watch", "/shorts") or path.startswith("/shorts/"):
        video_id = (query.get("v") or [path.rsplit("/", 1)[-1]])[0]
        if not VIDEO_ID.fullmatch(video_id):
            return None
        kind, item_id = "video", video_id
        url = "https://www.youtube.com/watch?v=" + item_id
    elif playlist_id or entry.get("_type") in ("playlist", "multi_video"):
        kind, item_id = "playlist", playlist_id or item_id
        if not item_id:
            return None
        url = playlist_browse_url(item_id, (query.get("v") or [""])[0])
    elif key == "youtubetab" and (item_id.startswith("UC") or path.startswith(("/channel/", "/@", "/c/", "/user/"))):
        kind = "channel"
        item_id = item_id or channel_id or path
        if not item_id:
            return None
        if item_id.startswith("UC"):
            url = "https://www.youtube.com/channel/" + item_id
        else:
            try:
                url = browse_url(url)
            except FeedError:
                return None
    elif VIDEO_ID.fullmatch(item_id) and entry.get("_type", "video") == "video":
        kind = "video"
        url = "https://www.youtube.com/watch?v=" + item_id
    else:
        return None
    return {
        "id": item_id,
        "title": text_or_empty(entry.get("title"), 2048),
        "url": url,
        "channel": text_or_empty(entry.get("channel") or entry.get("uploader"), 512),
        "channelId": channel_id,
        "channelUrl": channel_url(entry.get("channel_url") or entry.get("uploader_url"), channel_id),
        "channelThumbnail": text_or_empty(entry.get("channel_thumbnail")),
        "thumbnail": thumbnail_url(entry),
        "duration": number_or_none(entry.get("duration")),
        "viewCount": number_or_none(entry.get("view_count"), integer=True),
        "kind": kind,
        # The pinned YouTubeTab extractor resolves both old and new members
        # badge styles into this field while leaving public/unknown items alone.
        "membersOnly": kind == "video" and entry.get("availability") == "subscriber_only",
    }


def default_factory(options, account_context=None):
    return create_youtube_dl(options, account_context)


class FeedWorker:
    def __init__(self, factory=None, *, clock=None, scan_factor=5, batch_seconds=35,
                 feedback_sender=None, navigation_factory=None):
        self.factory = factory or default_factory
        self.using_default_factory = factory is None
        self.feedback_sender = feedback_sender
        self.navigation_factory = navigation_factory
        self.navigator = None
        self.source = None
        self.clock = clock or time.monotonic
        self.scan_factor = scan_factor
        self.batch_seconds = batch_seconds
        self.ydl = None
        self.entries = None
        self.pending = None
        self.seen = set()
        self.generation = None
        self.deduplicate = False
        self.hide_members_only = True
        self.account_context = None
        self.recommendation_session = None
        self.feedback_handles = {}
        self.dismissed_videos = set()
        self.dismissed_channels = set()
        self.exhausted = True

    def close(self):
        entries, ydl, navigator = self.entries, self.ydl, self.navigator
        self.navigator = self.source = None
        self.entries = self.ydl = self.pending = None
        self.seen.clear()
        self.generation = None
        self.hide_members_only = True
        self.account_context = None
        self.recommendation_session = None
        self.feedback_handles.clear()
        self.dismissed_videos.clear()
        self.dismissed_channels.clear()
        self.exhausted = True
        resources = (entries, ydl) if entries is navigator else (entries, navigator, ydl)
        for resource in resources:
            close = getattr(resource, "close", None)
            if close:
                try:
                    quiet_call(close)
                except Exception:
                    pass

    def open(self, request):
        self.close()
        hide_members_only = request.get("hideMembersOnly", True)
        if not isinstance(hide_members_only, bool):
            raise FeedError("unsupported")
        self.hide_members_only = hide_members_only
        self.account_context = validate_account_context(request.get("accountContext"))
        url, self.deduplicate = resolve_source(request.get("source"))
        self.source = request.get("source")
        options = {
            "quiet": True,
            "logger": QuietLogger(),
            "noprogress": True,
            "mark_watched": False,
            "skip_download": True,
            "getcomments": False,
            "extract_flat": True,
            "socket_timeout": 12,
            "extractor_retries": 1,
            "retries": 1,
            "cachedir": False,
            "js_runtimes": {},
            "remote_components": [],
        }
        cookie_path = request.get("cookiePath")
        if cookie_path is not None:
            if not isinstance(cookie_path, str) or not cookie_path:
                raise FeedError("auth")
            if not Path(cookie_path).is_file():
                raise FeedError("auth") from None
            # SessionService supplies a per-process managed lease, never the import original.
            # Avoid another plaintext copy that could survive forced process termination.
            options["cookiefile"] = cookie_path
        options = selected_ydl_options(options, self.account_context)
        self.ydl = (quiet_call(self.factory, options, self.account_context) if self.using_default_factory
                    else quiet_call(self.factory, options))
        self.recommendation_session = quiet_call(
            install_recommendation_support, self.ydl, self.account_context)
        if self.navigation_factory is not None:
            self.navigator = self.navigation_factory(self.ydl, self.source, url, self.account_context)
        elif self.using_default_factory:
            if self.source == "home":
                from home_feed import HomeFeed
                self.navigator = HomeFeed(self.ydl, self.account_context)
            elif urllib.parse.urlsplit(url).path.rstrip("/").endswith("/videos") and channel_url(url):
                from channel_feed import ChannelFeed
                self.navigator = ChannelFeed(self.ydl, url, self.account_context)
        if self.navigator is not None:
            quiet_call(self.navigator.initialize)
            self.entries = iter(self.navigator)
            self.generation = request["generation"]
            self.exhausted = False
            return
        if request.get("source") == "shorts":
            self.entries = ShortsFeed(self.ydl, self.account_context)
            self.generation = request["generation"]
            self.exhausted = False
            return
        result = quiet_call(self.ydl.extract_info, url, download=False, process=False)
        redirects = set()
        for _ in range(MAX_REDIRECTS):
            if not isinstance(result, Mapping):
                raise FeedError("unavailable")
            if result.get("_type") not in ("url", "url_transparent"):
                break
            target = browse_url(result.get("url"))
            if target in redirects:
                raise FeedError("unsupported")
            redirects.add(target)
            result = quiet_call(self.ydl.extract_info, target, download=False, process=False)
        if not isinstance(result, Mapping) or result.get("_type") not in ("playlist", "multi_video"):
            raise FeedError("unsupported")
        entries = result.get("entries")
        if entries is None:
            raise FeedError("unavailable")
        self.entries = iter(entries)
        self.generation = request["generation"]
        self.exhausted = False

    def select_filter(self, request):
        if (self.navigator is None or self.entries is None or
                isinstance(request.get("previousGeneration"), bool) or
                request.get("previousGeneration") != self.generation or
                request.get("generation") <= self.generation or
                request.get("source") != self.source or
                validate_account_context(request.get("accountContext")) != self.account_context):
            raise FeedError("unavailable")
        handle = request.get("filterId")
        if not isinstance(handle, str) or not 1 <= len(handle) <= 128:
            raise FeedError("unsupported")
        quiet_call(self.navigator.select_filter, handle)
        self.entries = iter(self.navigator)
        self.pending = None
        self.seen.clear()
        self.feedback_handles.clear()
        self.generation = request["generation"]
        self.exhausted = False

    def _attach_feedback_handles(self, item, raw):
        if item["kind"] != "video" or not isinstance(raw, Mapping):
            return
        endpoints = raw.get("_recommendation_endpoints")
        if not isinstance(endpoints, Mapping):
            endpoints = extract_feedback_actions(raw.get("_recommendation_renderer"))
        handles = {}
        for action, validated in validate_feedback_actions(endpoints).items():
            handle = secrets.token_urlsafe(24)
            self.feedback_handles[handle] = (action, item["id"], item["channelId"], validated)
            handles[action] = handle
        if handles:
            item["recommendationActions"] = handles

    def recommendation_action(self, request):
        if (self.ydl is None or self.entries is None or self.generation != request["generation"]
                or validate_account_context(request.get("accountContext")) != self.account_context):
            raise FeedError("unavailable")
        action, video_id, handle = request.get("action"), request.get("videoId"), request.get("actionHandle")
        if not isinstance(action, str) or action not in ACTIONS or not isinstance(video_id, str) or not VIDEO_ID.fullmatch(video_id):
            raise FeedError("unsupported")
        if not isinstance(handle, str) or len(handle) > 128:
            raise FeedError("unsupported")
        stored = self.feedback_handles.get(handle)
        if stored is None or stored[:2] != (action, video_id):
            raise FeedError("unavailable")
        _action, _video_id, channel_id, endpoint = stored
        if self.feedback_sender is None and self.recommendation_session is None:
            raise FeedError("unavailable")
        # Consume before the write: a timeout may mean YouTube already applied
        # it. Reusing that handle must never submit an accidental second write.
        del self.feedback_handles[handle]
        if self.feedback_sender is not None:
            confirmed = quiet_call(self.feedback_sender, endpoint, video_id, self.account_context)
        else:
            if isinstance(self.entries, ShortsFeed) or self.navigator is not None:
                navigation = self.navigator or self.entries
                self.recommendation_session.ie = navigation.extractor
                self.recommendation_session.config = navigation.config
            confirmed = quiet_call(self.recommendation_session.send, endpoint, video_id)
        if confirmed is not True:
            raise FeedError("feedback")
        self.dismissed_videos.add(video_id)
        if action == "dont_recommend_channel" and channel_id:
            self.dismissed_channels.add(channel_id)
        self.feedback_handles = {key: value for key, value in self.feedback_handles.items()
                                 if value[1] != video_id and not (
                                     action == "dont_recommend_channel" and channel_id and value[2] == channel_id)}
        if self.pending is not None and (self.pending["id"] in self.dismissed_videos
                                        or self.pending["channelId"] in self.dismissed_channels):
            self.pending = None
        return {"action": action, "videoId": video_id, "channelId": channel_id, "success": True}

    def _read_item(self, budget):
        if self.pending is not None:
            item, self.pending = self.pending, None
            return item
        while not self.exhausted:
            if budget["remaining"] <= 0:
                raise FeedError("unavailable")
            if self.clock() > budget["deadline"]:
                raise FeedError("network")
            budget["remaining"] -= 1
            try:
                raw = quiet_call(next, self.entries)
            except StopIteration:
                self.exhausted = True
                return None
            item = normalize_entry(raw)
            if item is None:
                continue
            if self.hide_members_only and item["membersOnly"]:
                budget["skippedMembersOnly"] += 1
                continue
            if item["kind"] == "video" and (item["id"] in self.dismissed_videos
                                             or item["channelId"] in self.dismissed_channels):
                continue
            identity = (item["kind"], item["id"])
            if self.deduplicate and identity in self.seen:
                continue
            if self.deduplicate:
                self.seen.add(identity)
            self._attach_feedback_handles(item, raw)
            return item
        return None

    def events(self, request):
        request_id = request.get("requestId", "") if isinstance(request, Mapping) else ""
        generation = request.get("generation", 0) if isinstance(request, Mapping) else 0
        if not isinstance(request_id, str) or len(request_id) > 128:
            request_id = ""
        if isinstance(generation, bool) or not isinstance(generation, int) or generation < 0:
            generation = 0
        base = {"requestId": request_id, "generation": generation}
        operation_started = False
        count = 0
        budget = {"skippedMembersOnly": 0}
        try:
            if (not isinstance(request, Mapping) or not request_id
                    or isinstance(request.get("generation"), bool)
                    or request.get("generation") != generation):
                raise FeedError("unsupported")
            command = request.get("command")
            if command == "ping":
                yield {"type": "pong", **base}
                return
            if command == "feed.close":
                self.close()
                yield {"type": "done", **base, "hasMore": False, "count": 0}
                return
            if command == "recommendation.action":
                result = self.recommendation_action(request)
                yield {"type": "feedback.result", **base, **result}
                return
            batch_size = request.get("batchSize", MAX_BATCH)
            if isinstance(batch_size, bool) or not isinstance(batch_size, int) or not 1 <= batch_size <= MAX_BATCH:
                raise FeedError("unsupported")
            if command == "feed.open":
                operation_started = True
                self.open(request)
            elif command == "feed.select":
                operation_started = True
                self.select_filter(request)
            elif command == "feed.next":
                if self.entries is None or self.generation != generation:
                    raise FeedError("unavailable")
                operation_started = True
            else:
                raise FeedError("unsupported")
            if self.navigator is not None and command in ("feed.open", "feed.select"):
                yield {"type": "metadata", **base, "metadata": self.navigator.metadata}
            budget.update(remaining=max(MAX_BATCH, batch_size * self.scan_factor) + 1,
                          deadline=self.clock() + self.batch_seconds)
            for _ in range(batch_size):
                item = self._read_item(budget)
                if item is None:
                    break
                count += 1
                yield {"type": "item", **base, "item": item}
            if not self.exhausted:
                self.pending = self._read_item(budget)
            skipped = {"skippedMembersOnly": budget["skippedMembersOnly"]} if budget["skippedMembersOnly"] else {}
            yield {"type": "done", **base, "hasMore": self.pending is not None, "count": count, **skipped}
        except Exception as error:
            code = classify_error(error)
            if operation_started:
                self.close()
            if operation_started and count:
                # A failed iterator may already have advanced or become unusable.
                # Retain delivered items, but require a new open rather than retry
                # this cursor and risk skipping or duplicating the next page.
                skipped = {"skippedMembersOnly": budget["skippedMembersOnly"]} if budget["skippedMembersOnly"] else {}
                yield {"type": "done", **base, "hasMore": False, "count": count, **skipped,
                       "warning": {"code": code,
                                   "message": SAFE_MESSAGES[code] + " 받은 항목은 유지했습니다. 새로고침으로 다시 조회하세요."}}
            else:
                yield {"type": "error", **base, "code": code, "message": SAFE_MESSAGES[code]}


def run(stdin=None, stdout=None, *, factory=None):
    stdin = stdin or sys.stdin
    stdout = stdout or sys.stdout
    worker = FeedWorker(factory)
    try:
        while True:
            line = stdin.readline(MAX_LINE + 1)
            if not line:
                break
            if len(line) > MAX_LINE:
                # Drain this frame so its remainder cannot become another command.
                while line and not line.endswith("\n"):
                    line = stdin.readline(MAX_LINE + 1)
                request = None
            else:
                try:
                    request = json.loads(line)
                except (ValueError, RecursionError):
                    # json.loads also rejects oversized integers and excessive
                    # nesting outside JSONDecodeError. Discard only this frame.
                    request = None
            for event in worker.events(request):
                stdout.write(json.dumps(event, ensure_ascii=False, allow_nan=False) + "\n")
                stdout.flush()
    finally:
        worker.close()


if __name__ == "__main__":
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    run()
