"""Bundled yt-dlp CLI adapter with one authoritative selected channel.

The caller sends only account metadata on a private stdin pipe. Cookie paths
remain disposable SessionService leases. The existing per-extractor adapter
pins API headers and context; no installed yt-dlp class is modified.
"""
from __future__ import annotations

import json
import sys
import re
import urllib.parse
import io
from contextlib import redirect_stdout, redirect_stderr

from youtube_session import MAX_LINE, create_youtube_dl, validate_account_context


def _caption_language(value):
    return isinstance(value, str) and len(value) <= 64 and re.fullmatch(
        r"[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,16}){0,3}", value) is not None


def _caption_format(formats):
    """Select one supported safe URL without fetching the subtitle stream."""
    if not isinstance(formats, list):
        return None
    chosen, rank = None, 99
    for item in formats[:128]:
        if not isinstance(item, dict):
            continue
        extension = item.get("ext")
        if not isinstance(extension, str):
            continue
        priority = {"vtt": 0, "srt": 1, "ass": 2, "ssa": 3}.get(extension, 99)
        url = item.get("url")
        if priority >= rank or not isinstance(url, str) or not 1 <= len(url) <= 16384 or "\\" in url or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in url):
            continue
        try:
            parsed = urllib.parse.urlsplit(url)
            safe = (parsed.scheme == "https" and parsed.hostname == "www.youtube.com"
                    and parsed.path == "/api/timedtext" and parsed.port in (None, 443)
                    and not parsed.username and not parsed.password and not parsed.fragment)
        except ValueError:
            safe = False
        if not safe:
            continue
        chosen, rank = {"ext": item["ext"], "url": url}, priority
        name = item.get("name")
        if isinstance(name, str) and 0 < len(name) <= 256 and not any(ord(c) < 32 or ord(c) == 127 for c in name):
            chosen["name"] = name
    return chosen


def compact_subtitle_catalog(info):
    """Collapse original-source translation variants while retaining each target language."""
    if not isinstance(info, dict) or not isinstance(info.get("id"), str):
        raise ValueError("Invalid subtitle metadata")
    manual = info.get("subtitles") or {}
    automatic = info.get("automatic_captions") or {}
    if not isinstance(manual, dict) or not isinstance(automatic, dict):
        raise ValueError("Invalid subtitle metadata")
    result = {"id": info["id"], "subtitles": {}, "automatic_captions": {}}
    manual_keys = sorted((key for key in manual if _caption_language(key)), key=len, reverse=True)
    manual_key_set = set(manual_keys)
    for language in manual_keys[:1024]:
        if chosen := _caption_format(manual[language]):
            result["subtitles"][language] = [chosen]
    candidates = {}
    for language, formats in automatic.items():
        if not isinstance(language, str) or len(language) > 160 or re.fullmatch(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*", language) is None:
            continue
        target, score = language, 1
        if language.endswith("-orig"):
            target, score = language[:-5], 0
        else:
            # Earlier separators have longer suffixes, preserving the original
            # longest-source match without scanning every manual language.
            for separator in re.finditer("-", language):
                source = language[separator.end():]
                if source in manual_key_set and _caption_language(language[:separator.start()]):
                    target, score = language[:separator.start()], 2 if source == "en" else 3
                    break
        if not _caption_language(target) or (target not in candidates and len(candidates) >= 1024):
            continue
        chosen = _caption_format(formats)
        if chosen is not None and (target not in candidates or score < candidates[target][0]):
            candidates[target] = (score, chosen)
    remaining = max(0, 1024 - len(result["subtitles"]))
    result["automatic_captions"] = {language: [candidate[1]] for language, candidate in sorted(candidates.items())[:remaining]}
    return result


ERROR_CODES = frozenset(("signin_age", "bot", "member", "private", "unavailable", "rate_limit", "network", "session", "generic"))


def classify_error(error):
    """Return only an allowlisted reason; never publish upstream diagnostic text."""
    text = str(error).lower()
    groups = (
        ("bot", ("not a bot", "not-a-bot", "bot detection", "captcha challenge", "confirm you're human", "confirm you’re human")),
        ("rate_limit", ("429", "too many requests", "rate limit", "rate-limit")),
        ("member", ("members-only", "members only", "join this channel", "available to channel members", "membership required")),
        ("private", ("private video", "video is private", "this video is private")),
        ("generic", ("drm protected", "drm-protected")),
        ("signin_age", ("login_required", "sign in", "log in", "login required", "age-restricted", "age restricted", "confirm your age", "age verification")),
        ("session", ("cookies are no longer valid", "cookies have expired", "cookie file is missing", "invalid account request")),
        ("network", ("timed out", "timeout", "unable to resolve", "name resolution", "getaddrinfo", "network", "connection reset", "connection refused", "certificate verify failed")),
        ("unavailable", ("video unavailable", "video is unavailable", "video has been removed", "video has been deleted", "not available in your country", "copyright")),
    )
    return next((code for code, terms in groups if any(term in text for term in terms)), "generic")


def _safe_progress(line):
    match = re.fullmatch(r"FK_PROGRESS:\s*([0-9]{1,3}(?:\.[0-9]{1,8})?)%\s*", line)
    if match and 0 <= float(match[1]) <= 100:
        return "FK_PROGRESS:" + match[1] + "%"
    if line.startswith("[Merger]"):
        return "[Merger] Combining media"
    if line.startswith("[VideoRemuxer]"):
        return "[VideoRemuxer] Remuxing media"
    return None


class _BoundedOutput(io.StringIO):
    def __init__(self, limit, progress=None):
        super().__init__()
        self._limit, self._length = limit, 0
        self._progress, self._line = progress, ""
        self._raw_line, self._long_progress = False, None

    def reconfigure(self, **_kwargs):
        pass

    def _retain(self, value):
        if self._length + len(value) > self._limit:
            raise ValueError("Extraction output limit exceeded")
        self._length += len(value)
        return super().write(value)

    def _part(self, part, terminated):
        if self._raw_line:
            self._retain(part)
        elif self._long_progress:
            # Merger/remuxer paths are never retained, even on oversized lines.
            pass
        elif len(self._line) + len(part) > 512:
            prefix = self._line + part[:513 - len(self._line)]
            if prefix.startswith(("[Merger]", "[VideoRemuxer]")):
                self._long_progress = _safe_progress(prefix)
            else:
                self._retain(self._line)
                self._retain(part)
                self._raw_line = True
            self._line = ""
        else:
            self._line += part
        if terminated:
            if self._raw_line:
                self._retain("\n")
            elif self._long_progress:
                self._progress(self._long_progress)
            elif safe := _safe_progress(self._line.rstrip("\r")):
                self._progress(safe)
            else:
                self._retain(self._line)
                self._retain("\n")
            self._line, self._raw_line, self._long_progress = "", False, None

    def write(self, value):
        if not self._progress:
            return self._retain(value)
        start = 0
        while start < len(value):
            end = value.find("\n", start)
            if end < 0:
                self._part(value[start:], False)
                break
            self._part(value[start:end], True)
            start = end + 1
        return len(value)

    def finish(self):
        if self._line or self._long_progress:
            if self._long_progress:
                self._progress(self._long_progress)
            elif safe := _safe_progress(self._line.rstrip("\r")):
                self._progress(safe)
            else:
                self._retain(self._line)
            self._line, self._long_progress = "", None


def run(argv=None):
    # A failed extractor may have printed partial JSON containing signed URLs.
    # Release normal CLI stdout only on success and keep both captures bounded.
    # Configure the real pipes before replacing them with bounded StringIO.
    # main() cannot reconfigure the original Windows CP949/CP1252 destination
    # while redirected, and multilingual subtitle names must reach C# as UTF-8.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    destination = sys.stdout
    def progress(line):
        destination.write(line + "\n")
        destination.flush()
    output, errors = _BoundedOutput(32 * 1024 * 1024, progress), _BoundedOutput(2 * 1024 * 1024, progress)
    failure = None
    try:
        with redirect_stdout(output), redirect_stderr(errors):
            try:
                status = main(argv)
            finally:
                output.finish()
                errors.finish()
    except (Exception, KeyboardInterrupt, SystemExit) as exc:
        status, failure = 1, exc
    if status:
        code = classify_error(errors.getvalue() + "\n" + (str(failure) if failure else ""))
        print("FK_YT_ERROR:" + code, file=sys.stderr)
        return 1
    # Only withheld non-progress data remains. Avoid cloning the complete JSON
    # and a list of its lines while releasing it after successful extraction.
    output.seek(0)
    while chunk := output.read(65536):
        sys.stdout.write(chunk)
    sys.stdout.flush()
    # History marking can return exit 0 after a non-fatal warning. Return only
    # the allowlisted classification, never private URLs or account diagnostics.
    if "unable to mark" in errors.getvalue().lower():
        print("FK_YT_ERROR:" + classify_error(errors.getvalue()), file=sys.stderr)
        return 1
    return 0


def main(argv=None):
    import yt_dlp
    from yt_dlp.globals import IN_CLI, plugin_dirs
    from yt_dlp.postprocessor.ffmpeg import FFmpegPostProcessor

    IN_CLI.value = True
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    line = sys.stdin.readline(MAX_LINE + 1)
    if not line or len(line) > MAX_LINE:
        raise ValueError("Invalid account request")
    request = json.loads(line)
    if not isinstance(request, dict) or set(request) not in ({"accountContext"}, {"accountContext", "operation"}) or request.get("operation") not in (None, "subtitle_catalog"):
        raise ValueError("Invalid account request")
    account = validate_account_context(request["accountContext"])
    parsed = yt_dlp.parse_options(argv)
    plugin_dirs.value = parsed.options.plugin_dirs
    parsed.ydl_opts["cachedir"] = False
    if not parsed.urls:
        raise ValueError("Missing video URL")
    if parsed.options.ffmpeg_location:
        FFmpegPostProcessor._ffmpeg_location.set(parsed.options.ffmpeg_location)
    # All callers explicitly ignore configs and plugins. Parsing through the
    # pinned release retains yt-dlp's normal format/subtitle/download options.
    with create_youtube_dl(parsed.ydl_opts, account) as ydl:
        try:
            if request.get("operation") == "subtitle_catalog":
                # Prevent forced CLI output from publishing the large original
                # subtitle dictionaries or signed URLs before compaction.
                ydl.params.update({"dump_single_json": False, "forcejson": False,
                    "forceprint": {}, "print_to_file": {}, "quiet": True,
                    "simulate": True, "skip_download": True, "mark_watched": False,
                    "writeautomaticsub": True, "writesubtitles": False,
                    "subtitleslangs": ["^$"], "check_formats": False,
                    "ignore_no_formats_error": True, "writeinfojson": False,
                    "writethumbnail": False, "listsubtitles": False, "listformats": False})
                metadata = ydl.extract_info(parsed.urls[0], download=False)
                print(json.dumps(compact_subtitle_catalog(metadata), ensure_ascii=False))
                return 0
            return ydl.download(parsed.urls)
        finally:
            # The context manager owns the one close call. Its temporary lease
            # must remain usable during extraction but must never be resaved.
            ydl.params["cookiefile"] = None


def run_live_fragment_stream(argv):
    """One native live-from-start track on stdout; never a merged CLI stdout.

    The parent starts video and audio consumers concurrently. The native DASH
    downloader uses short-lived fragment files in the parent's private owned
    working directory; stdout is a backpressured binary pipe, not a recording.
    """
    import yt_dlp
    from yt_dlp.globals import IN_CLI, plugin_dirs
    from yt_dlp.utils import DownloadError

    class SafeLogger:
        code = "generic"

        def debug(self, _message):
            pass

        def warning(self, message):
            self.code = classify_error(message)

        error = warning

    logger = SafeLogger()
    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
        line = sys.stdin.readline(MAX_LINE + 1)
        request = json.loads(line) if line and len(line) <= MAX_LINE else None
        if (not isinstance(request, dict)
                or set(request) != {"accountContext", "operation", "track", "maxHeight"}
                or request["operation"] != "live_fragment_stream"
                or request["track"] not in ("video", "audio")
                or type(request["maxHeight"]) is not int
                or not 0 <= request["maxHeight"] <= 16384):
            raise ValueError("Invalid account request")
        account = validate_account_context(request["accountContext"])
        IN_CLI.value = True
        parsed = yt_dlp.parse_options(argv)
        plugin_dirs.value = []
        if (len(parsed.urls) != 1 or re.fullmatch(
                r"https://www\.youtube\.com/watch\?v=[A-Za-z0-9_-]{11}", parsed.urls[0]) is None):
            raise ValueError("Invalid video URL")
        # A caller-supplied general CLI output format cannot redirect bytes to a
        # file, concatenate two never-ending tracks, or print JSON into media.
        track_format = "bestvideo" if request["track"] == "video" else "bestaudio"
        track_format += "[protocol=http_dash_segments_generator]"
        if request["track"] == "video" and request["maxHeight"]:
            track_format += f"[height<={request['maxHeight']}]"

        fragment_progress = {"index": 0, "completed": 0}

        def bound_fragment(progress):
            # HttpFD's max_filesize only checks a known Content-Length. The
            # DASH progress is cumulative; its fragment_index advances only when
            # a fragment finishes. Bound this fragment, not the whole broadcast.
            # The fixed HttpFD block size limits overshoot to one 64 KiB block.
            downloaded = progress.get("downloaded_bytes", 0)
            index = progress.get("fragment_index", fragment_progress["index"])
            if downloaded - fragment_progress["completed"] > 64 * 1024 * 1024:
                raise DownloadError("Live fragment exceeded its size limit")
            if index > fragment_progress["index"]:
                fragment_progress.update(index=index, completed=downloaded)

        parsed.ydl_opts.update({
            "format": track_format, "live_from_start": True,
            "outtmpl": {"default": "-"}, "paths": {},
            "logger": logger, "quiet": True, "no_warnings": True,
            "noprogress": True, "logtostderr": True,
            "simulate": False, "skip_download": False, "ignoreerrors": False,
            "noplaylist": True, "cachedir": False, "mark_watched": False,
            "writeinfojson": False, "writethumbnail": False,
            "writesubtitles": False, "writeautomaticsub": False,
            "writeannotations": False, "getcomments": False,
            "forcejson": False, "dump_single_json": False, "forceprint": {},
            "print_to_file": {}, "postprocessors": [], "postprocessor_hooks": [],
            "progress_hooks": [bound_fragment], "download_archive": None,
            "external_downloader": {"default": "native"},
            "concurrent_fragment_downloads": 1, "continuedl": False,
            "nopart": False, "keep_fragments": False, "_no_ytdl_file": True,
            "skip_unavailable_fragments": False, "buffersize": 65536,
            "noresizebuffer": True, "max_filesize": 64 * 1024 * 1024,
            "socket_timeout": 15, "retries": 1, "fragment_retries": 1,
            "extractor_retries": 1, "overwrites": True,
        })
        with create_youtube_dl(parsed.ydl_opts, account) as ydl:
            try:
                result = ydl.download(parsed.urls)
            finally:
                ydl.params["cookiefile"] = None
        if result:
            print("FK_YT_ERROR:" + logger.code, file=sys.stderr, flush=True)
            return 1
        return 0
    except (Exception, KeyboardInterrupt, SystemExit) as error:
        code = classify_error(error)
        if code == "generic":
            code = logger.code
        print("FK_YT_ERROR:" + code, file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    if sys.argv[1:2] == ["--fk-live-fragment-stream"]:
        sys.exit(run_live_fragment_stream(sys.argv[2:]))
    sys.exit(run(sys.argv[1:]))
