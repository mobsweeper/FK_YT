"""Synthetic account-cookie file boundaries; no browser or YouTube requests."""
import contextlib
import http.cookiejar
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import youtube_session as session
import yt_dlp
import yt_dlp.extractor.youtube


ACCOUNT = {"id": "synthetic", "pageId": "", "dataSyncId": "111||", "authUser": 0,
           "signinUrl": "/signin?action_handle_signin=true&authuser=0"}


class AccountOutputBoundaryTests(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[1] / "artifacts" / "session-review-r6-python"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=parent)
        self.root = Path(self.temp.name)
        self.source = self.root / "source.txt"
        self.source.write_text("synthetic source", encoding="utf-8")
        self.output = self.root / "selected.txt"
        self.on_page = lambda: None
        self.saved_to_handle = False
        owner = self

        class Jar:
            def save(self, filename):
                if hasattr(filename, "write"):
                    owner.saved_to_handle = True
                    filename.write("synthetic selected")
                else:
                    Path(filename).write_text("synthetic selected", encoding="utf-8")

        class Downloader:
            def __init__(self, options):
                self.params = options
                self.cookiejar = Jar()
            def close(self):
                assert self.params["cookiefile"] is None

        class Extractor:
            is_authenticated = True
            def __init__(self, _ydl): pass
            def initialize(self): pass
            def _download_webpage(self, *_args):
                owner.on_page()
                return "synthetic html"
            def extract_ytcfg(self, *_args):
                return {"LOGGED_IN": True, "DATASYNC_ID": "111||", "SESSION_INDEX": 0}
            def _extract_data_sync_id(self, cfg): return cfg["DATASYNC_ID"]
            def _extract_session_index(self, cfg): return cfg["SESSION_INDEX"]
            def generate_api_headers(self, **_kwargs): return {}
            def _extract_context(self, _cfg): return {}
            def _call_api(self, *_args, **_kwargs): return {}

        self.patches = contextlib.ExitStack()
        self.patches.enter_context(patch.object(yt_dlp, "YoutubeDL", Downloader))
        self.patches.enter_context(patch.object(yt_dlp.extractor.youtube, "YoutubeTabIE", Extractor))

    def tearDown(self):
        self.patches.close()
        self.temp.cleanup()

    def select(self):
        return session.select_account_cookie(str(self.source), ACCOUNT, str(self.output))

    def assert_input_rejected_before_downloader(self, cookie_path):
        sentinel = self.root / "unrelated-output.txt"
        sentinel.write_text("unrelated sentinel", encoding="utf-8")
        for operation in (lambda: session.list_accounts(str(cookie_path)),
                          lambda: session.select_account_cookie(str(cookie_path), ACCOUNT, str(self.output))):
            with patch.object(yt_dlp, "YoutubeDL", side_effect=AssertionError("reached downloader")) as downloader:
                with self.assertRaises(session.SessionError):
                    operation()
                downloader.assert_not_called()
            self.assertFalse(self.output.exists(), "Input rejection created an output cookie file")
            self.assertEqual(sentinel.read_text(), "unrelated sentinel")
            self.assertEqual(self.source.read_text(), "synthetic source")

    def test_normal_input_lease_is_shared_by_list_and_select(self):
        self.assertEqual(session.session_options(str(self.source))["cookiefile"], str(self.source))
        with patch.object(session, "parse_accounts", return_value=[ACCOUNT]):
            self.assertEqual(session.list_accounts(str(self.source)), [ACCOUNT])
        self.assertTrue(self.select()["verified"])
        self.assertTrue(self.saved_to_handle)
        self.assertEqual(self.output.read_text(), "synthetic selected")
        self.assertEqual(self.source.read_text(), "synthetic source")

    def test_input_file_reparse_flag_rejected_before_downloader(self):
        original = session._is_link_or_reparse
        with patch.object(session, "_is_link_or_reparse",
                          side_effect=lambda path: path == self.source or original(path)):
            self.assert_input_rejected_before_downloader(self.source)

    def test_input_file_symlink_rejected_before_downloader(self):
        linked = self.root / "linked-source.txt"
        try:
            linked.symlink_to(self.source)
        except OSError as error:
            if getattr(error, "winerror", None) == 1314:
                self.skipTest("Windows file symlink creation requires an unavailable privilege")
            raise
        try:
            self.assert_input_rejected_before_downloader(linked)
        finally:
            linked.unlink()

    @unittest.skipUnless(os.name == "nt", "Windows junction boundary")
    def test_input_parent_junction_rejected_before_downloader(self):
        target = self.root / "input-target"
        target.mkdir()
        (target / "nested").mkdir()
        for path in (target / "source.txt", target / "nested" / "source.txt"):
            path.write_text("synthetic junction source", encoding="utf-8")
        linked = self.root / "input-link"
        result = subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(linked), str(target)],
                                capture_output=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        try:
            for relative in (Path("source.txt"), Path("nested") / "source.txt"):
                self.assert_input_rejected_before_downloader(linked / relative)
                self.assertEqual((target / relative).read_text(), "synthetic junction source")
        finally:
            linked.rmdir()

    def test_save_uses_owned_exclusive_handle_and_keeps_source(self):
        self.assertTrue(self.select()["verified"])
        self.assertTrue(self.saved_to_handle)
        self.assertEqual(self.output.read_text(), "synthetic selected")
        self.assertEqual(self.source.read_text(), "synthetic source")

    def test_pinned_cookiejar_accepts_exclusively_owned_text_handle(self):
        from yt_dlp.cookies import YoutubeDLCookieJar
        jar = YoutubeDLCookieJar()
        jar.set_cookie(http.cookiejar.Cookie(
            version=0, name="SYNTHETIC", value="offline-only", port=None, port_specified=False,
            domain=".youtube.com", domain_specified=True, domain_initial_dot=True,
            path="/", path_specified=True, secure=True, expires=None, discard=True,
            comment=None, comment_url=None, rest={}, rfc2109=False))
        with self.output.open("x", encoding="utf-8") as owned:
            jar.save(filename=owned)
            self.assertFalse(owned.closed)
        saved = self.output.read_text(encoding="utf-8")
        self.assertIn("# Netscape HTTP Cookie File", saved)
        self.assertIn("SYNTHETIC\toffline-only", saved)
        self.assertEqual(self.source.read_text(), "synthetic source")

    def test_output_created_during_lookup_is_neither_overwritten_nor_removed(self):
        self.on_page = lambda: self.output.write_text("unrelated sentinel")
        with self.assertRaises((session.SessionError, OSError)):
            self.select()
        self.assertEqual(self.output.read_text(), "unrelated sentinel")

    def test_lookup_failure_does_not_remove_unowned_output(self):
        def fail():
            self.output.write_text("unrelated sentinel")
            raise session.SessionError("auth")
        self.on_page = fail
        with self.assertRaises(session.SessionError):
            self.select()
        self.assertTrue(self.output.exists(), "Failure cleanup removed a file this call never created")
        self.assertEqual(self.output.read_text(), "unrelated sentinel")

    @unittest.skipUnless(os.name == "nt", "Windows junction boundary")
    def test_parent_junction_rejected_before_downloader(self):
        target = self.root / "outside"
        target.mkdir()
        linked = self.root / "link"
        result = subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(linked), str(target)],
                                capture_output=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.output = linked / "selected.txt"
        try:
            with patch.object(yt_dlp, "YoutubeDL", side_effect=AssertionError("reached downloader")):
                with self.assertRaises(session.SessionError):
                    self.select()
            self.assertFalse((target / "selected.txt").exists())
        finally:
            linked.rmdir()


if __name__ == "__main__":
    unittest.main()
