"""Offline contract checks against the bundled extractor; no account is read."""
import io
import json
import random
import re
import sys
import types
import unittest
from unittest.mock import patch

import selected_ytdlp


class Input(io.StringIO):
    def reconfigure(self, **_kwargs):
        pass


class SelectedCliTests(unittest.TestCase):
    ACCOUNT = {"id": "fixture-brand", "pageId": "123456", "dataSyncId": "123456||fixture_user", "authUser": 2}

    def run_adapter(self, account, extra=()):
        original = selected_ytdlp.create_youtube_dl
        inspected = {}

        def factory(options, supplied):
            inspected["options"] = options
            inspected["account"] = supplied
            ydl = original(options, supplied)

            def download(_self, urls):
                inspected["urls"] = urls
                ie = ydl.get_info_extractor("Youtube")
                stale = {"SESSION_INDEX": 0, "DATASYNC_ID": "999||stale_user", "DELEGATED_SESSION_ID": "999",
                         "INNERTUBE_CONTEXT": {"client": {}, "user": {"onBehalfOfUser": "999"}}}
                if account is not None:
                    headers = ie.generate_api_headers(ytcfg=stale, session_index=0,
                                                      delegated_session_id="999", user_session_id="stale_user")
                    self.assertEqual(str(headers["X-Goog-AuthUser"]), "2")
                    self.assertEqual(headers["X-Goog-PageId"], "123456")
                    self.assertEqual(ie._extract_context(stale)["user"]["onBehalfOfUser"], "123456")
                    configured = ie.extract_ytcfg("fixture", 'ytcfg.set({"SESSION_INDEX":0,"DATASYNC_ID":"999||stale_user"});')
                    self.assertEqual(configured["SESSION_INDEX"], 2)
                    self.assertEqual(configured["DATASYNC_ID"], account["dataSyncId"])
                    self.assertEqual(ydl.params["extractor_args"]["youtubetab"]["skip"], ["webpage", "authcheck"])
                else:
                    self.assertFalse(hasattr(ie, "_fk_account_context"))
                    self.assertIsNone(ydl.params.get("cookiefile"))
                    self.assertFalse(ydl.params.get("mark_watched"))
                    self.assertIs(ydl.params.get("cachedir"), False)
                return 0

            ydl.download = types.MethodType(download, ydl)
            return ydl

        args = ["--ignore-config", "--no-plugin-dirs", "--simulate", "--no-mark-watched", *extra,
                "https://www.youtube.com/watch?v=aqz-KE-bpKQ"]
        with patch.object(sys, "stdin", Input(json.dumps({"accountContext": account}) + "\n")), \
                patch.object(selected_ytdlp, "create_youtube_dl", factory):
            self.assertEqual(selected_ytdlp.main(args), 0)
        return inspected

    def test_selected_headers_and_comment_options_survive_stale_config(self):
        result = self.run_adapter(self.ACCOUNT, ["--write-comments", "--extractor-args",
            "youtube:data_sync_id=123456||fixture_user;player_skip=webpage,configs;comment_sort=new;max_comments=100,100,30,3"])
        youtube = result["options"]["extractor_args"]["youtube"]
        self.assertEqual(youtube["comment_sort"], ["new"])
        self.assertEqual(youtube["max_comments"], ["100", "100", "30", "3"])
        self.assertEqual(youtube["data_sync_id"], [self.ACCOUNT["dataSyncId"]])

    def test_incognito_has_no_account_or_cookie_persistence(self):
        result = self.run_adapter(None)
        self.assertIsNone(result["account"])
        self.assertIsNone(result["options"].get("cookiefile"))

    def test_invalid_account_never_starts_extraction(self):
        with patch.object(sys, "stdin", Input(json.dumps({"accountContext": self.ACCOUNT | {"authUser": 100}}))), \
                patch.object(selected_ytdlp, "create_youtube_dl") as factory:
            with self.assertRaises(Exception):
                selected_ytdlp.main(["--ignore-config", "--no-plugin-dirs", "https://www.youtube.com/watch?v=aqz-KE-bpKQ"])
            factory.assert_not_called()

    def test_unknown_operation_never_starts_extraction(self):
        with patch.object(sys, "stdin", Input(json.dumps({"accountContext": self.ACCOUNT, "operation": "write-everything"}))), \
                patch.object(selected_ytdlp, "create_youtube_dl") as factory:
            with self.assertRaises(ValueError):
                selected_ytdlp.main(["--ignore-config", "--no-plugin-dirs", "https://www.youtube.com/watch?v=fixture0001"])
            factory.assert_not_called()

    def test_cookie_lease_is_never_saved_on_any_context_manager_exit(self):
        from yt_dlp import YoutubeDL
        original_factory = selected_ytdlp.create_youtube_dl
        original_close = YoutubeDL.close
        for operation in (None, "subtitle_catalog"):
            for outcome in (0, 1, "exception"):
                with self.subTest(operation=operation, outcome=outcome):
                    closed, would_save = [], []
                    def factory(options, account):
                        # No real cookie file is loaded or written by this fixture.
                        options["cookiefile"] = "synthetic-disposable-lease.txt"
                        ydl = original_factory(options, account)
                        def action(*_args, **_kwargs):
                            self.assertEqual(ydl.params["cookiefile"], "synthetic-disposable-lease.txt")
                            if outcome == "exception":
                                raise RuntimeError("offline extractor failure")
                            return {"id": "fixture0001"} if operation else outcome
                        ydl.download = ydl.extract_info = action
                        return ydl
                    def close(ydl):
                        closed.append(ydl)
                        original_close(ydl)
                    def save_cookies(ydl):
                        if ydl.params.get("cookiefile") is not None:
                            would_save.append(ydl.params["cookiefile"])
                    request = {"accountContext": self.ACCOUNT}
                    if operation:
                        request["operation"] = operation
                    with patch.object(sys, "stdin", Input(json.dumps(request) + "\n")), \
                            patch.object(sys, "stdout", Input()), \
                            patch.object(selected_ytdlp, "create_youtube_dl", factory), \
                            patch.object(YoutubeDL, "close", close), \
                            patch.object(YoutubeDL, "save_cookies", save_cookies):
                        args = ["--ignore-config", "--no-plugin-dirs", "--simulate", "--no-mark-watched",
                                "https://www.youtube.com/watch?v=fixture0001"]
                        if outcome == "exception":
                            with self.assertRaises(RuntimeError):
                                selected_ytdlp.main(args)
                        else:
                            self.assertEqual(selected_ytdlp.main(args), 0 if operation else outcome)
                    self.assertEqual(len(closed), 1)
                    self.assertEqual(would_save, [])


class AccessFailureTests(unittest.TestCase):
    def test_redirected_windows_pipe_emits_multilingual_catalog_as_utf8(self):
        # The real CLI redirects extraction into StringIO before main() runs.
        # Reconfiguring that capture cannot change a CP949/CP1252 parent pipe.
        payload = json.dumps({"id": "fixture0001", "languages": ["한국어", "English", "Māori", "日本語", "العربية", "😀"]}, ensure_ascii=False) + "\n"
        for encoding in ("cp949", "cp1252"):
            with self.subTest(encoding=encoding):
                output_bytes, error_bytes = io.BytesIO(), io.BytesIO()
                output = io.TextIOWrapper(output_bytes, encoding=encoding, newline="\n")
                errors = io.TextIOWrapper(error_bytes, encoding=encoding, newline="\n")
                request = io.TextIOWrapper(io.BytesIO('{"label":"한국어 Māori"}\n'.encode("utf-8")), encoding=encoding)
                def successful(_argv):
                    # Matches the existing main() call after stdout redirection.
                    sys.stdin.reconfigure(encoding="utf-8")
                    sys.stdout.reconfigure(encoding="utf-8")
                    self.assertEqual(json.loads(sys.stdin.readline())["label"], "한국어 Māori")
                    sys.stdout.write(payload)
                    return 0
                try:
                    with patch.object(selected_ytdlp, "main", successful), patch.object(sys, "stdin", request), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
                        self.assertEqual(selected_ytdlp.run([]), 0)
                    output.flush(); errors.flush()
                    self.assertEqual(output_bytes.getvalue().decode("utf-8"), payload)
                    self.assertEqual(error_bytes.getvalue(), b"")
                finally:
                    request.close(); output.close(); errors.close()

    def test_failure_classification_keeps_authentication_narrow(self):
        cases = (("Sign in to confirm your age", "signin_age"), ("LOGIN_REQUIRED", "signin_age"),
                 ("Sign in to confirm you're not a bot", "bot"), ("captcha challenge before playback", "bot"),
                 ("Members-only video. Sign in", "member"), ("Private video. Sign in", "private"),
                 ("HTTP Error 429: Too Many Requests", "rate_limit"), ("has been rate-limited by YouTube", "rate_limit"),
                 ("getaddrinfo failed", "network"), ("Video unavailable", "unavailable"),
                 ("Your cookies are no longer valid", "session"), ("DRM-protected video. Sign in", "generic"),
                 ("Unknown error in cookie file", "generic"))
        for text, code in cases:
            with self.subTest(code=code, text=text):
                self.assertEqual(selected_ytdlp.classify_error(text), code)
                self.assertIn(code, selected_ytdlp.ERROR_CODES)

    def test_cli_failure_discards_partial_json_raw_stderr_and_traceback(self):
        for raises in (False, True):
            output, errors = Input(), Input()
            def failing(_argv):
                print('{"url":"https://private.fixture.invalid/signed-secret"}')
                print("selected-account-secret cookie-secret Sign in to confirm your age", file=sys.stderr)
                if raises:
                    raise RuntimeError("private-cookie-secret stack-secret")
                return 1
            with patch.object(selected_ytdlp, "main", failing), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
                self.assertEqual(selected_ytdlp.run([]), 1)
            self.assertEqual(output.getvalue(), "")
            self.assertEqual(errors.getvalue(), "FK_YT_ERROR:signin_age\n")

    def test_cli_success_keeps_normal_stdout_and_mark_watched_contract(self):
        output, errors = Input(), Input()
        def successful(_argv):
            print('[youtube] Marking watched\n{"fixture":true}')
            print("upstream private warning-secret", file=sys.stderr)
            return 0
        with patch.object(selected_ytdlp, "main", successful), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 0)
        self.assertEqual(output.getvalue(), '[youtube] Marking watched\n{"fixture":true}\n')
        self.assertEqual(errors.getvalue(), "")

    def test_upstream_output_capture_is_bounded(self):
        sink = selected_ytdlp._BoundedOutput(8)
        sink.write("12345678")
        with self.assertRaises(ValueError):
            sink.write("9")
        self.assertEqual(sink.getvalue(), "12345678")

    def test_forwarded_progress_does_not_use_retained_output_budget(self):
        lines = []
        sink = selected_ytdlp._BoundedOutput(32, lines.append)
        for _index in range(1000):
            sink.write("FK_PRO")
            sink.write("GRESS: 25.0%\n")
            sink.write("[Merger] private-media-path\n")
        sink.write('{"fixture":true}\n')
        self.assertEqual(lines, ["FK_PROGRESS:25.0%", "[Merger] Combining media"] * 1000)
        self.assertEqual(sink.getvalue(), '{"fixture":true}\n')
        with self.assertRaises(ValueError):
            sink.write("private non-progress output that exceeds the remaining budget\n")

    def test_success_replays_metadata_in_small_chunks_without_line_list_copy(self):
        class Destination(Input):
            largest_write = 0
            def write(self, value):
                self.largest_write = max(self.largest_write, len(value))
                return super().write(value)
        output, errors = Destination(), Input()
        payload = '{"fixture":"' + "x" * (1024 * 1024) + '"}\n'
        def successful(_argv):
            sys.stdout.write(payload)
            return 0
        with patch.object(selected_ytdlp, "main", successful), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 0)
        self.assertEqual(output.getvalue(), payload)
        self.assertLessEqual(output.largest_write, 65536)
        self.assertEqual(errors.getvalue(), "")

    def test_chunk_boundaries_preserve_data_and_sanitize_oversized_progress_paths(self):
        output, errors = Input(), Input()
        payload = '{"fixture":"한글 row"}\r\n[youtube] Marking watched'
        def successful(_argv):
            for piece in ("FK_PRO", "GRESS: 10.0%\r", "\n", payload[:13], payload[13:],
                          "\n[Mer", "ger] ", "private-media-path" * 100, "\n", "FK_PROGRESS:100%"):
                sys.stdout.write(piece)
            return 0
        with patch.object(selected_ytdlp, "main", successful), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 0)
        self.assertEqual(output.getvalue(), "FK_PROGRESS:10.0%\n[Merger] Combining media\nFK_PROGRESS:100%\n" + payload + "\n")
        self.assertEqual(errors.getvalue(), "")

    def test_failure_with_unterminated_private_metadata_keeps_only_safe_reason(self):
        output, errors = Input(), Input()
        def failing(_argv):
            sys.stdout.write('{"url":"https://private.fixture.invalid/signed-secret')
            sys.stderr.write("cookie-secret Sign in to confirm your age")
            raise RuntimeError("private-extractor-secret")
        with patch.object(selected_ytdlp, "main", failing), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 1)
        self.assertEqual(output.getvalue(), "")
        self.assertEqual(errors.getvalue(), "FK_YT_ERROR:signin_age\n")

    def test_nonfatal_history_failure_keeps_only_fixed_negative_acknowledgement(self):
        output, errors = Input(), Input()
        def warning(_argv):
            print("[youtube] Marking watched")
            print("Unable to mark watched: cookie-secret signed-secret", file=sys.stderr)
            return 0
        with patch.object(selected_ytdlp, "main", warning), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 0)
        self.assertEqual(output.getvalue(), "[youtube] Marking watched\n")
        self.assertEqual(errors.getvalue(), "WARNING: Unable to mark watched\n")

    def test_download_progress_remains_live_without_disclosing_media_paths(self):
        output, errors = Input(), Input()
        def downloading(_argv):
            sys.stdout.write("FK_PRO")
            sys.stdout.write("GRESS: 25.0%\n")
            self.assertEqual(output.getvalue(), "FK_PROGRESS:25.0%\n")
            print("[Merger] signed-secret private-file-secret", file=sys.stderr)
            self.assertEqual(output.getvalue(), "FK_PROGRESS:25.0%\n[Merger] Combining media\n")
            print("FK_PROGRESS:200%")
            print("FK_PROGRESS:secret-token%")
            print("FK_PROGRESS: 100.0%", file=sys.stderr)
            print("{\"signed-url-secret\":true}")
            raise RuntimeError("HTTP Error 429 signed-cookie-secret")
        with patch.object(selected_ytdlp, "main", downloading), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 1)
        self.assertEqual(output.getvalue(), "FK_PROGRESS:25.0%\n[Merger] Combining media\nFK_PROGRESS:100.0%\n")
        self.assertEqual(errors.getvalue(), "FK_YT_ERROR:rate_limit\n")

    def test_successful_download_does_not_replay_old_progress(self):
        output, errors = Input(), Input()
        def downloading(_argv):
            print("FK_PROGRESS: 10.0%")
            print("[VideoRemuxer] private-file-secret")
            print("FK_PROGRESS:100%")
            return 0
        with patch.object(selected_ytdlp, "main", downloading), patch.object(sys, "stdout", output), patch.object(sys, "stderr", errors):
            self.assertEqual(selected_ytdlp.run([]), 0)
        self.assertEqual(output.getvalue(), "FK_PROGRESS:10.0%\n[VideoRemuxer] Remuxing media\nFK_PROGRESS:100%\n")
        self.assertEqual(errors.getvalue(), "")


def caption(name="fixture", ext="vtt", query=""):
    return {"ext": ext, "name": name, "url": "https://www.youtube.com/api/timedtext?v=fixture0001" + query}


def legacy_subtitle_catalog(info):
    """Frozen pre-optimization search, used only as an equivalence oracle."""
    manual, automatic = info.get("subtitles") or {}, info.get("automatic_captions") or {}
    result = {"id": info["id"], "subtitles": {}, "automatic_captions": {}}
    manual_keys = sorted((key for key in manual if selected_ytdlp._caption_language(key)), key=len, reverse=True)
    for language in manual_keys[:1024]:
        if chosen := selected_ytdlp._caption_format(manual[language]):
            result["subtitles"][language] = [chosen]
    candidates = {}
    for language, formats in automatic.items():
        if not isinstance(language, str) or len(language) > 160 or re.fullmatch(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*", language) is None:
            continue
        target, score = language, 1
        if language.endswith("-orig"):
            target, score = language[:-5], 0
        else:
            for source in manual_keys:
                suffix = "-" + source
                if language.endswith(suffix) and selected_ytdlp._caption_language(language[:-len(suffix)]):
                    target, score = language[:-len(suffix)], 2 if source == "en" else 3
                    break
        if not selected_ytdlp._caption_language(target) or (target not in candidates and len(candidates) >= 1024):
            continue
        chosen = selected_ytdlp._caption_format(formats)
        if chosen is not None and (target not in candidates or score < candidates[target][0]):
            candidates[target] = (score, chosen)
    remaining = max(0, 1024 - len(result["subtitles"]))
    result["automatic_captions"] = {language: [candidate[1]] for language, candidate in sorted(candidates.items())[:remaining]}
    return result


class SubtitleCatalogTests(unittest.TestCase):
    def test_source_ranking_and_first_equal_score_candidate_are_preserved(self):
        first, tied, english = caption("first"), caption("tied"), caption("from English")
        plain, original = caption("plain"), caption("original")
        info = {"id": "fixture0001", "subtitles": {"fr": [], "de": [], "en": []},
                "automatic_captions": {"ja-fr": [first], "ja-de": [tied],
                    "ko-fr": [first], "ko-en": [english], "ko-de": [tied],
                    "pt-fr": [first], "pt-en": [english], "pt": [plain],
                    "es": [plain], "es-orig": [original], "es-fr": [first]}}
        result = selected_ytdlp.compact_subtitle_catalog(info)
        self.assertEqual(result, legacy_subtitle_catalog(info))
        self.assertEqual(result["automatic_captions"], {"es": [original], "ja": [first], "ko": [english], "pt": [plain]})

    def test_overlapping_multisegment_suffixes_keep_longest_and_exact_case(self):
        manual = {key: [] for key in ("en", "US", "en-US", "pt", "BR", "pt-BR", "zh-Hant-HK", "HK", "abc-a-b-c")}
        info = {"id": "fixture0001", "subtitles": manual, "automatic_captions": {
            key: [caption(key)] for key in ("ja-en-US", "ko-pt-BR", "en-Latn-zh-Hant-HK", "ja-abc-a-b-c", "ja-en-us")}}
        result = selected_ytdlp.compact_subtitle_catalog(info)
        self.assertEqual(result, legacy_subtitle_catalog(info))
        self.assertEqual(set(result["automatic_captions"]), {"ja", "ko", "en-Latn", "ja-en-us"})
        self.assertEqual(result["automatic_captions"]["ja"][0]["name"], "ja-en-US")

    def test_seeded_mixed_catalogs_are_equivalent_to_original_suffix_search(self):
        rng = random.Random(4723)
        sources = ["en", "fr", "de", "US", "en-US", "pt-BR", "zh-Hant-HK", "abc-a-b-c", "zz-x0"]
        targets = ["en", "ja", "ko", "pt-BR", "zh-Hans-CN", "xx-y0", "a", "en-a-b-c-d"]
        formats = [[caption("vtt")], [caption("srt", ext="srt")], [],
                   [caption() | {"url": "https://untrusted.invalid/caption"}], [caption("ass", ext="ass")]]
        for iteration in range(80):
            manual = {source: rng.choice(formats) for source in rng.sample(sources, rng.randrange(len(sources) + 1))}
            manual.update({"bad-key-!": [], 13: [], "four": []})
            automatic = {}
            for _ in range(120):
                target = rng.choice(targets)
                ending = rng.choice(["", "-orig", "-" + rng.choice(sources), "-unknown"])
                automatic[target + ending] = rng.choice(formats)
            automatic.update({"en--fr": [caption()], "en-" + "a" * 170: [caption()], None: []})
            info = {"id": "fixture0001", "subtitles": manual, "automatic_captions": automatic}
            with self.subTest(iteration=iteration):
                self.assertEqual(selected_ytdlp.compact_subtitle_catalog(info), legacy_subtitle_catalog(info))

    def test_large_catalog_cap_still_uses_sources_beyond_manual_output_limit(self):
        manual = {f"zz-x{i:04d}": [] for i in range(1400)}
        automatic = {f"xx-y{i:04d}-zz-x1399": [caption(f"translated {i}")] for i in range(1100)}
        original = caption("late original")
        automatic["xx-y0000-orig"] = [original]
        info = {"id": "fixture0001", "subtitles": manual, "automatic_captions": automatic}
        result = selected_ytdlp.compact_subtitle_catalog(info)
        self.assertEqual(result, legacy_subtitle_catalog(info))
        self.assertEqual(len(result["automatic_captions"]), 1024)
        self.assertEqual(result["automatic_captions"]["xx-y0000"], [original])
        self.assertNotIn("xx-y1024", result["automatic_captions"])

    def test_large_source_translation_product_retains_all_target_languages(self):
        manual = {f"zz-x{i}": [caption()] for i in range(50)}
        targets = [f"xx-y{i}" for i in range(160)]
        auto = {f"{target}-{source}": [caption()] for source in manual for target in targets}
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": manual, "automatic_captions": auto})
        self.assertEqual(len(auto), 8000)
        self.assertEqual(set(result["automatic_captions"]), set(targets))
        self.assertEqual(len(result["subtitles"]), 50)
        self.assertLess(len(json.dumps(result)), 128 * 1024)
        self.assertTrue(all(len(value) == 1 for value in result["automatic_captions"].values()))

    def test_natural_original_wins_and_urls_are_not_modified(self):
        original = caption("English Original", query="&lang=en")
        translated = caption("English from French", query="&tlang=en-fr")
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": {"fr": [caption()]},
            "automatic_captions": {"en-fr": [translated], "en": [caption("English compatibility")], "en-orig": [original]}})
        self.assertEqual(result["automatic_captions"], {"en": [original]})
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": {"fr": [caption()]}, "automatic_captions": {"en-fr": [translated]}})
        self.assertEqual(result["automatic_captions"]["en"][0]["url"], translated["url"])

    def test_longest_source_suffix_keeps_target_region(self):
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": {"en": [caption()], "pt-BR": [caption()], "zh-Hant-HK": [caption()]},
            "automatic_captions": {"zh-Hans-pt-BR": [caption()], "pt-BR-en": [caption()], "zh-Hans-CN-zh-Hant-HK": [caption()]}})
        self.assertEqual(set(result["automatic_captions"]), {"zh-Hans", "pt-BR", "zh-Hans-CN"})

    def test_vtt_priority_and_unsafe_data_are_bounded(self):
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": {
            "ja": [caption(ext="srt"), caption(ext="vtt")], "en,all": [caption()],
            "fr": [caption() | {"url": "https://other.invalid/private"}], "de": [caption(ext="json3")]}})
        self.assertEqual(set(result["subtitles"]), {"ja"})
        self.assertEqual(result["subtitles"]["ja"][0]["ext"], "vtt")
        many = {f"zz-x{i}": [caption()] for i in range(1200)}
        result = selected_ytdlp.compact_subtitle_catalog({"id": "fixture0001", "subtitles": many, "automatic_captions": many})
        self.assertLessEqual(len(result["subtitles"]) + len(result["automatic_captions"]), 1024)

    def test_catalog_operation_selected_and_guest_disable_forced_prints_and_download(self):
        original_factory = selected_ytdlp.create_youtube_dl
        for account in (None, SelectedCliTests.ACCOUNT):
            with self.subTest(selected=account is not None):
                inspected = {}
                def factory(options, supplied):
                    inspected["account"] = supplied
                    ydl = original_factory(options, supplied)
                    def extract(url, download):
                        inspected["download"] = download
                        inspected["options"] = dict(ydl.params)
                        ie = ydl.get_info_extractor("Youtube")
                        if account is not None:
                            headers = ie.generate_api_headers(ytcfg={"SESSION_INDEX": 0}, session_index=0)
                            self.assertEqual(str(headers["X-Goog-AuthUser"]), "2")
                            self.assertEqual(headers["X-Goog-PageId"], "123456")
                        else:
                            self.assertIsNone(ydl.params.get("cookiefile"))
                            self.assertFalse(hasattr(ie, "_fk_account_context"))
                        return {"id": "fixture0001", "subtitles": {"ja": [caption()]}, "automatic_captions": {"es-ja": [caption()]}}
                    ydl.extract_info = extract
                    ydl.download = lambda *_a: self.fail("Catalog command used normal download execution")
                    return ydl
                output = Input()
                request = {"accountContext": account, "operation": "subtitle_catalog"}
                with patch.object(sys, "stdin", Input(json.dumps(request) + "\n")), patch.object(sys, "stdout", output), \
                        patch.object(selected_ytdlp, "create_youtube_dl", factory):
                    self.assertEqual(selected_ytdlp.main(["--ignore-config", "--no-plugin-dirs", "--dump-single-json", "https://www.youtube.com/watch?v=fixture0001"]), 0)
                result = json.loads(output.getvalue())
                self.assertEqual(set(result), {"id", "subtitles", "automatic_captions"})
                self.assertEqual(set(result["automatic_captions"]), {"es"})
                self.assertEqual(inspected["account"], account)
                self.assertFalse(inspected["download"])
                options = inspected["options"]
                self.assertTrue(options["simulate"] and options["writeautomaticsub"])
                self.assertFalse(options["mark_watched"] or options["dump_single_json"] or options["forcejson"])
                self.assertEqual(options["subtitleslangs"], ["^$"])
                self.assertFalse(options["forceprint"] or options["print_to_file"])


if __name__ == "__main__":
    unittest.main()
