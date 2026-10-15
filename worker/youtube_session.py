"""YouTube channel identities and isolated, verified channel cookie leases.

The bundled yt-dlp release is the adapter boundary. No global extractor patch,
browser database mutation, package installation, or automatic history write.
NDJSON commands: account.list and account.select. Cookie paths must be disposable
leases supplied by SessionService; account metadata belongs in its DPAPI store.
"""
from __future__ import annotations

import contextlib
import copy
import hashlib
import io
import json
import os
import re
import stat
import sys
import types
import urllib.parse
from pathlib import Path


MAX_LINE = 65536
SESSION_ID = re.compile(r"^[A-Za-z0-9_-]{1,256}$")
PAGE_ID = re.compile(r"^[0-9]{1,64}$")


class SessionError(Exception):
    def __init__(self, code="auth"):
        self.code = code
        super().__init__("YouTube channel session is unavailable")


class QuietLogger:
    def debug(self, *_args, **_kwargs):
        pass
    info = warning = error = debug


class DiscardOutput(io.TextIOBase):
    """Accept incidental upstream text without retaining private diagnostics."""
    def write(self, value):
        if not isinstance(value, str):
            raise TypeError("text output required")
        return len(value)

    def reconfigure(self, **_kwargs):
        pass

    def writable(self):
        return True


def quiet_call(function, *args, **kwargs):
    with contextlib.redirect_stdout(DiscardOutput()), contextlib.redirect_stderr(DiscardOutput()):
        return function(*args, **kwargs)


def parse_data_sync_id(value):
    if not isinstance(value, str) or len(value) > 1024 or value.count("||") != 1:
        raise SessionError("unsupported")
    first, second = value.split("||")
    if not SESSION_ID.fullmatch(first) or second and not SESSION_ID.fullmatch(second):
        raise SessionError("unsupported")
    return (first, second) if second else ("", first)


def validate_account_context(value):
    if value is None:
        return None
    if not isinstance(value, dict):
        raise SessionError("unsupported")
    sync = value.get("dataSyncId")
    page, _user = parse_data_sync_id(sync)
    account_id = value.get("id")
    page_id = value.get("pageId", "")
    auth_user = value.get("authUser", 0)
    if (not isinstance(account_id, str) or not account_id or len(account_id) > 320
            or not isinstance(page_id, str) or page != page_id
            or page_id and not PAGE_ID.fullmatch(page_id)
            or isinstance(auth_user, bool) or not isinstance(auth_user, int)
            or not 0 <= auth_user <= 99):
        raise SessionError("unsupported")
    result = {"id": account_id, "pageId": page_id, "dataSyncId": sync, "authUser": auth_user}
    signin = value.get("signinUrl")
    if signin is not None:
        result["signinUrl"] = validate_signin_url(signin, result)
    return result


def validate_signin_url(value, account_context):
    if not isinstance(value, str) or not value or len(value) > 8192 or any(
        character.isspace() or ord(character) < 32 for character in value
    ) or "\\" in value:
        raise SessionError("unsupported")
    if value.startswith("//"):
        value = "https:" + value
    elif value.startswith("/"):
        value = "https://www.youtube.com" + value
    try:
        parsed = urllib.parse.urlsplit(value)
        query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)
        if (parsed.scheme != "https" or parsed.hostname != "www.youtube.com"
                or parsed.port not in (None, 443) or parsed.username or parsed.password
                or parsed.path != "/signin" or parsed.fragment):
            raise SessionError("unsupported")
    except ValueError:
        raise SessionError("unsupported") from None
    for key in ("pageid", "authuser", "action_handle_signin", "next"):
        if len(query.get(key, [])) > 1:
            raise SessionError("unsupported")
    if query.get("pageid", [""])[0] != account_context["pageId"]:
        raise SessionError("unsupported")
    if query.get("authuser", ["0"])[0] != str(account_context["authUser"]):
        raise SessionError("unsupported")
    if query.get("action_handle_signin", [""])[0] != "true":
        raise SessionError("unsupported")
    # Preserve server-issued parameters; constrain the final navigation target.
    pairs = [(key, val) for key, val in urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
             if key != "next"]
    pairs.append(("next", "https://www.youtube.com/"))
    return urllib.parse.urlunsplit(("https", "www.youtube.com", "/signin",
                                    urllib.parse.urlencode(pairs), ""))


def configure_ytcfg(ytcfg, account_context):
    result = copy.deepcopy(ytcfg or {})
    account = validate_account_context(account_context)
    if account is None:
        return result
    delegated, user = parse_data_sync_id(account["dataSyncId"])
    result.pop("DELEGATED_SESSION_ID", None)
    result.pop("USER_SESSION_ID", None)
    if delegated:
        result["DELEGATED_SESSION_ID"] = delegated
    result["USER_SESSION_ID"] = user
    result["DATASYNC_ID"] = account["dataSyncId"]
    result["SESSION_INDEX"] = account["authUser"]
    if isinstance(result.get("INNERTUBE_CONTEXT"), dict):
        context_user = result["INNERTUBE_CONTEXT"].setdefault("user", {})
        context_user.pop("onBehalfOfUser", None)
        if delegated:
            context_user["onBehalfOfUser"] = delegated
    return result


def account_api_headers(ie, ytcfg, account_context, **kwargs):
    account = validate_account_context(account_context)
    cfg = configure_ytcfg(ytcfg, account)
    if account is not None:
        delegated, user = parse_data_sync_id(account["dataSyncId"])
        kwargs.update(delegated_session_id=delegated or None,
                      user_session_id=user, session_index=account["authUser"])
    return ie.generate_api_headers(ytcfg=cfg, **kwargs)


def selected_ydl_options(options, account_context):
    result = copy.deepcopy(options)
    account = validate_account_context(account_context)
    if account is None:
        return result
    args = result.setdefault("extractor_args", {})
    youtube = args.setdefault("youtube", {})
    youtube["data_sync_id"] = [account["dataSyncId"]]
    youtube["player_skip"] = list(dict.fromkeys([*youtube.get("player_skip", []), "webpage", "configs"]))
    # Preserve native authenticated/Premium clients and request Safari's muxed
    # HLS formats without admitting a default-account webpage response.
    youtube["player_client"] = ["default", "web_safari"]
    tab = args.setdefault("youtubetab", {})
    tab["skip"] = list(dict.fromkeys([*tab.get("skip", []), "webpage", "authcheck"]))
    return result


def install_account_context(ie, account_context):
    """Patch only this extractor instance; ytcfg from other accounts cannot win."""
    account = validate_account_context(account_context)
    if account is None or getattr(ie, "_fk_account_context", None) == account:
        return ie
    if getattr(ie, "_fk_account_context", None) is not None:
        # An extractor owns one cursor and one identity. Reusing an old wrapped
        # instance for a second identity would layer the previous auth closure.
        raise SessionError("auth")
    if not hasattr(ie, "generate_api_headers") or not hasattr(ie, "_extract_context"):
        return ie
    original_headers, original_context = ie.generate_api_headers, ie._extract_context
    original_cfg = ie.extract_ytcfg

    def headers(_self, *, ytcfg=None, **kwargs):
        delegated, user = parse_data_sync_id(account["dataSyncId"])
        kwargs.update(delegated_session_id=delegated or None, user_session_id=user,
                      session_index=account["authUser"])
        return original_headers(ytcfg=configure_ytcfg(ytcfg, account), **kwargs)

    def context(_self, ytcfg=None, default_client="web"):
        result = copy.deepcopy(original_context(configure_ytcfg(ytcfg, account), default_client))
        user = result.setdefault("user", {})
        user.pop("onBehalfOfUser", None)
        if account["pageId"]:
            user["onBehalfOfUser"] = account["pageId"]
        return result

    def extract_cfg(_self, video_id, webpage):
        cfg = original_cfg(video_id, webpage)
        # A missing webpage/config must stay falsey so yt-dlp can select its
        # complete default client config. Header/context wrappers still pin the
        # selected identity when that native fallback is used.
        return configure_ytcfg(cfg, account) if cfg else cfg

    ie.generate_api_headers = types.MethodType(headers, ie)
    ie._extract_context = types.MethodType(context, ie)
    ie.extract_ytcfg = types.MethodType(extract_cfg, ie)
    ie._fk_account_context = account
    return ie


def _disable_external_plugins():
    # Embedded YoutubeDL uses this global, not an options-dictionary key.
    # Apply before every constructor, including direct account operations.
    from yt_dlp.globals import plugin_dirs
    plugin_dirs.value = []


def create_youtube_dl(options, account_context=None):
    from yt_dlp import YoutubeDL
    _disable_external_plugins()
    account = validate_account_context(account_context)

    class ChannelYoutubeDL(YoutubeDL):
        def get_info_extractor(self, ie_key):
            return install_account_context(super().get_info_extractor(ie_key), account)

    return ChannelYoutubeDL(selected_ydl_options(options, account))


def walk_objects(value, depth=0):
    if depth > 30:
        return
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk_objects(child, depth + 1)
    elif isinstance(value, list):
        for child in value[:1000]:
            yield from walk_objects(child, depth + 1)


def text_value(value):
    if isinstance(value, str):
        return value[:512]
    if isinstance(value, dict):
        simple = value.get("simpleText")
        if isinstance(simple, str):
            return simple[:512]
        runs = value.get("runs", [])
        if not isinstance(runs, list):
            raise SessionError("unsupported")
        return "".join(run.get("text", "")[:512] for run in runs[:1000]
                       if isinstance(run, dict) and isinstance(run.get("text", ""), str))[:512]
    return ""


def parse_accounts(response, default_ytcfg):
    channels, seen = [], set()
    malformed = False
    if not isinstance(response, (dict, list)) or not isinstance(default_ytcfg, dict):
        raise SessionError("unsupported")
    index = default_ytcfg.get("SESSION_INDEX", 0)
    for item in walk_objects(response):
        if "accountName" not in item or item.get("isDisabled") is True or item.get("hasChannel") is not True:
            continue
        try:
            service = item.get("serviceEndpoint", {})
            if not isinstance(service, dict):
                raise SessionError("unsupported")
            endpoint = service.get("selectActiveIdentityEndpoint", {})
            if not isinstance(endpoint, dict):
                raise SessionError("unsupported")
            tokens = endpoint.get("supportedTokens", [])
            if not isinstance(tokens, list):
                raise SessionError("unsupported")
            page_id, sync, signin = "", None, None
            for token in tokens:
                if not isinstance(token, dict):
                    continue
                page_token = token.get("pageIdToken", {})
                data_token = token.get("datasyncIdToken", {})
                signin_token = token.get("accountSigninToken", {})
                if not all(isinstance(value, dict) for value in (page_token, data_token, signin_token)):
                    raise SessionError("unsupported")
                page_id = page_token.get("pageId", page_id)
                sync = data_token.get("datasyncIdToken", data_token.get("datasyncId", sync))
                signin = signin_token.get("signinUrl", signin)
            if not isinstance(sync, str) or not isinstance(signin, str):
                raise SessionError("unsupported")
            parse_data_sync_id(sync)
            # Settings/history keys may persist this identifier in plaintext.
            # Internal identity values remain inside the DPAPI metadata.
            account_id = "channel:" + hashlib.sha256(
                (str(index) + "\n" + sync).encode("utf-8")).hexdigest()
            account = validate_account_context({"id": account_id, "pageId": page_id,
                                                "dataSyncId": sync, "authUser": int(index),
                                                "signinUrl": signin})
            name, handle = text_value(item.get("accountName")), text_value(item.get("channelHandle"))
        except (SessionError, ValueError, TypeError):
            malformed = True
            continue
        if account_id in seen:
            continue
        seen.add(account_id)
        channels.append({**account, "name": name, "handle": handle,
                         "isSelected": item.get("isSelected") is True})
    if not channels:
        raise SessionError("unsupported" if malformed else "auth")
    return channels


def session_options(cookie_path):
    if not isinstance(cookie_path, str) or not cookie_path:
        raise SessionError("auth")
    source = Path(cookie_path).absolute()
    # Defense in depth for SessionService's trusted disposable leases. Check
    # the input entry and every parent before yt-dlp opens the cookie pathname.
    for entry in (source, *source.parents):
        if _is_link_or_reparse(entry):
            raise SessionError("unsupported")
    if not source.is_file():
        raise SessionError("auth")
    return {"quiet": True, "logger": QuietLogger(), "noprogress": True,
            "cookiefile": cookie_path, "cachedir": False, "socket_timeout": 12,
            "retries": 0, "extractor_retries": 0, "mark_watched": False,
            "skip_download": True, "getcomments": False, "extract_flat": True,
            "js_runtimes": {}, "remote_components": []}


def close_without_cookie_save(ydl):
    ydl.params["cookiefile"] = None
    quiet_call(ydl.close)


def bootstrap_session(cookie_path):
    from yt_dlp import YoutubeDL
    from yt_dlp.extractor.youtube import YoutubeTabIE
    _disable_external_plugins()
    ydl = quiet_call(YoutubeDL, session_options(cookie_path))
    try:
        ie = YoutubeTabIE(ydl)
        quiet_call(ie.initialize)
        page = quiet_call(ie._download_webpage, "https://www.youtube.com/", "account")
        cfg = ie.extract_ytcfg("account", page)
        if cfg.get("LOGGED_IN") is not True or not ie.is_authenticated:
            raise SessionError("auth")
        parse_data_sync_id(ie._extract_data_sync_id(cfg))
        return ydl, ie, cfg
    except Exception:
        close_without_cookie_save(ydl)
        raise


def list_accounts(cookie_path):
    ydl, ie, cfg = bootstrap_session(cookie_path)
    try:
        response = quiet_call(ie._call_api, "account/accounts_list", {
            "requestType": "ACCOUNTS_LIST_REQUEST_TYPE_CHANNEL_SWITCHER",
            "callCircumstance": "SWITCHING_USERS_FULL",
        }, "account", headers=ie.generate_api_headers(ytcfg=cfg), context=ie._extract_context(cfg))
        return parse_accounts(response, cfg)
    finally:
        close_without_cookie_save(ydl)


def _is_link_or_reparse(path):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    attributes = getattr(info, "st_file_attributes", None)
    if os.name == "nt" and attributes is None:
        raise SessionError("unsupported")
    return stat.S_ISLNK(info.st_mode) or bool(
        attributes is not None and attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def _check_output_parent(output):
    if not output.is_absolute() or not output.parent.is_dir():
        raise SessionError("unsupported")
    for directory in (output.parent, *output.parent.parents):
        if _is_link_or_reparse(directory):
            raise SessionError("unsupported")


def select_account_cookie(cookie_path, account_context, output_cookie_path):
    from yt_dlp import YoutubeDL
    from yt_dlp.extractor.youtube import YoutubeTabIE
    _disable_external_plugins()
    account = validate_account_context(account_context)
    if account is None or not account.get("signinUrl"):
        raise SessionError("auth")
    output = Path(output_cookie_path)
    _check_output_parent(output)
    if _is_link_or_reparse(output) or output.exists():
        raise SessionError("unsupported")
    ydl = quiet_call(YoutubeDL, session_options(cookie_path))
    owned_stat = None
    try:
        ie = YoutubeTabIE(ydl)
        quiet_call(ie.initialize)
        page = quiet_call(ie._download_webpage, account["signinUrl"], "account-select")
        cfg = ie.extract_ytcfg("account-select", page)
        actual_sync = ie._extract_data_sync_id(cfg)
        if (cfg.get("LOGGED_IN") is not True or not ie.is_authenticated
                or actual_sync != account["dataSyncId"]
                or ie._extract_session_index(cfg) != account["authUser"]):
            raise SessionError("auth")
        # The caller supplies a new path inside its private temporary directory.
        # Original imported cookie file and encrypted root session are never saved.
        _check_output_parent(output)
        # Exclusive creation cannot truncate a file that appeared during the
        # account request. yt-dlp accepts a file object, so it never reopens this
        # pathname and loses the creation/ownership decision.
        with output.open("x", encoding="utf-8") as destination:
            owned_stat = os.fstat(destination.fileno())
            _check_output_parent(output)
            quiet_call(ydl.cookiejar.save, filename=destination)
        return {"id": account["id"], "verified": True,
                "dataSyncId": actual_sync, "authUser": account["authUser"]}
    except Exception:
        if owned_stat is not None:
            try:
                _check_output_parent(output)
                current = output.lstat()
                if (owned_stat.st_ino and not _is_link_or_reparse(output)
                        and (current.st_dev, current.st_ino) == (owned_stat.st_dev, owned_stat.st_ino)):
                    output.unlink()
            except (OSError, SessionError):
                pass  # The C# owner can retry exact GUID-artifact cleanup after stop proof.
        raise
    finally:
        close_without_cookie_save(ydl)


def command_result(request):
    if not isinstance(request, dict):
        raise SessionError("unsupported")
    command = request.get("command")
    if command == "account.list":
        return {"type": "accounts", "channels": list_accounts(request.get("cookiePath"))}
    if command == "account.select":
        return {"type": "accountSelected", **select_account_cookie(
            request.get("cookiePath"), request.get("accountContext"), request.get("outputCookiePath"))}
    raise SessionError("unsupported")


def run(stdin=None, stdout=None):
    stdin, stdout = stdin or sys.stdin, stdout or sys.stdout
    line = stdin.readline(MAX_LINE + 1)
    request_id = ""
    try:
        if not line or len(line) > MAX_LINE:
            raise SessionError("unsupported")
        request = json.loads(line)
        request_id = request.get("requestId", "") if isinstance(request, dict) else ""
        if not isinstance(request_id, str) or len(request_id) > 128:
            request_id = ""
            raise SessionError("unsupported")
        response = command_result(request)
    except Exception as error:
        code = error.code if isinstance(error, SessionError) else "auth"
        response = {"type": "error", "code": code,
                    "message": "선택한 YouTube 채널을 확인하지 못했습니다. 로그인 연결을 갱신하세요."}
    response["requestId"] = request_id
    stdout.write(json.dumps(response, ensure_ascii=False, allow_nan=False) + "\n")
    stdout.flush()


if __name__ == "__main__":
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    run()
