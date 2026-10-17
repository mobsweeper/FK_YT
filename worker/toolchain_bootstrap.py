"""Select an app-verified package set without changing embedded Python's _pth.

No network, installation, user environment, site import, or third-party plugin
discovery is performed here. The C# launcher owns the absolute paths and hashes.
"""
from __future__ import annotations

import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import runpy
import sys


def ensure_regular_path(path):
    for item in (path, *path.parents):
        if item.is_symlink() or getattr(item, "is_junction", lambda: False)():
            raise ValueError("Linked toolchain paths are not accepted")


def select_packages(packages):
    selected = Path(packages)
    ensure_regular_path(selected)
    if not selected.is_absolute() or not selected.is_dir():
        raise ValueError("Invalid toolchain packages")
    selected = selected.resolve(strict=True)
    # The shipped embedded interpreter has exactly the two yt-dlp packages in
    # site-packages. Remove its old set, rather than silently filling holes in
    # an incomplete overlay with an older installed package.
    sys.path[:] = [str(selected)] + [value for value in sys.path
                                   if value and Path(value).name.lower() != "site-packages"
                                   and os.path.normcase(str(Path(value).resolve())) != os.path.normcase(str(selected))]
    for name in ("yt_dlp", "yt_dlp_ejs"):
        if name in sys.modules:
            raise ValueError("The package set was selected too late")
        ensure_regular_path(selected / name / "__init__.py")
        if not (selected / name / "__init__.py").is_file():
            raise ValueError("Incomplete toolchain packages")
    return selected


def _version(value):
    return tuple(int(part) for part in value.split("."))


def check_toolchain(expected_yt, expected_ejs):
    import yt_dlp
    from yt_dlp import version
    import yt_dlp_ejs
    from yt_dlp.extractor.youtube import YoutubeTabIE
    from yt_dlp.globals import IN_CLI, plugin_dirs
    from yt_dlp.postprocessor.ffmpeg import FFmpegPostProcessor

    if _version(version.__version__) != _version(expected_yt):
        raise ValueError("yt-dlp package version mismatch")
    if _version(importlib.metadata.version("yt-dlp-ejs")) != _version(expected_ejs):
        raise ValueError("EJS package version mismatch")
    required = ("generate_api_headers", "_extract_context", "extract_ytcfg",
                "_download_ytcfg", "_call_api", "_extract_metadata_from_tabs",
                "_extract_lockup_view_model", "_extract_video", "_extract_data_sync_id",
                "_extract_session_index")
    if any(not callable(getattr(YoutubeTabIE, name, None)) for name in required):
        raise ValueError("yt-dlp worker API compatibility check failed")
    if not hasattr(IN_CLI, "value") or not hasattr(plugin_dirs, "value") or not callable(getattr(FFmpegPostProcessor._ffmpeg_location, "set", None)):
        raise ValueError("yt-dlp adapter compatibility check failed")
    parsed = yt_dlp.parse_options(["--ignore-config", "--no-plugin-dirs", "--no-cache-dir",
                                  "--skip-download", "--no-js-runtimes",
                                  "https://www.youtube.com/watch?v=fixture0001"])
    if not parsed.urls or not isinstance(parsed.ydl_opts, dict):
        raise ValueError("yt-dlp CLI compatibility check failed")
    print(json.dumps({"ytDlp": version.__version__, "ejs": importlib.metadata.version("yt-dlp-ejs"),
                      "python": ".".join(map(str, sys.version_info[:3]))}, separators=(",", ":")))


def main(argv=None):
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--packages", required=True)
    parser.add_argument("--script")
    parser.add_argument("--check", nargs=2, metavar=("YT_DLP", "EJS"))
    parser.add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    select_packages(args.packages)
    if args.check:
        if args.script or args.arguments:
            raise ValueError("Invalid toolchain check")
        check_toolchain(*args.check)
        return 0
    if not args.script:
        raise ValueError("A worker is required")
    script = Path(args.script)
    ensure_regular_path(script)
    worker_root = Path(__file__).resolve().parent
    if not script.is_absolute() or script.resolve(strict=True).parent != worker_root or script.suffix != ".py" or script.resolve() == Path(__file__).resolve():
        raise ValueError("Only application workers are allowed")
    arguments = args.arguments[1:] if args.arguments[:1] == ["--"] else args.arguments
    sys.argv = [str(script), *arguments]
    runpy.run_path(str(script), run_name="__main__")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        # Never expose upstream diagnostic text or private worker requests.
        sys.stderr.write("Toolchain compatibility or worker startup failed.\n")
        raise SystemExit(1)
