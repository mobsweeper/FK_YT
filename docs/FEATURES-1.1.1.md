# FK_YT 1.1.1 features and limits

FK_YT 1.1.1 uses application version `1.1.1`. It adds explicit history opt-in and a durable sending queue to the 1.1 playback and interface features. See the [release files and availability](RELEASE-1.1.1.md), [build instructions](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/docs/BUILD-1.1.1.md) and [licensing record](LICENSING-1.1.1.md). Portable and installed packages share the same application DLL and common app source; the installed-mode marker selects the data directory.

## Watch history and explicit choice

YouTube history sending is **off by default**. An older setting without the current preference version is turned off once: its old enabled value may have come from the previous default. Settings explains this migration. Enable **Send watch history to my YouTube account** if wanted. A saved explicit choice persists across restarts and account changes. Incognito blocks sending without changing that choice.

Normal-mode local history and resume positions are independent and continue while sending is off. Enabling does not backfill old local history. A new sending record requires 30 seconds of actual eligible playback after opting in, or 80% for a video shorter than 30 seconds. Pausing, buffering, seeking and position jumps do not count as watched time. A later qualifying playback can create a separate record for the same video; this is not a once-per-video lifetime guarantee.

Accepted records persist in `viewer.sqlite`, scoped to their account and channel. A single sender processes the oldest eligible record first and commits its claim before contacting YouTube. Sending requires a connected, validated account/channel, opt-in, normal mode and healthy storage. Refresh the channel list if requested. Settings displays these states:

| State | Meaning and next action |
| --- | --- |
| Pending | Never attempted; eligible for sequential automatic sending once prerequisites hold |
| Held | Never attempted; use **Resume held records** after checking the selected account/channel and resolving the reason for the hold |
| Unconfirmed | An attempt may have reached YouTube, but its outcome is unknown; preserved without automatic resend |
| Failed | The attempt reported failure; preserved without automatic retry |
| Sent | The send operation was acknowledged; verify the account's history separately |
| Confirmed | The item was subsequently observed in the account's history |

After a normal restart, never-attempted pending records continue when the saved preference is on and the connected account/channel validates. An interrupted in-progress attempt becomes Unconfirmed. Turning sending off, switching/reconnecting accounts, switching channels or entering incognito holds unsent work. Re-enabling, leaving incognito or restarting does not release held records. **Resume held records** releases only the current account/channel's held records; it does not retry Failed or Unconfirmed attempts. **Refresh pending status** rechecks queue storage/status and does not release held records or validate an account.

Bot-check, request-limit and session errors hold further sending immediately. Two consecutive ambiguous outcomes also hold the remaining queue. A single other failed or unconfirmed attempt can be followed by other pending records. Resolve the connection/account issue before manually resuming. A queue read/write failure stops sending; check storage, then refresh pending status. A restart is not a retry mechanism for an attempted record.

YouTube can show a submitted item as fully watched. The app's precise local resume position remains separate. Unofficial account integration carries service-policy and account-access restriction risks. Offline checks do not guarantee YouTube acceptance or reliable account-history reflection. Incognito does not read/send queued work or save local history/resume positions; it does not hide the IP address or delete deliberate downloads.

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

The history implementation previously passed isolated offline checks: 50 C# core groups, 17 UI groups and 262 Python checks. The existing updater checks recorded 249 assertions, and a native mpv cache probe used local media. These check sets have different scopes and are not a combined total. The normal 1.1.1 app was published offline with the pinned SDK/cache and zero compiler warnings. Product tests and internal work records are excluded from the public source.

No new manual 1.1.1 installation/removal or actual YouTube/account-history test is claimed. The maintainer's Windows 10/11 installation, installed-app launch/playback, installer-update and removal confirmation on 2026-10-17 belongs to **1.1**. It does not verify the new history behavior. A fresh long playback/live run, full live-cache fill/memory pressure, official experimental-tool activation, installation timing, detailed UAC/permissions and SmartScreen behavior remain outside the new offline checks. See [RELEASE-1.1.1.md](RELEASE-1.1.1.md#verification-scope).

FK_YT's own code/documents remain MIT. Original external runtime notices and corresponding sources apply to the unchanged bundled tools. See [license.md](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/license.md), [LICENSE](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/LICENSE) and [LICENSING-1.1.1.md](LICENSING-1.1.1.md). Source access and hashes do not establish a complete independent external-cohort rebuild or independent supplier-package GPG verification. The historical final librsvg lock is unavailable; its labelled reconstruction and other limits remain documented in the source collections.
