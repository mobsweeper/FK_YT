# FK_YT 1.1 features and limits

FK_YT 1.1 uses application version `1.1.0` and the latest preview6 product behavior. See the [release files and publication status](https://github.com/mobsweeper/FK_YT/blob/v1.1/docs/RELEASE-1.1.md), [build instructions](https://github.com/mobsweeper/FK_YT/blob/v1.1/docs/BUILD-1.1.md) and [licensing record](https://github.com/mobsweeper/FK_YT/blob/v1.1/docs/LICENSING-1.1.md). The portable and installed packages share the same application DLL and common app source; an installed-mode marker selects the data directory.

## Playback and interface

- Theater mode can keep the comments/recommendations panel open. Comments still load only when requested.
- Normal/theater windows can collapse the bottom playback controls, with a separate restore control. The manual choice lasts for the current app session. Fullscreen/popout keep automatic hiding.
- Dark, contrasting playback/list-card tooltips make explanatory text readable.
- Pause input during initial player startup is retained until the current media can accept it. Rapid live seek steps accumulate in order within confirmed cache ranges. Failed or stale requests no longer leave a provisional position displayed indefinitely; both player windows use the same LIVE readiness check.
- Explicitly seeking an ordinary video to zero can update its saved resume position before a positive position was observed. Repeated valid paused observations retain the confirmed zero. Initial automatic zero observations still preserve an existing resume position. Live and incognito playback do not use this VOD resume-saving path.
- Late channel/live metadata keeps the exact queue occurrence and previous-video history in sync. A next-video preload that finishes after the current video becomes live is discarded.
- Shorts filtering excludes **explicitly marked served-ad cards**. It preserves ordinary videos and creator paid-promotion disclosures. Every upstream ad variant has not been verified.

## Live playback

Metadata distinguishes current, upcoming, ended/processing, archived and ordinary media. Live seeking uses only **confirmed local mpv cache ranges**, including gaps. It does not promise the server's full DVR window. **LIVE** reopens the current broadcast edge and preserves an intentional pause.

Guest/default-slot live playback keeps the existing mpv extraction path for initial playback, LIVE, quality changes and reconnection. Other Google sign-in slots require a separately validated, single muxed HTTPS HLS stream. Unsupported selected-account live transports fail explicitly; FK_YT does not substitute another account. A public live response that lacked enough codec metadata to confirm combined audio and video was rejected by this selected-account resolver while ordinary mpv playback worked. Missing metadata is not treated as proof of a supported muxed stream.

After live EOF/disconnection, the app checks broadcast status and can retry up to three times with 2/5/10-second delays. It waits for playback-error classification and stops retries for authentication, access or rate-limit failures. Automatic next-video playback requires confirmation that the broadcast ended. Manual pause, stop, media/account changes and incognito changes cancel recovery. These controls do not promise uninterrupted playback.

Live playback does not use VOD resume positions or next-video preloading when live status is known before loading, or when the unified selected-account resolver identifies it. A direct guest/default-slot URL can be identified by mpv metadata only after loading. Earliest EOF handling and an old saved VOD resume position on that path still need separate live testing. No extra extraction is forced for every ordinary video to hide this limitation.

Live chat, scheduled-stream waiting and recording are not included. A larger local buffer cannot retrieve data the viewer never received. Server DVR before joining requires a separate verified playback/timeline path and is not implemented here.

## RAM-only cache

Ordinary videos retain a 15-second forward target and **32 MiB forward / 8 MiB backward budgets per demuxer**. Confirmed live playback uses a **6 GiB (6,442,450,944-byte) backward budget per demuxer**. The forward budget remains 32 MiB; unused forward capacity is not donated to the backward budget.

These figures add the configured forward and backward budgets; they are **not strict total-RAM ceilings**. They apply to compressed media packets, not decoded frames.

| Playback/cache instance | Forward | Backward | Configured sum |
| --- | ---: | ---: | ---: |
| Current ordinary video (including its audio) | 32 MiB | 8 MiB | about 40 MiB |
| One next-video preload (including its audio) | 32 MiB | 8 MiB | up to another configured 40 MiB |
| Current ordinary video + one next-video preload | — | — | about 80 MiB combined configured budget |
| One confirmed live stream (including its audio) | 32 MiB | 6 GiB = 6,144 MiB | 6,176 MiB = 6.03125 GiB |

**Separate video/audio URLs do not normally mean two full caches in FK_YT.** Its split-stream playback path joins them into one EDL timeline; the timeline shares the packet cache, and its child demuxers do not keep another full cache. There is no fixed video-versus-audio split within that shared budget. Only genuinely independent top-level demuxers would each receive a budget, so the normal live path is not described as “6 GiB for video plus 6 GiB for audio.” See the [bundled mpv cache settings](https://github.com/mpv-player/mpv/blob/ec38361c56b2d91e417c4c3e58184535d1a1aa2e/DOCS/man/options.rst#L4279-L4324) and [timeline implementation](https://github.com/mpv-player/mpv/blob/ec38361c56b2d91e417c4c3e58184535d1a1aa2e/demux/demux_timeline.c#L614-L618).

For ordinary videos, mpv can donate unused forward space to the backward cache: 8 MiB is its baseline backward setting, not a separate hard cap; the combined budget remains 40 MiB. A preload normally has little or no played-back data, so the 80 MiB row is a conservative sum of settings, not expected initial usage. Known live playback disables this donation and next-video preloading. mpv can slightly exceed its packet-budget settings for technical reasons.

Thumbnail memory (96 MiB estimated image data), decoders, GPU textures, UI, subtitles, network buffers and packet-pool overhead are separate. For example, the listed packet budgets plus the thumbnail budget sum to about **176 MiB for current+next ordinary videos**, or **6.125 GiB for one live stream**; these partial sums still do not bound application/process RAM. Thumbnail disk cache (512 MiB) is separate from RAM, and playback disk caching is disabled. Buffers grow as data arrives rather than being fully preallocated.

The cache grows as data arrives. **6 GiB is not allocated in advance and is not a whole-process RAM limit.** Decoder, audio/video and application allocations are separate. It is not a guarantee that every past segment stays seekable or a guaranteed number of rewind minutes. The 10 MiB HTTP request size is a separate per-request limit.

If live status arrives after loading and next-video preload was already attempted, the current playback keeps its original budget to avoid changing another preloaded entry's demuxer. Reopening the now-known live stream with LIVE applies the live budget. Cache changes are scoped to the exact playing native entry so a late response cannot change the next video's options.

Disk caching is disabled. The operating system reclaims playback buffers when the player exits, including after a crash. This RAM-only cache is separate from explicit downloads: completed downloads and backups remain. Normal download cleanup waits for its owned process tree to exit. There is no new next-start sweep of abandoned download folders or broad deletion of user data.

The live allowance does not diagnose or claim to fix ordinary long-video playback failures. The web player's “Video paused. Continue watching?” prompt is not an established cause in this native app. No periodic forced-resume command was added.

## Manual experimental tool updates

Settings offers manual stable yt-dlp/Deno checks, staging, rollback and bundled-tool restore. The yt-dlp EXE, matching Python package and EJS are staged together. The verified selection applies on the **next app start**; the running process keeps its original tool set. Python, mpv and FFmpeg are not updated by this feature. No automatic online check or installation occurs.

**Keep the bundled tools if playback works normally.** Upstream changes can break playback, lists, comments or subtitles even after offline checks pass. Downloads use official HTTPS services without YouTube cookies. Hash checks use publishers' HTTPS metadata; they are not an independent publisher-signature guarantee. Unsupported dependencies, malformed archives, hash mismatches and incompatible worker APIs stop an update.

Verification/extraction runs outside the UI thread and checks cancellation between files/blocks. A completed restart reservation remains visible if cancellation arrives afterward. Failed staging waits for owned probe processes to stop before cleanup. Interrupted activation preserves the previous rollback choice; unresolved recovery cannot be bypassed by scheduling another selection.

A previous set with a confirmed manifest/file-integrity failure is unavailable for rollback during the current app session, including failed startup activation and direct rollback. Failed requests refresh the button while preserving the error. Cancellation, transient file-access failures and offline probe failures are not automatically labelled corruption. A restart can offer the set again, but activation still requires revalidation. Some rejected linked-path cases have not been unified with this session-invalid button state.

Portable updates live in `data/tools` beside the app; installed updates live in `%LOCALAPPDATA%\FK_YT\data\tools`. Original tools are preserved. Completed older versions are retained for recovery, with **no automatic old-version cleanup or total storage cap**. Preserve needed data before manual cleanup.

The tool directory contains executable code trusted by the current Windows user. Local hashes cannot authenticate against another process able to rewrite both tools and their manifest. Each staged version retains provenance and license/source notices, but the original release source archives describe only the original bundled versions. Review notices and corresponding sources before copying a downloaded overlay into another public distribution. Deno's retained tagged notices/source references are a subset, not a verified complete binary third-party-license inventory.

## Process and request handling

Player IPC checks the connected server against the owned mpv process before sending commands. Child processes omit the TLS key-log environment variable while preserving proxy/certificate settings. Channel thumbnails use the shared CDN URL validation. These narrow changes are not a claim that every runtime/path or authenticated-media security question has been resolved.

## Verification boundary

The latest preview6 work recorded 61/61 offline regression groups and 249 offline updater assertions, plus focused checks of the exact normal DLL. Installer preparation recorded 29/29 PowerShell fixtures, 27/27 packaging fixtures, payload identity checks and an NSIS compile. Those results are distinct from final 1.1 compilation/packaging and from actual public YouTube playback. They are not newly rerun whole-suite results for the version-only promotion.

The maintainer subsequently confirmed Windows 10/11 installation, installed-app launch/playback, installer updates and removal on 2026-10-17. A detailed authenticated/live matrix, a fresh two-hour autoplay/live run, complete live-cache fill/memory pressure, official experimental tool download/activation, installation timing and detailed UAC/permission scenarios remain outside that confirmation. A prior two-hour autoplay result on an earlier candidate does not establish a new 1.1 result. See [RELEASE-1.1.md](https://github.com/mobsweeper/FK_YT/blob/main/docs/RELEASE-1.1.md#verification-scope).

FK_YT's own code/documents remain MIT. Original external runtime notices and corresponding sources remain applicable to the unchanged bundled tools. See [license.md](https://github.com/mobsweeper/FK_YT/blob/v1.1/license.md) and the [licensing record](https://github.com/mobsweeper/FK_YT/blob/v1.1/docs/LICENSING-1.1.md); offline checks and source access are not legal certification.
