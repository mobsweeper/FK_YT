"""Offline selected-account tests with synthetic identities only."""
import copy
import io
import json
import os
import subprocess
import sys
import textwrap
import tracemalloc
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import youtube_session as session


PRIMARY = {"id": "primary:111", "pageId": "", "dataSyncId": "111||", "authUser": 0,
           "signinUrl": "/signin?action_handle_signin=true&authuser=0&next=%2F&skip_identity_prompt=true"}
BRAND = {"id": "page:222", "pageId": "222", "dataSyncId": "222||111", "authUser": 0,
         "signinUrl": "/signin?action_handle_signin=true&authuser=0&pageid=222&next=%2F"}


def account_item(context, selected=False):
    tokens = [{"accountSigninToken": {"signinUrl": context["signinUrl"]}},
              {"datasyncIdToken": {"datasyncIdToken": context["dataSyncId"]}}]
    if context["pageId"]:
        tokens.append({"pageIdToken": {"pageId": context["pageId"]}})
    return {"accountItemRenderer": {"accountName": {"runs": [{"text": "Synthetic channel"}]},
        "channelHandle": {"simpleText": "@synthetic"}, "hasChannel": True,
        "isSelected": selected, "serviceEndpoint": {
            "selectActiveIdentityEndpoint": {"supportedTokens": tokens}}}}


class ContextTests(unittest.TestCase):
    def test_embedded_constructors_disable_external_plugins_before_import(self):
        # A fresh process is essential: yt-dlp loads plugins only once, so a
        # previously constructed downloader would otherwise hide the defect.
        test_root = Path(__file__).resolve().parents[1] / "artifacts" / "final-hardening-20261015" / "worker"
        directory = test_root / ("plugin-fixture-" + uuid.uuid4().hex)
        plugin = directory / "plugins" / "fixture" / "yt_dlp_plugins" / "extractor" / "inert_marker.py"
        plugin.parent.mkdir(parents=True)
        source = directory / "source.txt"
        output = directory / "selected.txt"
        marker = directory / "marker.txt"
        plugin.write_text("import os\nfrom pathlib import Path\n"
                          "Path(os.environ['FK_PLUGIN_TEST_MARKER']).write_text('inert fixture', encoding='utf-8')\n",
                          encoding="utf-8")
        source.write_text("# Netscape HTTP Cookie File\n", encoding="utf-8")
        script = textwrap.dedent("""
            import json
            import os
            import sys
            from pathlib import Path
            from unittest.mock import patch
            import youtube_session as session
            import yt_dlp
            import yt_dlp.extractor.youtube
            from yt_dlp.globals import plugin_dirs

            # Restrict the loader to our inert fixture even for the positive
            # control; no real user/system plugin directory is searched.
            directory = Path(os.environ['FK_PLUGIN_TEST_ROOT'])
            plugin_dirs.value = [str(directory / 'plugins')]
            options = {'quiet': True, 'cachedir': False, 'skip_download': True,
                       'mark_watched': False, 'js_runtimes': {}, 'remote_components': []}
            account = {'id': 'synthetic', 'pageId': '', 'dataSyncId': '111||', 'authUser': 0,
                       'signinUrl': '/signin?action_handle_signin=true&authuser=0'}

            class OfflineTabIE:
                is_authenticated = True
                def __init__(self, ydl):
                    pass
                def initialize(self):
                    pass
                def _download_webpage(self, *_args):
                    return 'synthetic fixture'
                def extract_ytcfg(self, *_args):
                    return {'LOGGED_IN': True, 'DATASYNC_ID': '111||', 'SESSION_INDEX': 0}
                def _extract_data_sync_id(self, cfg):
                    return cfg['DATASYNC_ID']
                def _extract_session_index(self, cfg):
                    return cfg['SESSION_INDEX']

            mode = sys.argv[1]
            with patch.object(yt_dlp.extractor.youtube, 'YoutubeTabIE', OfflineTabIE):
                if mode == 'control':
                    ydl = yt_dlp.YoutubeDL(options)
                    session.close_without_cookie_save(ydl)
                elif mode == 'factory':
                    ydl = session.create_youtube_dl(options)
                    session.close_without_cookie_save(ydl)
                elif mode == 'bootstrap':
                    ydl, _ie, _cfg = session.bootstrap_session(str(directory / 'source.txt'))
                    session.close_without_cookie_save(ydl)
                elif mode == 'select':
                    result = session.select_account_cookie(str(directory / 'source.txt'), account,
                                                           str(directory / 'selected.txt'))
                    assert result['verified'] is True
                else:
                    raise AssertionError('Unknown fixture mode')
            print(json.dumps({'pluginImported': Path(os.environ['FK_PLUGIN_TEST_MARKER']).exists(),
                              'pluginDirs': plugin_dirs.value}))
        """)
        env = os.environ.copy()
        env.pop("YTDLP_NO_PLUGINS", None)
        env.update(FK_PLUGIN_TEST_ROOT=str(directory), FK_PLUGIN_TEST_MARKER=str(marker),
                   APPDATA=str(directory / "appdata"), XDG_CONFIG_HOME=str(directory / "xdg"),
                   USERPROFILE=str(directory / "home"), HOME=str(directory / "home"))
        try:
            for mode in ("control", "factory", "bootstrap", "select"):
                with self.subTest(mode=mode):
                    marker.unlink(missing_ok=True)
                    result = subprocess.run([sys.executable, "-B", "-c", script, mode],
                                            cwd=directory, env=env, capture_output=True, text=True, timeout=30)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    summary = json.loads(result.stdout)
                    self.assertEqual(summary["pluginImported"], mode == "control")
                    if mode != "control":
                        self.assertEqual(summary["pluginDirs"], [])
        finally:
            for owned_file in (plugin, source, output, marker):
                owned_file.unlink(missing_ok=True)
            for owned_directory in (plugin.parent, plugin.parent.parent, plugin.parent.parent.parent,
                                    plugin.parent.parent.parent.parent, directory):
                owned_directory.rmdir()

    def test_primary_and_brand_and_inconsistent_id_rejected(self):
        self.assertEqual(session.parse_data_sync_id("111||"), ("", "111"))
        self.assertEqual(session.parse_data_sync_id("222||111"), ("222", "111"))
        self.assertEqual(session.validate_account_context(BRAND)["pageId"], "222")
        with self.assertRaises(session.SessionError):
            session.validate_account_context({**BRAND, "pageId": "333"})
        for bad in ("", "111", "111|||", "222||111||333", "a\nb||c"):
            with self.subTest(bad=bad), self.assertRaises(session.SessionError):
                session.parse_data_sync_id(bad)

    def test_signin_only_exact_youtube_and_matching_target(self):
        for value in ("https://evil.test/signin?action_handle_signin=true",
                      "https://www.youtube.com@evil.test/signin",
                      BRAND["signinUrl"].replace("pageid=222", "pageid=333"),
                      BRAND["signinUrl"] + "&authuser=1",
                      BRAND["signinUrl"].replace("authuser=0", "authuser=1"),
                      "https://www.youtube.com/\\evil/signin",
                      "https://www.youtube.com/watch?v=dQw4w9WgXc"):
            with self.subTest(value=value), self.assertRaises(session.SessionError):
                session.validate_account_context({**BRAND, "signinUrl": value})
        valid = session.validate_account_context(BRAND)
        self.assertIn("next=https%3A%2F%2Fwww.youtube.com%2F", valid["signinUrl"])

    def test_selected_cfg_replaces_previous_brand_without_mutating(self):
        cfg = {"DELEGATED_SESSION_ID": "333", "USER_SESSION_ID": "999",
               "DATASYNC_ID": "333||999", "SESSION_INDEX": 2,
               "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"},
                                     "user": {"onBehalfOfUser": "333"}}}
        original = copy.deepcopy(cfg)
        selected = session.configure_ytcfg(cfg, BRAND)
        self.assertEqual(selected["DELEGATED_SESSION_ID"], "222")
        self.assertEqual(selected["USER_SESSION_ID"], "111")
        self.assertEqual(selected["INNERTUBE_CONTEXT"]["user"]["onBehalfOfUser"], "222")
        primary = session.configure_ytcfg(cfg, PRIMARY)
        self.assertNotIn("DELEGATED_SESSION_ID", primary)
        self.assertNotIn("onBehalfOfUser", primary["INNERTUBE_CONTEXT"]["user"])
        self.assertEqual(cfg, original)

    def test_options_avoid_embedded_primary_tracking_response(self):
        opts = {"extractor_args": {"youtube": {"comment_sort": ["top"]}}}
        selected = session.selected_ydl_options(opts, BRAND)
        self.assertEqual(selected["extractor_args"]["youtube"]["data_sync_id"], ["222||111"])
        self.assertEqual(selected["extractor_args"]["youtube"]["player_skip"], ["webpage", "configs"])
        self.assertEqual(selected["extractor_args"]["youtube"]["player_client"], ["default", "web_safari"])
        self.assertEqual(selected["extractor_args"]["youtubetab"]["skip"], ["webpage", "authcheck"])
        self.assertEqual(opts, {"extractor_args": {"youtube": {"comment_sort": ["top"]}}})
        self.assertEqual(session.selected_ydl_options(opts, None), opts)

    def test_selected_client_policy_replaces_overrides_without_changing_guest_options(self):
        options = {"extractor_args": {"youtube": {"player_client": ["web"], "player_skip": ["js"]}}}
        original = copy.deepcopy(options)
        selected = session.selected_ydl_options(options, BRAND)
        self.assertEqual(selected["extractor_args"]["youtube"]["player_client"], ["default", "web_safari"])
        self.assertEqual(selected["extractor_args"]["youtube"]["player_skip"], ["js", "webpage", "configs"])
        self.assertEqual(options, original)
        self.assertEqual(session.selected_ydl_options(options, None), original)

    def test_pinned_authenticated_clients_keep_safari_and_selected_identity_without_webpage(self):
        # Exercise the pinned client's selection and real player header/context
        # construction. Intercept the API boundary; no cookies or network are used.
        video_id = "fixture0001"
        url = "https://www.youtube.com/watch?v=" + video_id
        premium_data = {"topbar": {"desktopTopbarRenderer": {"logo": {"topbarLogoRenderer": {
            "iconImage": {"iconType": "YOUTUBE_PREMIUM_LOGO"}}}}}}
        for source in (PRIMARY, BRAND):
            for auth_user in (0, 2):
                for premium in (False, True):
                    account = {**source, "authUser": auth_user,
                               "signinUrl": source["signinUrl"].replace("authuser=0", "authuser=" + str(auth_user))}
                    with self.subTest(brand=bool(account["pageId"]), authUser=auth_user, premium=premium):
                        ydl = session.create_youtube_dl({"cachedir": False, "quiet": True,
                            "js_runtimes": {}, "remote_components": []}, account)
                        try:
                            ie = ydl.get_info_extractor("Youtube")
                            requests = []

                            def player_response(**kwargs):
                                self.assertEqual(kwargs["ep"], "player")
                                self.assertEqual(kwargs["query"]["videoId"], video_id)
                                context = ie._extract_context(kwargs["ytcfg"], kwargs["default_client"])
                                requests.append((kwargs["default_client"], kwargs["headers"], context))
                                return {"videoDetails": {"videoId": video_id},
                                        "playabilityStatus": {"status": "OK"}, "streamingData": {}}

                            with patch.object(type(ie), "_has_auth_cookies", True), \
                                    patch.object(ie, "_jsc_director", SimpleNamespace(providers={}), create=True), \
                                    patch.object(ie, "_get_sid_authorization_header", return_value="synthetic-authorization"), \
                                    patch.object(ie, "_download_webpage", side_effect=AssertionError("Unexpected webpage request")), \
                                    patch.object(ie, "_download_ytcfg", side_effect=AssertionError("Unexpected client config request")), \
                                    patch.object(ie, "_download_initial_data", return_value=premium_data if premium else {}), \
                                    patch.object(ie, "_extract_player_url", return_value=None), \
                                    patch.object(ie, "_download_player_url", return_value=None), \
                                    patch.object(ie, "fetch_po_token", return_value=None), \
                                    patch.object(ie, "_extract_response", side_effect=player_response):
                                initial = ie._initial_extract(url, {}, url, ie._webpage_client, video_id)
                            self.assertIsNone(initial[0])
                            defaults = ie._DEFAULT_PREMIUM_CLIENTS if premium else ie._DEFAULT_AUTHED_CLIENTS
                            self.assertEqual([client for client, _headers, _context in requests], [*defaults, "web_safari"])
                            for client, headers, context in requests:
                                self.assertEqual(headers["X-Goog-AuthUser"], auth_user)
                                self.assertEqual(headers.get("X-Goog-PageId", ""), account["pageId"])
                                self.assertEqual(headers["Authorization"], "synthetic-authorization")
                                self.assertEqual(context.get("user", {}).get("onBehalfOfUser", ""), account["pageId"])
                                native_client = ie._get_default_ytcfg(client)["INNERTUBE_CONTEXT"]["client"]
                                self.assertEqual(context["client"]["clientName"], native_client["clientName"])
                                self.assertEqual(context["client"]["clientVersion"], native_client["clientVersion"])
                                if client == "web_safari":
                                    self.assertIn("Safari/", headers["User-Agent"])
                                    self.assertEqual(context["client"]["userAgent"], native_client["userAgent"])
                            self.assertEqual(ie._fk_account_context, session.validate_account_context(account))
                        finally:
                            session.close_without_cookie_save(ydl)

    def test_missing_native_config_remains_falsey_for_client_defaults(self):
        for missing in ({}, None):
            with self.subTest(missing=missing):
                class EmptyConfigIE:
                    def extract_ytcfg(self, _video_id, _webpage):
                        return missing
                    def generate_api_headers(self, **kwargs):
                        return kwargs
                    def _extract_context(self, _ytcfg=None, _default_client="web"):
                        return {"client": {"clientName": "WEB"}}
                ie = session.install_account_context(EmptyConfigIE(), BRAND)
                result = ie.extract_ytcfg("fixture0001", None)
                self.assertIs(result, missing)
                self.assertFalse(result)
                self.assertEqual(ie._fk_account_context, session.validate_account_context(BRAND))

    def test_pinned_video_default_client_config_keeps_selected_identity_without_webpage(self):
        # Exercise yt-dlp's actual native `extract_ytcfg(...) or default` path.
        # All calls that could retrieve a webpage/API/player response are mocked.
        for source in (PRIMARY, BRAND):
            account = {**source, "authUser": 2,
                       "signinUrl": source["signinUrl"].replace("authuser=0", "authuser=2")}
            with self.subTest(brand=bool(account["pageId"])):
                ydl = session.create_youtube_dl({"cachedir": False, "quiet": True,
                    "js_runtimes": {}, "remote_components": []}, account)
                try:
                    ie = ydl.get_info_extractor("Youtube")
                    skips = ydl.params["extractor_args"]["youtube"]["player_skip"]
                    self.assertIn("webpage", skips)
                    self.assertIn("configs", skips)
                    self.assertEqual(ydl.params["extractor_args"]["youtube"]["data_sync_id"], [account["dataSyncId"]])
                    with patch.object(ie, "_download_initial_webpage", return_value=None), \
                            patch.object(ie, "_download_initial_data", return_value={}), \
                            patch.object(ie, "_get_requested_clients", return_value=["web"]), \
                            patch.object(ie, "_extract_player_responses", return_value=([], None)) as responses:
                        initial = ie._initial_extract("https://www.youtube.com/watch?v=fixture0001", {},
                            "https://www.youtube.com/watch?v=fixture0001", "web", "fixture0001")
                    cfg = initial[1]
                    self.assertEqual(cfg["INNERTUBE_CONTEXT"]["client"]["clientName"], "WEB")
                    self.assertTrue(cfg["INNERTUBE_CONTEXT"]["client"]["clientVersion"])
                    self.assertIs(responses.call_args.args[4], cfg)
                    original = copy.deepcopy(cfg)
                    headers = ie.generate_api_headers(ytcfg=cfg)
                    self.assertEqual(headers["X-Goog-AuthUser"], 2)
                    self.assertEqual(headers.get("X-Goog-PageId", ""), account["pageId"])
                    context = ie._extract_context(cfg)
                    self.assertEqual(context["client"]["clientName"], "WEB")
                    self.assertEqual(context.get("user", {}).get("onBehalfOfUser", ""), account["pageId"])
                    self.assertEqual(ie._fk_account_context["dataSyncId"], account["dataSyncId"])
                    self.assertEqual(cfg, original)
                finally:
                    session.close_without_cookie_save(ydl)

    def test_actual_pinned_extractor_uses_selected_auth_user_and_page(self):
        # Constructing extractors and generating headers makes no network request.
        ydl = session.create_youtube_dl({"cachedir": False, "quiet": True,
                                       "js_runtimes": {}, "remote_components": []}, BRAND)
        try:
            ie = ydl.get_info_extractor("YoutubeTab")
            headers = ie.generate_api_headers(ytcfg={"DELEGATED_SESSION_ID": "333", "SESSION_INDEX": 9})
            self.assertEqual(headers["X-Goog-PageId"], "222")
            self.assertEqual(headers["X-Goog-AuthUser"], 0)
            context = ie._extract_context({})
            self.assertEqual(context["client"]["clientName"], "WEB")
            self.assertEqual(context["user"]["onBehalfOfUser"], "222")
            video = ydl.get_info_extractor("Youtube")
            self.assertEqual(video.generate_api_headers()["X-Goog-PageId"], "222")
            with self.assertRaises(session.SessionError):
                session.install_account_context(ie, PRIMARY)
        finally:
            session.close_without_cookie_save(ydl)

    def test_real_supported_tokens_parse_opaque_metadata(self):
        response = {"actions": [account_item(PRIMARY, True), account_item(BRAND)]}
        channels = session.parse_accounts(response, {"SESSION_INDEX": 0})
        self.assertEqual(len(channels), 2)
        self.assertEqual(len({item["id"] for item in channels}), 2)
        self.assertTrue(all(item["id"].startswith("channel:") and len(item["id"]) == 72 for item in channels))
        self.assertNotEqual(channels[0]["id"], PRIMARY["id"])
        renamed = copy.deepcopy(response)
        renamed["actions"][0]["accountItemRenderer"]["accountName"] = {"simpleText": "Renamed synthetic channel"}
        self.assertEqual([item["id"] for item in session.parse_accounts(renamed, {"SESSION_INDEX": 0})],
                         [item["id"] for item in channels])
        self.assertTrue(channels[0]["isSelected"])
        self.assertEqual(channels[1]["dataSyncId"], "222||111")
        self.assertEqual(channels[1]["handle"], "@synthetic")

    def test_disabled_or_other_google_login_not_returned(self):
        primary = account_item(PRIMARY, True)
        disabled = account_item(BRAND)
        disabled["accountItemRenderer"]["isDisabled"] = True
        other = account_item({**BRAND, "signinUrl": BRAND["signinUrl"].replace("authuser=0", "authuser=1")})
        self.assertEqual(len(session.parse_accounts({"items": [primary, disabled, other]}, {"SESSION_INDEX": 0})), 1)

    def test_malformed_channel_item_cannot_hide_a_valid_channel(self):
        malformed = []
        for changes in ({"serviceEndpoint": None},
                        {"serviceEndpoint": {"selectActiveIdentityEndpoint": []}},
                        {"serviceEndpoint": {"selectActiveIdentityEndpoint": {"supportedTokens": [{"pageIdToken": None}]}}},
                        {"serviceEndpoint": {"selectActiveIdentityEndpoint": {"supportedTokens": [{"datasyncIdToken": []}]}}},
                        {"serviceEndpoint": {"selectActiveIdentityEndpoint": {"supportedTokens": [{"accountSigninToken": None}]}}},
                        {"accountName": {"runs": None}},
                        {"channelHandle": {"runs": 5}}):
            item = account_item(BRAND)
            item["accountItemRenderer"].update(changes)
            malformed.append(item)
        bad_sync = account_item(BRAND)
        bad_sync["accountItemRenderer"]["serviceEndpoint"]["selectActiveIdentityEndpoint"]["supportedTokens"][1] = {
            "datasyncIdToken": {"datasyncIdToken": "invalid-sync"}}
        malformed.append(bad_sync)
        expected = session.parse_accounts({"items": [account_item(PRIMARY)]}, {"SESSION_INDEX": 0})
        for item in malformed:
            with self.subTest(item=item):
                self.assertEqual(session.parse_accounts({"items": [item, account_item(PRIMARY)]}, {"SESSION_INDEX": 0}), expected)

    def test_all_malformed_channels_are_distinct_from_no_authenticated_channels(self):
        malformed = account_item(BRAND)
        malformed["accountItemRenderer"]["serviceEndpoint"] = None
        with self.assertRaises(session.SessionError) as failure:
            session.parse_accounts({"items": [malformed]}, {"SESSION_INDEX": 0})
        self.assertEqual(failure.exception.code, "unsupported")
        with self.assertRaises(session.SessionError) as failure:
            session.parse_accounts({"items": []}, {"SESSION_INDEX": 0})
        self.assertEqual(failure.exception.code, "auth")

    def test_quiet_calls_discard_large_diagnostics_and_preserve_failure_classification(self):
        import feed_worker
        chunk = "synthetic-private-diagnostic" * 2500
        for call in (session.quiet_call, feed_worker.quiet_call):
            def noisy():
                for _index in range(64):
                    sys.stdout.write(chunk)
                    sys.stderr.write(chunk)
                raise TimeoutError("offline network timeout")
            tracemalloc.start()
            try:
                with self.assertRaises(TimeoutError) as failure:
                    call(noisy)
                _current, peak = tracemalloc.get_traced_memory()
            finally:
                tracemalloc.stop()
            self.assertLess(peak, 512 * 1024)
            self.assertEqual(feed_worker.classify_error(failure.exception), "network")

    def test_protocol_never_emits_exception_text(self):
        output = io.StringIO()
        with patch.object(session, "command_result", side_effect=ValueError("PRIVATE SECRET")):
            session.run(io.StringIO('{"command":"account.list","requestId":"x"}\n'), output)
        self.assertNotIn("PRIVATE SECRET", output.getvalue())
        result = json.loads(output.getvalue())
        self.assertEqual((result["type"], result["code"], result["requestId"]), ("error", "auth", "x"))

    def test_close_cannot_write_imported_cookie_file(self):
        class FakeYDL:
            params = {"cookiefile": "original-source.txt"}
            def close(self):
                self.saved_cookie = self.params.get("cookiefile")
        ydl = FakeYDL()
        session.close_without_cookie_save(ydl)
        self.assertIsNone(ydl.saved_cookie)

    def test_selected_cookie_requires_exact_identity_before_save(self):
        import yt_dlp
        import yt_dlp.extractor.youtube
        test_root = Path(__file__).resolve().parents[1] / "artifacts" / "channel-research"
        test_root.mkdir(parents=True, exist_ok=True)
        import uuid
        directory = test_root / ("session-fixture-" + uuid.uuid4().hex)
        directory.mkdir()
        source = directory / "source.txt"
        output = directory / "selected.txt"
        try:
            source.write_text("synthetic source cookies", encoding="utf-8")

            class FakeJar:
                def save(self, filename):
                    if hasattr(filename, "write"):
                        filename.write("synthetic selected cookies")
                    else:
                        Path(filename).write_text("synthetic selected cookies", encoding="utf-8")

            class FakeYDL:
                def __init__(self, options):
                    self.params = options
                    self.cookiejar = FakeJar()
                def close(self):
                    assert self.params["cookiefile"] is None

            class FakeIE:
                is_authenticated = True
                actual_sync = BRAND["dataSyncId"]
                def __init__(self, ydl):
                    self.ydl = ydl
                def initialize(self):
                    pass
                def _download_webpage(self, url, _video_id):
                    self.url = url
                    return "synthetic html"
                def extract_ytcfg(self, *_args):
                    return {"LOGGED_IN": True, "DATASYNC_ID": self.actual_sync, "SESSION_INDEX": 0}
                def _extract_data_sync_id(self, cfg):
                    return cfg["DATASYNC_ID"]
                def _extract_session_index(self, cfg):
                    return cfg["SESSION_INDEX"]

            with patch.object(yt_dlp, "YoutubeDL", FakeYDL), \
                    patch.object(yt_dlp.extractor.youtube, "YoutubeTabIE", FakeIE):
                result = session.select_account_cookie(str(source), BRAND, str(output))
                self.assertTrue(result["verified"])
                self.assertEqual(output.read_text(), "synthetic selected cookies")
                self.assertEqual(source.read_text(), "synthetic source cookies")
                output.unlink()
                FakeIE.actual_sync = PRIMARY["dataSyncId"]
                with self.assertRaises(session.SessionError):
                    session.select_account_cookie(str(source), BRAND, str(output))
                self.assertFalse(output.exists())
                self.assertEqual(source.read_text(), "synthetic source cookies")
                with self.assertRaises(session.SessionError):
                    session.select_account_cookie(str(source), BRAND, str(source))
        finally:
            for owned_file in (source, output):
                owned_file.unlink(missing_ok=True)
            directory.rmdir()


if __name__ == "__main__":
    unittest.main()
