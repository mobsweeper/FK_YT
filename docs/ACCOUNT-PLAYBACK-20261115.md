# Account playback update — 2026-11-15

This local update builds on FK_YT 1.1.2 and the popout-browsing update. It has not been published as a GitHub release. Earlier release pages and verification records describe their named versions.

## Changes

- Updated the standalone yt-dlp executable and embedded Python package from 2026.08.19 to 2026.10.26. EJS 0.8.0, Deno 2.9.7, mpv, FFmpeg and the app's .NET runtime are unchanged.
- Selected-account ordinary video playback loads the watch page and verifies its original login, channel session and Google account slot before using its metadata. The generic `player_skip=webpage,configs` and forced client list are removed only for this verified operation. A mismatched page fails closed; there is no guest/other-account retry.
- Validated muxed HLS renditions from the same response are preferred within the saved quality cap. Available qualities reflect these renditions. Direct-only higher resolutions may therefore be absent. If no eligible HLS rendition exists, the same account's normal direct selection remains available; this does not guarantee that every CDN URL will work.
- Prefetch uses the same verified resolver and preserves the existing mpv prepared-entry adoption. Changing quality refreshes the resolved quality list.
- A missing watch-page response is classified as a network failure; a present but mismatched account remains a session error.

## Verification

- Release build completed without compiler errors. Product DLL SHA-256: `1753C72271C658F4DBE07411221D613F7681B1D141F788CAF4B9C055AD000943`.
- Existing offline Python worker regressions: 280/280; new guard and CLI regressions: 8/8. Focused C# playback/quality boundary groups: 11/11.
- Real network playback through this exact DLL's `YouTubeService` and `MpvPlayerService`, with the copied saved account, decoded both `j00vR-Sm5CA` and `aqz-KE-bpKQ` at 1920×1080 and 44.1 kHz stereo. Each advanced for at least 10 seconds. The second video adopted its prepared entry in the same mpv process with no fallback extraction.
- A decoded frame was verified locally and deleted. Audio output was muted, so this verifies audio decoding rather than human listening. No main-window click-through or long-session/whole-video test is claimed. Computer Use was not used.
- The isolated test's settings, encrypted sessions and SQLite file remained byte-identical. Temporary cookie leases and frame captures were removed.

This does not establish compatibility with every account/video, age-restricted content, long playback, all live transports, audio-only mode or all network conditions. Incognito, live and audio-only routing were intentionally left unchanged. No cookie reset or modification was applied to the original account.

## Files and operation

- `FK_YT-account-playback-20261115-portable.zip`: extract completely into a new folder and run `FK_YT/FK_YT.exe`. No personal data or test harness is included.
- `FK_YT-account-playback-20261115-personal-backup.zip`: same app plus the owner's saved data. Keep private; encrypted sign-in data is tied to the Windows user. The Firefox browser profile itself is not included.
- `FK_YT-account-playback-20261115-source.zip`: editable app source plus the updated yt-dlp source/build materials in `third-party/yt-dlp-2026.10.26/`.

Build the application using the source's existing Bootstrap/Build/Publish scripts and its dependency lock; user execution does not require the SDK. `scripts/Package.py` remains the historical 1.1.2 assembly recipe and does not produce this update's three ZIPs. The local release ZIPs were assembled from the existing licensed portable baseline, matching self-contained application publish, current production workers, updated dependency lock and verified runtime replacements.

The inherited mpv/FFmpeg bootstrap entries describe original third-party inputs, not the later FK_YT-built media binaries in the portable baseline. Their bootstrap executable hashes do not match the shipped custom binaries. Those unchanged binaries were instead compared byte-for-byte with the prior licensed portable; the current file manifest and distribution provenance record the shipped hashes. Running Bootstrap alone is not a reproduction of those custom media builds; use their corresponding-source build materials.

## Licensing and source access

FK_YT remains MIT; external components retain their respective terms. The official Windows yt-dlp executable is GPL-3.0-or-later; the separately distributed Python wheel is Unlicense. Read the current `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt` and `notices/media/SOURCE-ACCESS.txt` in the portable ZIP. Previous source-access and licensing records are preserved as historical records, not current filenames.

The new source ZIP contains 51 upstream source/build archives and provenance for the updated yt-dlp binary and package. These are a collection prepared for this distribution, not an official upstream combined ZIP. The unchanged mpv/FFmpeg source archives and their hashes/access locations remain documented in the media source notice; both independent ZIPs are needed and are extracted into the same folder. Independent byte-for-byte binary reproduction was not performed, and the exact native MinGW CRT build revision remains inferred as explicitly recorded in the source README. No blanket legal-compliance guarantee is made.
