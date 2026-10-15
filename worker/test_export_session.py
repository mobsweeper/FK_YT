"""Offline exporter compatibility/security regressions with synthetic cookies only."""

import http.cookiejar
import os
import stat
import sys
import tempfile
import tracemalloc
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import export_session as exporter


class ExportCompatibilityTests(unittest.TestCase):
    def test_large_incidental_extractor_output_is_discarded_with_failure_preserved(self):
        test_root = Path(__file__).resolve().parents[1] / "artifacts" / "worker-tests"
        test_root.mkdir(parents=True, exist_ok=True)
        chunk = "synthetic-private-diagnostic" * 2500
        def noisy_extractor(*_args, **_kwargs):
            for _index in range(64):
                sys.stdout.write(chunk)
                sys.stderr.write(chunk)
            raise TimeoutError("offline network timeout")
        with tempfile.TemporaryDirectory(dir=test_root) as directory:
            tracemalloc.start()
            try:
                with self.assertRaises(TimeoutError):
                    exporter.export_session("synthetic", Path(directory) / "session.txt",
                        extractor=noisy_extractor, jar_factory=http.cookiejar.MozillaCookieJar)
                _current, peak = tracemalloc.get_traced_memory()
            finally:
                tracemalloc.stop()
            self.assertLess(peak, 512 * 1024)
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_reparse_output_parent_never_reaches_extractor_or_creates_work(self):
        output = Path("synthetic-output-parent") / "session.txt"
        parent = output.parent
        extractor = Mock(side_effect=AssertionError("extractor called through a linked output parent"))
        with patch.object(Path, "is_junction", None, create=True), \
                patch.object(Path, "is_symlink", return_value=False), \
                patch.object(Path, "is_dir", return_value=True), \
                patch.object(Path, "lstat", autospec=True, side_effect=lambda path, *args, **kwargs:
                    SimpleNamespace(st_file_attributes=stat.FILE_ATTRIBUTE_DIRECTORY | stat.FILE_ATTRIBUTE_REPARSE_POINT)
                    if path == parent else SimpleNamespace(st_file_attributes=stat.FILE_ATTRIBUTE_DIRECTORY)), \
                patch.object(Path, "mkdir") as mkdir, \
                patch.object(Path, "rmdir"), \
                patch.object(Path, "resolve", autospec=True, side_effect=lambda path: path), \
                patch.object(tempfile, "mkdtemp", return_value=str(output.with_suffix(".work") / "export-fixture")), \
                patch.object(exporter.shutil, "rmtree"):
            with self.assertRaises(ValueError):
                exporter.export_session("synthetic", output, extractor=extractor,
                    jar_factory=http.cookiejar.MozillaCookieJar)
            extractor.assert_not_called()
            mkdir.assert_not_called()

    def test_python311_without_is_junction_exports_and_cleans(self):
        test_root = Path(__file__).resolve().parents[1] / "artifacts" / "worker-tests"
        test_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=test_root) as directory:
            output = Path(directory) / "session.txt"
            cookie = http.cookiejar.Cookie(0, "SYNTHETIC", "fixture", None, False,
                                           ".youtube.com", True, True, "/", True,
                                           True, None, True, None, None, {})
            with patch.object(Path, "is_junction", None, create=True):
                result = exporter.export_session("synthetic", output,
                    extractor=lambda *args, **kwargs: [cookie],
                    jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(result, {"success": True, "count": 1})
            self.assertIn("SYNTHETIC", output.read_text(encoding="utf-8"))
            self.assertEqual(list(Path(directory).iterdir()), [output])

    def test_fallback_rejects_junction_and_other_reparse_tags(self):
        path = Path("synthetic-work")
        with patch.object(Path, "is_symlink", return_value=False), \
                patch.object(Path, "is_junction", None, create=True), \
                patch.object(Path, "lstat", return_value=SimpleNamespace(
                    st_file_attributes=stat.FILE_ATTRIBUTE_DIRECTORY | stat.FILE_ATTRIBUTE_REPARSE_POINT)):
            self.assertTrue(exporter.is_link_or_reparse(path))

    def test_windows_fallback_missing_attributes_fails_closed(self):
        path = Path("synthetic-work")
        with patch.object(Path, "is_symlink", return_value=False), \
                patch.object(Path, "is_junction", None, create=True), \
                patch.object(Path, "lstat", return_value=SimpleNamespace()), \
                patch.object(os, "name", "nt"):
            with self.assertRaises(ValueError):
                exporter.is_link_or_reparse(path)

    def test_reparse_directory_never_reaches_extractor_or_cleanup(self):
        test_root = Path(__file__).resolve().parents[1] / "artifacts" / "worker-tests"
        test_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=test_root) as directory:
            output = Path(directory) / "session.txt"
            work = output.with_suffix(".work")
            work.mkdir()
            sentinel = work / "preserved.txt"
            sentinel.write_text("synthetic sentinel", encoding="utf-8")
            with patch.object(Path, "is_junction", None, create=True), \
                    patch.object(Path, "is_symlink", return_value=False), \
                    patch.object(Path, "lstat", autospec=True, side_effect=lambda path, *args, **kwargs:
                        SimpleNamespace(st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
                        if path == work else original_lstat(path, *args, **kwargs)):
                with self.assertRaises(ValueError):
                    exporter.export_session("synthetic", output,
                        extractor=lambda *args, **kwargs: self.fail("extractor called"),
                        jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(sentinel.read_text(encoding="utf-8"), "synthetic sentinel")
            self.assertFalse(output.exists())


original_lstat = Path.lstat

if __name__ == "__main__":
    unittest.main()
