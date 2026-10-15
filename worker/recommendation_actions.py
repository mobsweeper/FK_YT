"""Preserve explicit YouTube card feedback endpoints without exposing tokens."""

from __future__ import annotations

import copy
from collections.abc import Mapping


ACTIONS = frozenset(("not_interested", "dont_recommend_channel"))
_LABELS = {
    "not interested": "not_interested",
    "관심 없음": "not_interested",
    "don't recommend channel": "dont_recommend_channel",
    "do not recommend channel": "dont_recommend_channel",
    "채널 추천 안 함": "dont_recommend_channel",
    "채널 추천 안함": "dont_recommend_channel",
    "이 채널 추천 안 함": "dont_recommend_channel",
}


class RecommendationError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def _at(value, *keys):
    for key in keys:
        if not isinstance(value, Mapping):
            return None
        value = value.get(key)
    return value


def _text(value):
    if isinstance(value, str):
        return value[:512]
    if not isinstance(value, Mapping):
        return ""
    direct = value.get("simpleText") or value.get("content")
    if isinstance(direct, str):
        return direct[:512]
    runs = value.get("runs")
    if isinstance(runs, (list, tuple)):
        return "".join(run.get("text", "") for run in runs[:32]
                       if isinstance(run, Mapping) and isinstance(run.get("text"), str))[:512]
    return ""


def _bounded_string(value, limit):
    return (isinstance(value, str) and 0 < len(value) <= limit
            and all(32 < ord(character) < 127 for character in value))


def _command_payload(command):
    if not isinstance(command, Mapping):
        return None
    metadata = _at(command, "commandMetadata", "webCommandMetadata")
    if metadata is not None and (not isinstance(metadata, Mapping)
                                 or metadata.get("apiUrl") != "/youtubei/v1/feedback"
                                 or metadata.get("sendPost") is not True):
        return None
    endpoint = command.get("feedbackEndpoint")
    if not isinstance(endpoint, Mapping) or not _bounded_string(endpoint.get("feedbackToken"), 16384):
        return None
    payload = {"feedbackToken": endpoint["feedbackToken"]}
    for key in ("isFeedbackTokenUnencrypted", "shouldMerge"):
        if key in endpoint:
            if not isinstance(endpoint[key], bool):
                return None
            payload[key] = endpoint[key]
    if _bounded_string(endpoint.get("cpn"), 512):
        payload["cpn"] = endpoint["cpn"]
    if _bounded_string(command.get("clickTrackingParams"), 8192):
        payload["clickTrackingParams"] = command["clickTrackingParams"]
    return payload


def _direct_payloads(command, depth=0):
    # Follow only command envelopes. Do not follow feedbackEndpoint.actions:
    # those contain UI replacement and undo tokens with different semantics.
    if not isinstance(command, Mapping) or depth > 4:
        return []
    payload = _command_payload(command)
    if payload is not None:
        return [payload]
    if "feedbackEndpoint" in command:
        return []
    inner = command.get("innertubeCommand")
    if isinstance(inner, Mapping):
        return _direct_payloads(inner, depth + 1)
    commands = _at(command, "commandExecutorCommand", "commands")
    if isinstance(commands, (list, tuple)):
        return [payload for subcommand in commands[:16]
                for payload in _direct_payloads(subcommand, depth + 1)]
    return []


def _menu_candidate(candidate):
    label = _text(candidate.get("text") or candidate.get("title"))
    label = " ".join(label.replace("’", "'").casefold().split())
    action = _LABELS.get(label)
    icon = _at(candidate, "icon", "iconType")
    # NOT_INTERESTED is an unambiguous typed menu icon. REMOVE is also used
    # for history/playlist removal, so that icon alone never selects a channel.
    if action is None and icon == "NOT_INTERESTED":
        action = "not_interested"
    if action is None:
        return None
    if icon == "NOT_INTERESTED" and action != "not_interested":
        return None
    command = candidate.get("serviceEndpoint") or _at(
        candidate, "rendererContext", "commandContext", "onTap")
    payloads = _direct_payloads(command)
    return (action, payloads[0]) if len(payloads) == 1 else None


def extract_feedback_actions(renderer):
    """Return only clearly classified card menu actions; never infer from title."""
    if not isinstance(renderer, Mapping):
        return {}
    roots = [renderer.get("menu"), renderer.get("menuButton"),
             _at(renderer, "metadata", "lockupMetadataViewModel", "menuButton")]
    stack = [(root, 0) for root in roots if isinstance(root, Mapping)]
    found, conflicts = {}, set()
    visited = 0
    while stack and visited < 512:
        value, depth = stack.pop()
        visited += 1
        if depth > 16:
            continue
        if isinstance(value, (list, tuple)):
            stack.extend((child, depth + 1) for child in value[:128])
            continue
        if not isinstance(value, Mapping):
            continue
        candidate = value.get("menuServiceItemRenderer") or value.get("listItemViewModel")
        if isinstance(candidate, Mapping):
            action = _menu_candidate(candidate)
            if action:
                name, payload = action
                if name in found and found[name] != payload:
                    conflicts.add(name)
                else:
                    found[name] = payload
            continue
        stack.extend((child, depth + 1) for child in list(value.values())[:64])
    return {name: payload for name, payload in found.items() if name not in conflicts}


def validate_feedback_actions(endpoints):
    """Copy the allowlisted fields from metadata produced by our adapters."""
    if not isinstance(endpoints, Mapping):
        return {}
    result = {}
    for action in ACTIONS:
        endpoint = endpoints.get(action)
        if not isinstance(endpoint, Mapping):
            continue
        payload = _command_payload({"feedbackEndpoint": endpoint,
                                    "clickTrackingParams": endpoint.get("clickTrackingParams")})
        if payload is not None:
            result[action] = payload
    return result


class RecommendationSession:
    def __init__(self, ydl, account_context=None):
        self.ie = ydl.get_info_extractor("YoutubeTab")
        self.account_context = copy.deepcopy(account_context)
        self.config = None
        self.data = None
        self._wrap_renderers()
        self._wrap_config()

    def _wrap_renderers(self):
        for method_name in ("_extract_video", "_extract_lockup_view_model"):
            original = getattr(self.ie, method_name)

            def wrapper(renderer, *args, _original=original, **kwargs):
                from channel_metadata import enrich_entry
                result = _original(renderer, *args, **kwargs)
                result = enrich_entry(result, renderer)
                if isinstance(result, Mapping):
                    endpoints = extract_feedback_actions(renderer)
                    if endpoints:
                        result = dict(result)
                        result["_recommendation_endpoints"] = endpoints
                return result

            setattr(self.ie, method_name, wrapper)

    def _wrap_config(self):
        original_data = self.ie._extract_data

        def extract_data(*args, **kwargs):
            result = original_data(*args, **kwargs)
            if isinstance(result, tuple) and len(result) == 2:
                self.data, self.config = result
            return result

        self.ie._extract_data = extract_data
        original_config = self.ie._download_ytcfg

        def download_config(*args, **kwargs):
            result = original_config(*args, **kwargs)
            if isinstance(result, Mapping):
                self.config = result
            return result

        self.ie._download_ytcfg = download_config

    def send(self, endpoint, video_id):
        from youtube_session import account_api_headers, configure_ytcfg

        if self.config is None:
            raise RecommendationError("auth")
        config = configure_ytcfg(self.config, self.account_context)
        headers = account_api_headers(self.ie, config, self.account_context)
        if not headers.get("Authorization"):
            raise RecommendationError("auth")
        context = self.ie._extract_context(config)
        if endpoint.get("clickTrackingParams"):
            context = copy.deepcopy(context)
            context["clickTracking"] = {"clickTrackingParams": endpoint["clickTrackingParams"]}
        query = {"feedbackTokens": [endpoint["feedbackToken"]],
                 "isFeedbackTokenUnencrypted": endpoint.get("isFeedbackTokenUnencrypted", False),
                 "shouldMerge": endpoint.get("shouldMerge", False)}
        if endpoint.get("cpn"):
            query["feedbackContext"] = {"cpn": endpoint["cpn"]}
        # Exactly one write. Never auto-retry ambiguous feedback failures.
        response = self.ie._call_api("feedback", query, video_id, headers=headers,
                                     context=context, note="Applying recommendation preference")
        confirmations = response.get("feedbackResponses") if isinstance(response, Mapping) else None
        if (not isinstance(confirmations, list) or not confirmations
                or any(not isinstance(value, Mapping) or value.get("isProcessed") is not True
                       for value in confirmations)):
            raise RecommendationError("feedback")
        return True


def install_recommendation_support(ydl, account_context=None):
    # Fixture factories need no yt-dlp package, browser, or account access.
    if not callable(getattr(ydl, "get_info_extractor", None)):
        return None
    return RecommendationSession(ydl, account_context)
