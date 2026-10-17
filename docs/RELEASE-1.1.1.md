# FK_YT 1.1.1 release

FK_YT 1.1.1 makes sending watch history to YouTube an explicit choice and preserves accepted history work in a durable local queue. The portable and installed variants share application version `1.1.1`, one application DLL and one common application source. The installed-mode marker selects the data location.

<!-- PUBLICATION-STATUS-BEGIN -->
Files for this release belong at [FK_YT v1.1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1.1). Check that page for current public availability and its `SHA256SUMS.txt` for final download hashes. Preparing or packaging these documents does not itself publish the files.
<!-- PUBLICATION-STATUS-END -->

## Changes from 1.1

- **YouTube history sending defaults to off.** An older setting without the new preference version is turned off once because an old `true` value may have come from the previous default. A notice explains the migration. Enable **Send watch history to my YouTube account** in Settings if wanted. A subsequently saved explicit choice persists across restarts and account changes.
- Normal-mode local watch history and resume positions remain available while YouTube history sending is off. Enabling sending does not upload old local history. Only eligible playback observed after opting in can create new queue records. Incognito blocks sending and local history/resume saving without changing the saved sending preference.
- Accepted work is stored in a **SQLite outbox**, scoped to its account and channel, and sent sequentially after a durable claim. Pending work survives a restart. An interrupted or ambiguous attempted request is kept as **Unconfirmed** and is not automatically resent. **Sent** describes the request acknowledgement; account-history confirmation remains separate.
- Turning sending off, changing account/channel, or entering incognito holds unsent work. Turning sending on or returning to the channel does not automatically release held records. Settings shows Pending, Held, Unconfirmed and Failed counts. After checking the selected account/channel and the connection, use **Resume held records** to release that channel's held work. It does not replay Unconfirmed or Failed attempts.
- Session, bot-check or request-limit errors hold further work. Two consecutive ambiguous results also pause the drain. Resolve the issue before explicitly resuming; a storage problem stops sending and preserves the queue for recovery.

Existing playback, live-cache and experimental-update behavior is described in [FEATURES-1.1.1.md](FEATURES-1.1.1.md). These changes do not guarantee that YouTube accepts an unofficial account operation or updates its history.

## Release files

| File | Contents / use |
| --- | --- |
| `FK_YT-1.1.1-portable.zip` | Ready-to-run app; extract the whole ZIP into a writable new folder |
| `FK_YT-1.1.1-Setup-x64.exe` | Administrator installer for Windows x64; English NSIS/MUI2 UI |
| `FK_YT-1.1.1-source.zip` | Common product source, locked inputs and public build/packaging instructions |
| `FK_YT-1.1.1-installer-source.zip` | Matching installer implementation, build controls, input locks and NSIS notices |
| `FK_YT_yt-dlp.zip` | Unchanged corresponding-source collection for the bundled yt-dlp distribution |
| `FK_YT_mpv-FFmpeg-part1.zip` | First independent ZIP of the unchanged media corresponding-source collection |
| `FK_YT_mpv-FFmpeg-part2.zip` | Second independent ZIP; extract after part1 into the same new folder |
| `SHA256SUMS.txt` | Final release-asset SHA-256 list; the list does not hash itself |
| `SOURCE-ACCESS.txt` | Current binary/source mapping and direct source-access information |
| `LICENSES-SUPPLEMENT.txt` | Unchanged supplemental notices and remaining source/license limits |

Only the portable ZIP or installer is needed to run the app. GitHub's **Code → Download ZIP** supplies source. Public release downloads do not require signing in. The installer has no publisher code signature; a matching SHA-256 establishes byte agreement, not publisher authentication.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1.1-portable.zip'
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1.1-Setup-x64.exe'
```

## Installation and data

The installer accepts Windows AMD64 build 19045 or later and installs into a supported folder directly beneath native 64-bit Program Files. This acceptance rule is separate from vendor OS support. Installation/removal requires administrator permission. The app runs as the normal user, and installation needs no additional runtime download.

| Variant | Start | Data |
| --- | --- | --- |
| Portable | Extract all files, then run `FK_YT/FK_YT.exe` | `data/` beside the app |
| Installed | Open **FK_YT (System)** from Start Menu | Each Windows user's `%LOCALAPPDATA%\FK_YT\data` |

Preserve `FK_YT.install-mode` in the installed payload. The installer does not automatically import portable or older per-user data. Before moving data, close the app and retain the original folder. Copy only to an empty destination as the same Windows user; do not merge SQLite databases or overwrite an active data folder. A PC or Windows-user change can require reconnecting sign-in information. Keep the complete database and settings together when preserving queue state; do not edit queue rows to force a retry.

Uninstall preserves all users' settings, sign-in data, local history, resume positions, queue records and downloads. There is no data-deletion checkbox or search of other user profiles. Added, modified or locked program files may remain. Portable removal deletes the files you remove, including app data if its `data/` directory is deleted.

The installer retains the 1.1 staging verification design: one full fixed-payload SHA-256 pass in protected staging, with path/type/length, ownership, rollback and removal checks. There is no added post-install full-file SHA pass. Installation timing has not been measured; no speedup is claimed.

## Source and runtime boundaries

The common app-source ZIP excludes FK_YT test projects, test scripts, internal work records, build outputs, personal data and extracted tools. It retains production sources, locks, public build recipes and documentation. Installer source is separate and consumes the exact portable payload; it does not require a different application DLL.

The bundled external runtimes remain the 1.1 baseline: .NET/Windows Desktop `10.0.13`, Python `3.13.16`, standalone yt-dlp `2026.08.19`, Python yt-dlp `2026.8.19`, EJS `0.8.0`, Deno `2.9.7`, pinned mpv `0.41.0-dev-gec38361c56-fkyt`, and FFmpeg/ffprobe `9.0.2`. Their original copyright/license texts and notices are preserved. The three external source ZIPs retain their previous bytes and hashes. A later experimental tool download needs the notices and sources for its own version.

See [BUILD-1.1.1.md](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/docs/BUILD-1.1.1.md), [LICENSING-1.1.1.md](LICENSING-1.1.1.md), [SOURCE-ACCESS-1.1.1.txt](SOURCE-ACCESS-1.1.1.txt), the unchanged [LICENSES-SUPPLEMENT-1.1.txt](LICENSES-SUPPLEMENT-1.1.txt), [license.md](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/license.md) and [LICENSE](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/LICENSE). Historical 1.0/1.1 documents and payload notices describe those releases.

## Verification scope

The history implementation underwent earlier isolated offline checks: 50 C# core groups, 17 UI groups and 262 Python checks passed. The existing updater checks recorded 249 assertions; a native mpv cache probe used local media. These are different check sets and are not a combined test total. Tests and internal verification records are excluded from the public product source.

The normal `1.1.1` app was published offline with the pinned SDK and locked cached packages, with zero compiler warnings. A publish or package integrity check does not prove real YouTube/account operation or an actual installer run. Check the final release inventory for packaged bytes.

No new manual Windows installation/removal or real YouTube test is claimed for 1.1.1. The maintainer's 2026-10-17 confirmation of Windows 10/11 installation, installed-app launch/playback, installer updates and removal belongs to **1.1**. It does not verify this release's history changes. Fresh authenticated history submission, interrupted-network account outcomes, long playback/live runs, full live-cache pressure, official experimental update activation, installation timing, detailed UAC/permissions and SmartScreen behavior remain outside the new offline checks.

Public source access and matching hashes are distribution checks. A complete independent, bit-identical rebuild of the external tool cohort and independent supplier-package GPG verification are not established; the supplier's historical final librsvg lock remains unavailable, as documented in the source collections.
