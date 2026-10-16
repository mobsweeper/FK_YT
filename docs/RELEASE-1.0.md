# FK_YT 1.0 — 배포 안내 / Distribution guide

Checked: **2026-10-16**. [한국어 사용 안내](../readme_KR.md) · [English user guide](../readme_EN.md)

포터블과 Program Files 관리자 설치기를 **공개 배포했습니다.** 저장소·릴리스와 대응 소스·고지를 포함한 자산 12개의 로그인 없는 접근과 서버 크기·해시 일치를 확인했습니다. 최종 설치기의 실제 설치·파일·등록·권한 검사는 확인했지만, 새 설치본 실행·재생·업데이트·실제 제거는 미검증입니다. 포터블 앱 소스와 설치형 앱 소스는 다릅니다.

The portable package and Program Files administrator installer are publicly released. The runnable EXE and portable ZIP are unchanged; source archives were separately repacked as described below. Anonymous access and matching server sizes/hashes were verified for all 12 selected assets, including corresponding sources and notices. The final installer's actual installation, files, registration, and permissions were checked locally; installed-app launch/playback, update, and actual removal of this revision remain unverified.

## Publication status

<!-- PUBLICATION-STATUS-BEGIN -->
**Published October 16, 2026 at 09:28:40 UTC (18:28:40 KST).** Repository `mobsweeper/FK_YT` is public and [release `v1.0`](https://github.com/mobsweeper/FK_YT/releases/tag/v1.0) is published. Repository/release API access and every asset download URL were verified without an Authorization header, cookies, GitHub CLI credentials, or browser session.

The release contains nine binary/ZIP assets plus `SOURCE-ACCESS.txt`, `LICENSES-SUPPLEMENT.txt`, and `SHA256SUMS.txt`: **12 assets**. All 12 URLs returned HTTP 206 to a first-byte Range GET, and anonymous API sizes/digests matched the selected local files and repository checksum inventory. The three notice/checksum text assets were also downloaded in full and their hashes checked. This is not a claim that every large ZIP/EXE was downloaded in full during the anonymous check. See the [licensing record](LICENSING-1.0.md), [source-access map](SOURCE-ACCESS.txt), and [supplemental notices](LICENSES-SUPPLEMENT.txt).
<!-- PUBLICATION-STATUS-END -->

## Files and hashes

These are the release asset names. GitHub uses dotted names **`1.0.portable.zip`** and **`1.0.source.zip`**. The runnable portable remains unchanged; application-source ZIPs were repacked for publication documentation and third-party source collections were repaired. Their new hashes therefore differ from archived pre-publication references. Internal build guides may retain older local input names. A later repackage requires new hashes.

| File | Purpose / status | Bytes |
| --- | --- | ---: |
| `FK_YT-1.0-Setup-x64.exe` | Optional administrator installer; Program Files, shared shortcuts, machine removal registration | 220931600 |
| `1.0.portable.zip` | Recommended extract-and-run app; unchanged clean portable | 295243814 |
| `1.0.source.zip` | Matching clean **portable app** source; publication documents updated, code/build inputs preserved | 564599 |
| `FK_YT-1.0-machine-app-source.zip` | Matching **installed app** source and offline deterministic recipe; publication documents updated | 581075 |
| `FK_YT-1.0-installer-source.zip` | Installer/helper source, updated build guide/input pins/source access, original executable source preserved | 1018688 |
| `machine-public-payload.zip` | Exact assembled build input for recreating this installer; not the user portable download | 295477208 |
| `FK_YT_yt-dlp.zip` | FK_YT-assembled corresponding-source collection; publication metadata repaired | 165234763 |
| `FK_YT_mpv-FFmpeg-part1.zip` | Media source packages; publication metadata repaired | 1608074669 |
| `FK_YT_mpv-FFmpeg-part2.zip` | Media sources/build controls; repaired metadata and added legal notices | 118680866 |

```text
4e18c26503cab284c10843a4c076d4bcbdb79ab3a82e29cf95feab48bc905a4a  FK_YT-1.0-Setup-x64.exe
05fd94e0901e22903db33d3bc4ee9f57bfb0509b93fb5010ef1aa6ad5df25d72  1.0.portable.zip
862379cad447100962d2161d87205327b5b4f500d5bc4e5fe107b188a7564c35  1.0.source.zip
17df8c037cf2906c997d5753b88a521320649b8c8a8e09a7ae26e13b104f83e9  FK_YT-1.0-machine-app-source.zip
7df084b882b3e9a158a1fb4ac8c04607f89bb65c1f0c50f107a2b45e7254bae9  FK_YT-1.0-installer-source.zip
a581d63f619c31643cbb5473ab3d41732d8966bd4485bd5af2847040819a428e  machine-public-payload.zip
e22f4df8dcb40205dc9b2b2ade373a6d6f3d3e5adab786b36f628999504c8ab2  FK_YT_yt-dlp.zip
b53ab130c5f1102874f7edbdfc49b0baa30fa1eb1251ad476e9e3624512c4129  FK_YT_mpv-FFmpeg-part1.zip
5f499d855e975df0f8c72e0e2a3d3949b13f8f121a9c241273ca2b1c0337cc53  FK_YT_mpv-FFmpeg-part2.zip
```

The block above identifies the selected binary/ZIP assets. Use the final release's **`SHA256SUMS.txt`**, with the dotted GitHub asset names, for the complete inventory including supplemental notices. The repository's [checksum file](../SHA256SUMS.txt) must be synchronized with that inventory; older inventories do not cover the complete release. The installer EXE is unsigned (`NotSigned`); hashes do not replace code signing.

No personal-backup ZIP or user `data/` directory belongs in the public release.

## Installation, updates, and removal

The [Korean](../readme_KR.md) and [English](../readme_EN.md) guides contain the user steps. **`1.0.portable.zip` is recommended for straightforward setup:** extract the whole ZIP into a writable folder and run `FK_YT.exe`. The administrator installer checks thousands of files and can take several minutes; its details view reports phases and file counts. Both provide the same viewing/playback features and external runtime tools. Their application DLLs differ to select the appropriate data location. This is not a measured playback-performance comparison.

The implementation is:

- NSIS **3.13**, stock **x86 Unicode** engine installing an **x64** application; **MUI2**, English only, **zlib** compression. No language selector.
- Installation and removal require administrator permission. Default **native 64-bit `%ProgramFiles%\FK_YT`**, normally `C:\Program Files\FK_YT`, with 64-bit **HKLM** registration. The app remains **`asInvoker`** and runs normally from Start Menu after Setup closes; elevated Setup does not launch it.
- Shared Start Menu entries are **`FK_YT (System)`** and **`Uninstall FK_YT (System)`**. A shared Desktop shortcut is optional and off by default. Program files/state/shortcuts use protected Administrators-owned permissions with Users read/execute access. No startup task or service is added.
- Native x64/AMD64, Windows build **19045 or later**; x86/ARM64 rejected. This is an installer policy floor, not a measured minimum runtime requirement or a vendor-support guarantee. Built-in 64-bit Windows PowerShell is used for installer operations.
- The directory page permits only a direct app-folder child of native 64-bit Program Files, empty or already owned by the machine installer, with a root length of at most 100 characters. Portable and legacy per-user installs are not migration targets.
- Installed mode uses **the running Windows user's `%LOCALAPPDATA%\FK_YT\data`** for settings, encrypted sign-in, history, and resume positions. The exact `FK_YT.install-mode` marker (`machine-v1` plus LF) selects that mode. Portable mode without that marker keeps **`<app folder>\data`**. DPAPI sign-in encryption remains tied to the Windows user.
- Machine updates are designed to preserve profile data and user-added files. Close FK_YT first; optional normal close is limited to the selected installation and does not force-kill processes. Actual updates of this final revision have not been verified.
- Machine removal is designed to remove unchanged owned program files, common shortcuts, and HKLM registration. It **always preserves every user's profile data and downloads**, does not enumerate user profiles, and rejects production `RemoveData`. There is no data-deletion checkbox. Added/modified/locked program-folder files can remain. Actual removal of this final revision has not been verified.
- Existing per-user installs, shortcuts, and data are not moved, replaced, or removed automatically. For manual migration, close both apps, retain the source, and copy as the original Windows user into an empty destination; do not merge SQLite databases. Remove your own profile data separately only if you deliberately want to erase it.
- .NET Runtime, Python, yt-dlp, Deno, mpv, FFmpeg/ffprobe, their required DLLs and runtime data are included. No separate SDK or system-wide runtime install is requested. Setup works offline; online YouTube/RYD features need internet.
- The installer EXE reports **`NotSigned`**. No real downloaded-file SmartScreen warning text or reputation result was established by the local installation tests.

The version table in both READMEs describes the actual clean payload. NSIS, .NET SDK **10.0.402**, and **7zr 26.04** are build tools, not extra user installation steps. The bundled .NET/Windows Desktop **Runtime is 10.0.13**.

## Verification scope

The original **11,345-file portable ZIP remains unchanged**. The machine payload overlays the approved rebuilt `FK_YT.dll` for data-path selection and adds the fixed mode marker and provenance/manifest records. **10,930 external runtime, worker, and original-notice files were verified byte-for-byte unchanged.** The resulting input contains 11,349 files; installer additions bring it to **11,360 static files**, with generated `Uninstall.exe` tracked separately. The installed app source is therefore not interchangeable with the portable source.

| Check | Established result |
| --- | --- |
| Compile | NSIS build succeeded with warnings treated as errors; zero compiler warnings/errors |
| Offline fixtures | 27 installer/helper transaction checks, 19 packaging checks, and 7 app data-path groups passed; fixtures are separate from real HKLM/Program Files lifecycle tests |
| App rebuild | Matching installed-app source rebuilt offline; EXE/DLL/deps/runtimeconfig hashes matched repeat builds and builds in a different source/build root |
| Test system | Windows 10 Pro 22H2 x64, **19045.6456**, existing development PC; user approved elevated installation |
| Actual machine installation | Installed to default `C:\Program Files\FK_YT`; all 11,360 static files and the generated uninstaller matched their expected records; no untracked files or program-folder data |
| Registration / permissions | Machine registration and common start/remove shortcuts checked; sampled program directories/files/state/shortcuts had protected Administrators-owned ACLs with Users read/execute |
| Installer temporary files | Non-elevated file creation in the extracted Temp directory and generic read of the helper were denied. Label-specific queries of the directory, helper, and System DLL returned no explicit integrity-label ACE (Windows treats unlabeled objects as Medium). This does not verify the uninstaller's pre-callback copy path |
| Final installed app launch / playback | **Unverified**; source/path checks and successful file installation do not establish a normal app launch or playback |
| Final machine update / actual removal | **Unverified**; previous per-user-installer lifecycle results and helper fixtures do not establish these operations for this revision |
| Portable startup | Clean portable extraction produced a normal app window and normal exit; no user account/cookies or online video requests were used |

Earlier verification snapshots were recorded **before UAC installation**. Later observations establish the installation/file/permission checks above; they do not add launch, update, or removal results. Earlier timing/lifecycle results concern a different per-user installer and are not final administrator-installer proof.

This does **not** establish clean-PC prerequisites, Windows 11 runtime qualification, another administrator's UAC credentials, real GPU/audio/subtitle rendering, live YouTube/account behavior, every upgrade migration, real disk-full recovery, power-loss recovery, or the uninstaller's initial Temp-copy protections. SmartScreen behavior after a real distribution download is also untested. This documentation work did not run the app or installer.

## Corresponding source and licenses

FK_YT's own code and documentation use [MIT](../LICENSE). This does not relicense third-party software. The user guides summarize the actual licenses and their installed locations; the [licensing record](LICENSING-1.0.md) distinguishes completed documentation/source work from remaining checks. `THIRD-PARTY-NOTICES.txt` and the original texts under `notices/` and each runtime folder remain the detailed component records.

- Official upstream yt-dlp EXE **2026.08.19** is retained. Its PyInstaller combined distribution is GPLv3-or-later; the separate Python wheel **2026.8.19** is Unlicense. Python inside the EXE is **3.10.11**, not worker Python **3.13.16**. See [upstream's explanation](https://github.com/yt-dlp/yt-dlp#licensing).
- mpv **0.41.0-dev-gec38361c56-fkyt** was built by FK_YT from commit `ec38361c56b2d91e417c4c3e58184535d1a1aa2e`. Its source modification identifies the build in version metadata; retained build controls also avoid personal build paths. Original functionality was not represented as FK_YT's authorship. Patches and modification notices remain in the media source collection.
- FFmpeg/ffprobe **9.0.2**, MSYS2 UCRT64 **9.0.2-1**, use `--enable-gpl --enable-version3 --enable-shared`. The distributed mpv/media combination therefore cannot be described simply as MIT or an LGPL-only FFmpeg build. Library-specific terms remain in `notices/media/`. See [FFmpeg's licensing overview](https://ffmpeg.org/legal.html).
- Deno's component notices and MPL source archives are already inside the runtime package. NuGet, Python, yt-dlp/ejs, and media dependency notices have separate indexes; broad dependency inventories can include build/optional dependencies and are not claims that every entry is linked.
- The installer adds `notices/installer/nsis/`: original NSIS/MUI2, zlib, and actual `System`, `nsExec`, `nsDialogs` plugin notices, including the SystemCall MIT exception. This installer uses zlib, not the optional bzip2/LZMA compression modules. The umbrella upstream notice is retained in full. See [NSIS licensing](https://nsis.sourceforge.io/License). Installer-owned scripts/documents use MIT.

`FK_YT_yt-dlp.zip` and the two media ZIPs are **collections assembled by FK_YT**, not official upstream ZIPs under those names. They include sources, dependency packages, patches, notices, build instructions and origin/hash indexes. Both media archives are **independent ZIP files, not split-volume pieces**. Extract part1 and then part2 into the **same new directory**. Their duplicate top-level source README/index files were checked and are identical.

### Legacy notice mapping

Original runtime/payload notices were preserved, so they still name earlier archives. The current source collections were **repacked**, not merely renamed; their SHA-256 values changed again during publication repair. Use the current hashes in “Files and hashes” above and the [source-access map](SOURCE-ACCESS.txt) for the complete archive mapping. The installed `INSTALLER-SOURCE-ACCESS.txt` remains its build-time snapshot rather than the current release inventory.

| Legacy filename in unchanged payload notices | Legacy SHA-256 | Current asset |
| --- | --- | --- |
| `yt-dlp-2026.08.19-corresponding-source.zip` | `79b63a0324dbc4202ef1cf90cd46372f29d414d0c335669eb7a77e7398b380f1` | `FK_YT_yt-dlp.zip` |
| `FK_YT-20261016-media-corresponding-source-1.zip` | `d5efb6f1a41da8bcc9c2c8b7248d8c5a0aaf62ab2fb016468d52015fab882560` | `FK_YT_mpv-FFmpeg-part1.zip` |
| `FK_YT-20261016-media-corresponding-source-2.zip` | `8cd57af6917ff2582a0eae3a3fa778ab790e0580f176afe32f4350146709d478` | `FK_YT_mpv-FFmpeg-part2.zip` |

Archived payload notices and input metadata can retain earlier source hashes and access-status snapshots from build time; they are not the current inventory or access status. The runnable binaries remain unchanged. The [source-access map](SOURCE-ACCESS.txt) and [supplemental notices](LICENSES-SUPPLEMENT.txt), also supplied as release assets, provide URLs, dotted filenames, hash mappings, and legal additions. Repaired source ZIPs have their own updated metadata and hashes; the supplements do not replace original copyright/license texts. Public access to the release and matching source assets has been verified as recorded above.

### Getting the published sources

These commands use the published `v1.0` tag and exact asset filenames. The matching source collections, both app-source variants, installer source, build payload, and notices are publicly accessible alongside the binaries without an additional source-access fee. You can also download them directly from the release page without signing in.

```powershell
gh release view v1.0 --repo mobsweeper/FK_YT
gh release download v1.0 --repo mobsweeper/FK_YT --pattern 'FK_YT_yt-dlp.zip' --pattern 'FK_YT_mpv-FFmpeg-part1.zip' --pattern 'FK_YT_mpv-FFmpeg-part2.zip' --pattern 'SOURCE-ACCESS.txt' --pattern 'LICENSES-SUPPLEMENT.txt' --pattern 'SHA256SUMS.txt' --dir .\FK_YT-sources
# Application/installer sources and the exact installer build payload:
gh release download v1.0 --repo mobsweeper/FK_YT --pattern '1.0.source.zip' --pattern 'FK_YT-1.0-machine-app-source.zip' --pattern 'FK_YT-1.0-installer-source.zip' --pattern 'machine-public-payload.zip' --dir .\FK_YT-sources
```

### Publication source repairs

The three third-party collections update **40 distributor metadata/helper entries**, while **3,099 retained original entries** were checked byte-for-byte identical. No distributed runtime executable or upstream source/legal text was rewritten. Six source-side notice files were added: the exact version-associated difflib 0.4.0 notice, MIT and Apache-2.0 notices for both winapi GNU architecture crates 0.4.0, and simd_helpers 0.1.0's official LICENSE-only follow-up. The latter is explicitly identified as a subsequent upstream notice, not a file claimed to have been present in the original crate. `LICENSES-SUPPLEMENT.txt` retains these notice texts and their origins.

Four source blobs—`block 0.1.6`, `malloc_buf 0.0.6`, `objc-foundation 0.1.1`, and `objc_id 0.1.1`—were intentionally excluded because they belong to a macOS-only dependency branch unused by the native Windows build, and their standalone project-specific notices were not independently recovered. The Windows-focused collection supplies **643 crate archives**. Upstream cross-platform lock files remain intact, so those files can still name the excluded crates; this is not a complete macOS source collection.

Broken distributor URL fields were repaired where evidence existed. **1,242 unrecoverable historical URL fields are explicitly `null`**, with the reason documented rather than an invented origin. Source index sizes/hashes were verified, and both media ZIPs' shared index/README/publication notes match. Application-source repacks change publication documents and metadata while preserving application code, build inputs, and original legal texts. This publication work did not rebuild or execute the application.

### Remaining source/reproducibility limits

Provided source archives, notices and hashes were checked; this is not a legal certification or a claim of complete byte-identical reproduction. In particular:

- The complete tool/runtime cohort and official yt-dlp EXE have not been independently rebuilt byte-for-byte.
- The supplier's final historical librsvg Cargo.lock was not captured. The supplied reconstruction and conservative source superset are labelled as such, not presented as the exact historical lock. Independent supplier-package GPG verification has not been established.
- The simd_helpers addition is a labelled upstream follow-up; the four macOS-only source exclusions and unrecovered historical URLs remain explicitly documented. These repairs do not recover every historical build input or certify every legal issue.
- A few preserved legacy notices refer to build evidence not included in the public source collection. Public build controls provide the current guidance; those historical references are not promises of additional downloadable files.

## Source and build boundaries

**GitHub app/worker code is aligned with the portable clean application source in `1.0.source.zip`.** The diagnostic removal covers demo/smoke dispatch, fixture helpers and diagnostic process-ID reporting, plus the worker's ping/pong command and its two affected tests. Normal error handling, Job Object ownership and process cleanup remain. The installed app adds the approved `RuntimePaths.cs` data-path selection change; its full matching source is **`FK_YT-1.0-machine-app-source.zip`**, not the portable source alone.

The four removed diagnostic sources are `MainWindow.FeatureVerification.cs`, `MainWindow.PlaybackVerification.cs`, `MainWindow.Verification.cs`, and `Playback/PlaybackSmoke.cs`. Developer unit tests remain separate from the application. The repository is still not a byte-for-byte copy of the entire source ZIP: `scripts/Publish.ps1` retains its repository-specific document-copy handling, and the source ZIP includes release materials absent from this checkout. These differences do not justify replacing the final runtime with historical Bootstrap downloads.

| Operation | Actual source/tool | What it establishes |
| --- | --- | --- |
| Prepare SDK / compile repository | Existing `scripts/Bootstrap.ps1 -Component Dotnet`, then `scripts/Build.ps1` | Builds the aligned app source; does not recreate the full final runtime package |
| Prepare historical runtime | Existing `scripts/Bootstrap.ps1` and `scripts/Bootstrap-Ffmpeg.ps1` | Acquires locked historical supplier binaries, not the final licensed media cohort |
| Local development package | Existing `scripts/Publish.ps1` | Self-contained `win-x64` folder; fresh direct child of `dist/` required. Does not publish online or produce NSIS |
| Clean portable app rebuild | `1.0.source.zip` → `release-1.0/BUILDING.txt` | Locked restore/publish of portable app; use a fresh matching portable extraction for runtime/notices |
| Installed app rebuild | `FK_YT-1.0-machine-app-source.zip` → `MACHINE-APP-BUILD.md`, `scripts/BuildMachineApp.ps1` | Offline locked/deterministic build including the installed data-path change; not an app execution test |
| Exact media build controls | Both media source ZIPs → supplied build instructions / `REBUILD-MPV.ps1` | Separate media build inputs and instructions; not a full-cohort reproduction claim |
| Exact supplied installer input | `machine-public-payload.zip` | Already assembled payload pinned by the active installer build lock; use directly for the supported installer command |
| New payload assembly | Installer source → `prepare_machine_payload.py` | Can regenerate a reviewed payload; repacked app-source metadata makes its ZIP hash differ from the supplied original input |
| Installer build | `FK_YT-1.0-installer-source.zip` → `docs/BUILDING.md`, `build_installer.py`, `src/FK_YT.nsi` | Packages the pinned supplied payload using current matching source references; executable source preserved, byte-identical EXE rebuild not claimed |

The SDK is **10.0.402**. The repository's `dependencies.lock.json` still identifies historical shinchiro mpv and Gyan FFmpeg inputs, while final binaries use the controlled mpv/MSYS2 builds above. The final source archive itself labels that acquisition lock historical. Do not weaken its hash checks to substitute final binaries into the historical Publish workflow.

The installer source ZIP is separate from this repository and does not embed the complete installed-app source or payload. For its supported build, use the supplied **`machine-public-payload.zip`** directly, the current matching installed-app source archive, the three repaired third-party source collections, Python 3.10+, and the exact official NSIS 3.13 binary/source archives in its toolchain lock. After arranging the inputs as described in the source ZIP's `docs/BUILDING.md`, run:

```powershell
python build_installer.py --portable 'inputs/machine-public-payload.zip' --app-source 'inputs/FK_YT-1.0-machine-app-source.zip' --release-sources 'inputs/corresponding-source' --tooling-dir 'tooling' --stage-dir 'stage' --output-dir 'output' --compile
```

The supplied payload's exact SHA-256 is **`a581d63f619c31643cbb5473ab3d41732d8966bd4485bd5af2847040819a428e`**. Rebuilding the app separately requires the pinned **.NET SDK 10.0.402**, **PowerShell 7**, and a reviewed locked offline NuGet cache. Regenerating a payload with the repacked app-source archive records a different source hash in its metadata, so the resulting payload ZIP **will not match the supplied payload hash**. Review and pin such a new payload in a separate build lock; do not weaken the original check or present it as the same input. Inputs/cache/toolchain are prerequisites rather than files already embedded in this checkout or installer-source ZIP, and the helper does not download them.

The installer-source publication repack changes only its build guide, active input pins, and source-access notice. **24 original entries, including executable source, tests, original licenses, icons, and build manifests, remain byte-identical.** Historical build-time input/source-access snapshots are retained separately. Recompiling now changes embedded publication metadata/guidance, so the result is not claimed to reproduce the already supplied EXE byte-for-byte. Source repacking was not a new installer compile or lifecycle test, and compiling does not upload or publish a release.

The earlier repository comparison found all 68 shared `src/` and `worker/` files identical to the **portable** source after diagnostic removal. Its locked Release build passed with zero warnings/errors, and 238 offline Python tests passed. The installed-app production source previously rebuilt the expected DLL hash `d09ef13fce3029a8943ddd149eeb770dde024543276fe8e7ab98a200111723d6`; app EXE/DLL/deps/runtimeconfig also matched repeat builds in different paths. The current app-source publication repacks preserve **102 portable and 106 installed-app code/build-input files** and original legal texts, with ZIP CRC and exact manifests checked; they were not another app rebuild or launch. This does not mean every third-party runtime was rebuilt byte-for-byte. There is no C# test project in the repository; `Build.ps1 -Test` is not a working instruction here. Historical Publish output is not the curated final user distribution.

## Publication verification and remaining checks

공개와 비인증 접근·업로드 해시 확인은 완료했습니다. 실행용 EXE·포터블은 그대로이며, 소스 ZIP의 문서·출처·추가 고지는 위 범위에서 보완했습니다. 공개 접근 확인과 아래 런타임 미검증 범위는 별개입니다.

1. Keep the **12 selected release assets** and the complete checksum inventory synchronized if any bytes change. The installed-app source must accompany the installed DLL; portable source alone does not describe that app change. Preserve original legal texts and the explicit source/reproducibility limits above.
2. **Completed:** the public repository, published `v1.0`, and all 12 asset URLs were checked without authentication; server sizes/SHA-256 matched the selected local files and repository checksum inventory. The three notice/checksum texts also passed full anonymous download/hash checks. The first-byte checks on larger assets establish public access and the recorded server metadata, not a new full-file download/hash test.
3. Keep final installed-app launch/playback, real machine update/removal, and the uninstaller's initial Temp-copy ACL/access checks explicitly unverified until tested. The observed installer-Temp checks and earlier per-user lifecycle results do not complete them.
4. Retain the untested clean Windows 11/no-system-runtime, alternate UAC account, SmartScreen, real GPU/audio, live account, disk-full and power-loss limits. Do not turn the existing Windows 10 development-PC checks into clean-machine proof.
