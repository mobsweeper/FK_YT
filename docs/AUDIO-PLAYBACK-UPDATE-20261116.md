# Original audio update — 2026-11-16

This local update builds on `FK_YT-account-playback-reviewfix-20261116`. It has not been published as a GitHub release. Earlier release notes describe their named builds.

## Changes

The previous account-playback update retained yt-dlp's higher-ranked candidate only when HLS streams had the same height. A higher-resolution dubbed rendition could still displace the original audio. The ordinary video selector also lacked the explicit original-audio preference already used by the audio-only path.

This update restores that preference across ordinary video extraction, verified HLS playback and prepared direct media. It uses yt-dlp's explicit original-audio marker, not the application's display language or the account's region. When original audio is identified, supported original renditions take priority; resolution limits and stream safety checks still apply. Among original HLS renditions of the same height, yt-dlp's higher rank remains preferred.

The selected-account checks, prepared-item ownership and retry requirements, delayed-quality cancellation, live transport handling and tool compatibility checks from the preceding package are retained. No account reset, cookie change, UI-language change or account-region change is part of this update.

## Compatibility and limits

When the extractor provides no original-audio marker, the existing compatibility selection remains available. The native format selector can still use a compatible non-original rendition when no original fits the requested format or height limit. Resolved VOD metadata with an identified original rejects a selected dubbed audio stream instead of silently treating it as the original. These are preferences and validation rules, not a claim that every upstream catalog identifies its audio correctly.

The reported video's URL was unavailable. Verification therefore uses controlled format catalogs, the bundled yt-dlp selector and exact-build regression checks; it does not prove the audio language of that particular online video. The previous release's two-video network test is historical evidence for its own DLL, not a new network test of this build.

The self-contained x64 Release build passed 24 original-audio/parser checks, 11 pinned yt-dlp selector checks, 21 preceding playback-contract checks, 3 local IPC checks and 4 delayed-quality checks. The previous DLL failed 10 of the audio/parser checks and 4 of the selector checks. Only the 3 named-pipe fixtures needed a less restricted local test environment; the product's security policy was unchanged. Worker source files are unchanged and the previous 288+9 worker results were not counted as new runs.

Product DLL SHA-256: `AC2A04078699EC5AC7C9F7B7AFB73AC96DC2E417A65C85807760C629FAE910ED`.

## Packages and preserved components

- `FK_YT-original-audio-reviewfix-20261116-portable.zip`: extract into a new folder and run `FK_YT/FK_YT.exe`. It contains the required runtimes, user guides and license notices, without saved accounts, history or test harnesses.
- `FK_YT-original-audio-reviewfix-20261116-personal-backup.zip`: the same application with the owner's saved data. Keep it private. DPAPI sign-in data is tied to the Windows user; the Firefox profile is not included.
- `FK_YT-original-audio-reviewfix-20261116-source.zip`: editable application source plus the existing yt-dlp source/build collection under `third-party/yt-dlp-2026.10.26/`.

The installed application and its data are not overwritten. Use the personal backup on the same Windows account to retain the captured settings, sign-in data and history. Check the archives against the accompanying `SHA256SUMS.txt`.

Bundled runtimes remain unchanged: yt-dlp EXE and Python package 2026.10.26, worker Python 3.13.16, EJS 0.8.0, Deno 2.9.7, the existing custom mpv/FFmpeg binaries, and .NET Runtime 10.0.13. The SDK is only needed for source builds. Existing Bootstrap/Build/Publish scripts remain in the source archive; historical `scripts/Package.py` is not the assembly recipe for these local update ZIPs.

FK_YT is MIT; external components retain their own licenses. The official standalone Windows yt-dlp EXE is GPL-3.0-or-later, while its separate Python wheel is Unlicense. Original copyright and license texts are preserved. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, `SOURCE-ACCESS.txt` and `notices/media/SOURCE-ACCESS.txt` in the portable package.

The source ZIP retains the 51 upstream source/build archives collected for this distribution. Unchanged mpv/FFmpeg source access remains documented in the media notice: both independent ZIPs are required and are extracted into the same folder. Historical notices retain their original filenames and hashes; the current source-access notice identifies this build's source ZIP and hash. External binaries were not rebuilt in this update, and previously disclosed reproducibility limitations remain.
