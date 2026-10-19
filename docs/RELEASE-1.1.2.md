# FK_YT 1.1.2: downloads, installation and source

Release files for this version belong at [FK_YT v1.1.2](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1.2). Check that page for current availability and its `SHA256SUMS.txt` for final download hashes. The portable and installed variants use application version `1.1.2`, the same application DLL and common application source.

## Release files

| File | Purpose |
| --- | --- |
| `FK_YT-1.1.2-portable.zip` | Ready-to-run app; extract the whole ZIP into a new writable folder |
| `FK_YT-1.1.2-Setup-x64.exe` | Administrator installer for Windows x64; English NSIS/MUI2 pages |
| `FK_YT-1.1.2-source.zip` | Matching product source, locked inputs and build/packaging instructions |
| `FK_YT-1.1.2-installer-source.zip` | Matching installer implementation, input locks and NSIS notices |
| `FK_YT_yt-dlp.zip` | Corresponding source for the bundled yt-dlp executable |
| `FK_YT_mpv-FFmpeg-part1.zip` | First independent ZIP of the media corresponding-source collection |
| `FK_YT_mpv-FFmpeg-part2.zip` | Second independent ZIP; extract after part1 into the same new folder |
| `SHA256SUMS.txt` | Final release-asset SHA-256 list; it does not hash itself |
| `SOURCE-ACCESS.txt` | Binary/source mapping and direct source-access information |
| `LICENSES-SUPPLEMENT.txt` | Supplemental notices and retained source/license limits |

Only the portable ZIP or installer is needed to run the app. Public release downloads do not require signing in. GitHub's **Code → Download ZIP** supplies source rather than a ready-to-run app. The installer has no publisher code signature; a matching SHA-256 establishes matching bytes, not publisher authentication.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1.2-portable.zip'
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1.2-Setup-x64.exe'
```

## Installation and data

The installer accepts Windows AMD64 build 19045 or later and uses a supported folder directly beneath native 64-bit Program Files. This acceptance rule is separate from vendor OS support. Installation and removal require administrator permission; the app runs as the normal user. Required runtimes are included, so installation needs no additional runtime download.

| Variant | Start | Data |
| --- | --- | --- |
| Portable | Extract every file, then run `FK_YT/FK_YT.exe` | `data/` beside the app |
| Installed | Open **FK_YT (System)** from Start Menu after Setup closes | Each Windows user's `%LOCALAPPDATA%\FK_YT\data` |

Keep `runtime/`, `worker/`, DLLs and legal notices together. Preserve `FK_YT.install-mode` in the installed payload. Setup does not automatically import portable or earlier per-user data, and does not launch the app with elevated permissions.

Close the app before backing up or moving data. Preserve the original database and settings together, and copy only to an empty destination as the same Windows user. Do not merge SQLite databases, edit history-queue rows to force a retry or overwrite an active data folder. A different PC or Windows user can require reconnecting sign-in information.

Uninstall preserves all users' settings, sign-in information, local history, resume positions, queue records and downloads. There is no data-deletion checkbox or search of other user profiles. Added, modified or locked program files may remain. Deleting the portable `data/` folder deletes that copy's app data.

The installer uses the existing protected-staging verification design: one full fixed-payload SHA-256 pass, with path/type/length, ownership, rollback and removal checks. Installation time has not been measured.

## Usage and source

See [usage and limits](FEATURES-1.1.2.md) and the [English](guides/User-guide-1.1.2.txt), [Korean](guides/사용설명서-1.1.2.txt) or [Japanese](guides/ご利用ガイド-1.1.2.txt) guide for audio mode, live playback, history settings and tool updates.

The common source ZIP retains production sources, required build/packaging controls, locks and public documents. FK_YT tests, build outputs and personal data are excluded. External source collections retain their upstream source, tests, workflows, patches, interface definitions, notices and recipes.

See [BUILD-1.1.2.md](BUILD-1.1.2.md), [LICENSING-1.1.2.md](LICENSING-1.1.2.md), [SOURCE-ACCESS-1.1.2.txt](SOURCE-ACCESS-1.1.2.txt) and the unchanged [supplemental notices](LICENSES-SUPPLEMENT-1.1.txt). FK_YT-owned code and documents use MIT; original external terms remain applicable. Later manually downloaded tool versions require their own notices and corresponding sources before redistribution.

## Verification limits

The release body records checks actually performed on the final files. A build, local-media fixture or package-integrity check does not establish real YouTube playback, audible language, every account/video combination, long-duration autoplay or full live-cache memory pressure. The cause of an earlier intermittent immediate exit remains unconfirmed.

No new manual 1.1.2 UAC installation, update or removal test is claimed here. Compiling an installer does not establish those results. The maintainer's Windows 10/11 installation and removal confirmation for 1.1 remains historical evidence for that version.

Source access and matching hashes do not establish a complete independent rebuild of the external tools or independent supplier-package GPG verification. The supplier's historical final librsvg lock remains unavailable; the source collections retain the labelled reconstruction and other source limits.
