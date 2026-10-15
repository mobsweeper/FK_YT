# FK_YT 1.0 — 배포 안내 / Distribution guide

Checked: **2026-10-16**. [한국어 사용 안내](../readme_KR.md) · [English user guide](../readme_EN.md)

설치기·포터블 제작과 로컬 검증은 완료했지만 **공개 배포는 아직 하지 않았습니다.** GitHub 앱·작업자 소스에도 clean과 같은 진단 코드 제거를 반영했습니다. 이 저장소 갱신으로 기존 EXE·ZIP 내부 문서나 바이너리를 바꾸지는 않았습니다.

The installer and portable package are built and locally verified within the scope below. **They are not publicly released.** GitHub app/worker sources now include the clean release's diagnostic-code removal. This repository update does not replace any existing EXE, ZIP, or document inside an archive.

## Publication status

GitHub API inspection on the date above confirmed:

- Repository: `mobsweeper/FK_YT`, **private**.
- Release tag: **`v1.0`**, **draft**, not a published release.
- Attached assets: `FK_YT_yt-dlp.zip`, `FK_YT_mpv-FFmpeg-part1.zip`, `FK_YT_mpv-FFmpeg-part2.zip`, and their `SHA256SUMS.txt`.
- Not attached: the installer EXE, portable ZIP, application-source ZIP, and installer-source ZIP.
- The draft source assets' GitHub digests and sizes match the local archives below. This verifies the draft uploads, **not public access**.

[Releases](https://github.com/mobsweeper/FK_YT/releases) is a project reference requiring repository access while private. No draft URL is presented as a public download. The intended page after publication is `https://github.com/mobsweeper/FK_YT/releases/tag/v1.0`; its availability to general users has not been verified. This documentation change does not publish the release or change repository visibility.

## Files and hashes

Names are exact, including the spaces in `1.0 portable.zip` and `1.0 source.zip`. SHA-256 values and sizes were recalculated from the final local files. A subsequent repackage will require new hashes.

| File | Purpose / status | Bytes |
| --- | --- | ---: |
| `FK_YT-1.0-Setup-x64.exe` | Optional installer with shortcuts and Windows uninstall integration; local final candidate | 220474101 |
| `1.0 portable.zip` | Recommended extract-and-run app; local final candidate | 295243814 |
| `1.0 source.zip` | Exact clean application-source snapshot; local | 727085 |
| `FK_YT-1.0-installer-source.zip` | Installer source, build instructions, pinned inputs/toolchain; local | 999620 |
| `FK_YT_yt-dlp.zip` | FK_YT-assembled corresponding-source collection; draft attachment | 165231636 |
| `FK_YT_mpv-FFmpeg-part1.zip` | Media source packages; draft attachment | 1608019975 |
| `FK_YT_mpv-FFmpeg-part2.zip` | Media source, patches, build controls, dependency sources; draft attachment | 118613552 |

```text
f42ddc19f0dcc2c180e0b3ffe677ab9c76d302c1402f42ca1ba5e681d8c519c7  FK_YT-1.0-Setup-x64.exe
05fd94e0901e22903db33d3bc4ee9f57bfb0509b93fb5010ef1aa6ad5df25d72  1.0 portable.zip
8bc5353985d5d874ba86dc5bb43f82b1a164fdaa9eb6393f1a70c7aa46323586  1.0 source.zip
e016e5cfe0751a90c71ee3373bfb1a97664044140e891e7e52fec951649f6805  FK_YT-1.0-installer-source.zip
29271824515a7ca05ac74d236b7a4d23e6cced9ba76f65974f7993f105791c47  FK_YT_yt-dlp.zip
b37fad3902adbd1e12fdd78e390af108c50ebb170207af07c2a7edcbfc493928  FK_YT_mpv-FFmpeg-part1.zip
7c45ffe820565ae35c4733b4093d8e0b56341e74fe09f3c56ee61faaba66d8fc  FK_YT_mpv-FFmpeg-part2.zip
```

The repository's [SHA256SUMS.txt](../SHA256SUMS.txt) currently covers **only the EXE and portable ZIP**. The draft attachment of the same name currently covers **only the three third-party source ZIPs**. They are different inventories, not conflicting hashes for the same file. Before publication, provide one clearly scoped release checksum inventory covering every published asset. Installer output also has individual `.sha256` files and `release-output-manifest.json`.

No personal-backup ZIP or user `data/` directory belongs in the public release.

## Installation, updates, and removal

The [Korean](../readme_KR.md) and [English](../readme_EN.md) guides contain the user steps. **The portable ZIP is the recommended option for straightforward setup:** the EXE installer's bundled verification script checks SHA-256 hashes file by file, which can take time, while portable users extract and run the app without those installer steps. Both contain the same app, playback features, and runtime tools; the installer adds installation, shortcut, and removal management. This is not a measured playback-performance comparison. Release-checksum verification is recommended for either download.

The implementation is:

- NSIS **3.13**, stock **x86 Unicode** engine installing an **x64** application; **MUI2**, English only, **zlib** compression. No language selector.
- Per-user install, no administrator elevation. Default **`%LOCALAPPDATA%\Programs\FK_YT`**, with HKCU registration. Start Menu shortcut is created; Desktop shortcut is optional and off by default. No startup task or service is added.
- Native x64/AMD64, Windows build **19045 or later**; x86/ARM64 rejected. This is an installer policy floor, not a measured minimum runtime requirement or a vendor-support guarantee. Built-in 64-bit Windows PowerShell is used for installer operations.
- An empty destination or a recognized installer-owned directory is required. Arbitrary nonempty folders, including portable folders, are rejected. Installation-root length is limited to 100 characters.
- The app uses **`<app folder>\data`** for its settings, encrypted login session, history, and resume data in both installed and portable layouts. Account encryption is tied to the Windows user.
- Updating the same owned installation preserves data and user-added files. Close FK_YT first. An optional normal-close request affects only that installation; it does not forcibly kill all processes with the same name.
- Default uninstall removes matching owned program files, shortcuts, and registration while keeping data and user-added files. Modified or locked files can remain. Deleting `data/` requires a separate unchecked-by-default option and an additional confirmation. Files downloaded elsewhere are not removed.
- A data-containing folder left after uninstall is not automatically recognized for reinstall. Preserve data, install into an empty folder, then restore it while the app is closed.
- .NET Runtime, Python, yt-dlp, Deno, mpv, FFmpeg/ffprobe, their required DLLs and runtime data are included. No separate SDK or system-wide runtime install is requested. Setup works offline; online YouTube/RYD features need internet.
- The installer EXE reports **`NotSigned`**. No real downloaded-file SmartScreen warning text or reputation result was established by the local installation tests.

The version table in both READMEs describes the actual clean payload. NSIS, .NET SDK **10.0.402**, and **7zr 26.04** are build tools, not extra user installation steps. The bundled .NET/Windows Desktop **Runtime is 10.0.13**.

## Verification scope

The installer was built from the pinned portable ZIP without rebuilding the app or acquiring replacement tools. All **11,345 original files** were preserved. The installer adds 11 files: a supplemental source-access notice, an installer manifest, and original NSIS notices/source excerpts. **11,356 static payload files** were checked after installation. Generated `Uninstall.exe` and installer ownership state are tracked separately.

| Check | Established result |
| --- | --- |
| Compile | NSIS build succeeded with warnings treated as errors; zero compiler warnings/errors |
| Boundary checks | 18 build-helper unit checks and 16 PowerShell transaction fixtures passed; these are separate from full installation tests |
| Test system | Windows 10 Pro 22H2 x64, **19045.6456**, existing development PC, non-elevated user |
| Install / launch | Actual installation, Start Menu entry, normal app window, app-local CoreCLR/WPF loading, normal close |
| Files | Full installed payload matched the expected hashes; an independent file check agreed |
| Update | Running-target update without close consent was refused; after closing, same-version update succeeded and retained synthetic data and an added file |
| Default uninstall | Program registration, matching program files and shortcuts removed; synthetic data and user-added file retained |
| English UI | Installer/uninstaller pages and cancellation/error/confirmation controls inspected; not a full visual-layout or multi-DPI test |
| Tools / local media | Installed tools ran; 1-second local video/audio with selected external WebVTT reached normal EOF using CPU/null outputs |

This does **not** establish a clean-PC prerequisite test, Windows 11 runtime qualification, real GPU/audio/subtitle rendering, live YouTube/account behavior, every upgrade migration, actual disk-full recovery, or power-loss recovery. No fresh installation or playback was performed merely for this documentation update. Tests used isolated synthetic data; current user sessions were not needed.

## Corresponding source and licenses

FK_YT's own code and documentation use [MIT](../LICENSE). This does not relicense third-party software. The user guides summarize the actual licenses and their installed locations. `THIRD-PARTY-NOTICES.txt` and the original texts under `notices/` and each runtime folder remain the detailed component records.

- Official upstream yt-dlp EXE **2026.08.19** is retained. Its PyInstaller combined distribution is GPLv3-or-later; the separate Python wheel **2026.8.19** is Unlicense. Python inside the EXE is **3.10.11**, not worker Python **3.13.16**. See [upstream's explanation](https://github.com/yt-dlp/yt-dlp#licensing).
- mpv **0.41.0-dev-gec38361c56-fkyt** was built by FK_YT from commit `ec38361c56b2d91e417c4c3e58184535d1a1aa2e`. Its source modification identifies the build in version metadata; retained build controls also avoid personal build paths. Original functionality was not represented as FK_YT's authorship. Patches and modification notices remain in the media source collection.
- FFmpeg/ffprobe **9.0.2**, MSYS2 UCRT64 **9.0.2-1**, use `--enable-gpl --enable-version3 --enable-shared`. The distributed mpv/media combination therefore cannot be described simply as MIT or an LGPL-only FFmpeg build. Library-specific terms remain in `notices/media/`. See [FFmpeg's licensing overview](https://ffmpeg.org/legal.html).
- Deno's component notices and MPL source archives are already inside the runtime package. NuGet, Python, yt-dlp/ejs, and media dependency notices have separate indexes; broad dependency inventories can include build/optional dependencies and are not claims that every entry is linked.
- The installer adds `notices/installer/nsis/`: original NSIS/MUI2, zlib, and actual `System`, `nsExec`, `nsDialogs` plugin notices, including the SystemCall MIT exception. This installer uses zlib, not the optional bzip2/LZMA compression modules. The umbrella upstream notice is retained in full. See [NSIS licensing](https://nsis.sourceforge.io/License). Installer-owned scripts/documents use MIT.

`FK_YT_yt-dlp.zip` and the two media ZIPs are **collections assembled by FK_YT**, not official upstream ZIPs under those names. They include sources, dependency packages, patches, notices, build instructions and origin/hash indexes. Both media archives are **independent ZIP files, not split-volume pieces**. Extract part1 and then part2 into the **same new directory**. Their duplicate top-level source README/index files were checked and are identical.

### Legacy notice mapping

Original payload notices were preserved, so they still name the prior archives. The archives were **repacked**, not just renamed; their SHA-256 values changed. The installer adds `INSTALLER-SOURCE-ACCESS.txt` to explain the mapping below without editing original copyright/license texts. For current names, use the hashes in “Files and hashes” above.

| Legacy filename in unchanged payload notices | Legacy SHA-256 | Current asset |
| --- | --- | --- |
| `yt-dlp-2026.08.19-corresponding-source.zip` | `79b63a0324dbc4202ef1cf90cd46372f29d414d0c335669eb7a77e7398b380f1` | `FK_YT_yt-dlp.zip` |
| `FK_YT-20261016-media-corresponding-source-1.zip` | `d5efb6f1a41da8bcc9c2c8b7248d8c5a0aaf62ab2fb016468d52015fab882560` | `FK_YT_mpv-FFmpeg-part1.zip` |
| `FK_YT-20261016-media-corresponding-source-2.zip` | `8cd57af6917ff2582a0eae3a3fa778ab790e0580f176afe32f4350146709d478` | `FK_YT_mpv-FFmpeg-part2.zip` |

The current installer supplement still correctly describes a **private draft**. The portable ZIP has the legacy notices without that installer supplement. Neither should be described as already containing this updated repository guide.

### Getting the sources after publication

These commands use the real intended tag and exact filenames. **They are conditional instructions, not proof that a public download exists today.** Before running them, the published release and your access must be confirmed. Source access must accompany access to the corresponding binaries without an additional source-access fee.

```powershell
gh release view v1.0 --repo mobsweeper/FK_YT
gh release download v1.0 --repo mobsweeper/FK_YT --pattern 'FK_YT_yt-dlp.zip' --pattern 'FK_YT_mpv-FFmpeg-part1.zip' --pattern 'FK_YT_mpv-FFmpeg-part2.zip' --pattern 'SHA256SUMS.txt' --dir .\FK_YT-sources
# Application and installer sources, once attached:
gh release download v1.0 --repo mobsweeper/FK_YT --pattern '1.0 source.zip' --pattern 'FK_YT-1.0-installer-source.zip' --dir .\FK_YT-sources
```

### Remaining source/reproducibility limits

Provided source archives, notices and hashes were checked; this is not a legal certification or a claim of complete byte-identical reproduction. In particular:

- The complete tool/runtime cohort and official yt-dlp EXE have not been independently rebuilt byte-for-byte.
- The supplier's final historical librsvg Cargo.lock was not captured. The supplied reconstruction and conservative source superset are labelled as such, not presented as the exact historical lock. Independent supplier-package GPG verification has not been established.
- The Rust coverage report records eight conservative entries without independently recovered standalone legal texts; declared license expressions and exact source archives are retained. Resolving that evidence gap remains separate from copying notice files.
- Some supplementary origin links in `media-source/rust/RUST-SOURCE-COVERAGE.md` inside media part2 were damaged during metadata normalization. Correcting them requires a new source ZIP and updated hashes. Exact source archives and origin/hash indexes are separate retained records.
- A few preserved legacy notices refer to build evidence not included in the public source collection. Public build controls provide the current guidance; those historical references are not promises of additional downloadable files.

## Source and build boundaries

**GitHub app/worker code is aligned with the clean application source in `1.0 source.zip`.** The diagnostic removal covers demo/smoke dispatch, fixture helpers and diagnostic process-ID reporting, plus the worker's ping/pong command and its two affected tests. Normal error handling, Job Object ownership and process cleanup remain. Newer repository introductions, screenshots and documentation are preserved.

The four removed diagnostic sources are `MainWindow.FeatureVerification.cs`, `MainWindow.PlaybackVerification.cs`, `MainWindow.Verification.cs`, and `Playback/PlaybackSmoke.cs`. Developer unit tests remain separate from the application. The repository is still not a byte-for-byte copy of the entire source ZIP: `scripts/Publish.ps1` retains its repository-specific document-copy handling, and the source ZIP includes release materials absent from this checkout. These differences do not justify replacing the final runtime with historical Bootstrap downloads.

| Operation | Actual source/tool | What it establishes |
| --- | --- | --- |
| Prepare SDK / compile repository | Existing `scripts/Bootstrap.ps1 -Component Dotnet`, then `scripts/Build.ps1` | Builds the aligned app source; does not recreate the full final runtime package |
| Prepare historical runtime | Existing `scripts/Bootstrap.ps1` and `scripts/Bootstrap-Ffmpeg.ps1` | Acquires locked historical supplier binaries, not the final licensed media cohort |
| Local development package | Existing `scripts/Publish.ps1` | Self-contained `win-x64` folder; fresh direct child of `dist/` required. Does not publish online or produce NSIS |
| Clean app rebuild | `1.0 source.zip` → `release-1.0/BUILDING.txt` | Locked restore/publish of clean app; use a fresh matching portable extraction for runtime/notices |
| Exact media build controls | Both media source ZIPs → supplied build instructions / `REBUILD-MPV.ps1` | Separate media build inputs and instructions; not a full-cohort reproduction claim |
| Installer build | `FK_YT-1.0-installer-source.zip` → `docs/BUILDING.md`, `build_installer.py`, `src/FK_YT.nsi` | Packages the pinned portable unchanged; no Bootstrap or app rebuild |

The SDK is **10.0.402**. The repository's `dependencies.lock.json` still identifies historical shinchiro mpv and Gyan FFmpeg inputs, while final binaries use the controlled mpv/MSYS2 builds above. The final source archive itself labels that acquisition lock historical. Do not weaken its hash checks to substitute final binaries into the historical Publish workflow.

The installer source ZIP is separate from this Git repository. It requires Python 3.10+, the exact official NSIS 3.13 binary/source archives in its toolchain lock, and all pinned inputs. From an extracted installer-source folder with those toolchain files prepared, its supported explicit-path command is:

```powershell
python build_installer.py --portable 'inputs/1.0 portable.zip' --app-source 'inputs/1.0 source.zip' --release-sources 'inputs/corresponding-source' --stage-dir 'stage' --output-dir 'output' --compile
```

Those `inputs/` files and the toolchain are prerequisites, not files already present in this Git checkout or the installer-source ZIP. The helper does not download them. Use the archive's guide for their pinned locations, and fresh staging/output for changed inputs. Compiling does not upload or publish a release.

After diagnostic removal, all 68 shared `src/` and `worker/` files match the clean source ZIP byte-for-byte. A Release build using SDK 10.0.402, locked restore and the existing local NuGet cache passed with zero warnings/errors. All 238 offline Python tests passed. No application launch, online account request, or new media playback test was performed. There is no C# test project in the current repository; `Build.ps1 -Test` must not be used as a working instruction here. The historical Publish script also does not copy the language guides/assets as a complete documentation bundle; its output is not the curated final user distribution.

## Work still required before a public release

공개 전에는 아래 실제 작업이 남아 있습니다. **이번 문서 커밋으로 EXE·ZIP이 바뀐 것은 아닙니다.**

1. Include the clean source ZIP with the final release and keep the documented packaging/runtime distinctions intact. Diagnostic-code removal is now synchronized in GitHub app/worker code; this does not synchronize the historical media acquisition workflow.
2. Correct broken supplementary source links and unresolved source/notice references without altering original legal texts. If source ZIP bytes change, regenerate their hashes and all dependent mappings/locks. Preserve and explicitly qualify remaining reconstruction/reproducibility limits.
3. Prepare the real binary-and-source publication location. Update additive source-access guidance, user guides and input locks to that location; preserve original notices. Add the current source mapping to the portable documentation as well. These are **archive changes requiring repackaging**, not changes made by this commit.
4. Rebuild the installer from the resulting verified portable/input set in a fresh stage; repackage installer source, regenerate output manifests and checksums, and repeat checks appropriate to the changed inputs. Existing hashes in this guide identify the current candidates only.
5. Upload the matched installer, portable, application source, installer source, three third-party source assets and a clearly scoped checksum inventory. The current draft's four source-related assets alone are insufficient as a user download set. No personal backups or user data.
6. Repository visibility and release publication require a separate authorized action. When published, verify unauthenticated access to both binaries and their matching sources on `v1.0`; then update the dated status in all READMEs. A successful authenticated draft inspection does not satisfy this check.
7. Clean Windows 11 standard-user testing without a system SDK/.NET remains unperformed. Complete it or explicitly retain the limitation; do not turn the existing Windows 10 development-PC test into clean-machine proof. Keep signing/SmartScreen and untested runtime/power-loss limits separate as well.

No repository visibility, release publication, installed application, user data, existing EXE, or ZIP is changed by this source/documentation update.
