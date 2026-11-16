# FK_YT 1.3

**[한국어 안내](readme_KR.md)** · [English](README.md)

A personal YouTube viewer for Windows. Lists and a player, without keeping the YouTube webpage running.

**Watch, queue, and keep going.** Playback controls, quality, subtitles, and the next videos share one window. Pop the player out when you need more room.

![FK_YT playing a video, with playback controls and the next-video queue](assets/screenshots/playback-en.png)

| Home | Incognito mode | Settings and sign-in |
| :---: | :---: | :---: |
| [![FK_YT home screen while recommendations load](assets/screenshots/home-en.png)](assets/screenshots/home-en.png) | [![FK_YT incognito start screen](assets/screenshots/incognito-en.png)](assets/screenshots/incognito-en.png) | [![FK_YT settings and sign-in screen](assets/screenshots/settings-en.png)](assets/screenshots/settings-en.png) |
| Home screen while recommendations load. | Browse without using sign-in cookies or saving watch history and resume positions. | Connect a session and choose how watch history and members-only videos are handled. |

*Account details and selected thumbnails are blurred. Videos and artwork shown belong to their respective rights holders.*

For 1.3 installation and source access, see the [release guide](docs/RELEASE-1.3.md). For changes since 1.1.2, see the [1.3 patch notes](#13-13-patch-notes).

<!-- PUBLICATION-STATUS-BEGIN -->
> **[FK_YT v1.3 on GitHub Releases](https://github.com/mobsweeper/FK_YT/releases/tag/v1.3)**
>
> [Portable ZIP](https://github.com/mobsweeper/FK_YT/releases/download/v1.3/FK_YT-1.3-portable.zip) · [Windows x64 installer](https://github.com/mobsweeper/FK_YT/releases/download/v1.3/FK_YT-1.3-Setup-x64.exe) · [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.3/SHA256SUMS.txt)
<!-- PUBLICATION-STATUS-END -->

## 1. Language

[English](README.md) · [한국어](readme_KR.md)

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

FK_YT distinguishes live playback, supports seeking within confirmed local live-cache ranges, and includes a LIVE control. Theater mode can keep comments/recommendations open, and normal/theater playback controls can be collapsed. Settings offers **manual, experimental** yt-dlp/Deno updates that take effect after restart. Shorts filtering removes explicitly marked served-ad cards. See the [1.1 feature guide](docs/FEATURES-1.1.md) for the original introduction of these features.

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
| YouTube watch history | Sending is off by default. To enable it for normal playback, use **Settings → History → Send watch history to my YouTube account** |
| Likes, dislikes, recommendation feedback | Requests sent to YouTube when you choose the corresponding action |
| Return YouTube Dislike | Count queries send the video ID to the RYD API. This request path does not attach the app's YouTube sign-in cookies |
| Manual tool checks/downloads | Only when requested in Settings; official HTTPS services, without YouTube cookies |
| Build downloads | Source-build scripts contact dependency hosts and NuGet package providers |

Services you contact, including YouTube and RYD, can see ordinary connection information such as your IP address. RYD dislike counts are estimates.

Incognito mode does not use sign-in cookies or save app watch history/resume positions. It also disables account history and rating writes. **It does not hide your IP address or erase files you deliberately download.**

There are security limits. Tools may need temporary plaintext cookie files, and Firefox session extraction can create a temporary copy of the original cookie database. Encrypting stored cookies does not protect against every malicious program running as you or an already compromised PC. **Use a dedicated Firefox profile for YouTube.** Do not share or upload `data/`, cookie exports, or personal-backup ZIPs.

## 5. Requirements and versions

- Installer acceptance floor: **Windows 10 22H2, build 19045 or later, x64 (AMD64)**. Windows 11 x64 is also a target. x86 and ARM64 are not accepted. This does not mean every Windows 10 version is supported.
- The maintainer confirmed installation, installed-app launch/playback, installer updates and removal for **version 1.1** on Windows 10 and Windows 11 on 2026-10-17. The Windows 10 development PC ran **Windows 10 Pro 22H2 x64, build 19045.6456**. See the [1.1 verification record](docs/RELEASE-1.1.md#verification-scope).
- Installer acceptance is separate from vendor support. The tested Windows 10 Pro 22H2 system is not presented as an officially supported .NET 10 environment. Check editions and lifecycle status in the [.NET 10 supported OS table](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
- Users **do not need to install .NET, Python, Deno, mpv, FFmpeg, or an SDK separately**. The required runtimes and tools are included in the app folder. Setup uses Windows' built-in 64-bit PowerShell.
- Account connection uses Firefox or a Netscape-format cookie file you supply. Search and some playback also work without signing in.

**The 1.3 portable and installed versions use the same app DLL and the same application source.** The installer adds `FK_YT.install-mode` to select each Windows user's data directory. The table lists the tools shipped with 1.3. Manually selected tool updates can differ from this table.

| Bundled component | Version / build | Purpose |
| --- | --- | --- |
| .NET / Windows Desktop Runtime | Both **10.0.13**, self-contained win-x64 | Run the WPF app |
| Python | **3.13.16**, embedded x64 | Feed/account workers |
| yt-dlp EXE / Python package | **2026.10.26** | Extraction, metadata, downloads |
| Python inside yt-dlp EXE | **3.14.10** | Separate from the worker's Python |
| yt-dlp-ejs | **0.8.0** | JavaScript support |
| Deno | **2.9.7** | JavaScript runtime |
| mpv | **0.41.0-dev-gec38361c56-fkyt** | Playback; build prepared by FK_YT |
| FFmpeg / ffprobe | **9.0.2**, MSYS2 UCRT64 `9.0.2-1` | GPLv3-enabled shared-library build |
| Microsoft.Data.Sqlite / SQLitePCLRaw | **10.0.13 / 3.0.5** | Local database |
| Native SQLite package | **3.53.4** | Separate from Python's included SQLite |

Build-only tools are **.NET SDK 10.0.402**, **7zr 26.04** for extraction, and **NSIS 3.13** for installer creation. The SDK, compiler, and 7zr are not installed into the application folder. Setup uses NSIS **Unicode / Modern UI 2 (MUI2), English only, and zlib compression**. The NSIS x86 Unicode engine installs the x64 app payload.

## 6. Installation and launch

### For users: which file should I get?

**We recommend `FK_YT-1.3-portable.zip` for a straightforward setup.** Extract the whole archive into a new writable folder and run `FK_YT.exe` inside `FK_YT` with your normal Windows permissions. **Both packages provide the same viewing/playback features, app DLL, and bundled runtime tools.** The installer adds machine-wide program installation, shared shortcuts, Windows removal registration, and the marker that selects per-user data. Check the release checksum for either download.

Public release downloads are available without a GitHub sign-in. Choose the portable ZIP or Setup EXE to run the app. The source ZIP is for developers. [FK_YT 1.3 on GitHub Releases](https://github.com/mobsweeper/FK_YT/releases/tag/v1.3)

Setup checks each fixed payload file once with SHA-256 in protected staging, reuses directory-permission checks within each phase, and checks file paths, types and lengths during installation.

| Detail | Installer EXE | Portable ZIP |
| --- | --- | --- |
| First launch | Approve Windows' administrator request → follow Setup's English pages → close Setup → open **FK_YT (System)** from Start Menu | Extract the **whole** ZIP into a new folder → run `FK_YT.exe` inside `FK_YT` |
| Default location | Native 64-bit `%ProgramFiles%\FK_YT`, normally `C:\Program Files\FK_YT` | A writable folder you choose |
| Administrator rights | Required for installation and removal; the app runs with normal user permissions | Not required in a writable location |
| Shortcuts | Shared Start Menu: **FK_YT (System)** and **Uninstall FK_YT (System)**; optional shared Desktop shortcut, off by default | None created automatically |
| Data | Each running Windows user's `%LOCALAPPDATA%\FK_YT\data` | `<extracted FK_YT folder>\data` |
| Removal | Windows app removal, **Uninstall FK_YT (System)**, or installed `Uninstall.exe`. Profile data is preserved | Choose **Exit** from the tray icon, then delete the folder. Deleting `data` also deletes your app data |

The directory page accepts an empty app folder directly inside native 64-bit Program Files, or an existing machine installation it owns. It rejects portable folders and earlier per-user installations rather than migrating them. The installation root must be at most 100 characters long. Setup does not automatically launch the app with elevated permissions. Keep `runtime/`, `worker/`, DLLs, license notices, and the installed-mode marker intact.

**Installation itself works offline.** Setup does not download additional runtimes. YouTube browsing, streaming, comments, downloads, and RYD count queries need internet access. The app's English/Korean/Japanese selection is separate from the English installer and does not change account or region settings.

### Updates and data

- **Installed version.** Fully exit FK_YT before running a compatible newer machine installer as administrator. Select the existing machine installation. Setup preserves per-user profile data and user-added files. Incompatible, modified, or running installations can be refused. The maintainer confirmed installer updates for version 1.1 on Windows 10 and Windows 11 on 2026-10-17. The experimental yt-dlp/Deno updater is manual and applies after restart.
- **Portable version.** Fully exit the app and keep the old folder as a backup. Extract the new ZIP into a separate empty folder, copy your preserved `data/` into the new `FK_YT/` folder, then launch it.
- **Moving from portable or an earlier per-user install:** Nothing is imported automatically. Close both apps, keep the original data, and copy it as the same Windows user into an **empty** `%LOCALAPPDATA%\FK_YT\data` after installation. Do not merge SQLite databases or overwrite a data folder already in use. Existing per-user apps, shortcuts, and data are left separate.
- Sign-in data is tied to the Windows user. Moving to a different PC or Windows account may require reconnecting the session.

### What happens when I uninstall?

Fully exit FK_YT, then use Windows **Settings → Apps → FK_YT → Uninstall**, **Uninstall FK_YT (System)** in Start Menu, or installed `Uninstall.exe`, and approve the administrator request. The machine uninstaller is designed to remove unchanged owned program files, shared shortcuts, and machine registration. **It preserves every user's settings, sign-in information, watch history, resume positions, and downloads.** Added, modified, or locked program-folder files may remain. The maintainer confirmed removal for version 1.1 on Windows 10 and Windows 11 on 2026-10-17.

To delete your own installed-app data separately, fully exit FK_YT and remove your `%LOCALAPPDATA%\FK_YT\data` only after keeping anything you need. This also removes that profile's saved sign-in and history. Downloads saved elsewhere and data in earlier portable/per-user folders are separate. If modified or added files remain in Program Files after removal, preserve them and use an empty supported folder for reinstalling.

### File verification and download commands

The installer is supplied without a publisher code signature. Run `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.3-Setup-x64.exe'` in PowerShell and compare the result with the listed checksum for that file. [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.3/SHA256SUMS.txt) · [Download list](docs/RELEASE-1.3.md#release-files)

These GitHub CLI commands request the `v1.3` assets listed on the release page.

```powershell
gh release view v1.3 --repo mobsweeper/FK_YT
gh release download v1.3 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.3-portable.zip' --dir .\FK_YT-download
# For the installer, use this instead of the portable download:
gh release download v1.3 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.3-Setup-x64.exe' --dir .\FK_YT-download
```

### For developers: repository builds and the final release differ

**`FK_YT-1.3-source.zip` contains the common product source for both portable and installed apps.** The installer has its own `FK_YT-1.3-installer-source.zip`. [Source and build](docs/RELEASE-1.3.md#source-and-build)

Compiling the repository requires Git, **PowerShell 7**, and internet access. Cloning this public repository with Git does not require a GitHub sign-in. Ordinary users do not need to build anything.

```powershell
git clone https://github.com/mobsweeper/FK_YT.git
Set-Location FK_YT
git checkout v1.3
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
pwsh -NoProfile -File .\scripts\Build.ps1
```

This prepares a project-local SDK and builds the app; it does not prepare all external playback tools. Full `Bootstrap.ps1` and `Bootstrap-Ffmpeg.ps1` prepare **historical supplier mpv/FFmpeg builds**, so they must not be used to restore the final licensed runtime. `Publish.ps1` packages a prepared development runtime into a new self-contained directory beneath `dist/`; it does not create the final licensed package, an NSIS installer, or a GitHub release. Preserve locked hashes and investigate failures instead of disabling verification.

Tool setup: [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) · [Git](https://git-scm.com/install/windows) · [GitHub CLI](https://github.com/cli/cli#installation)

## 7. Basic usage

### Create a dedicated Firefox profile

Prefer a **separate profile signed in only to YouTube** over your everyday browsing profile.

1. Enter `about:profiles` in Firefox's address bar.
2. Choose **Create a New Profile** and give it a recognizable name, such as `FK_YT`. Keep the default folder unless you have a reason to change it.
3. After creating it, check that your everyday profile is still the default. Use **Set as default profile** on the old profile if necessary.
4. Under the new profile, choose **Launch profile in new browser**.
5. Sign in to YouTube in that window. Avoid adding unrelated site logins or importing your everyday profile's data into it. Mozilla account synchronization is not required.
6. In FK_YT, open **Settings → Account**, select the profile, and connect or refresh the Firefox session.
7. If the profile is missing from the list, use **Choose profile folder**. Select the profile's **Root Directory** from `about:profiles`, not the Local Directory used for cache.

See [Mozilla's profile-management guide](https://support.mozilla.org/en-US/kb/profile-manager-create-remove-switch-firefox-profiles) for details. If the session expires, sign in again in that dedicated profile and refresh the connection in FK_YT.

### Browse and play

- Enter a search term or a YouTube video, channel, or playlist URL. Signing in enables account features such as personalized Home and subscriptions.
- Use **YouTube channel** in the sidebar to switch between channels belonging to the same Google account. Interface language is separate from YouTube's region setting.
- Open a video or playlist to play it. Click a channel's name or avatar to browse its videos. Channel sorting and Home categories only show choices returned by the server.
- The default quality is **Best quality (auto)**. A numeric choice sets a resolution ceiling. If a video lacks that resolution, playback selects an available quality at or below the ceiling.
- Open **Subtitles ▾** to turn subtitles off or select creator-provided, automatically generated, or translated captions. Available languages depend on the video; English, Korean, and Japanese are prioritized in the list.
- Click **Comments** to fetch them. Choose top or newest order.
- Right-click a video card and choose **Play next** or **Add to queue** to build your own queue. Enable next-video autoplay to keep playing through it.
- **Pop out** lets playback continue in its own window while you browse in the main window. Use **Return** to bring it back. Theater mode can keep comments/recommendations open. You can collapse and restore normal/theater controls. Fullscreen and pop-out controls hide when idle.
- The main window's **X** hides FK_YT in the Windows notification area. Use the tray icon to restore the window, play or pause, skip to the next video, or **Exit** the app before updating, backing up, or removing it.
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
docs/RELEASE-1.3.md     Release files, hashes, installation, and source access
docs/RELEASE-1.1.2.md   Previous release record
docs/BUILD-1.1.2.md     Previous build and release assembly instructions
src/                    C# WPF app, player, account and storage services
worker/                 Product Python workers
scripts/                Development bootstrap, build, and local Publish
dependencies.lock.json  Development tools and historical supplier runtime pins
LICENSE / license.md    MIT license and distribution guidance
```

`tools/`, `runtime/`, `dist/`, and `data/` are local build/runtime folders. The installer source is available in `FK_YT-1.3-installer-source.zip`. User data and personal backups are excluded from release files.

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

The 1.3 app source ZIP contains the corresponding-source collection for the bundled yt-dlp 2026.10.26. The bundled mpv and FFmpeg use the two FK_YT-prepared media collections from v1.1.2. Download both **independent ZIPs** and extract part1, then part2, into the **same folder**. [1.3 app source](https://github.com/mobsweeper/FK_YT/releases/download/v1.3/FK_YT-1.3-source.zip) · [Media part 1](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.2/FK_YT_mpv-FFmpeg-part1.zip) · [Media part 2](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.2/FK_YT_mpv-FFmpeg-part2.zip)

The 1.3 distribution guide lists the app and installer source, checksums, and source for the bundled tools. Source downloads are available without an additional fee. Keep matching source accessible alongside the binaries when redistributing. The v1.1.2 source map records the two retained media ZIPs and their checksums. [1.3 distribution guide](docs/RELEASE-1.3.md#corresponding-source) · [v1.1.2 source map](docs/SOURCE-ACCESS-1.1.2.txt) · [Supplemental notices](docs/LICENSES-SUPPLEMENT-1.1.txt)

For the earlier media-source publication, distributor metadata and broken links were repaired, and six legal-notice files were added with their provenance recorded. Four macOS-only crate archives unused by the native Windows build were excluded; upstream cross-platform locks remain intact. Unrecoverable historical URL fields are marked unknown. See the [original licensing record](docs/LICENSING-1.1.md).

**A complete bit-for-bit rebuild of all external binaries and recovery of the exact historical supplier librsvg lock are not complete.** Use the release page to check availability of the matching source assets. The simd_helpers notice is a labelled upstream follow-up, not a claim that its original crate contained that file. See the licensing record and distribution guide for the precise scope and remaining limits; these checks are not a legal certification.

## 12. Miscellaneous

| Item | Details |
| --- | --- |
| Writing the code | Delegated to AI |
| Writing this README | Also delegated to AI |
| Estimated debugging reviews delegated to agents | **About 330** |
| Certified bug-free | Absolutely not |

Based on **22 completed parallel review rounds × 15 review assignments**, completed through October 19 and checked on October 20, 2026. Nine additional rounds (135 assignments) were added to the previous estimate of 195. Repeat assignments to the same agents count; retries of failed review requests, the main agent's own checks, separate external reviews, and individual follow-up questions do not. That's not 330 different AI models, and definitely not a claim of 330 bugs fixed.

*This counts static code reviews only. Dynamic checks, including actual app execution and playback, could not be tallied.*

## 13. 1.3 patch notes

- Playback verifies the selected YouTube account and can recover using a compatible HLS stream for that account.
- Improved original-audio selection for playback and next-video preparation.
- Added a custom queue and Windows tray controls for play/pause, next video, restore, and exit.
- Browse in the main window while the pop-out player keeps playing.
- Improved autoplay and queue handling when a video ends just after loading.
- The sidebar adapts to the window width with a compact channel menu. Connecting an account keeps its full channel list, and the account-status tooltip works while its button is disabled.
- Updated bundled yt-dlp to **2026.10.26**. The standalone executable embeds Python **3.14.10**.
