"""Export only Google/YouTube cookies from a user-selected Firefox profile."""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import shutil
import stat
import sys
import tempfile
from pathlib import Path

from youtube_session import DiscardOutput


class QuietLogger:
    def debug(self, _message):
        pass

    info = debug
    warning = debug
    error = debug

    def progress_bar(self):
        return None


def permitted_domain(domain):
    if not isinstance(domain, str):
        return False
    domain = domain.lstrip(".").lower().rstrip(".")
    return any(domain == allowed or domain.endswith("." + allowed)
               for allowed in ("youtube.com", "google.com"))


def is_link_or_reparse(path):
    """Reject Windows reparse points even on Python before Path.is_junction."""
    if path.is_symlink():
        return True
    is_junction = getattr(path, "is_junction", None)
    if callable(is_junction) and is_junction():
        return True
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", None)
    except FileNotFoundError:
        return False
    if attributes is None:
        if os.name == "nt":
            # Do not follow a Windows directory when the fallback cannot prove it safe.
            raise ValueError("cannot inspect work directory reparse attributes")
        return False
    return bool(attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def export_session(profile, output, *, extractor=None, jar_factory=None):
    # Imports and extraction occur only after the app's explicit connect action.
    with contextlib.redirect_stdout(DiscardOutput()), contextlib.redirect_stderr(DiscardOutput()):
        if extractor is None or jar_factory is None:
            from yt_dlp.cookies import YoutubeDLCookieJar, extract_cookies_from_browser
            extractor = extractor or extract_cookies_from_browser
            jar_factory = jar_factory or YoutubeDLCookieJar
        output_path = Path(output)
        if not output_path.parent.is_dir():
            raise ValueError("output directory missing")
        # The app owns this output container. A link in any existing ancestor
        # would redirect cookie copies outside that private work boundary.
        for directory in (output_path.parent, *output_path.parent.parents):
            if is_link_or_reparse(directory):
                raise ValueError("output directory is not a regular directory")
        work_path = output_path.with_suffix(".work")
        previous_tempdir = tempfile.tempdir
        created_work = False
        job_path = None
        try:
            try:
                work_path.mkdir(exist_ok=False)
                created_work = True
            except FileExistsError:
                pass
            if is_link_or_reparse(work_path) or not work_path.is_dir():
                raise ValueError("work directory is not a regular directory")
            # An interrupted export can leave the container behind. Each retry
            # owns a new child, so it never removes or reuses stale cookie files.
            job_path = Path(tempfile.mkdtemp(prefix="export-", dir=work_path))
            # Upstream copies cookies.sqlite into tempfile.TemporaryDirectory.
            # Keep that copy under an app-owned job directory so C# can clean it
            # even if its Job Object forcibly terminates this helper.
            tempfile.tempdir = str(job_path.resolve())
            cookies = extractor("firefox", profile=profile, logger=QuietLogger())
            descriptor, temporary_name = tempfile.mkstemp(prefix="filtered-", suffix=".txt", dir=job_path)
            os.close(descriptor)
            filtered = jar_factory(temporary_name)
            count = 0
            for cookie in cookies:
                if permitted_domain(cookie.domain):
                    filtered.set_cookie(cookie)
                    count += 1
            if count == 0:
                raise ValueError("no permitted cookies")
            filtered.save(ignore_discard=True, ignore_expires=True)
            os.replace(temporary_name, output_path)
            return {"success": True, "count": count}
        finally:
            tempfile.tempdir = previous_tempdir
            if job_path is not None:
                shutil.rmtree(job_path)
            if created_work and not is_link_or_reparse(work_path):
                # Only remove the container we created, and only when empty.
                # Other invocations or stale files must survive this cleanup.
                try:
                    work_path.rmdir()
                except FileNotFoundError:
                    pass
                except OSError:
                    if not work_path.is_dir():
                        raise


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, _message):
        self.exit(2, "Firefox 연결 인수를 확인해 주세요.\n")


def main(argv=None):
    parser = SafeArgumentParser(description="Export a selected Firefox YouTube session.")
    parser.add_argument("--profile", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        result = export_session(args.profile, args.output)
    except Exception:
        # Never print upstream exceptions, cookie values, or profile paths.
        sys.stderr.write("Firefox 연결 정보를 가져오지 못했습니다. 로그인과 선택한 프로필을 확인해 주세요.\n")
        return 1
    sys.stdout.write(json.dumps(result) + "\n")
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
