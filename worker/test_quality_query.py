"""Offline resolution query fixtures against the bundled yt-dlp implementation."""
import copy
import io
import json
import sys
import unittest
from unittest.mock import patch

import selected_ytdlp
from youtube_session import create_youtube_dl, validate_account_context


URL = "https://www.youtube.com/watch?v=aqz-KE-bpKQ"
BASE_ARGUMENTS = ["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist",
                  "--skip-download", "--no-mark-watched", "--no-write-info-json",
                  "--no-write-thumbnail", "--socket-timeout", "15", "--retries", "1",
                  "--extractor-retries", "1", "--no-write-comments", "--dump-single-json", URL]
ACCOUNT = {"id": "fixture-brand", "pageId": "123456", "dataSyncId": "123456||fixture_user", "authUser": 2}


class TextPipe(io.StringIO):
    def reconfigure(self, **_kwargs):
        pass


def exercise_resolution_query(arguments, account=None):
    """Fail every stream probe without making requests; retain raw format metadata."""
    import yt_dlp
    from yt_dlp.utils import DownloadError, ExtractorError

    fixture = {"id": "aqz-KE-bpKQ", "title": "Offline quality fixture", "extractor": "Youtube",
               "formats": [{"format_id": str(height), "url": f"https://fixture.invalid/{height}.m3u8",
                            "ext": "mp4", "protocol": "m3u8_native", "height": height, "width": height * 16 // 9,
                            "vcodec": "avc1", "acodec": "mp4a", "__needs_testing": True}
                           for height in (720, 1080)]}
    output, error, checks, inspected = TextPipe(), TextPipe(), [], {}

    def factory(options, supplied):
        ydl = create_youtube_dl(options, supplied)
        inspected["account"] = supplied
        inspected["options"] = ydl.params
        ydl._out_files.out = output

        def fail_probe(formats, **_kwargs):
            checks.extend(fmt["format_id"] for fmt in formats)
            return iter(())

        ydl._check_formats = fail_probe
        # Replace only network extraction. The real format processing and CLI
        # JSON printing still run, including automatic __needs_testing probes.
        ydl.extract_info = lambda *_args, **_kwargs: ydl.process_ie_result(copy.deepcopy(fixture), download=False)
        return ydl

    with patch.object(sys, "stdout", output), patch.object(sys, "stderr", error):
        try:
            if account is not None:
                with patch.object(sys, "stdin", TextPipe(json.dumps({"accountContext": account}) + "\n")), \
                        patch.object(selected_ytdlp, "create_youtube_dl", factory):
                    result = selected_ytdlp.main(arguments)
            else:
                parsed = yt_dlp.parse_options(arguments)
                with factory(parsed.ydl_opts, None) as ydl:
                    result = ydl.download(parsed.urls)
            failure = None
        except (DownloadError, ExtractorError):
            result, failure = 1, "format-selection"
    data = json.loads(output.getvalue()) if output.getvalue().strip() else None
    return {"exitCode": result, "failure": failure, "streamProbeAttempts": len(checks),
            "formats": data.get("formats", []) if data else [],
            # C# includes channel display fields. The adapter deliberately
            # keeps only validated authority fields (and a non-null signin URL).
            "accountPreserved": inspected.get("account") == validate_account_context(account),
            "checkFormats": inspected.get("options", {}).get("check_formats"),
            "skipDownload": inspected.get("options", {}).get("skip_download"),
            "markWatched": bool(inspected.get("options", {}).get("mark_watched"))}


class QualityQueryTests(unittest.TestCase):
    def test_csharp_channel_display_fields_do_not_change_selected_authority(self):
        account = ACCOUNT | {"name": "Fixture display name", "handle": "@fixture", "isSelected": True,
                             "signinUrl": None}
        result = exercise_resolution_query(["--no-check-formats", *BASE_ARGUMENTS], account)
        self.assertEqual(result["exitCode"], 0)
        self.assertTrue(result["accountPreserved"])
        for changed in ({"id": "other-fixture"}, {"authUser": 3}, {"dataSyncId": "123456||other_user"},
                        {"pageId": "654321", "dataSyncId": "654321||fixture_user"}):
            with self.subTest(changedFields=tuple(changed)), \
                    patch.object(selected_ytdlp, "validate_account_context", return_value=ACCOUNT | changed):
                result = exercise_resolution_query(["--no-check-formats", *BASE_ARGUMENTS], account)
                self.assertFalse(result["accountPreserved"])

    def test_original_query_loses_metadata_when_automatic_stream_probe_fails(self):
        for account in (None, ACCOUNT):
            with self.subTest(selected=account is not None):
                result = exercise_resolution_query(BASE_ARGUMENTS, account)
                self.assertEqual(result["exitCode"], 1)
                self.assertEqual(result["failure"], "format-selection")
                self.assertGreater(result["streamProbeAttempts"], 0)
                self.assertEqual(result["formats"], [])

    def test_metadata_only_query_keeps_resolutions_without_stream_probes(self):
        for account in (None, ACCOUNT):
            with self.subTest(selected=account is not None):
                result = exercise_resolution_query(["--no-check-formats", *BASE_ARGUMENTS], account)
                self.assertEqual(result["exitCode"], 0)
                self.assertEqual(result["streamProbeAttempts"], 0)
                self.assertEqual([fmt["height"] for fmt in result["formats"]], [720, 1080])
                self.assertTrue(result["accountPreserved"])
                self.assertIs(result["checkFormats"], False)
                self.assertTrue(result["skipDownload"])
                self.assertFalse(result["markWatched"])


if __name__ == "__main__":
    if sys.argv[1:] == ["--fixture"]:
        request = json.loads(sys.stdin.readline())
        print(json.dumps(exercise_resolution_query(request["arguments"], request.get("accountContext"))))
    else:
        unittest.main()
