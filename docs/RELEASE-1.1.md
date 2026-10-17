# FK_YT 1.1 release

FK_YT 1.1 uses application version `1.1.0`. It adds live playback controls, manual experimental tool updates, and playback/UI fixes described in [FEATURES-1.1.md](FEATURES-1.1.md). The portable and installed variants use one application DLL and one common application source. Their data locations differ through the installed-mode marker.

<!-- PUBLICATION-STATUS-BEGIN -->
Release files and availability: [FK_YT v1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1). Verify downloads with [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt); the runtime and installation verification limits below apply independently of publication.
<!-- PUBLICATION-STATUS-END -->

## Files and hashes

Use the release asset **SHA256SUMS.txt**, available from the [release download](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt), for the final file hashes. This page does not duplicate source-archive hashes inside their own inputs. Verify the filename and bytes you downloaded, rather than a preview file with a similar name.

| Release file | Contents / use |
| --- | --- |
| `FK_YT-1.1-portable.zip` | Recommended ready-to-run app; extract the whole ZIP into a writable folder |
| `FK_YT-1.1-Setup-x64.exe` | Administrator installer for Windows x64; English NSIS/MUI2 UI, Program Files, shared shortcuts and removal registration |
| `FK_YT-1.1-source.zip` | Common product source for the portable and installed app; no test projects or internal review/verification records |
| `FK_YT-1.1-installer-source.zip` | Matching NSIS/PowerShell/Python installer sources, input locks, legal notices and build instructions |
| `FK_YT_yt-dlp.zip` | FK_YT-assembled corresponding-source collection for the unchanged bundled yt-dlp distribution |
| `FK_YT_mpv-FFmpeg-part1.zip` | First independent ZIP in the unchanged bundled media corresponding-source collection |
| `FK_YT_mpv-FFmpeg-part2.zip` | Second independent ZIP; extract after part1 into the same folder |
| `SHA256SUMS.txt` | Final release-asset SHA-256 inventory; the checksum list does not hash itself |
| `SOURCE-ACCESS.txt` | Binary/source archive mapping and source-access information |
| `LICENSES-SUPPLEMENT.txt` | Supplemental distribution notices and remaining source/license limitations |

Only the portable ZIP or installer is needed to run FK_YT. GitHub's **Code → Download ZIP** downloads source, not the ready-to-run application. Public GitHub release downloads do not require signing in. The installer is supplied without a publisher code signature; a matching SHA-256 proves byte agreement, not publisher authentication.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1-portable.zip'
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1-Setup-x64.exe'
```

## Installation and data

The installer accepts Windows AMD64 build 19045 or later and installs into a supported folder directly beneath native 64-bit Program Files. This acceptance rule is separate from vendor OS support. It requires administrator permission for installation/removal; FK_YT itself remains a normal-user application. No additional runtime download is needed during installation.

| Variant | Application | Data |
| --- | --- | --- |
| Portable | Run `FK_YT/FK_YT.exe` after extracting the complete archive | `data/` beside the app |
| Installed | Open **FK_YT (System)** from Start Menu | Each Windows user's `%LOCALAPPDATA%\FK_YT\data` |

The installed payload adds `FK_YT.install-mode`; preserve that marker. The installer does not automatically import portable or old per-user data. Close the app, preserve the original folder, and copy data only into an empty destination as the same Windows user. Do not merge SQLite databases. Saved sign-in information can require reconnection after a PC or Windows-user change.

Uninstall preserves all users' settings, sign-in data, watch history, resume positions and downloads. There is no user-data deletion checkbox and no search of other user profiles. Added, modified or locked program-folder files may remain. The maintainer confirmed Windows 10/11 installation, installed-app launch/playback, installer updates and removal on 2026-10-17; see the verification scope below.

## Installer changes

Each fixed payload file now receives one full SHA-256 pass in the administrator-protected staging copy, replacing duplicate full hashing of the source and protected copy. Source preflight still checks path, file type, existence and length. Repeated directory ACL work is reused only within each plan/apply phase; leaf-path and reparse checks remain.

There is no full-file SHA pass after installation; that was already true before this change. Standalone validation, generated-uninstaller hashes, existing-file ownership, rollback and removal protections are retained. NSIS zlib compression and CRC checks are retained. **Actual installation time has not been measured, so no time saving or percentage improvement is claimed.** The portable package remains the recommended simple setup.

## Source and build boundaries

- **Common app source:** `FK_YT-1.1-source.zip` contains the product C# and Python sources, dependency locks, build/packaging recipes, public documentation and blurred screenshots. It excludes tests, local build outputs, internal review reports, personal data and extracted tools. Unlike 1.0, a separate machine-app source ZIP is unnecessary: the app DLL is shared.
- **Installer source:** `FK_YT-1.1-installer-source.zip` contains the installer implementation and pinned assembly inputs. Follow its `docs/BUILDING.md` and input locks. It adds installed-mode metadata and installer notices to the exact portable payload without rebuilding the app DLL.
- **External corresponding sources:** the three `FK_YT_*.zip` collections describe the unchanged bundled external binaries, not arbitrary future versions downloaded by the experimental updater. Both media ZIPs are independent archives, not split volumes.
- **Build tools versus release runtime:** SDK bootstrap and ordinary development Publish differ from final licensed-runtime assembly. The historical dependency lock's supplier mpv/FFmpeg downloads do not reproduce the final bundled media set. See [BUILD-1.1.md](BUILD-1.1.md).

FK_YT's own code and documents use MIT. External components retain their licenses. See [license.md](../license.md), [LICENSING-1.1.md](LICENSING-1.1.md), [SOURCE-ACCESS.txt](SOURCE-ACCESS-1.1.txt) and [LICENSES-SUPPLEMENT.txt](LICENSES-SUPPLEMENT-1.1.txt). Retained 1.0 documents describe that historical release and are not the 1.1 build recipe.

## Verification scope

The following results belong to the **preview6 implementation and installer preparation that preceded 1.1**. They are not a claim that the entire suite was rerun after the final version/documentation change.

| Prior check | Recorded result / boundary |
| --- | --- |
| Offline application regression | 61/61 groups passed; forced rebuild had zero warnings/errors |
| Offline tool-update checks | 249 assertions passed, including focused rollback and manifest-structure cases; these counts overlap and are not added together |
| Exact normal preview6 DLL | Lifecycle 1/1 group and new-features 3/3 groups passed in an existing WPF harness; this was not apphost launch or real media playback |
| Preview6 payload/source archives | CRC, SHA-256, manifest and allowed-change comparisons passed |
| Installer PowerShell fixtures | 29/29 passed in an isolated environment; not a real Program Files/HKLM installation |
| Installer packaging fixtures | 27/27 passed with synthetic inputs |
| Preview6 NSIS compilation | NSIS 3.13 compilation passed with warnings treated as errors; payload and source identity were checked |

The normal 1.1.0 application was compiled using the preserved .NET 10.0.13 runtime cache. Product method IL and resources matched the clean normal preview6 implementation, with no playback-diagnostic types or PDBs in the output. This confirms the version promotion and normal build scope; it is not a rerun of the earlier whole suite. Final release checksums identify the packaged assets independently of the real-world items below.

**Maintainer-confirmed after publication (2026-10-17):** installation, installed-app launch/playback, installer updates and removal on Windows 10 and Windows 11. This is the maintainer's reported verification, separate from the agent's prior offline build/fixture results. Additional OS edition/build, clean-system/standard-user conditions and account/live test details were not supplied.

**Not established by that confirmation:** a new two-hour autoplay/live run; full live-cache fill/memory pressure; official experimental tool download/activation; installation timing; a detailed UAC/permissions matrix; a clean Windows 11 standard-user environment; and SmartScreen behavior after public download. Basic playback confirmation is not a complete authenticated/live test matrix.

Public source access and matching checksums are distribution checks. Successful build/packaging does not establish account compatibility, uninterrupted long playback, every upstream ad variant, full server DVR, or a complete bit-for-bit rebuild of all external binaries. Exact historical supplier librsvg lock recovery and the external-source limits remain documented in the licensing record.

## Post-release documentation and source changes (2026-10-17)

The repository documentation incorporates the maintainer's Windows 10/11 confirmation above. The published v1.1 EXE, portable ZIP, application/installer source ZIPs, source supplements, checksums and tag remain unchanged; documents inside those frozen archives retain their publication-time verification statements.

On the development branch, the manual tool updater now derives its User-Agent version from the application assembly instead of the fixed `FK_YT/1.0` string. The published v1.1 binary still sends the original string. This request-identification correction does not change update URLs, payloads or verification rules. It reaches executable users only in a future rebuilt package; changing README or source alone does not patch an installed app.
