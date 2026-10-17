<a name="english"></a>

# FK_YT 1.1

**[한국어 안내](#korean)** · [English](#english)

A personal YouTube viewer for Windows. Lists and a player, without keeping the YouTube webpage running.

**Watch, queue, and keep going.** Playback controls, quality, subtitles, and the next videos share one window. Pop the player out when you need more room.

![FK_YT playing a video, with playback controls and the next-video queue](assets/screenshots/playback-en.png)

| Home | Incognito mode | Settings and sign-in |
| :---: | :---: | :---: |
| [![FK_YT home screen while recommendations load](assets/screenshots/home-en.png)](assets/screenshots/home-en.png) | [![FK_YT incognito start screen](assets/screenshots/incognito-en.png)](assets/screenshots/incognito-en.png) | [![FK_YT settings and sign-in screen](assets/screenshots/settings-en.png)](assets/screenshots/settings-en.png) |
| Home screen while recommendations load. | Browse without using sign-in cookies or saving watch history and resume positions. | Connect a session and choose how watch history and members-only videos are handled. |

*Account details and selected thumbnails are blurred. Videos and artwork shown belong to their respective rights holders.*

<!-- PUBLICATION-STATUS-BEGIN -->
> **1.1 release files and verification**
> **[Release files and availability: FK_YT v1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1).** Compare downloads with [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt). Installation, installed-app launch/playback, installer updates and removal have been verified on Windows 10 and Windows 11, as confirmed by the maintainer on 2026-10-17. Installation time has not been measured. See the [files and verification scope](docs/RELEASE-1.1.md), [1.1 changes](docs/FEATURES-1.1.md), and [licensing record](docs/LICENSING-1.1.md).
<!-- PUBLICATION-STATUS-END -->

## 1. Language

[English](#english) · [한국어](#korean)

The app interface supports English, Korean, and Japanese. Changing the interface language does not change your YouTube account's region.

## 2. What it does

FK_YT does not keep a YouTube webpage running inside the app. It uses C# WPF for the interface, Python workers and yt-dlp for the information it needs, and mpv for playback. Deno supports yt-dlp's JavaScript processing; FFmpeg handles tasks such as merging downloaded video and audio.

Browse Home, subscriptions, search, Shorts, channels, and playlists, then watch videos and read comments in the same window. Quality and subtitle selection, autoplay, a pop-out player, theater mode, and downloads are included. Availability depends on the account and what YouTube serves for each video.

The current source limits several kinds of work:

| Item | Current setting |
| --- | --- |
| Feed requests | Up to 90 entries per batch; fetch the next batch as you scroll |
| Thumbnail memory cache | 96 MiB of estimated image data |
| Thumbnail disk cache | 512 MiB, at most 8,192 entries |
| Thumbnail requests | 4 concurrent, up to 128 waiting |
| Ordinary-video mpv buffering | `cache-secs=15`, `demuxer-max-bytes=32MiB`, `demuxer-max-back-bytes=8MiB` |
| Confirmed live backward cache | 6 GiB per demuxer; forward budget stays 32 MiB; RAM only, allocated as data arrives |
| Next-video preparation | One autoplay candidate, attempted when 60 seconds or less remain |
| Comments | Up to 100, fetched when you click the comments button |
| Quality/full subtitle catalogs | Requested when you open the menu; reuse available results |

**These are individual cache and request settings, not a cap on total application RAM, GPU memory, or CPU use.** Actual usage varies with resolution, codecs, decoding, media duration, and your system. This is not a promise to beat Firefox in every workload or deliver perfectly gapless autoplay.

1.1 adds live-status handling, seeking within confirmed local live-cache ranges, and a LIVE control. Theater mode can keep comments/recommendations open, and normal/theater playback controls can be collapsed. Settings offers **manual, experimental** yt-dlp/Deno updates that take effect after restart. Shorts filtering removes explicitly marked served-ad cards; it does not promise to detect every upstream ad variant. See [1.1 features and limits](docs/FEATURES-1.1.md).

The 6 GiB live value is a backward-cache budget per demuxer, **not a whole-app RAM limit or a fixed allocation**. It cannot retrieve broadcast data from before you joined. A stream recognized as live only after loading can retain its original budget if a next-video preload was already attempted; reopening it with LIVE applies the live budget.

### Theoretical configured packet-cache budgets

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

## 3. Why this exists

I wanted to watch YouTube, not pay rent in RAM. Leaving it running in Firefox on my PC kept pushing memory usage up, and restarting the browser got old. So I had this made.

FK_YT's code and the words you're reading were all handed off to AI. I said "make it" and it did.

I made it for myself. After-sales service may consist of absolutely nothing.

That is the author's experience on one PC, not a finding that every Firefox installation has a memory leak. The AI statement covers FK_YT's own code and documentation; mpv, yt-dlp, and the other dependencies belong to their respective projects. Continued maintenance and fixes for future YouTube changes are not guaranteed.

## 4. Accounts and privacy

**FK_YT itself has no feature that collects usage history or sign-in information for a developer-operated server.** Playing online media still involves external services.

| Data or action | Handling |
| --- | --- |
| Settings, history, resume positions | Installed version: `%LOCALAPPDATA%\FK_YT\data` for each Windows user. Portable version: `data/` inside the extracted app folder |
| Sign-in session | Connected from a Firefox profile or Netscape cookie file. The stored app session is encrypted with Windows DPAPI for the current Windows user |
| YouTube requests | Feeds, playback, comments, and other features contact YouTube/Google and media/image CDNs. Authenticated features use the selected session |
| YouTube watch history | In normal mode, attempts to report actual playback when history synchronization is enabled. It is enabled by default |
| Likes, dislikes, recommendation feedback | Requests sent to YouTube when you choose the corresponding action |
| Return YouTube Dislike | Count queries send the video ID to the RYD API. This request path does not attach the app's YouTube sign-in cookies |
| Manual tool checks/downloads | Only when requested in Settings; official HTTPS services, without YouTube cookies |
| Build downloads | Source-build scripts contact dependency hosts and NuGet package providers |

Services you contact, including YouTube and RYD, can see ordinary connection information such as your IP address. RYD dislike counts are estimates.

Incognito mode does not use sign-in cookies or save app watch history/resume positions. It also disables account history and rating writes. **It does not hide your IP address or erase files you deliberately download.**

There are security limits. Tools may need temporary plaintext cookie files, and Firefox session extraction can create a temporary copy of the original cookie database. Encrypting stored cookies does not protect against every malicious program running as you or an already compromised PC. **Use a dedicated Firefox profile for YouTube.** Do not share or upload `data/`, cookie exports, or personal-backup ZIPs.

## 5. Requirements and versions

- Installer acceptance floor: **Windows 10 22H2, build 19045 or later, x64 (AMD64)**. Windows 11 x64 is also a target. x86 and ARM64 are not accepted. This does not mean every Windows 10 version is supported.
- Local work used **Windows 10 Pro 22H2 x64, build 19045.6456**, a PC with development tools. **The maintainer confirmed installation, installed-app launch/playback, installer updates and removal on Windows 10 and Windows 11 on 2026-10-17.** The additional Windows 11 edition/build and clean-system/standard-user conditions were not recorded; those broader environment claims are not implied.
- Installer acceptance is separate from vendor support. The tested Windows 10 Pro 22H2 system is not presented as an officially supported .NET 10 environment. Check editions and lifecycle status in the [.NET 10 supported OS table](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
- Users **do not need to install .NET, Python, Deno, mpv, FFmpeg, or an SDK separately**. The required runtimes and tools are included in the app folder. Setup uses Windows' built-in 64-bit PowerShell.
- Account connection uses Firefox or a Netscape-format cookie file you supply. Search and some playback also work without signing in.

These are the bundled versions, preserved from the existing runtime set. **The 1.1 portable and installed versions use the same app DLL and the same application source.** The installer adds `FK_YT.install-mode` to select each Windows user's data directory. The historical [dependency lock](dependencies.lock.json) alone does not describe the final media binaries. Manually selected tool updates can differ from this table.

| Bundled component | Version / build | Purpose |
| --- | --- | --- |
| .NET / Windows Desktop Runtime | Both **10.0.13**, self-contained win-x64 | Run the WPF app |
| Python | **3.13.16**, embedded x64 | Feed/account workers |
| yt-dlp EXE / Python package | **2026.08.19 / 2026.8.19** | Extraction, metadata, downloads |
| Python inside yt-dlp EXE | **3.10.11** | Separate from the worker's Python |
| yt-dlp-ejs | **0.8.0** | JavaScript support |
| Deno | **2.9.7** | JavaScript runtime |
| mpv | **0.41.0-dev-gec38361c56-fkyt** | Playback; build prepared by FK_YT |
| FFmpeg / ffprobe | **9.0.2**, MSYS2 UCRT64 `9.0.2-1` | GPLv3-enabled shared-library build |
| Microsoft.Data.Sqlite / SQLitePCLRaw | **10.0.13 / 3.0.5** | Local database |
| Native SQLite package | **3.53.4** | Separate from Python's included SQLite |

Build-only tools are **.NET SDK 10.0.402**, **7zr 26.04** for extraction, and **NSIS 3.13** for installer creation. The SDK, compiler, and 7zr are not installed into the application folder. Setup uses NSIS **Unicode / Modern UI 2 (MUI2), English only, and zlib compression**. The NSIS x86 Unicode engine installs the x64 app payload.

## 6. Installation and launch

### For users: which file should I get?

**We recommend `FK_YT-1.1-portable.zip` for a straightforward setup.** Extract the whole archive into a new writable folder and run `FK_YT.exe` inside `FK_YT`; administrator permission is not needed there. **Both packages provide the same viewing/playback features, app DLL, and bundled runtime tools.** The installer adds machine-wide program installation, shared shortcuts, Windows removal registration, and the marker that selects per-user data. Check the release checksum for either download.

Release files and their availability are listed at [FK_YT 1.1 on GitHub Releases](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1). Public release downloads do not require signing in. `Code → Download ZIP` and `FK_YT-1.1-source.zip` are developer source downloads, not ready-to-run packages.

The installer reduces duplicate integrity work: each fixed payload file receives one complete SHA-256 pass in its protected staging copy, and repeated directory-permission work is reused within each phase. Path/type/length checks and update/removal protections remain. Post-install full-file SHA checks were already absent before this change. **Actual 1.1 installation time has not been measured; no speedup percentage or completion time is claimed.**

| Detail | Installer EXE | Portable ZIP |
| --- | --- | --- |
| First launch | Approve Windows' administrator request → follow Setup's English pages → close Setup → open **FK_YT (System)** from Start Menu | Extract the **whole** ZIP into a new folder → run `FK_YT.exe` inside `FK_YT` |
| Default location | Native 64-bit `%ProgramFiles%\FK_YT`, normally `C:\Program Files\FK_YT` | A writable folder you choose |
| Administrator rights | Required for installation and removal; the app runs with normal user permissions | Not required in a writable location |
| Shortcuts | Shared Start Menu: **FK_YT (System)** and **Uninstall FK_YT (System)**; optional shared Desktop shortcut, off by default | None created automatically |
| Data | Each running Windows user's `%LOCALAPPDATA%\FK_YT\data` | `<extracted FK_YT folder>\data` |
| Removal | Windows app removal, **Uninstall FK_YT (System)**, or installed `Uninstall.exe`; profile data is preserved | Close the app and delete its folder; deleting `data` also deletes your app data |

The directory page accepts an empty app folder directly inside native 64-bit Program Files, or an existing machine installation it owns. It rejects portable folders and earlier per-user installations rather than migrating them. The installation root must be at most 100 characters long. Setup does not automatically launch the app with elevated permissions. Keep `runtime/`, `worker/`, DLLs, license notices, and the installed-mode marker intact.

**Installation itself works offline.** Setup does not download additional runtimes. YouTube browsing, streaming, comments, downloads, and RYD count queries need internet access. The app's English/Korean/Japanese selection is separate from the English installer and does not change account or region settings.

### Updates and data

- **Installed version:** Close FK_YT before running a compatible newer machine installer as administrator and selecting the existing machine installation. The implementation preserves per-user profile data and user-added files; incompatible, modified, or running installations can be refused. Installer updates were verified on Windows 10 and Windows 11 (maintainer confirmation, 2026-10-17). This does not verify the separate experimental yt-dlp/Deno updater. The application has no automatic online updater; the separate experimental tool updater is manual and applies after restart.
- **Portable version:** Close the app and keep the old folder as a backup. Extract the new ZIP into a separate empty folder, copy your preserved `data/` into the new `FK_YT/` folder, then launch it.
- **Moving from portable or an earlier per-user install:** Nothing is imported automatically. Close both apps, keep the original data, and copy it as the same Windows user into an **empty** `%LOCALAPPDATA%\FK_YT\data` after installation. Do not merge SQLite databases or overwrite a data folder already in use. Existing per-user apps, shortcuts, and data are left separate.
- Sign-in data is tied to the Windows user. Moving to a different PC or Windows account may require reconnecting the session.

### What happens when I uninstall?

Use Windows **Settings → Apps → FK_YT → Uninstall**, **Uninstall FK_YT (System)** in Start Menu, or installed `Uninstall.exe`, and approve the administrator request. The machine uninstaller is designed to remove unchanged owned program files, shared shortcuts, and machine registration. **It always preserves every user's settings, sign-in information, watch history, resume positions, and downloads; there is no user-data deletion checkbox.** It does not search other user profiles. Added, modified, or locked program-folder files may remain. Removal was verified on Windows 10 and Windows 11 (maintainer confirmation, 2026-10-17).

To delete your own installed-app data separately, close FK_YT and remove your `%LOCALAPPDATA%\FK_YT\data` only after keeping anything you need. This also removes that profile's saved sign-in and history. Downloads saved elsewhere and data in earlier portable/per-user folders are separate. If modified or added files remain in Program Files after removal, preserve them and use an empty supported folder for reinstalling.

### File verification and download commands

The installer is supplied without a publisher code signature. SmartScreen behavior after a real distribution download has not been tested, so no specific warning or wording is claimed. Use [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt) and the [file inventory](docs/RELEASE-1.1.md#files-and-hashes). For example, run `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1-Setup-x64.exe'` in PowerShell and compare it with the release checksum. A checksum verifies matching bytes; it does not replace a code signature.

These GitHub CLI commands request the `v1.1` assets listed on the release page. Direct public release downloads also work without signing in.

```powershell
gh release view v1.1 --repo mobsweeper/FK_YT
gh release download v1.1 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.1-portable.zip' --dir .\FK_YT-download
# For the installer, use this instead of the portable download:
gh release download v1.1 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.1-Setup-x64.exe' --dir .\FK_YT-download
```

### For developers: repository builds and the final release differ

**`FK_YT-1.1-source.zip` contains the common product source for both portable and installed apps.** The public source excludes test projects, internal review records, and local verification outputs. Normal error handling and process cleanup are retained. The installer has its own `FK_YT-1.1-installer-source.zip`. See [build instructions](docs/BUILD-1.1.md) and [source/build boundaries](docs/RELEASE-1.1.md#source-and-build-boundaries).

Compiling the repository requires Git, **PowerShell 7**, and internet access. Cloning this public repository with Git does not require a GitHub sign-in. Ordinary users do not need to build anything.

```powershell
git clone https://github.com/mobsweeper/FK_YT.git
Set-Location FK_YT
git checkout v1.1
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
pwsh -NoProfile -File .\scripts\Build.ps1
```

This prepares a project-local SDK and builds the app; it does not prepare all external playback tools. Full `Bootstrap.ps1` and `Bootstrap-Ffmpeg.ps1` prepare **historical supplier mpv/FFmpeg builds**, so they must not be used to restore the final licensed runtime. `Publish.ps1` packages a prepared development runtime into a new self-contained directory beneath `dist/`; it does not create the final licensed package, an NSIS installer, or a GitHub release. Preserve locked hashes and investigate failures instead of disabling verification.

The [release verification scope](docs/RELEASE-1.1.md#verification-scope) distinguishes earlier preview6 checks from checks of the final 1.1 artifacts. Source publication and successful compilation do not establish actual playback, account compatibility, installation, or long-duration behavior.

Tool setup: [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) · [Git](https://git-scm.com/install/windows) · [GitHub CLI](https://github.com/cli/cli#installation)

## 7. Basic usage

### Create a dedicated Firefox profile

Prefer a **separate profile signed in only to YouTube** over your everyday browsing profile.

1. Enter `about:profiles` in Firefox's address bar.
2. Choose **Create a New Profile** and give it a recognizable name, such as `FK_YT`. Keep the default folder unless you have a reason to change it.
3. After creating it, check that your everyday profile is still the default. Use **Set as default profile** on the old profile if necessary.
4. Under the new profile, choose **Launch profile in new browser**.
5. Sign in to YouTube in that window. Avoid adding unrelated site logins or importing your everyday profile's data into it. Mozilla account synchronization is not required.
6. In FK_YT, open **Settings · Sign-in**, select the profile, and click **Connect / Refresh Firefox session**.
7. If the profile is missing from the list, use **Choose profile folder**. Select the profile's **Root Directory** from `about:profiles`, not the Local Directory used for cache.

See [Mozilla's profile-management guide](https://support.mozilla.org/en-US/kb/profile-manager-create-remove-switch-firefox-profiles) for details. If the session expires, sign in again in that dedicated profile and refresh the connection in FK_YT.

### Browse and play

- Enter a search term or a YouTube video, channel, or playlist URL. Signing in enables account features such as personalized Home and subscriptions.
- Use **YouTube channel** in the sidebar to switch between channels belonging to the same Google account. Interface language is separate from YouTube's region setting.
- Open a video or playlist to play it. Click a channel's name or avatar to browse its videos. Channel sorting and Home categories only show choices returned by the server.
- The default quality is **Best quality (auto)**. A numeric choice sets a resolution ceiling. If a video lacks that resolution, playback selects an available quality at or below the ceiling.
- Open **Subtitles ▾** to turn subtitles off or select creator-provided, automatically generated, or translated captions. Available languages depend on the video; English, Korean, and Japanese are prioritized in the list.
- Click **Comments** to fetch them. Choose top or newest order.
- Enable next-video autoplay to continue through the queue. If preparation fails, the next item may use ordinary loading.
- **Pop out** opens a separate player window; use the return control to bring it back. Theater mode can keep comments/recommendations open. Normal/theater controls can be collapsed and restored; fullscreen and pop-out controls hide when idle. The manual collapse choice lasts for the app session.
- **Share**, or right-click a video card, to copy its video link. Use **Download** only for content you are permitted to save.
- Local resume positions and YouTube account history are separate. A successful history request does not guarantee exact cross-device position sync, and YouTube may mark a video as completed.

### Live playback and tool updates

- Live seeking uses only confirmed local mpv cache ranges, including gaps. **LIVE** reloads the broadcast edge and preserves an intentional pause. It cannot seek through the server's complete DVR window.
- Live disconnections can reconnect up to three times after 2/5/10 seconds. Authentication, access, and rate-limit errors stop retries. Automatic next-video playback waits for confirmation that the broadcast ended. Manual pause, stop, media/account changes, and incognito changes cancel recovery.
- Guest/default-slot live playback uses the existing mpv path. Other Google sign-in slots need a validated single muxed HTTPS HLS stream; unsupported selected-account transports fail explicitly without substituting another account.
- In Settings, tool checks, staging, rollback, and bundled-tool restore are manual and experimental. They apply on the **next app start**. Keep bundled tools if playback works normally. Older completed versions are retained with no automatic cleanup or total storage cap. See [tool update limits](docs/FEATURES-1.1.md#manual-experimental-tool-updates).

[English 1.1 supplementary guide](docs/guides/User-guide-1.1.txt) · [한국어 보충 안내](docs/guides/사용설명서-1.1.txt) · [日本語補足ガイド](docs/guides/ご利用ガイド-1.1.txt)

## 8. Project layout

```text
README.md              Bilingual repository landing README
readme_KR.md            Korean guide
readme_EN.md            English guide
assets/screenshots/    README screenshots with account details blurred
docs/RELEASE-1.1.md     Artifacts, hashes, source access, verification scope
docs/FEATURES-1.1.md    1.1 features and known limits
docs/BUILD-1.1.md       Compile, publish, and release assembly boundaries
src/                    C# WPF app, player, account and storage services
worker/                 Product Python workers
scripts/                Development bootstrap, build, and local Publish
dependencies.lock.json  Development tools and historical supplier runtime pins
LICENSE / license.md    MIT license and distribution guidance
```

`tools/`, `runtime/`, `dist/`, and `data/` are local build/runtime folders. Installer sources are prepared separately in `FK_YT-1.1-installer-source.zip`, not in this repository. User data and personal backups are excluded from release files.

## 9. Limitations

- **Authentication:** Expired cookies or additional authentication requirements may require reconnecting. The app does not bypass age, membership, or region restrictions. Handling an ordinary content warning does not substitute for signing in or verifying age.
- **YouTube changes:** Some features depend on unofficial extraction paths. Site changes can break feeds, playback, comments, and history reporting.
- **Subtitles:** Discovering a language does not guarantee its caption download will succeed. Requests may fail with HTTP 429, expired URLs, or unavailable captions. Not every translated language is guaranteed to load.
- **Quality and playback:** Available formats depend on the account/server, GPU and codec support, and network. Autoplay can still buffer.
- **Account behavior:** Recommendation changes, history update timing, and cross-device resume positions are not guaranteed. RYD estimates need not match YouTube's internal counts.
- **Live:** Local cache is not server DVR; live chat, scheduled-stream waiting, and recording are not included. Late recognition of a direct guest/default-slot live URL can occur after loading; earliest EOF and an old VOD resume position on that path still need separate live testing.
- **Tool updates:** Offline checks do not establish every future upstream version's compatibility or a complete Deno binary third-party-license inventory. Publisher HTTPS hashes are not an independent signature guarantee. See [the full limits](docs/FEATURES-1.1.md).
- **Support:** Environments other than Windows 10 22H2, and every DPI/GPU combination, have not been validated. Ongoing updates and fixes are not guaranteed.

For a useful bug report, include the app version, Windows version, whether incognito mode was enabled, reproduction steps, and the displayed error. Include a video URL only if you can share it. Do not attach cookies, authentication tokens, `data/`, or personal backups.

## 10. YouTube Terms of Service and account risk

FK_YT is not an official YouTube/Google app and is not endorsed by or affiliated with them. Automated requests, unofficial access, and downloads may conflict with the [YouTube Terms of Service](https://www.youtube.com/static?template=terms&hl=en), depending on how the app is used.

The app is designed to avoid unnecessary requests, but it does not guarantee YouTube approval or compliance with its terms. The likelihood of rate limiting, feature blocks, or account action has not been established. Fewer requests are **not evidence that the risk of account action is "very low."**

Review the terms and your rights to the content before use. A dedicated Firefox profile reduces the scope of cookie exposure; it is not protection against account action.

## 11. Licensing and external projects

**FK_YT's own code and documentation are distributed under the [MIT License](LICENSE).** See [license.md](license.md) for the distribution summary. External projects retain their respective licenses.

MIT it is. The author probably couldn't tell you what that means, though.

Paths below are relative to the **installed or extracted app folder**. Full component, copyright, and license information is retained in `THIRD-PARTY-NOTICES.txt`, `notices/`, and each runtime directory. This table does not replace those texts.

| Included component | License summary / notice location |
| --- | --- |
| .NET / WPF, Microsoft.Data.Sqlite | MIT plus included component and Microsoft distribution terms; `notices/dotnet/`, `notices/nuget/` |
| SQLitePCLRaw / SQLite | Apache-2.0 / public domain; `notices/nuget/` |
| Python | PSF plus historical and included-library terms; `runtime/python/LICENSE.txt`, `notices/python/` |
| yt-dlp Python package / EXE | Package: Unlicense; combined PyInstaller EXE: GPLv3-or-later. See `LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, and `notices/` under `runtime/yt-dlp/` |
| yt-dlp-ejs / Meriyah / astring | Unlicense / ISC / MIT; original notices in `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/` |
| Deno and its components | MIT plus component terms; `runtime/deno/LICENSE.md`, `notices/deno/`. MPL component sources are included in `notices/deno/third-party/sources/mpl/` |
| mpv / FFmpeg and linked libraries | GPLv2-or-later mpv core combined with GPLv3-enabled FFmpeg: GPLv3-or-later distribution plus library terms. Full texts and build/source index in `notices/media/` |
| NSIS / MUI2 / zlib / plugins | Primarily zlib/libpng; original notices for the used `System`, `nsExec`, `nsDialogs`, and SystemCall MIT exception are retained. Added by the **installer only** under `notices/installer/nsis/` |

See [yt-dlp's licensing explanation](https://github.com/yt-dlp/yt-dlp#licensing) for the source/EXE distinction and the [NSIS license page](https://nsis.sourceforge.io/License) for its component terms. 7zr is a build-only tool, absent from the user distribution. RYD is an external service; its server software is not bundled.

The final mpv binary was built by FK_YT from the upstream commit with a build-identifying version-metadata change. FFmpeg is the MSYS2 UCRT64 build and its dependency cohort. These are not the historical shinchiro/Gyan binaries acquired by Bootstrap. Original and modification notices and build instructions are retained.

Corresponding source is supplied in **collections assembled by FK_YT**: `FK_YT_yt-dlp.zip`, `FK_YT_mpv-FFmpeg-part1.zip`, and `FK_YT_mpv-FFmpeg-part2.zip`. These collections are not official ZIP releases made by the upstream projects. The media files are **independent ZIPs**, not split volumes: get both and extract part1, then part2, into the **same folder**. Users do not need these source ZIPs to run the app.

The [v1.1 release page](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1) lists the binary and corresponding-source assets together, without an additional source-access fee. Use that page to check availability. Keep matching source accessible alongside the binaries when redistributing. The [distribution guide](docs/RELEASE-1.1.md) lists the common app source, separate installer source, and checksums. The [licensing record](docs/LICENSING-1.1.md), [source-access map](docs/SOURCE-ACCESS-1.1.txt), and [supplemental notices](docs/LICENSES-SUPPLEMENT-1.1.txt) explain component terms, archive mappings, and remaining limits while retaining the original legal texts. The two text notices are also release assets.

The source collections' distributor metadata and broken links were repaired, and six legal-notice files were added with their provenance recorded. Four macOS-only crate archives unused by the native Windows build were excluded; upstream cross-platform locks remain intact. Unrecoverable historical URL fields are explicitly marked unknown rather than guessed. Retained upstream source/legal entries and application code/build inputs were checked unchanged.

**A complete bit-for-bit rebuild of all external binaries and recovery of the exact historical supplier librsvg lock are not complete.** Use the release page to check availability of the matching source assets. The simd_helpers notice is a labelled upstream follow-up, not a claim that its original crate contained that file. See the licensing record and distribution guide for the precise scope and remaining limits; these checks are not a legal certification.

## 12. Miscellaneous

| Item | Details |
| --- | --- |
| Writing the code | Delegated to AI |
| Writing this README | Also delegated to AI |
| Estimated debugging reviews delegated to agents | **About 195** |
| Certified bug-free | Absolutely not |

Based on **13 parallel review rounds × 15 review assignments**, checked on October 17, 2026. Repeat assignments to the same agents count; the main agent's own checks, separate external reviews, and individual follow-up questions do not. That's not 195 different AI models, and definitely not a claim of 195 bugs fixed.

*This counts static code reviews only. Dynamic checks, including actual app execution and playback, could not be tallied.*

---

<a name="korean"></a>

# FK_YT 1.1

**[English guide](#english)** · [한국어](#korean)

Windows용 개인 YouTube 뷰어. 웹페이지 대신 필요한 목록과 플레이어만 띄웁니다.

**영상 연속재생** 재생 조작, 화질, 자막, 다음 영상 목록을 한 화면에 모았습니다. 플레이어만 따로 띄우고 싶으면 팝아웃을 누르면 됩니다.

![영상 재생과 하단 조작 버튼, 다음 영상 목록이 표시된 FK_YT](assets/screenshots/playback-ko.png)

| 홈 추천 | 시크릿 모드 | 설정·로그인 연결 |
| :---: | :---: | :---: |
| [![FK_YT 홈 추천 목록 로딩 화면](assets/screenshots/home-ko.png)](assets/screenshots/home-ko.png) | [![FK_YT 시크릿 모드 시작 화면](assets/screenshots/incognito-ko.png)](assets/screenshots/incognito-ko.png) | [![FK_YT 설정·로그인 연결 화면](assets/screenshots/settings-ko.png)](assets/screenshots/settings-ko.png) |
| 홈 추천 목록을 불러오는 화면입니다. | 로그인 쿠키를 사용하지 않고, 시청 기록과 이어보기 위치를 저장하지 않습니다. | 로그인 연결, 시청 기록 전송, 멤버십 영상 숨기기를 설정합니다. |

*계정 정보와 일부 썸네일은 가렸습니다. 화면 속 영상·이미지의 권리는 각 권리자에게 있습니다.*

<!-- PUBLICATION-STATUS-BEGIN -->
> **1.1 배포 파일·검증 범위**
> **[릴리스 파일과 접근 상태: FK_YT v1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1)** 다운로드한 파일은 [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt)와 비교하세요. 설치·설치 후 실행과 재생·설치기 업데이트·제거는 **Windows 10/11에서 검증됐습니다**(제작자 확인, 2026-10-17). 설치 시간은 별도로 측정하지 않았습니다. [파일·검증 범위](docs/RELEASE-1.1.md), [1.1 변경 내용](docs/FEATURES-1.1.md), [라이선스 점검 기록](docs/LICENSING-1.1.md)을 확인해 주세요.
<!-- PUBLICATION-STATUS-END -->

## 1. 언어 선택

[한국어](#korean) · [English](#english)

앱 UI는 한국어·영어·일본어를 지원합니다. UI 언어를 바꿔도 YouTube 계정의 지역 설정은 바뀌지 않습니다.

## 2. 프로젝트 소개

FK_YT는 YouTube 웹페이지를 앱 안에 상주시켜 두지 않습니다. C# WPF로 화면을 만들고, Python 작업자와 yt-dlp로 필요한 정보를 가져오며, 영상은 mpv로 재생합니다. Deno는 yt-dlp의 JavaScript 처리를 지원하고, FFmpeg는 다운로드한 영상·음성의 결합 등에 사용합니다.

홈 추천·구독·검색·Shorts·채널·재생목록을 탐색하고, 같은 창에서 영상과 댓글을 볼 수 있습니다. 화질·자막 선택, 자동재생, 팝아웃, 영화관 모드, 다운로드도 제공합니다. 서버와 계정이 허용하는 범위에서 동작하며, 모든 영상에서 모든 기능이 제공되는 것은 아닙니다.

필요 이상의 작업을 줄이기 위해 현재 소스에는 다음 제한을 두었습니다.

| 항목 | 현재 설정 |
| --- | --- |
| 목록 조회 | 한 번에 최대 90개, 아래로 이동하면 다음 묶음 조회 |
| 썸네일 메모리 캐시 | 추정 이미지 크기 기준 96MiB |
| 썸네일 디스크 캐시 | 512MiB, 최대 8,192개 |
| 썸네일 요청 | 동시 4개, 대기 최대 128개 |
| 일반 영상 mpv 버퍼 | `cache-secs=15`, `demuxer-max-bytes=32MiB`, `demuxer-max-back-bytes=8MiB` |
| 확인된 라이브 후방 캐시 | demuxer별 6 GiB 예산, 전방은 32 MiB 유지. 디스크에 저장하지 않고 수신한 만큼 RAM 사용 |
| 다음 영상 준비 | 자동재생 대상 1개, 현재 영상의 남은 시간이 60초 이하일 때 준비 시도 |
| 댓글 | 버튼을 눌렀을 때 최대 100개 조회 |
| 화질·전체 자막 목록 | 메뉴를 열 때 조회하고, 사용 가능한 기존 결과는 재사용 |

**이 수치는 개별 캐시와 요청의 설정값이며, 앱 전체 RAM·GPU 메모리·CPU 사용량의 상한이 아닙니다.** 실제 사용량은 해상도, 코덱, 디코더, 영상 길이와 실행 환경에 따라 달라집니다. Firefox보다 항상 적은 자원을 쓴다거나 자동재생이 완전히 무끊김이라고 보장하지 않습니다.

1.1에는 라이브 상태 구분, 확인된 로컬 라이브 캐시 구간의 탐색과 LIVE 버튼을 추가했습니다. 영화관 모드에서 댓글·추천을 열어둘 수 있고, 일반·영화관 창의 하단 조작부를 접을 수 있습니다. 설정의 yt-dlp·Deno 업데이트는 **직접 실행하는 실험 기능**이며 앱을 다시 시작한 뒤 적용됩니다. Shorts는 광고로 명시된 제공 카드만 거르며, 모든 광고 형태를 찾아낸다고 보장하지 않습니다. 자세한 내용은 [1.1 기능과 한계](docs/FEATURES-1.1.md)에 적었습니다.

라이브의 6 GiB는 **demuxer별 후방 캐시 예산이며 앱 전체 RAM 상한이나 미리 할당하는 양이 아닙니다.** 시청 전에 받은 적 없는 방송 구간을 가져오는 기능도 아닙니다. 로딩 뒤 늦게 라이브로 판별되고 다음 영상 준비를 이미 시도했다면 기존 버퍼 예산을 유지할 수 있으며, LIVE로 다시 열면 라이브 예산을 적용합니다.

### 패킷 캐시의 이론상 설정 예산

아래는 전방·후방 설정값을 더한 값이며 **앱 전체 RAM의 절대 상한이 아닙니다.** 디코딩된 화면이 아니라 압축된 영상·음성 패킷을 보관하는 예산입니다.

| 재생·캐시 단위 | 전방 | 후방 | 설정값 합계 |
| --- | ---: | ---: | ---: |
| 현재 일반 영상 1개(해당 음성 포함) | 32 MiB | 8 MiB | 약 40 MiB |
| 다음 영상 프리로드 1개(해당 음성 포함) | 32 MiB | 8 MiB | 추가 설정 예산 약 40 MiB |
| 현재 일반 영상 + 다음 영상 준비 | — | — | 두 캐시의 설정 예산 합계 약 80 MiB |
| 확인된 라이브 1개(해당 음성 포함) | 32 MiB | 6 GiB = 6,144 MiB | 6,176 MiB = 6.03125 GiB |

**영상·음성 URL이 분리됐다고 FK_YT의 캐시가 보통 두 배가 되는 것은 아닙니다.** 분리 스트림 재생은 하나의 EDL 타임라인으로 합쳐 패킷 캐시를 공유하며, 하위 demuxer가 같은 크기의 캐시를 또 유지하지 않습니다. 공유 예산 안에서 영상과 음성에 고정된 몫을 따로 배정하지도 않습니다. 독립적인 최상위 demuxer를 각각 열 때에만 예산이 각각 적용되므로, 일반적인 라이브 경로를 “영상 6 GiB + 음성 6 GiB”로 계산하지 않습니다. [동봉 mpv의 캐시 설정 설명](https://github.com/mpv-player/mpv/blob/ec38361c56b2d91e417c4c3e58184535d1a1aa2e/DOCS/man/options.rst#L4279-L4324)과 [타임라인 구현](https://github.com/mpv-player/mpv/blob/ec38361c56b2d91e417c4c3e58184535d1a1aa2e/demux/demux_timeline.c#L614-L618)을 기준으로 했습니다.

일반 영상은 남는 전방 공간을 후방으로 나눠줄 수 있어, 8 MiB는 후방 기본 설정이며 개별적인 절대 상한은 아닙니다. 합계 예산은 40 MiB입니다. 재생 전 프리로드에는 이미 재생한 후방 데이터가 거의 없으므로, 80 MiB는 초기 실사용량이 아니라 설정을 보수적으로 합한 값입니다. 라이브로 확인된 재생은 이 공간 공유와 다음 영상 프리로드를 끕니다. mpv 내부 처리상 패킷 예산을 조금 초과할 수도 있습니다.

썸네일 메모리(추정 이미지 데이터 96 MiB), 디코더·GPU 텍스처·UI·자막·네트워크 버퍼·패킷 풀의 부가 메모리는 별도입니다. 예를 들어 표의 패킷 예산과 썸네일 예산만 더하면 **일반 영상+다음 영상 약 176 MiB**, **라이브 1개 약 6.125 GiB**지만, 이 부분 합계로 앱·프로세스 전체 RAM을 제한하는 것은 아닙니다. 썸네일 디스크 캐시 512 MiB는 RAM과 별도이며, 영상 재생 디스크 캐시는 꺼져 있습니다. 버퍼는 미리 전부 할당하지 않고 받은 데이터만큼 늘어납니다.

## 3. 만든 이유

유튜브 좀 오래 틀어놨다고 Firefox가 램을 계속 잡아먹길래 빡쳐서 만들게됨.

FK_YT 코드랑 지금 보고 있는 이 글 전부 AI한테 시킴. 만들라고 했더니 만들어줌 ㅋㅋ

그냥 내가 쓰려고 만든 거라 A/S 없을 수 있음.

위 이야기는 제작자가 자기 PC에서 겪은 일입니다. Firefox 전체의 메모리 누수를 입증했다는 뜻은 아닙니다. AI로 작성했다는 설명은 FK_YT 자체 코드와 문서에 대한 것이며, mpv·yt-dlp 등 외부 프로젝트는 각 프로젝트의 작업입니다. 지속적인 유지보수나 향후 YouTube 변경에 대한 대응은 보장하지 않습니다.

## 4. 계정 및 개인정보

**FK_YT 자체에는 개발자 서버로 이용 기록이나 로그인 정보를 수집·전송하는 기능을 넣지 않았습니다.** 다만 영상을 보려면 외부 서비스와 통신합니다.

| 구분 | 처리 방식 |
| --- | --- |
| 설정·시청 기록·이어보기 | 설치형은 Windows 사용자별 `%LOCALAPPDATA%\FK_YT\data`, 포터블은 압축 해제한 앱 폴더 안의 `data/`에 로컬 저장 |
| 로그인 세션 | Firefox 프로필 또는 Netscape 형식 쿠키 파일에서 연결. 앱에 저장하는 세션은 Windows 사용자 계정에 묶어 DPAPI로 암호화 |
| YouTube 요청 | 목록·재생·댓글 조회 등에 YouTube/Google 및 영상·이미지 CDN 사용. 로그인 기능에는 선택한 세션 사용 |
| YouTube 시청 기록 | 일반 모드에서 기록 반영 설정이 켜져 있으면 실제 재생을 기준으로 계정 기록 전송 시도. 기본 설정은 켜짐 |
| 좋아요·싫어요·추천 피드백 | 사용자가 해당 동작을 선택하면 YouTube 계정에 요청 |
| Return YouTube Dislike | 수치 조회 시 영상 ID로 RYD API 요청. 이 경로에는 앱의 YouTube 로그인 쿠키를 붙이지 않음 |
| 수동 도구 확인·다운로드 | 설정에서 직접 요청했을 때 공식 HTTPS 서비스에 접속. YouTube 쿠키를 붙이지 않음 |
| 빌드 도구 다운로드 | 소스 빌드 시 스크립트가 의존 도구와 NuGet 패키지 제공처에 접속 |

RYD와 YouTube 등 접속한 서비스는 일반 네트워크 요청의 IP 주소 등을 볼 수 있습니다. RYD의 싫어요 수는 추정치입니다.

시크릿 모드에서는 로그인 쿠키를 사용하지 않고 앱의 시청 기록·이어보기를 저장하지 않습니다. YouTube 계정에 기록이나 평가를 쓰는 기능도 사용하지 않습니다. **IP를 숨기는 기능은 아니며, 직접 다운로드한 파일까지 없애주지는 않습니다.**

보안에는 한계가 있습니다. 도구가 읽는 동안 쿠키가 임시 평문 파일로 존재할 수 있고, Firefox 세션을 추출하는 과정에서 원본 쿠키 DB의 임시 복사본이 생길 수 있습니다. 저장된 쿠키의 암호화가 감염된 PC나 같은 사용자 권한의 악성 프로그램까지 막아주는 것은 아닙니다. **YouTube용 Firefox 전용 프로필을 권장합니다.** `data/`, 쿠키 파일, 개인백업 ZIP은 공유하거나 GitHub에 올리지 마세요.

## 5. 실행 환경

- 설치기가 허용하는 최소 조건: **Windows 10 22H2, 빌드 19045 이상, x64(AMD64)**. Windows 11 x64도 대상입니다. x86·ARM64는 설치 대상이 아닙니다. “Windows 10 전체 지원”이라는 뜻은 아닙니다.
- 로컬 작업 환경: 개발 도구가 있는 **Windows 10 Pro 22H2 x64, 19045.6456** PC. **설치·설치 후 실행과 재생·설치기 업데이트·제거는 Windows 10/11에서 검증됐습니다**(제작자 확인, 2026-10-17). 추가 Windows 11 검증의 에디션·빌드와 초기화된 PC·표준 사용자 조건은 기록되지 않았으므로 그 범위까지 검증했다고 표현하지 않습니다.
- 설치 허용 조건과 공급자의 지원 정책은 별개입니다. 테스트한 Windows 10 Pro 22H2를 .NET 10의 공식 지원 환경이라고 주장하지 않습니다. 에디션·수명 주기는 [.NET 10 지원 OS 표](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)를 확인하세요.
- 일반 사용자는 **.NET·Python·Deno·mpv·FFmpeg나 SDK를 따로 설치할 필요가 없습니다.** 실행에 쓰는 런타임·도구는 앱 폴더에 포함되어 있습니다. 설치기는 Windows 기본 64비트 PowerShell을 사용합니다.
- 로그인 연결에는 Firefox 또는 직접 준비한 Netscape 형식 쿠키 파일을 사용합니다. 로그인 없이 검색·일부 영상 재생도 가능합니다.

아래는 기존 런타임 묶음에서 보존한 번들 버전입니다. **1.1 포터블과 설치형은 같은 앱 DLL과 하나의 공통 앱 소스를 사용합니다.** 설치기가 `FK_YT.install-mode`를 추가해 Windows 사용자별 데이터 경로를 선택합니다. 예전 다운로드 대상을 기록한 [의존성 잠금 파일](dependencies.lock.json)만으로 만든 표가 아닙니다. 사용자가 수동 업데이트한 도구 버전은 이 표와 다를 수 있습니다.

| 배포에 포함된 구성 요소 | 버전 / 빌드 | 역할 |
| --- | --- | --- |
| .NET / Windows Desktop Runtime | 둘 다 **10.0.13**, self-contained win-x64 | WPF 앱 실행 |
| Python | **3.13.16**, embedded x64 | 목록·계정 등 작업자 |
| yt-dlp EXE / Python 패키지 | **2026.08.19 / 2026.8.19** | 추출·메타데이터·다운로드 |
| yt-dlp EXE 내부 Python | **3.10.11** | 위 작업자 Python과 별개의 번들 |
| yt-dlp-ejs | **0.8.0** | JavaScript 처리 지원 |
| Deno | **2.9.7** | JavaScript 런타임 |
| mpv | **0.41.0-dev-gec38361c56-fkyt** | FK_YT에서 준비한 빌드; 영상 재생 |
| FFmpeg / ffprobe | **9.0.2**, MSYS2 UCRT64 `9.0.2-1` | GPLv3 활성화 공유 라이브러리 빌드 |
| Microsoft.Data.Sqlite / SQLitePCLRaw | **10.0.13 / 3.0.5** | 로컬 DB |
| 네이티브 SQLite 패키지 | **3.53.4** | Python에 포함된 SQLite와는 별개 |

빌드 전용 도구는 **.NET SDK 10.0.402**, 압축 해제용 **7zr 26.04**, 설치기 제작용 **NSIS 3.13**입니다. SDK·컴파일러·7zr는 사용자용 앱 폴더에 설치하지 않습니다. 설치기는 NSIS **Unicode / Modern UI 2(MUI2), 영어 한 언어, zlib 압축**을 사용합니다. NSIS의 x86 Unicode 실행 엔진이 x64 앱을 설치하는 구조입니다.

## 6. 설치 및 구동

### 일반 사용자: 어떤 파일을 받으면 되나요?

**간단하게 쓰려면 `FK_YT-1.1-portable.zip`을 권장합니다.** 쓰기 가능한 새 폴더에 전부 압축을 풀고 `FK_YT` 안의 `FK_YT.exe`를 실행하면 되며, 이 경우 관리자 권한이 필요 없습니다. **두 방식의 탐색·재생 기능, 앱 DLL과 포함된 실행 도구는 같습니다.** 설치형은 시스템 공용 프로그램 설치·바로가기·Windows 제거 등록과 사용자별 데이터 경로를 선택하는 마커를 추가합니다. 어느 파일을 받든 배포 해시를 확인하세요.

배포 파일과 접근 상태는 [GitHub Releases의 FK_YT 1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1)에서 확인하세요. 공개 릴리스 파일은 로그인 없이 받을 수 있습니다. `Code → Download ZIP`과 `FK_YT-1.1-source.zip`은 실행 패키지가 아닌 개발용 소스입니다.

설치기의 중복 검사를 줄였습니다. 고정 payload 파일은 관리자 전용 보호 사본에서 전체 SHA-256을 한 번 검사하고, 같은 단계 안의 폴더 권한 처리를 재사용합니다. 경로·종류·길이 검사와 업데이트·제거 보호는 유지했습니다. 설치 후 전체 SHA 재검사가 없는 것은 이전부터의 동작입니다. **실제 정식 1.1 설치 시간을 측정하지 않았으므로 단축 초나 개선 비율을 주장하지 않습니다.**

| 구분 | 설치형 EXE | 포터블 ZIP |
| --- | --- | --- |
| 시작 방법 | Windows 관리자 요청 승인 → 영어 안내에 따라 설치 → 설치기를 닫고 시작 메뉴의 **FK_YT (System)** 실행 | 새 폴더에 **전부** 압축 해제 → `FK_YT` 폴더의 `FK_YT.exe` 실행 |
| 기본 위치 | native 64비트 `%ProgramFiles%\FK_YT`, 보통 `C:\Program Files\FK_YT` | 직접 압축을 푼, 쓰기 가능한 폴더 |
| 관리자 권한 | 설치·제거에 필요; 앱은 일반 사용자 권한으로 실행 | 필요 없음; 쓰기 가능한 위치 사용 |
| 바로가기 | 공용 시작 메뉴의 **FK_YT (System)**·**Uninstall FK_YT (System)**, 공용 바탕화면은 선택 사항(기본 꺼짐) | 자동 생성하지 않음 |
| 데이터 | 실행한 Windows 사용자별 `%LOCALAPPDATA%\FK_YT\data` | `<압축 해제한 FK_YT 폴더>\data` |
| 제거 | Windows 앱 제거·**Uninstall FK_YT (System)**·설치된 `Uninstall.exe`; 프로필 데이터 보존 | 앱을 닫고 폴더 삭제; `data`도 지우면 데이터까지 삭제됨 |

폴더 선택 화면은 native 64비트 Program Files 바로 아래의 빈 앱 폴더나 이 설치기가 관리하는 기존 시스템 설치본만 허용합니다. 포터블·예전 사용자별 설치본을 자동 이전하지 않으며 그 위 설치도 거부합니다. 설치 경로는 100자 이내여야 합니다. 관리자 설치기가 앱을 자동 실행하지는 않습니다. `runtime/`, `worker/`, DLL·라이선스 고지·설치형 마커는 그대로 두세요.

**설치 자체는 오프라인으로 가능합니다.** 필요한 런타임을 인터넷에서 추가로 받는 설치기가 아닙니다. YouTube 탐색·스트리밍·댓글·다운로드·RYD 수치 조회에는 인터넷이 필요합니다. 앱의 한국어·영어·일본어 선택은 영어 설치 화면과 별개이며 계정·지역 설정을 바꾸지 않습니다.

### 업데이트와 데이터 보존

- **설치형:** 앱을 종료하고 호환되는 새 시스템 설치기를 관리자 권한으로 실행해 기존 시스템 설치 위치를 선택합니다. 구현은 사용자별 프로필 데이터·추가 파일을 보존하며, 호환되지 않거나 변경되었거나 실행 중인 설치본은 거부할 수 있습니다. 설치기 업데이트는 Windows 10/11에서 검증됐습니다(제작자 확인, 2026-10-17). 별도 실험 기능인 yt-dlp·Deno 업데이트의 검증을 뜻하지는 않습니다. 앱 자동 온라인 업데이트는 없습니다. 별도의 실험적 도구 업데이트는 직접 요청하며 재시작 뒤 적용합니다.
- **포터블:** 앱을 종료하고 기존 폴더를 먼저 보관합니다. 새 ZIP은 별도의 빈 폴더에 풀고, 보관한 `data/`를 새 `FK_YT/` 안에 복사한 뒤 실행하세요.
- **포터블·예전 사용자별 설치본에서 이동:** 자동으로 가져오지 않습니다. 두 앱을 모두 닫고 원본 데이터를 보관한 뒤, 설치를 마치고 원래 Windows 사용자로 **빈** `%LOCALAPPDATA%\FK_YT\data`에 복사합니다. SQLite DB를 병합하거나 이미 사용 중인 데이터 폴더에 덮어쓰지 마세요. 예전 사용자별 앱·바로가기·데이터는 별도로 남습니다.
- 로그인 정보는 Windows 사용자 계정에 묶여 있습니다. 다른 PC·Windows 계정으로 옮기면 로그인 연결을 다시 해야 할 수 있습니다.

### 제거할 때 무엇이 남나요?

Windows **설정 → 앱 → FK_YT → 제거**, 시작 메뉴의 **Uninstall FK_YT (System)** 또는 설치된 `Uninstall.exe`를 실행하고 관리자 요청을 승인합니다. 시스템 제거기는 변경되지 않은 소유 프로그램 파일·공용 바로가기·시스템 등록만 제거하도록 구현했습니다. **모든 사용자의 설정·로그인 정보·시청 기록·이어보기·다운로드는 항상 보존하며, 사용자 데이터 삭제 체크박스는 없습니다.** 다른 사용자의 프로필도 검색하지 않습니다. 직접 추가·변경했거나 잠긴 프로그램 폴더 파일은 남을 수 있습니다. 제거 동작은 Windows 10/11에서 검증됐습니다(제작자 확인, 2026-10-17).

자신의 설치형 데이터도 따로 지우려면 앱을 닫고 필요한 내용을 보관한 뒤 자신의 `%LOCALAPPDATA%\FK_YT\data`를 삭제하세요. 해당 프로필의 저장 로그인·기록도 삭제됩니다. 다른 위치의 다운로드와 예전 포터블·사용자별 폴더의 데이터는 별도입니다. 제거 후 Program Files에 추가·수정 파일이 남으면 먼저 보관하고, 다시 설치할 때는 허용 범위의 빈 폴더를 사용하세요.

### 파일 확인과 다운로드 명령

설치기는 게시자 코드 서명 없이 제공합니다. 실제 배포 다운로드 후의 SmartScreen 경고 유무·문구는 시험하지 않았으므로 특정 경고가 반드시 뜬다고 단정하지 않습니다. [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt)와 [배포 파일 목록](docs/RELEASE-1.1.md#files-and-hashes)을 사용하세요. 예를 들어 PowerShell에서 `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.1-Setup-x64.exe'`를 실행하고 릴리스 해시와 비교합니다. 해시는 파일 일치 확인용이며 코드 서명을 대신하지는 않습니다.

아래 GitHub CLI 명령은 릴리스 페이지에 있는 `v1.1` 파일을 요청합니다. 공개 릴리스 페이지에서 로그인 없이 직접 다운로드해도 됩니다.

```powershell
gh release view v1.1 --repo mobsweeper/FK_YT
gh release download v1.1 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.1-portable.zip' --dir .\FK_YT-download
# 설치형을 원하면 위 다운로드 대신:
gh release download v1.1 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.1-Setup-x64.exe' --dir .\FK_YT-download
```

### 개발자: 저장소 빌드와 최종 배포본은 구분해 주세요

**`FK_YT-1.1-source.zip`은 포터블과 설치형이 함께 사용하는 제품 소스입니다.** 공개 소스에는 테스트 프로젝트·내부 검토 기록·로컬 검증 산출물을 넣지 않았습니다. 일반 오류 처리와 프로세스 정리는 유지했습니다. 설치기는 별도 `FK_YT-1.1-installer-source.zip`을 사용합니다. [빌드 안내](docs/BUILD-1.1.md)와 [소스·빌드 범위](docs/RELEASE-1.1.md#source-and-build-boundaries)를 참고하세요.

저장소 앱을 컴파일하려면 Git, **PowerShell 7**, 인터넷이 필요합니다. Git으로 공개 저장소를 복제할 때는 GitHub 로그인이 필요 없습니다. 일반 사용자는 이 과정을 할 필요가 없습니다.

```powershell
git clone https://github.com/mobsweeper/FK_YT.git
Set-Location FK_YT
git checkout v1.1
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
pwsh -NoProfile -File .\scripts\Build.ps1
```

이 명령은 프로젝트 내부에 SDK를 준비하고 앱을 빌드합니다. 실행용 외부 도구를 전부 준비하는 명령은 아닙니다. `Bootstrap.ps1`의 전체 실행과 `Bootstrap-Ffmpeg.ps1`은 **예전 공급사 mpv·FFmpeg**를 준비하므로 최종 라이선스 정리 런타임 복원용으로 사용하면 안 됩니다. `Publish.ps1`은 준비된 개발용 런타임으로 새 `dist/` 하위 폴더에 self-contained 실행 폴더를 만들지만, 최종 라이선스 정리 패키지·NSIS 설치기·GitHub 릴리스를 만들지는 않습니다. 해시 검증을 끄지 말고 다운로드·검증 실패 원인을 확인하세요.

[배포 검증 범위](docs/RELEASE-1.1.md#verification-scope)는 이전 preview6 검사와 최종 1.1 산출물 검사를 구분합니다. 소스 공개와 컴파일 성공이 실제 재생·계정 호환성·설치·장시간 동작까지 입증하지는 않습니다.

도구 설치 안내: [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) · [Git](https://git-scm.com/install/windows) · [GitHub CLI](https://github.com/cli/cli#installation)

## 7. 기본 사용법

### Firefox에 YouTube 전용 프로필 만들기

평소 쓰는 Firefox 프로필을 그대로 연결하기보다 **YouTube에만 로그인한 새 프로필**을 쓰는 것을 권장합니다.

1. Firefox 주소창에 `about:profiles`를 입력합니다.
2. **새 프로필 만들기 / Create a New Profile**를 선택하고 이름을 `FK_YT`처럼 알아보기 쉽게 정합니다. 특별한 이유가 없다면 폴더는 기본 위치를 사용합니다.
3. 새 프로필을 만든 뒤, 평소 프로필이 계속 기본값인지 확인합니다. 필요하면 기존 프로필에서 **기본 프로필로 설정**을 누릅니다.
4. 새 프로필의 **새 브라우저에서 프로필 실행 / Launch profile in new browser**를 눌러 엽니다.
5. 그 창에서 YouTube에 로그인합니다. 전용 프로필에는 다른 사이트 로그인을 추가하거나 기존 프로필 데이터를 가져오지 않는 편이 목적에 맞습니다. Mozilla 계정 동기화는 필수가 아닙니다.
6. FK_YT의 **설정 · 로그인**에서 해당 프로필을 고르고 **Firefox 세션 연결 / 갱신**을 누릅니다.
7. 목록에 없다면 **프로필 폴더 선택**을 누릅니다. `about:profiles`에 표시된 새 프로필의 **루트 디렉터리**를 선택하세요. 캐시용 로컬 디렉터리와 다릅니다.

프로필 관리 기능의 자세한 설명은 [Mozilla 공식 안내](https://support.mozilla.org/en-US/kb/profile-manager-create-remove-switch-firefox-profiles)를 참고하세요. 로그인 연결이 만료되면 전용 프로필에서 다시 로그인한 뒤 FK_YT에서 갱신합니다.

### 탐색·재생

- 검색창에 검색어 또는 YouTube 영상·채널·재생목록 URL을 넣습니다. 로그인하면 홈 추천·구독 등 계정 기능을 사용할 수 있습니다.
- 왼쪽 **YouTube 채널**에서 같은 Google 계정에 속한 채널을 전환합니다. UI 언어 선택은 YouTube의 지역 설정과 별개입니다.
- 영상 카드나 재생목록을 열어 재생합니다. 채널 이름·아이콘을 누르면 채널 영상 목록으로 이동합니다. 정렬과 홈 분류는 서버가 제공하는 항목만 표시합니다.
- 기본 화질은 **최고 화질 (자동)**입니다. 숫자 화질은 해당 해상도 이하로 제한합니다. 영상에 그 해상도가 없으면 제한 이하에서 사용 가능한 화질을 선택합니다.
- **자막 ▾**에서 끄기·제작자 자막·자동 생성 및 번역 항목을 선택합니다. 지원 언어는 해당 영상이 제공하는 목록에 따르며 영어·한국어·일본어를 우선 표시합니다.
- **댓글 보기**를 눌러야 댓글을 조회합니다. 인기순·최신순을 선택할 수 있습니다.
- 다음 영상 자동재생을 켜면 목록의 다음 항목으로 넘어갑니다. 다음 영상 준비에 실패하면 일반 로딩으로 이어질 수 있습니다.
- **팝아웃**으로 별도 창을 열고 **복귀**로 돌려놓습니다. 영화관 모드에서도 댓글·추천을 열어둘 수 있습니다. 일반·영화관 창의 하단 조작부는 접기·복원이 가능하고, 선택은 현재 앱 세션 동안 유지됩니다. 전체화면·팝아웃에서는 유휴 시 조작부가 숨겨집니다.
- **공유** 또는 영상 카드 우클릭으로 영상 링크를 복사합니다. **다운로드**는 사용 권한이 있는 콘텐츠에 사용하세요.
- 로컬 이어보기와 YouTube 계정의 기록은 별개입니다. 기록 전송이 성공해도 정확한 재생 위치 동기화는 보장하지 않으며, YouTube에는 완주로 표시될 수 있습니다.

### 라이브 재생과 도구 업데이트

- 라이브 탐색은 끊긴 구간을 포함해 mpv가 확인한 로컬 캐시 범위만 사용합니다. **LIVE**는 방송 현재 지점을 다시 열고 사용자가 선택한 일시정지를 유지합니다. 서버 DVR 전 구간을 탐색하는 기능은 아닙니다.
- 라이브 연결 끊김은 2·5·10초 뒤 최대 세 번 재연결할 수 있습니다. 인증·접근·요청 제한 오류는 재시도하지 않으며, 방송 종료를 확인해야 다음 영상을 자동재생합니다. 수동 일시정지·정지·영상 또는 계정 변경·시크릿 전환은 복구를 취소합니다.
- 게스트·기본 로그인 슬롯은 기존 mpv 경로를 사용합니다. 다른 Google 로그인 슬롯은 오디오·비디오 결합을 검증한 단일 HTTPS HLS 스트림이 필요합니다. 지원하지 않는 선택 계정 전송 방식은 다른 계정으로 바꾸지 않고 오류를 표시합니다.
- 설정의 도구 확인·준비·롤백·번들 복원은 직접 실행하는 실험 기능이며 **다음 앱 시작**에 적용됩니다. 재생이 잘 되면 번들 도구 유지를 권장합니다. 완료한 과거 버전은 보관하며 자동 정리·전체 용량 상한은 없습니다. [업데이트 한계](docs/FEATURES-1.1.md#manual-experimental-tool-updates)를 참고하세요.

[한국어 1.1 보충 안내](docs/guides/사용설명서-1.1.txt) · [English supplement](docs/guides/User-guide-1.1.txt) · [日本語補足ガイド](docs/guides/ご利用ガイド-1.1.txt)

## 8. 프로젝트 구조

```text
README.md              메인 화면의 영문·한국어 README
readme_KR.md            한국어 안내
readme_EN.md            English guide
assets/screenshots/    계정 정보를 가린 README 스크린샷
docs/RELEASE-1.1.md     배포 파일·해시·대응 소스·검증 범위
docs/FEATURES-1.1.md    1.1 기능·알려진 한계
docs/BUILD-1.1.md       컴파일·Publish·최종 배포 조립 구분
src/                    C# WPF 앱, 플레이어 제어, 계정·저장 서비스
worker/                 제품 Python 작업자
scripts/                개발용 의존 도구 준비·빌드·로컬 Publish
dependencies.lock.json  개발 도구와 과거 공급사 런타임의 잠금 정보
LICENSE / license.md    MIT 라이선스·배포 안내
```

`tools/`, `runtime/`, `dist/`, `data/`는 로컬 빌드·실행 과정에서 생기는 폴더입니다. 설치기 소스는 별도 `FK_YT-1.1-installer-source.zip`으로 준비되어 있으며, 이 저장소에는 없습니다. 사용자 데이터나 개인백업은 배포 파일에 포함하지 않습니다.

## 9. 제한 사항

- **인증:** 쿠키가 만료되거나 추가 인증이 요구되면 다시 연결해야 합니다. 연령·멤버십·지역 등 접근 제한을 우회하는 기능은 제공하지 않습니다. 일반 콘텐츠 경고 처리가 로그인이나 성인 인증을 대신하지 않습니다.
- **YouTube 변경:** 비공식 추출 경로에 의존하는 부분이 있어 사이트 변경으로 목록·재생·댓글·기록 기능이 고장 날 수 있습니다.
- **자막:** 언어 목록을 얻었더라도 실제 자막 요청이 429, 만료, 제공 중단 등으로 실패할 수 있습니다. 모든 자동번역 언어의 로딩을 보장하지 않습니다.
- **화질·재생:** 계정과 서버가 제공하는 포맷, GPU와 코덱 지원, 네트워크에 따라 달라집니다. 자동재생에도 버퍼링이 생길 수 있습니다.
- **계정 동작:** 추천 변화, 시청 기록 반영 시점, 다른 기기의 이어보기 위치는 보장하지 않습니다. RYD 추정치가 YouTube 내부 수치와 같다는 보장도 없습니다.
- **라이브:** 로컬 캐시는 서버 DVR이 아니며, 라이브 채팅·예약 방송 대기·녹화는 포함하지 않습니다. 게스트·기본 슬롯의 직접 라이브 URL은 로딩 뒤 판별될 수 있어, 최초 EOF와 예전 VOD 이어보기 값의 처리는 별도 실제 라이브 검증이 필요합니다.
- **도구 업데이트:** 오프라인 검사만으로 향후 모든 upstream 버전의 호환성이나 Deno 바이너리 전체의 제3자 라이선스 목록이 입증되지는 않습니다. 게시자의 HTTPS 해시는 독립적인 서명 보증이 아닙니다. [전체 한계](docs/FEATURES-1.1.md)를 확인하세요.
- **지원:** Windows 10 22H2 외 환경과 모든 DPI·GPU 조합을 검증한 것은 아닙니다. 지속적인 업데이트나 문제 해결을 보장하지 않습니다.

문제를 제보할 때는 앱 버전, Windows 버전, 시크릿 모드 여부, 재현 순서와 화면에 나온 오류를 알려주세요. 영상 URL은 공유 가능한 경우에만 포함하고, 쿠키·인증 토큰·`data/`·개인백업은 첨부하지 마세요.

## 10. YouTube 이용약관과 계정 위험

FK_YT는 YouTube/Google의 공식 앱이 아니며 승인·제휴 관계가 없습니다. 자동화된 조회, 비공식 접근, 다운로드 등은 사용 방식에 따라 [YouTube 이용약관](https://www.youtube.com/static?template=terms&hl=en)과 충돌할 수 있습니다.

불필요한 요청을 줄이도록 설계했지만, YouTube의 승인이나 약관 준수를 보장하는 프로그램은 아닙니다. 요청 제한·기능 차단·계정 제재의 발생 가능성과 확률은 확인할 수 없습니다. 요청 수가 적다는 이유로 **“제재 위험이 상당히 낮다”라고 입증된 것은 아닙니다.**

사용 전 약관과 콘텐츠 이용 권한을 확인하세요. Firefox 전용 프로필은 쿠키 노출 범위를 줄이기 위한 것으로, 계정 제재를 막아주는 장치가 아닙니다.

## 11. 라이선스와 외부 프로젝트

**FK_YT 자체 코드와 문서는 [MIT License](LICENSE)로 배포합니다.** 배포 요약은 [license.md](license.md)를 참고하세요. 외부 프로젝트에는 각 프로젝트의 라이선스가 적용됩니다.

근데 제작자는 그게 뭔지 모를 거 같습니다.

아래 경로는 **설치 또는 압축 해제한 앱 폴더 기준**입니다. 전체 구성 요소·저작권·라이선스 전문은 `THIRD-PARTY-NOTICES.txt`, `notices/`, 각 런타임 폴더에 보존했습니다. 이 표는 그 원문을 대신하지 않습니다.

| 실제 포함 구성 요소 | 적용 라이선스 요약 / 고지 위치 |
| --- | --- |
| .NET / WPF, Microsoft.Data.Sqlite | MIT 및 포함 구성 요소·Microsoft 배포 조건; `notices/dotnet/`, `notices/nuget/` |
| SQLitePCLRaw / SQLite | Apache-2.0 / public domain; `notices/nuget/` |
| Python | PSF 및 역사적·포함 라이브러리 조건; `runtime/python/LICENSE.txt`, `notices/python/` |
| yt-dlp Python 패키지 / EXE | 패키지는 Unlicense, PyInstaller EXE 결합 배포물은 GPLv3-or-later; `runtime/yt-dlp/`의 `LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, `notices/` |
| yt-dlp-ejs / Meriyah / astring | Unlicense / ISC / MIT; `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/`의 원문 고지 |
| Deno와 포함 구성 요소 | MIT 및 구성 요소별 조건; `runtime/deno/LICENSE.md`, `notices/deno/`. MPL 대상 소스는 `notices/deno/third-party/sources/mpl/`에 포함 |
| mpv / FFmpeg와 연결 라이브러리 | GPLv2-or-later mpv 코어와 GPLv3 활성화 FFmpeg를 결합한 배포는 GPLv3-or-later 및 라이브러리별 조건; `notices/media/`의 전문·빌드/소스 인덱스 |
| NSIS / MUI2 / zlib / 플러그인 | 주로 zlib/libpng; 실제 사용한 `System`, `nsExec`, `nsDialogs`와 SystemCall의 MIT 예외 원문 포함. **설치형에 추가된** `notices/installer/nsis/` |

yt-dlp 소스와 번들 EXE의 구분은 [프로젝트의 라이선스 설명](https://github.com/yt-dlp/yt-dlp#licensing), NSIS 구성 요소별 조건은 [NSIS 라이선스 안내](https://nsis.sourceforge.io/License)에서도 확인할 수 있습니다. 7zr는 빌드 전용이며 사용자용 배포본에는 없습니다. RYD는 외부 서비스이며 서버 소프트웨어를 번들로 배포하는 것이 아닙니다.

최종 mpv는 원본 커밋에 빌드 식별용 버전 메타데이터 변경을 적용해 FK_YT에서 만든 바이너리입니다. FFmpeg는 MSYS2 UCRT64 빌드와 해당 의존 라이브러리 묶음입니다. 예전 Bootstrap의 shinchiro/Gyan 바이너리와 같다고 표시하지 않습니다. 원본과 수정 부분의 고지·빌드 지침을 함께 보존했습니다.

대응 소스는 **FK_YT에서 준비한 모음**인 `FK_YT_yt-dlp.zip`, `FK_YT_mpv-FFmpeg-part1.zip`, `FK_YT_mpv-FFmpeg-part2.zip`으로 제공합니다. 각 프로젝트가 직접 배포한 공식 ZIP이라는 뜻은 아닙니다. 미디어 두 파일은 분할 압축 볼륨이 아닌 **독립 ZIP**이므로 둘 다 받아 **같은 폴더에 part1 → part2 순서로** 풉니다. 앱 실행에는 이 소스 ZIP이 필요하지 않습니다.

1.1 바이너리와 대응 소스의 파일 목록·접근 상태는 같은 [v1.1 릴리스](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1)에서 확인할 수 있습니다. 별도 소스 접근 비용을 받지 않습니다. 재배포할 때도 실행 파일과 대응 소스를 함께 접근할 수 있게 유지해야 합니다. [배포 안내](docs/RELEASE-1.1.md)에 공통 앱 소스·별도 설치기 소스·해시를 적었습니다. [라이선스 점검 기록](docs/LICENSING-1.1.md), [소스 접근·파일 대응표](docs/SOURCE-ACCESS-1.1.txt), [추가 라이선스 고지](docs/LICENSES-SUPPLEMENT-1.1.txt)에서 구성 요소별 조건·압축 파일 대응 관계·남은 한계를 안내하며, 원래 법적 고지는 보존합니다. 두 텍스트 고지는 릴리스에도 첨부합니다.

소스 모음의 배포자 메타데이터·깨진 링크를 복구하고 출처를 기록한 법적 고지 파일 6개를 추가했습니다. native Windows 빌드에 쓰이지 않는 macOS 전용 crate ZIP 4개는 제외했으며, 플랫폼 공통 upstream 잠금 파일은 그대로 보존합니다. 복구 근거가 없는 과거 URL은 추측하지 않고 미확보로 명시했습니다. 유지한 upstream 소스·법적 고지와 앱 코드·빌드 입력이 바뀌지 않았는지도 대조했습니다.

**전체 외부 바이너리의 동일 바이트 재빌드와 공급사의 정확한 과거 librsvg 잠금 파일 복원은 완료하지 않았습니다.** 대응 소스 파일의 접근 상태는 릴리스 페이지에서 확인하세요. simd_helpers 고지는 upstream의 후속 고지임을 표시했으며 원래 crate에 들어 있던 파일이라고 주장하지 않습니다. 정확한 범위와 남은 한계는 라이선스 점검 기록과 배포 안내에 구분했으며, 이 점검이 법적 적합성 보증을 뜻하지는 않습니다.

## 12. 기타

| 항목 | 내용 |
| --- | --- |
| 코드 작성 | AI한테 시킴 |
| 이 글 작성 | 이것도 AI한테 시킴 |
| 에이전트에게 디버깅받은 횟수 (추정) | **약 195회** |
| 버그 0개 인증 | 그런 건 없음 |

2026-10-17에 확인한 병렬 검토 **13라운드 × 15개 검토 작업** 기준. 같은 에이전트에게 다시 맡긴 것도 포함했고, 메인의 자체 점검·외부 단독 리뷰·개별 추가 질문은 제외했습니다. 서로 다른 AI 195종이라는 뜻도, 버그 195개를 고쳤다는 뜻도 아님 ㅋㅋ

*정적 코드 검사만 집계한 횟수입니다. 실제 실행·재생 등을 확인한 동적 검사는 집계 불가.*
