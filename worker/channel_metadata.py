"""Small, read-only channel metadata helpers. Never resolve a video or URL."""
from collections.abc import Mapping
import re
import urllib.parse

CHANNEL_ID = re.compile(r"UC[A-Za-z0-9_-]{22}\Z")


def at(value, *keys):
    for key in keys:
        value = value.get(key) if isinstance(value, Mapping) else None
    return value


def channel_url(value=None, channel_id=None):
    if isinstance(channel_id, str) and CHANNEL_ID.fullmatch(channel_id):
        return "https://www.youtube.com/channel/" + channel_id + "/videos"
    if not isinstance(value, str) or len(value) > 4096 or "\\" in value or any(c.isspace() or ord(c) < 32 for c in value):
        return ""
    if value.startswith("/") and not value.startswith("//"):
        value = "https://www.youtube.com" + value
    try:
        uri = urllib.parse.urlsplit(value)
        if uri.scheme != "https" or uri.hostname not in ("youtube.com", "www.youtube.com", "m.youtube.com") or uri.port not in (None, 443) or uri.username or uri.password:
            return ""
        parts = uri.path.strip("/").split("/")
        size = 1 if parts[0].startswith("@") else 2
        if len(parts) not in (size, size + 1) or len(parts) > size and parts[-1] not in ("videos", "shorts", "streams", "playlists"):
            return ""
        name = urllib.parse.unquote(parts[0][1:] if size == 1 else parts[1])
        if not name or len(name) > 128 or any(c.isspace() or ord(c) < 32 or c in "/\\?#" for c in name):
            return ""
        if size == 2 and not (parts[0] in ("c", "user") or parts[0] == "channel" and CHANNEL_ID.fullmatch(name)):
            return ""
        return "https://www.youtube.com/" + "/".join(parts[:size]) + "/videos"
    except (ValueError, IndexError):
        return ""


def avatar_url(value):
    # Walk only the chosen avatar subtree, never arbitrary page/video thumbnails.
    pending = [(value, 0)]
    for _ in range(128):
        if not pending:
            break
        item, depth = pending.pop(0)
        if depth > 8:
            continue
        if isinstance(item, Mapping):
            url = item.get("url")
            if isinstance(url, str) and url.startswith("https://") and len(url) <= 4096:
                return url
            pending.extend((v, depth + 1) for v in list(item.values())[:16] if isinstance(v, (Mapping, list)))
        elif isinstance(item, list):
            pending.extend((v, depth + 1) for v in item[:16])
    return ""


def renderer_channel(renderer):
    if not isinstance(renderer, Mapping):
        return {}
    browse, title = {}, ""
    for key in ("shortBylineText", "longBylineText", "ownerText", "title"):
        runs = at(renderer, key, "runs")
        if not isinstance(runs, list):
            continue
        for run in runs[:8]:
            endpoint = at(run, "navigationEndpoint", "browseEndpoint")
            if isinstance(endpoint, Mapping) and channel_url(endpoint.get("canonicalBaseUrl"), endpoint.get("browseId")):
                browse = endpoint
                title = run.get("text", "")
                break
        if browse:
            break
    avatar = at(renderer, "channelThumbnailSupportedRenderers", "channelThumbnailWithLinkRenderer")
    if not isinstance(avatar, Mapping):
        avatar = at(renderer, "metadata", "lockupMetadataViewModel", "image", "decoratedAvatarViewModel")
    avatar_endpoint = at(avatar, "navigationEndpoint", "browseEndpoint") or at(avatar, "rendererContext", "commandContext", "onTap", "innertubeCommand", "browseEndpoint")
    if isinstance(avatar_endpoint, Mapping) and not browse:
        browse = avatar_endpoint
        rows = at(renderer, "metadata", "lockupMetadataViewModel", "metadata", "contentMetadataViewModel", "metadataRows")
        if isinstance(rows, list) and rows:
            parts = at(rows[0], "metadataParts")
            if isinstance(parts, list) and parts:
                title = at(parts[0], "text", "content") or ""
    url = channel_url(browse.get("canonicalBaseUrl"), browse.get("browseId"))
    result = {"channel_url": url} if url else {}
    identifier = browse.get("browseId")
    if isinstance(identifier, str) and CHANNEL_ID.fullmatch(identifier):
        result["channel_id"] = identifier
    if isinstance(title, str) and title:
        result["channel"] = title[:512]
    byline_id = browse.get("browseId")
    avatar_id = avatar_endpoint.get("browseId") if isinstance(avatar_endpoint, Mapping) else None
    thumbnail = "" if byline_id and avatar_id and byline_id != avatar_id else avatar_url(avatar)
    if thumbnail:
        result["channel_thumbnail"] = thumbnail
    return result


def enrich_entry(entry, renderer):
    if not isinstance(entry, Mapping):
        return entry
    metadata = renderer_channel(renderer)
    if metadata.get("channel_id") and entry.get("channel_id") and metadata["channel_id"] != entry["channel_id"]:
        return entry
    return {**metadata, **{key: value for key, value in entry.items() if value is not None and value != ""}}


def watch_owner(response):
    contents = at(response, "contents", "twoColumnWatchNextResults", "results", "results", "contents")
    if not isinstance(contents, list):
        return None
    for item in contents[:32]:
        owner = at(item, "videoSecondaryInfoRenderer", "owner", "videoOwnerRenderer")
        if not isinstance(owner, Mapping):
            continue
        browse = at(owner, "navigationEndpoint", "browseEndpoint")
        if not isinstance(browse, Mapping):
            continue
        url = channel_url(browse.get("canonicalBaseUrl"), browse.get("browseId"))
        if not url:
            continue
        runs = at(owner, "title", "runs")
        title = "".join(run.get("text", "") for run in runs[:8]
                        if isinstance(run, Mapping) and isinstance(run.get("text"), str)) if isinstance(runs, list) else ""
        return {"id": browse.get("browseId", ""), "url": url, "title": title[:512], "thumbnail": avatar_url(owner.get("thumbnail"))}
    return None
