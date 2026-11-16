# Account playback update — 2026-11-16

This local update builds on the 2026-11-15 account-playback package. It has not been published as a GitHub release. Earlier release pages, source-access notes and verification records describe their named versions.

## Changes

- Prefetch preserves the video's confirmed status and the requirement to verify the selected account. If mpv cannot adopt a prepared entry, the retry retains that requirement for both the default Google login slot and other slots. A successful adoption still reuses the prepared media without another extraction. Prepared metadata is tied to the queue occurrence, account lifetime, media generation and playback preferences.
- Among supported HLS streams at the highest height within the saved cap, ties retain yt-dlp's higher format preference instead of choosing its lowest-ranked entry. The existing HLS URL, header, account and DRM checks remain in place.
- A playback response's quality list cancels an older standalone quality lookup, preventing its late result or error from replacing the active list. Manual quality lookup remains available for playback paths that need it.
- The policy for starting a live response from the beginning is separate from VOD account verification. A stale status hint no longer forces an unnecessary muxed-HLS requirement before that live transport can be selected.
- Offline tool-update compatibility checks cover the private extractor calls used by verified playback. They reject missing or incompatible call contracts; they do not guarantee future online YouTube compatibility.

No account reset, cookie modification or switch to a different account is part of this update. No fixed delay was added for yt-dlp's optional future `available_at` metadata: its relevance to the observed playback issue has not been established. This update does not claim to identify the cause of every historical buffering or early-EOF report.

## Verification and limits

The self-contained x64 Release build uses .NET Runtime 10.0.13. Product DLL SHA-256: `22C10693A44AE67AE1D3E3BB14B2D8B01E53A4BCC297B6542CCADD46E6924A97`.

Offline checks passed: 24 focused playback/parser/IPC checks, 4 delayed quality-lookup checks, 288 worker regressions and 9 update-compatibility checks. The two junction fixtures and three named-pipe fixtures required a less restricted test environment; the product's path and pipe protections were not relaxed. New failure cases were checked against the previous DLL or worker before the repair.

Real network playback through this exact DLL's services and native mpv used a protected copy of the current saved account. Two videos decoded at 1920×1080 with 44.1 kHz stereo and advanced for 10.050 and 10.083 seconds. The next video adopted its prepared entry in the same mpv process with no fallback extraction. Audio output was muted, so this verifies audio decoding rather than human listening. Frame captures were temporary. Computer Use was not used.

The synthetic and real-network checks are distinct. They do not establish compatibility with every account, video, live stream or network condition. The full main-window click-through, long idle prefetch, whole-video playback and real multi-account fallback failures were not exercised in this run. No change was made to the original account or cookies; tests used a separate data copy.

## Packages

- `FK_YT-account-playback-reviewfix-20261116-portable.zip`: extract completely into a new folder and run `FK_YT/FK_YT.exe`. It includes the required runtimes, guides and notices, without personal data or test harnesses.
- `FK_YT-account-playback-reviewfix-20261116-personal-backup.zip`: the same application plus the owner's saved data. Keep this file private. DPAPI-encrypted sign-in information is tied to the Windows user; the Firefox profile itself is not included.
- `FK_YT-account-playback-reviewfix-20261116-source.zip`: editable application source and the updated yt-dlp source/build collection in `third-party/yt-dlp-2026.10.26/`.

The existing application and its data are not overwritten. To keep your session, use the private backup on the same Windows account. The portable package starts with its own data folder. Check each archive against the accompanying `SHA256SUMS.txt`.

## Runtimes, licensing and source access

Bundled runtimes are unchanged from the 2026-11-15 package: yt-dlp EXE/Python package 2026.10.26, worker Python 3.13.16, EJS 0.8.0, Deno 2.9.7, the existing custom mpv and FFmpeg builds, and .NET Runtime 10.0.13. Do not install the .NET SDK to run the packaged app. Source builds use the existing Bootstrap/Build/Publish scripts; the historical `scripts/Package.py` is not the assembly recipe for these local update ZIPs.

FK_YT remains MIT. External components retain their own licenses. The standalone official Windows yt-dlp executable is GPL-3.0-or-later; the separate Python wheel is Unlicense. Original license texts and copyright notices are preserved. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, the current source-access notice, and `notices/media/SOURCE-ACCESS.txt` in the package.

The source ZIP includes the same 51 upstream source/build archives for yt-dlp, collected for this distribution rather than published as one official upstream ZIP. Unchanged mpv/FFmpeg corresponding-source archives remain documented in the media notice; both independent ZIPs are required and are extracted into the same folder. Historical archive names and hashes describe earlier releases; current archive hashes are in this package's source-access notice and `SHA256SUMS.txt`.

The inherited mpv/FFmpeg bootstrap entries describe original inputs, not the later custom binaries. The shipped custom binaries are preserved from the prior licensed package and recorded by hash in the distribution manifest. A new byte-for-byte reproduction of external binaries was not performed; the previously disclosed MinGW CRT build-revision inference remains. No blanket legal-compliance guarantee is made.
