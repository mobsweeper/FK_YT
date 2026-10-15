"""One-shot WEB watch-next recommendations and explicit video ratings.

stdin: {command?, url, cookiePath: null|string, accountContext: null|object,
        maxItems: 30, rating?: "like"|"dislike"|"none", incognito?: bool}
stdout: one JSON related result (including rating), or a rating.get/set result.
CLI: --url URL [--cookie-file PATH] [--max-items 30]. --account-context-stdin
accepts the selected channel object on stdin without exposing it in arguments.

This worker owns no existing feed cursor. Browsing only requests client config
and `next`. An explicit rating.set makes one allowlisted rating write followed
by a readback; no player resolution, feedback, comments, or history writes.
The caller cancels network work by terminating this disposable process.
"""

from __future__ import annotations

import argparse
from collections.abc import Mapping
import json
from pathlib import Path
import re
import sys
import urllib.parse

from feed_worker import normalize_entry
from channel_metadata import enrich_entry, watch_owner
from youtube_session import (QuietLogger, SessionError, account_api_headers,
                             close_without_cookie_save, configure_ytcfg,
                             create_youtube_dl, quiet_call, validate_account_context)


VIDEO_ID = re.compile(r"^[A-Za-z0-9_-]{11}$")
MAX_ITEMS = 30
MAX_PAGES = 3
MAX_LINE = 65536
MAX_DESCRIPTION = 32768
RATING_ENDPOINTS = {"like": "like/like", "dislike": "like/dislike", "none": "like/removelike"}
RATING_STATUSES = {"LIKE": "like", "DISLIKE": "dislike", "INDIFFERENT": "none"}
SAFE_MESSAGES = {
    "unsupported": "지원되지 않는 영상 주소 또는 YouTube 요청입니다.",
    "auth": "선택한 YouTube 채널의 로그인 연결을 갱신하세요.",
    "unavailable": "이 영상의 YouTube 정보를 불러올 수 없습니다.",
    "network": "YouTube 요청을 완료하지 못했습니다. 연결 상태를 확인하세요.",
    "rating": "이 영상의 YouTube 평가를 변경할 수 없습니다.",
}
UNVERIFIED_RATING_MESSAGE = "YouTube에서 평가 변경 결과를 확인하지 못했습니다. 다시 조회해 확인하세요."


class RelatedFeedError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(SAFE_MESSAGES[code])


def _mapping(value):
    return value if isinstance(value, Mapping) else {}


def _at(value, *keys):
    for key in keys:
        value = _mapping(value).get(key)
    return value


def _bounded_description(value):
    """Keep uploader text plain and cap its encoded protocol size."""
    if isinstance(value, str):
        parts = [value]
    elif isinstance(value, Mapping):
        if isinstance(value.get("content"), str):
            parts = [value["content"]]
        elif isinstance(value.get("simpleText"), str):
            parts = [value["simpleText"]]
        elif isinstance(value.get("runs"), list):
            parts = [_mapping(run).get("text") for run in value["runs"][:2048]]
            parts = [part for part in parts if isinstance(part, str)]
            if not parts and value["runs"]:
                return None
        else:
            return None
    else:
        return None
    result, remaining = [], MAX_DESCRIPTION
    for part in parts:
        # Text may contain command-looking strings; it is never interpreted.
        part = part[:MAX_DESCRIPTION].replace("\r\n", "\n").replace("\r", "\n")
        part = "".join(character for character in part
                       if (ord(character) >= 32 or character in "\n\t")
                       and ord(character) != 127 and not 0xD800 <= ord(character) <= 0xDFFF)
        encoded = part.encode("utf-8")[:remaining]
        text = encoded.decode("utf-8", errors="ignore")
        result.append(text)
        remaining -= len(text.encode("utf-8"))
        if remaining == 0 or len(encoded) < len(part.encode("utf-8")):
            break
    return "".join(result)


def parse_description(response, video_id):
    """Read only the current video's full description, without extra queries."""
    current_id = _at(response, "currentVideoEndpoint", "watchEndpoint", "videoId")
    if current_id is not None and current_id != video_id:
        return None
    # Pinned YoutubeIE uses this full body as its watch-next metadata fallback.
    # Do not walk other panels, nested recommendations, or entity mutations.
    panels = _mapping(response).get("engagementPanels")
    if isinstance(panels, list):
        for wrapper in panels[:64]:
            panel = _mapping(_mapping(wrapper).get("engagementPanelSectionListRenderer"))
            if (panel.get("panelIdentifier") != "engagement-panel-structured-description"
                    and panel.get("targetId") != "engagement-panel-structured-description"):
                continue
            items = _at(panel, "content", "structuredDescriptionContentRenderer", "items")
            if not isinstance(items, list):
                continue
            for item in items[:64]:
                body = _mapping(_mapping(item).get("expandableVideoDescriptionBodyRenderer"))
                for key in ("attributedDescriptionBodyText", "descriptionBodyText"):
                    description = _bounded_description(body.get(key))
                    if description is not None:
                        return description
    contents = _at(response, "contents", "twoColumnWatchNextResults", "results", "results", "contents")
    if isinstance(contents, list):
        for item in contents[:64]:
            info = _mapping(_mapping(item).get("videoSecondaryInfoRenderer"))
            for key in ("attributedDescription", "description"):
                description = _bounded_description(info.get(key))
                if description is not None:
                    return description
    return None


def _watch_query(video_id):
    # Match pinned YoutubeIE's existing simple viewer-discretion acknowledgement.
    return {"videoId": video_id, "contentCheckOk": True, "racyCheckOk": True}


def video_id_from_url(value):
    """Validate before network access and strip list/time/tracking parameters."""
    if not isinstance(value, str) or len(value) > 8192:
        raise RelatedFeedError("unsupported")
    value = value.strip()
    if (not value or "\\" in value
            or any(character.isspace() or ord(character) < 32 for character in value)):
        raise RelatedFeedError("unsupported")
    if value.startswith("//"):
        value = "https:" + value
    elif not value.lower().startswith(("https://", "http://")):
        value = "https://" + value
    try:
        parsed = urllib.parse.urlsplit(value)
        host = (parsed.hostname or "").lower()
        if (parsed.scheme.lower() not in ("http", "https") or parsed.username or parsed.password
                or parsed.port not in (None, 80 if parsed.scheme.lower() == "http" else 443)
                or host != "youtu.be" and host != "youtube.com" and not host.endswith(".youtube.com")):
            raise RelatedFeedError("unsupported")
        segments = parsed.path.strip("/").split("/")
        if host == "youtu.be" and len(segments) == 1:
            video_id = urllib.parse.unquote(segments[0])
        elif host != "youtu.be" and parsed.path == "/watch":
            values = urllib.parse.parse_qs(parsed.query).get("v", [])
            video_id = values[0] if len(values) == 1 else ""
        elif host != "youtu.be" and len(segments) == 2 and segments[0] in ("shorts", "live"):
            video_id = urllib.parse.unquote(segments[1])
        else:
            video_id = ""
    except ValueError:
        raise RelatedFeedError("unsupported") from None
    if not VIDEO_ID.fullmatch(video_id):
        raise RelatedFeedError("unsupported")
    return video_id


def _unwrap(value, key):
    """Current view models can wrap the same type twice."""
    for _ in range(3):
        child = _mapping(value).get(key)
        if not isinstance(child, Mapping):
            break
        value = child
    return _mapping(value)


def _rating_commands(command, depth=0):
    """Inspect known command envelopes only; never execute response endpoints."""
    if not isinstance(command, Mapping) or depth > 6:
        return []
    endpoint = command.get("likeEndpoint")
    if isinstance(endpoint, Mapping):
        return [(endpoint, command.get("commandMetadata"))]
    inner = command.get("innertubeCommand")
    if isinstance(inner, Mapping):
        return _rating_commands(inner, depth + 1)
    result = []
    for key in ("serialCommand", "commandExecutorCommand"):
        commands = _at(command, key, "commands")
        if isinstance(commands, list):
            for child in commands[:16]:
                result.extend(_rating_commands(child, depth + 1))
    return result


def _button_rating_evidence(button, video_id, status, *, modern=False):
    """Return state and capability evidence for a single scoped rating button."""
    button = _mapping(button)
    if not button:
        return None, False, False
    if any(key in button and not isinstance(button[key], bool) for key in ("isDisabled", "isTogglingDisabled", "isToggled")):
        return None, False, True
    commands = []
    disabled = button.get("isDisabled") is True or button.get("isTogglingDisabled") is True
    if modern:
        for key in ("defaultButtonViewModel", "toggledButtonViewModel"):
            view = _unwrap(button.get(key), "buttonViewModel")
            if "isDisabled" in view and not isinstance(view["isDisabled"], bool):
                return None, False, True
            disabled |= view.get("isDisabled") is True
            commands.extend(_rating_commands(view.get("onTap")))
    else:
        for key in ("defaultServiceEndpoint", "toggledServiceEndpoint"):
            commands.extend(_rating_commands(button.get(key)))
    supported, invalid = False, False
    for endpoint, metadata in commands:
        pending = endpoint.get("status")
        if (_at(endpoint, "target", "videoId") != video_id
                or not isinstance(pending, str) or pending not in RATING_STATUSES):
            invalid = True
            continue
        web = _at(metadata, "webCommandMetadata")
        if (metadata is not None and not isinstance(metadata, Mapping)
                or web is not None and (not isinstance(web, Mapping) or web.get("sendPost") is not True
                                       or web.get("apiUrl") != "/youtubei/v1/" + RATING_ENDPOINTS[RATING_STATUSES[pending]])):
            invalid = True
            continue
        if pending == status:
            supported = True
    toggled = button.get("isToggled")
    return toggled if isinstance(toggled, bool) else None, supported and not disabled, invalid


def _entity_rating(model, response):
    entity = _mapping(model.get("likeStatusEntity"))
    if (model.get("likeStatusEntityKey") and entity.get("key")
            and model["likeStatusEntityKey"] != entity["key"]):
        return "unknown"
    statuses = [entity.get("likeStatus")] if "likeStatus" in entity else []
    key = model.get("likeStatusEntityKey") or entity.get("key")
    mutations = _at(response, "frameworkUpdates", "entityBatchUpdate", "mutations")
    if isinstance(key, str) and key and isinstance(mutations, list):
        for mutation in mutations[:256]:
            candidate = _mapping(_at(mutation, "payload", "likeStatusEntity"))
            if candidate.get("key") == key:
                statuses.append(candidate.get("likeStatus"))
    normalized = [RATING_STATUSES.get(status) if isinstance(status, str) else None for status in statuses]
    if not normalized or any(status is None for status in normalized) or len(set(normalized)) != 1:
        return "unknown"
    return normalized[0]


def parse_rating(response, video_id, *, authenticated=False):
    """Read only the current video's primary watch toolbar, never other cards."""
    unknown = {"value": "unknown", "canRate": False}
    current_id = _at(response, "currentVideoEndpoint", "watchEndpoint", "videoId")
    if current_id is not None and current_id != video_id:
        return unknown
    contents = _at(response, "contents", "twoColumnWatchNextResults", "results", "results", "contents")
    if not isinstance(contents, list):
        return unknown
    found = []
    for item in contents[:64]:
        buttons = _at(item, "videoPrimaryInfoRenderer", "videoActions", "menuRenderer", "topLevelButtons")
        if not isinstance(buttons, list):
            continue
        legacy_buttons = {}
        for wrapper in buttons[:32]:
            wrapper = _mapping(wrapper)
            old = wrapper.get("segmentedLikeDislikeButtonRenderer")
            modern = wrapper.get("segmentedLikeDislikeButtonViewModel")
            direct = wrapper.get("likeButtonRenderer")
            toggle = _mapping(wrapper.get("toggleButtonRenderer"))
            if sum(isinstance(value, Mapping) for value in (old, modern, direct)) > 1:
                return unknown
            if isinstance(old, Mapping):
                like = _mapping(_at(old, "likeButton", "toggleButtonRenderer"))
                dislike = _mapping(_at(old, "dislikeButton", "toggleButtonRenderer"))
            elif isinstance(modern, Mapping):
                modern = _unwrap(modern, "segmentedLikeDislikeButtonViewModel")
                like_model = _unwrap(modern.get("likeButtonViewModel"), "likeButtonViewModel")
                dislike_model = _unwrap(modern.get("dislikeButtonViewModel"), "dislikeButtonViewModel")
                like = _unwrap(like_model.get("toggleButtonViewModel"), "toggleButtonViewModel")
                dislike = _unwrap(dislike_model.get("toggleButtonViewModel"), "toggleButtonViewModel")
            elif isinstance(direct, Mapping):
                value = RATING_STATUSES.get(direct.get("likeStatus")) if isinstance(direct.get("likeStatus"), str) else None
                if _at(direct, "target", "videoId") != video_id:
                    return unknown
                found.append((value or "unknown", direct.get("likesAllowed") is True))
                continue
            else:
                icon = _at(toggle, "defaultIcon", "iconType")
                if icon in ("LIKE", "DISLIKE"):
                    legacy_buttons[icon] = toggle
                continue
            like_on, like_allowed, like_invalid = _button_rating_evidence(like, video_id, "LIKE", modern=isinstance(modern, Mapping))
            dislike_on, dislike_allowed, dislike_invalid = _button_rating_evidence(dislike, video_id, "DISLIKE", modern=isinstance(modern, Mapping))
            if like_invalid or dislike_invalid:
                return unknown
            if isinstance(modern, Mapping):
                value = _entity_rating(like_model, response)
            else:
                value = ("unknown" if like_on is None or dislike_on is None or like_on and dislike_on else
                         "like" if like_on else "dislike" if dislike_on else "none")
            found.append((value, like_allowed and dislike_allowed))
        if legacy_buttons:
            like_on, like_allowed, like_invalid = _button_rating_evidence(legacy_buttons.get("LIKE"), video_id, "LIKE")
            dislike_on, dislike_allowed, dislike_invalid = _button_rating_evidence(legacy_buttons.get("DISLIKE"), video_id, "DISLIKE")
            if like_invalid or dislike_invalid:
                return unknown
            value = ("unknown" if like_on is None or dislike_on is None or like_on and dislike_on else
                     "like" if like_on else "dislike" if dislike_on else "none")
            found.append((value, like_allowed and dislike_allowed))
    if not found or len({value for value, _ in found}) != 1 or found[0][0] == "unknown":
        return unknown
    logged_out = _at(response, "responseContext", "mainAppWebResponseContext", "loggedOut")
    return {"value": found[0][0], "canRate": bool(authenticated and logged_out is False
                                                   and all(allowed for _, allowed in found))}


def _check_response(response, *, cookie_path=None, rating_write=False):
    if (not isinstance(response, Mapping) or not response or response.get("error") is not None
            or response.get("success") is False):
        raise RelatedFeedError("rating" if rating_write else "unavailable")
    if cookie_path is not None and _at(response, "responseContext", "mainAppWebResponseContext", "loggedOut") is True:
        raise RelatedFeedError("auth")
    if rating_write and not isinstance(response.get("responseContext"), Mapping):
        raise RelatedFeedError("rating")


def recommendation_roots(response, *, continuation=False):
    """Confine parsing to watch recommendations; never walk the entire response."""
    response = _mapping(response)
    if not continuation:
        watch = _mapping(_at(response, "contents", "twoColumnWatchNextResults"))
        secondary = _mapping(watch.get("secondaryResults"))
        results = _at(secondary, "secondaryResults", "results")
        if not isinstance(results, list):
            results = secondary.get("results")
        return [results] if isinstance(results, list) else []
    roots = []
    watch_feed = _at(response, "continuationContents", "watchNextFeedContinuation", "contents")
    if isinstance(watch_feed, list):
        roots.append(watch_feed)
    for command_key in ("onResponseReceivedEndpoints", "onResponseReceivedActions", "onResponseReceivedCommands"):
        commands = response.get(command_key)
        if not isinstance(commands, list):
            continue
        for command in commands[:64]:
            for action_key in ("appendContinuationItemsAction", "reloadContinuationItemsCommand"):
                action = _mapping(_mapping(command).get(action_key))
                # A comments/engagement-panel continuation may also be returned.
                if action.get("targetId") != "watch-next-feed":
                    continue
                items = action.get("continuationItems")
                if isinstance(items, list):
                    roots.append(items)
    return roots


def _lockup_entry(renderer, extractor):
    entry = quiet_call(extractor._extract_lockup_view_model, dict(renderer))
    if not isinstance(entry, Mapping):
        return entry
    entry = dict(enrich_entry(entry, renderer))
    metadata = _mapping(_at(renderer, "metadata", "lockupMetadataViewModel"))
    # Current WEB next cards have plain channel text without commandRuns. Their
    # channel endpoint moved to the decorated avatar instead of the text row.
    browse = _mapping(_at(metadata, "image", "decoratedAvatarViewModel", "rendererContext",
                          "commandContext", "onTap", "innertubeCommand", "browseEndpoint"))
    avatar_channel_id = extractor.ucid_or_none(browse.get("browseId"))
    if avatar_channel_id:
        if not entry.get("channel_id"):
            entry["channel_id"] = avatar_channel_id
        rows = _at(metadata, "metadata", "contentMetadataViewModel", "metadataRows")
        if not entry.get("channel") and isinstance(rows, list) and rows:
            parts = _mapping(rows[0]).get("metadataParts")
            if isinstance(parts, list) and parts:
                channel = _at(parts[0], "text", "content")
                if isinstance(channel, str):
                    entry["channel"] = channel[:512]
    return entry


def parse_recommendations(response, extractor, *, continuation=False):
    """Return flat VideoItem metadata and only a related-feed continuation token."""
    roots = recommendation_roots(response, continuation=continuation)
    items, token = [], None
    visited = 0

    def visit(value, depth=0):
        nonlocal token, visited
        visited += 1
        if depth > 12 or visited > 2048:
            return
        if isinstance(value, list):
            for child in value[:256]:
                visit(child, depth + 1)
            return
        if not isinstance(value, Mapping):
            return
        for key in ("compactVideoRenderer", "videoRenderer", "gridVideoRenderer"):
            candidate = value.get(key)
            if isinstance(candidate, Mapping):
                video_id = candidate.get("videoId")
                if isinstance(video_id, str) and VIDEO_ID.fullmatch(video_id):
                    adapted = dict(candidate)
                    # Watch-page compact cards use longBylineText; the pinned
                    # flat parser expects the equivalent shortBylineText field.
                    if not adapted.get("shortBylineText") and adapted.get("longBylineText"):
                        adapted["shortBylineText"] = adapted["longBylineText"]
                    entry = enrich_entry(quiet_call(extractor._extract_video, adapted), adapted)
                    item = normalize_entry(entry)
                    if item and item["kind"] == "video":
                        items.append(item)
                return
        lockup = value.get("lockupViewModel")
        if isinstance(lockup, Mapping):
            # Radios, mixes, podcasts, and playlists are not individual next videos.
            if lockup.get("contentType") == "LOCKUP_CONTENT_TYPE_VIDEO":
                entry = _lockup_entry(lockup, extractor)
                item = normalize_entry(entry)
                if item and item["kind"] == "video":
                    items.append(item)
            return
        continuation_item = value.get("continuationItemRenderer")
        if isinstance(continuation_item, Mapping):
            candidate = _at(continuation_item, "continuationEndpoint", "continuationCommand", "token")
            if isinstance(candidate, str) and 0 < len(candidate) <= 16384 and token is None:
                token = candidate
            return
        # These containers belong to the scoped recommendation surface. Do not
        # follow menu endpoints, ads, comments, player data, or nested arbitrary dicts.
        section = value.get("itemSectionRenderer")
        if isinstance(section, Mapping) and section.get("targetId") in (None, "watch-next-feed"):
            visit(section.get("contents"), depth + 1)
        for key in ("compactAutoplayRenderer", "richGridRenderer"):
            container = value.get(key)
            if isinstance(container, Mapping):
                visit(container.get("contents"), depth + 1)
        rich = value.get("richItemRenderer")
        if isinstance(rich, Mapping):
            visit(rich.get("content"), depth + 1)

    for root in roots:
        visit(root)
    return items, token, bool(roots)


def load_related(request, *, factory=None):
    if not isinstance(request, Mapping):
        raise RelatedFeedError("unsupported")
    command = request.get("command", "related")
    if command not in ("related", "rating.get", "rating.set"):
        raise RelatedFeedError("unsupported")
    rating_value = request.get("rating")
    if command == "rating.set" and (not isinstance(rating_value, str) or rating_value not in RATING_ENDPOINTS):
        raise RelatedFeedError("unsupported")
    video_id = video_id_from_url(request.get("url"))
    incognito = request.get("incognito", False)
    if not isinstance(incognito, bool):
        raise RelatedFeedError("unsupported")
    if command == "rating.set" and incognito:
        raise RelatedFeedError("auth")
    limit = request.get("maxItems", MAX_ITEMS)
    if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
        raise RelatedFeedError("unsupported")
    limit = min(limit, MAX_ITEMS)
    try:
        account = None if incognito else validate_account_context(request.get("accountContext"))
    except SessionError:
        raise RelatedFeedError("unsupported") from None
    cookie_path = None if incognito else request.get("cookiePath")
    if cookie_path is not None and (not isinstance(cookie_path, str) or not cookie_path
                                    or not Path(cookie_path).is_file()):
        raise RelatedFeedError("auth")
    if account is not None and cookie_path is None:
        raise RelatedFeedError("auth")
    if command == "rating.set" and cookie_path is None:
        raise RelatedFeedError("auth")
    options = {"quiet": True, "logger": QuietLogger(), "noprogress": True,
               "cachedir": False, "socket_timeout": 12, "retries": 0,
               "extractor_retries": 0, "mark_watched": False, "skip_download": True,
               "getcomments": False, "extract_flat": True, "js_runtimes": {},
               "remote_components": []}
    if cookie_path is not None:
        options["cookiefile"] = cookie_path
    ydl = quiet_call(factory or create_youtube_dl, options, account)
    try:
        extractor = ydl.get_info_extractor("YoutubeTab")
        quiet_call(extractor.initialize)
        if cookie_path is not None and not extractor.is_authenticated:
            raise RelatedFeedError("auth")
        cfg = quiet_call(extractor._download_ytcfg, "web", video_id)
        if not isinstance(cfg, Mapping) or not cfg:
            raise RelatedFeedError("unavailable")
        if cookie_path is not None and cfg.get("LOGGED_IN") is False:
            raise RelatedFeedError("auth")
        cfg = configure_ytcfg(dict(cfg), account)
        headers = account_api_headers(extractor, cfg, account)
        context = extractor._extract_context(cfg)
        authenticated = (cookie_path is not None and extractor.is_authenticated
                         and cfg.get("LOGGED_IN") is True
                         and isinstance(headers.get("Authorization"), str) and bool(headers["Authorization"]))
        if command == "rating.set" and not authenticated:
            raise RelatedFeedError("auth")
        if command in ("rating.get", "rating.set"):
            response = quiet_call(extractor._call_api, "next", _watch_query(video_id), video_id,
                                  headers=headers, context=context)
            _check_response(response, cookie_path=cookie_path)
            rating = parse_rating(response, video_id, authenticated=authenticated)
            result = {"type": "rating", "sourceVideoId": video_id, **rating}
            if command == "rating.get":
                return result
            if _at(response, "responseContext", "mainAppWebResponseContext", "loggedOut") is not False:
                raise RelatedFeedError("auth")
            if not rating["canRate"]:
                raise RelatedFeedError("rating")
            # WEB rating bodies contain only target.videoId in addition to the
            # selected session context. Never forward renderer params/endpoints.
            # Source: github.com/SanuSanal/YouPipe/blob/main/lib/innertube/innertube.dart
            response = quiet_call(extractor._call_api, RATING_ENDPOINTS[rating_value],
                                  {"target": {"videoId": video_id}}, video_id,
                                  headers=headers, context=context)
            _check_response(response, cookie_path=cookie_path, rating_write=True)
            try:
                response = quiet_call(extractor._call_api, "next", _watch_query(video_id), video_id,
                                      headers=headers, context=context)
                _check_response(response, cookie_path=cookie_path)
                observed = parse_rating(response, video_id, authenticated=authenticated)
            except Exception:
                # The write already happened. Do not retry it or infer success
                # from the request, the old toolbar, or a diagnostic exception.
                observed = {"value": "unknown", "canRate": False}
            verified = observed["value"] == rating_value and observed["canRate"]
            result = {"type": "rating", "sourceVideoId": video_id, **observed, "verified": bool(verified)}
            if not verified:
                result["message"] = UNVERIFIED_RATING_MESSAGE
            return result
        query = _watch_query(video_id)
        seen_ids, seen_tokens = {video_id}, set()
        result_items, token, owner = [], None, None
        description = None
        rating = {"value": "unknown", "canRate": False}
        for page in range(MAX_PAGES):
            if page > 0:
                seen_tokens.add(query["continuation"])
            response = quiet_call(extractor._call_api, "next", query, video_id,
                                  headers=headers, context=context)
            _check_response(response, cookie_path=cookie_path)
            if page == 0:
                owner = watch_owner(response)
                rating = parse_rating(response, video_id, authenticated=authenticated)
                description = parse_description(response, video_id)
            items, token, scoped = parse_recommendations(response, extractor, continuation=page > 0)
            if not scoped:
                if page == 0 and owner is None and description is None:
                    raise RelatedFeedError("unavailable")
                token = None
                break
            for item in items:
                if item["id"] in seen_ids:
                    continue
                seen_ids.add(item["id"])
                result_items.append(item)
            if len(result_items) >= limit or not token or token in seen_tokens:
                break
            query = {"continuation": token}
        result = {"type": "related", "sourceVideoId": video_id, "source": "youtube-next",
                  "items": result_items[:limit], "channel": owner, "rating": rating,
                  "hasMore": len(result_items) > limit or bool(token and token not in seen_tokens)}
        if description is not None:
            result["description"] = description
        return result
    finally:
        # Cookie leases are caller-owned. yt-dlp close normally saves its jar;
        # suppress that write even when initialization/request/parsing fails.
        close_without_cookie_save(ydl)


def _read_json(stdin):
    line = stdin.readline(MAX_LINE + 1)
    if not line or len(line) > MAX_LINE:
        raise RelatedFeedError("unsupported")
    try:
        return json.loads(line)
    except (ValueError, TypeError):
        raise RelatedFeedError("unsupported") from None


def run(argv=None, *, stdin=None, stdout=None, factory=None):
    stdin, stdout = stdin or sys.stdin, stdout or sys.stdout
    parser = argparse.ArgumentParser(description="Read YouTube watch-page related recommendations")
    parser.add_argument("--url")
    parser.add_argument("--cookie-file")
    parser.add_argument("--max-items", type=int, default=MAX_ITEMS)
    parser.add_argument("--account-context-stdin", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.url is None:
            if args.cookie_file is not None or args.account_context_stdin:
                raise RelatedFeedError("unsupported")
            request = _read_json(stdin)
        else:
            request = {"url": args.url, "cookiePath": args.cookie_file, "maxItems": args.max_items,
                       "accountContext": _read_json(stdin) if args.account_context_stdin else None}
        response = load_related(request, factory=factory)
        status = 0
    except Exception as error:
        code = error.code if isinstance(error, (RelatedFeedError, SessionError)) else "network"
        response = {"type": "error", "code": code, "message": SAFE_MESSAGES.get(code, SAFE_MESSAGES["network"])}
        status = 1
    stdout.write(json.dumps(response, ensure_ascii=False, allow_nan=False) + "\n")
    stdout.flush()
    return status


if __name__ == "__main__":
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(run())
