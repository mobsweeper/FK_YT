# FK_YT 1.0

A personal YouTube viewer for Windows. Lists and a player, without keeping the YouTube webpage running.

> **README draft · October 16, 2026**
> This repository currently contains source only. An NSIS installer EXE and a portable ZIP are planned; no release has been published yet. Installer filenames, the NSIS version, and installation/uninstallation behavior will be documented when packaging is finalized.

## 1. Language

[English](readme_EN.md) · [한국어](readme_KR.md)

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
| mpv buffering options | `cache-secs=15`, `demuxer-max-bytes=32MiB`, `demuxer-max-back-bytes=8MiB` |
| Next-video preparation | One autoplay candidate, attempted when 60 seconds or less remain |
| Comments | Up to 100, fetched when you click the comments button |
| Quality/full subtitle catalogs | Requested when you open the menu; reuse available results |

**These are individual cache and request settings, not a cap on total application RAM, GPU memory, or CPU use.** Actual usage varies with resolution, codecs, decoding, media duration, and your system. This is not a promise to beat Firefox in every workload or deliver perfectly gapless autoplay.

## 3. Why this exists

I wanted to watch YouTube, not pay rent in RAM. Leaving it running in Firefox on my PC kept pushing memory usage up, and restarting the browser got old. So I had this made.

FK_YT's code and the words you're reading were all handed off to AI. I said "make it" and it did.

I made it for myself. After-sales service may consist of absolutely nothing.

That is the author's experience on one PC, not a finding that every Firefox installation has a memory leak. The AI statement covers FK_YT's own code and documentation; mpv, yt-dlp, and the other dependencies belong to their respective projects. Continued maintenance and fixes for future YouTube changes are not guaranteed.

## 4. Accounts and privacy

**FK_YT itself has no feature that collects usage history or sign-in information for a developer-operated server.** Playing online media still involves external services.

| Data or action | Handling |
| --- | --- |
| Settings, history, resume positions | Stored locally under the app's `data/` directory |
| Sign-in session | Connected from a Firefox profile or Netscape cookie file. The stored app session is encrypted with Windows DPAPI for the current Windows user |
| YouTube requests | Feeds, playback, comments, and other features contact YouTube/Google and media/image CDNs. Authenticated features use the selected session |
| YouTube watch history | In normal mode, attempts to report actual playback when history synchronization is enabled. It is enabled by default |
| Likes, dislikes, recommendation feedback | Requests sent to YouTube when you choose the corresponding action |
| Return YouTube Dislike | Count queries send the video ID to the RYD API. This request path does not attach the app's YouTube sign-in cookies |
| Build downloads | Source-build scripts contact dependency hosts and NuGet package providers |

Services you contact, including YouTube and RYD, can see ordinary connection information such as your IP address. RYD dislike counts are estimates.

Incognito mode does not use sign-in cookies or save app watch history/resume positions. It also disables account history and rating writes. **It does not hide your IP address or erase files you deliberately download.**

There are security limits. Tools may need temporary plaintext cookie files, and Firefox session extraction can create a temporary copy of the original cookie database. Encrypting stored cookies does not protect against every malicious program running as you or an already compromised PC. **Use a dedicated Firefox profile for YouTube.** Do not share or upload `data/`, cookie exports, or personal-backup ZIPs.

## 5. Requirements and versions

- Target: **Windows 10 / Windows 11, x64**.
- Actual usage testing: **Windows 10 22H2 only**. Equivalent behavior on Windows 11 has not yet been verified.
- Account connection: Firefox or a Netscape-format cookie file you supply. Search and some playback also work without signing in.
- Source builds: **PowerShell 7**, Git, and internet access. The CLI examples below also use GitHub CLI.
- Distributions are intended to include the .NET runtime and the required helper programs. A source ZIP is not a ready-to-run application.

Versions below come from the current [dependency lock](dependencies.lock.json) and project files. They will be updated if the final 1.0 installer contains different builds.

| Component | Version | Purpose |
| --- | --- | --- |
| .NET SDK | 10.0.402 | Build the `net10.0-windows` WPF app |
| Python | 3.13.16, embedded x64 | Feed/account workers |
| yt-dlp EXE / Python package | 2026.08.19 / 2026.8.19 | Extraction, metadata, downloads |
| yt-dlp-ejs | 0.8.0 | yt-dlp JavaScript support |
| Deno | 2.9.7 | JavaScript runtime |
| mpv | 20261014 / `ec38361c56` build | Playback |
| FFmpeg / ffprobe | 9.0.2 | Media processing |
| 7zr | 26.04 | Unpack build/runtime dependencies |
| Microsoft.Data.Sqlite | 10.0.13 | Local database |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Native SQLite integration |
| NSIS | Not yet selected | Planned installer builder |

## 6. Installation and launch

### For users: release downloads — planned

An installer EXE and a portable ZIP are planned for [GitHub Releases](https://github.com/mobsweeper/FK_YT/releases). **There are no releases at the time of this draft.** GitHub's `Code → Download ZIP` downloads source, not an installer.

- **NSIS installer EXE:** Planned. Installation location, shortcuts, updating, and what happens to user data during removal will be documented after the installer is implemented.
- **Portable ZIP:** Once published, extract the entire ZIP into a new writable folder and launch `FK_YT.exe`. Keep `runtime/`, `worker/`, DLLs, and license notices with it.
- In the current portable layout, app data lives in `data/`. Close the app and keep an original backup before moving it. Moving to another PC or Windows account may require reconnecting the sign-in session.

If you have GitHub CLI, these commands can download EXE assets **after a release is published**. They currently fail because no release exists. They download files; they do not automatically execute an installer.

```powershell
gh release view --repo mobsweeper/FK_YT
gh release download --repo mobsweeper/FK_YT --pattern '*.exe' --dir .\FK_YT-download
```

While the repository is private, you need repository access and GitHub authentication. [GitHub CLI download reference](https://cli.github.com/manual/gh_release_download)

### For developers: build from source

If needed, install the command-line prerequisites through Windows Package Manager. Skip tools you already have.

```powershell
winget install --id Microsoft.PowerShell --exact --source winget
winget install --id Git.Git --exact --source winget
winget install --id GitHub.cli --exact --source winget
```

Open a **new PowerShell 7 window** after installation. If you need access to the private repository and have not authenticated, run `gh auth login` first. Run the following steps in order, checking that each succeeds before continuing.

```powershell
gh repo clone mobsweeper/FK_YT
Set-Location FK_YT

pwsh -NoProfile -File .\scripts\Bootstrap.ps1
pwsh -NoProfile -File .\scripts\Bootstrap-Ffmpeg.ps1
pwsh -NoProfile -File .\scripts\Build.ps1
pwsh -NoProfile -File .\scripts\Publish.ps1

& .\dist\FK_YT\FK_YT.exe
```

`Bootstrap.ps1` prepares project-local `tools/` and `runtime/` using the dependency lock. FFmpeg has a separate bootstrap script. `Publish.ps1` produces a self-contained application directory at `dist/FK_YT/`; **it does not publish a GitHub release or generate an NSIS installer.** If the output directory is already populated, choose a different output path or preserve the existing output elsewhere.

This GitHub snapshot does not contain the C# test-project directory, so do not use `Build.ps1 -Test` here. If a dependency URL or hash check fails, investigate the error instead of disabling verification.

Official setup references: [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) · [Git](https://git-scm.com/install/windows) · [GitHub CLI](https://github.com/cli/cli#installation)

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
- **Pop out** opens a separate player window; use the return control to bring it back. Theater mode reduces surrounding UI. Fullscreen and pop-out controls hide when idle.
- **Share**, or right-click a video card, to copy its video link. Use **Download** only for content you are permitted to save.
- Local resume positions and YouTube account history are separate. A successful history request does not guarantee exact cross-device position sync, and YouTube may mark a video as completed.

## 8. Project layout

```text
src/                    C# WPF app, player control, account and storage services
worker/                 Python feed/account/metadata workers and related tests
scripts/                Dependency bootstrap, build, and local publishing scripts
dependencies.lock.json  Tool versions, download locations, verification hashes
.gitignore              Excludes private data, runtimes, and build outputs
LICENSE                 MIT license for FK_YT's own code and documentation
readme_KR.md            Korean guide
readme_EN.md            English guide
```

`tools/`, `runtime/`, `dist/`, and `data/` are local build/runtime directories. Application binaries and sign-in data are not part of the source upload.

## 9. Limitations

- **Authentication:** Expired cookies or additional authentication requirements may require reconnecting. The app does not bypass age, membership, or region restrictions. Handling an ordinary content warning does not substitute for signing in or verifying age.
- **YouTube changes:** Some features depend on unofficial extraction paths. Site changes can break feeds, playback, comments, and history reporting.
- **Subtitles:** Discovering a language does not guarantee its caption download will succeed. Requests may fail with HTTP 429, expired URLs, or unavailable captions. Not every translated language is guaranteed to load.
- **Quality and playback:** Available formats depend on the account/server, GPU and codec support, and network. Autoplay can still buffer.
- **Account behavior:** Recommendation changes, history update timing, and cross-device resume positions are not guaranteed. RYD estimates need not match YouTube's internal counts.
- **Support:** Environments other than Windows 10 22H2, and every DPI/GPU combination, have not been validated. Ongoing updates and fixes are not guaranteed.

For a useful bug report, include the app version, Windows version, whether incognito mode was enabled, reproduction steps, and the displayed error. Include a video URL only if you can share it. Do not attach cookies, authentication tokens, `data/`, or personal backups.

## 10. YouTube Terms of Service and account risk

FK_YT is not an official YouTube/Google app and is not endorsed by or affiliated with them. Automated requests, unofficial access, and downloads may conflict with the [YouTube Terms of Service](https://www.youtube.com/static?template=terms&hl=en), depending on how the app is used.

The app is designed to avoid unnecessary requests, but it does not guarantee YouTube approval or compliance with its terms. The likelihood of rate limiting, feature blocks, or account action has not been established. Fewer requests are **not evidence that the risk of account action is "very low."**

Review the terms and your rights to the content before use. A dedicated Firefox profile reduces the scope of cookie exposure; it is not protection against account action.

## 11. Licensing and external projects

**FK_YT's own code and documentation are distributed under the [MIT License](LICENSE).** External projects retain their respective licenses.

MIT it is. The author probably couldn't tell you what that means, though.

The following projects are used. Final distributions must retain the licenses and notices appropriate to the actual versions and builds included.

| Project | Purpose / license information |
| --- | --- |
| [.NET / WPF](https://github.com/dotnet/wpf) | App framework; inspect SDK/runtime `LICENSE.txt` and `ThirdPartyNotices.txt` |
| [Python](https://www.python.org/doc/copyright/) | Runtime and included components |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp#license) / [yt-dlp-ejs](https://github.com/yt-dlp/ejs) | Inspect source/package/bundled-EXE licensing and `THIRD_PARTY_LICENSES.txt` |
| [Deno](https://github.com/denoland/deno) | JavaScript runtime licenses and notices |
| [mpv](https://github.com/mpv-player/mpv/blob/master/Copyright) | GPL/LGPL applicability depends on the actual build |
| [FFmpeg](https://ffmpeg.org/legal.html) | Check GPL/LGPL conditions for the included features and libraries |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) / [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Inspect the actual NuGet packages' licenses, notices, and metadata |
| [7-Zip](https://www.7-zip.org/license.txt) | Build-time extraction tool; check its terms if included or redistributed |
| [Return YouTube Dislike](https://returnyoutubedislike.com/) | External dislike-estimate service, not statistics collected by FK_YT |
| [NSIS](https://nsis.sourceforge.io/License) | Planned installer builder; also check the compression modules and plugins actually used |

Do not assume yt-dlp's source and bundled executable have identical licensing. Its [release-file licensing notes](https://github.com/yt-dlp/yt-dlp#licensing) explain the GPLv3+ code in PyInstaller bundles. mpv and FFmpeg cannot simply be labeled "all MIT" either.

The build scripts check and copy dependency notices. **Including notices alone is not a guarantee that every redistribution obligation is satisfied.** Before publishing installer or portable binaries, review their license texts, any corresponding-source obligations and delivery method, actual build configurations, and NSIS components. That release licensing review is not marked complete.

## 12. Miscellaneous

| Item | Details |
| --- | --- |
| Writing the code | Delegated to AI |
| Writing this README | Also delegated to AI |
| Estimated debugging reviews delegated to agents | **About 165** |
| Certified bug-free | Absolutely not |

Based on **11 parallel review rounds × 15 review assignments**, checked on October 16, 2026. Repeat assignments to the same agents count; the main agent's own checks, separate external reviews, and individual follow-up questions do not. That's not 165 different AI models, and definitely not a claim of 165 bugs fixed.

*This counts static code reviews only. Dynamic checks, including actual app execution and playback, could not be tallied.*
