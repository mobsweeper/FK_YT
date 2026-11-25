# FK_YT 1.3.1 downloads, installation, and source

Both editions bundle yt-dlp 2026.11.23 and Deno 2.9.8. The distribution and installer version is 1.3.1. The Windows app assembly still reports 1.3.0.0. [FK_YT v1.3.1 on GitHub Releases](https://github.com/mobsweeper/FK_YT/releases/tag/v1.3.1)

## Release files

| Download | Contents |
| --- | --- |
| [FK_YT-1.3.1-portable.zip](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/FK_YT-1.3.1-portable.zip) | Ready-to-run Windows x64 app. Extract the whole ZIP into a new writable folder. |
| [FK_YT-1.3.1-Setup-x64.exe](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/FK_YT-1.3.1-Setup-x64.exe) | Machine-wide Windows x64 installer with English setup pages. |
| [FK_YT-1.3.1-source.zip](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/FK_YT-1.3.1-source.zip) | Matching application and worker source, build controls, dependency locks, and source and build material for the bundled yt-dlp and Deno versions. |
| [FK_YT-1.3.1-installer-source.zip](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/FK_YT-1.3.1-installer-source.zip) | Installer implementation, build instructions, input locks, and original NSIS notices. |
| [SOURCE-ACCESS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/SOURCE-ACCESS.txt) | Current public filenames and checksums for the source collections referenced inside the packages. |
| [LICENSES-SUPPLEMENT.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/LICENSES-SUPPLEMENT.txt) | Supplemental third-party notices. |
| [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/SHA256SUMS.txt) | SHA-256 checksums for release assets. |

To check a download, run the applicable PowerShell command and compare the result with the corresponding entry in `SHA256SUMS.txt`.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.3.1-portable.zip'
Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.3.1-Setup-x64.exe'
```

## Installation and data

- **Portable, recommended.** Extract the entire ZIP into a new writable folder. Run `FK_YT.exe` inside `FK_YT`. The app keeps its user data in `data/` beside the executable.
- **Installed.** Run Setup as administrator on Windows x64, build 19045 or later. Setup uses a folder directly beneath native 64-bit Program Files and defaults to `C:\Program Files\FK_YT`. When Setup closes, open **FK_YT (System)** from Start. The app runs with normal user permissions and stores data per Windows user in `%LOCALAPPDATA%\FK_YT\data`.
- Choose **Exit** from the Windows notification-area icon before upgrading, moving data, or uninstalling. For a portable upgrade, keep the old folder and copy its `data/` into the freshly extracted app folder. Uninstalling the installed app preserves per-user data. If a previously downloaded tool still takes precedence after the upgrade, select **Use bundled version** in Settings and restart FK_YT.

Keep the included `runtime/`, `worker/`, DLLs, and notices together. Setup uses NSIS 3.13 with Unicode, Modern UI 2 and zlib compression. It uses the included runtime payload and performs a SHA-256 pass on each fixed file in protected staging. [English install guide](../README.md#6-installation-and-launch) · [한국어 설치 안내](../readme_KR.md#6-설치-및-구동)

## Source and build

The app source archive contains the C# WPF app and Python workers. Use PowerShell 7 and the pinned .NET SDK **10.0.402**. To compile a self-contained app targeting the release runtime, run the following from the extracted source root with the locked NuGet inputs available.

```powershell
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
.\tools\dotnet\dotnet.exe publish .\src\FkYouTube\FkYouTube.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:RestoreLockedMode=true -p:RuntimeFrameworkVersion=10.0.13 `
  -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true `
  -o .\dist\app-1.3.1
```

The full Bootstrap scripts fetch historical supplier builds of mpv and FFmpeg. The public portable ZIP contains the licensed media builds used for this release. `scripts/Package.py` documents the older 1.1.2 packaging process.

For installer assembly, extract the installer source and follow its `docs/BUILDING.md` with the exact release inputs listed in its lock. The installer uses the portable payload and adds the installed-mode marker, installed guidance, and installer notices.

## Corresponding source

The app source ZIP includes 54 yt-dlp source and build archives under `third-party/yt-dlp-2026.11.23/` and the Deno 2.9.8 source collection under `third-party/deno-2.9.8/`. The packaged yt-dlp EXE embeds Python **3.14.10**. FK_YT's workers use Python **3.13.16**. The source collection documents the available MinGW CRT sources and the unconfirmed revision used to build the yt-dlp bootloader. The exact revision and a byte-for-byte rebuild have not been verified.

Some notices inside the portable ZIP use the longer packaging filenames. The public portable and source ZIPs have the same SHA-256 hashes as those files. See the [source-access map](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/SOURCE-ACCESS.txt) for the filename mapping.

For the bundled mpv, FFmpeg, and linked media libraries, use the two FK_YT-prepared media source ZIPs from v1.1.2. Download both independent archives. Extract part 1 and then part 2 into the same folder. [Media part 1](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.2/FK_YT_mpv-FFmpeg-part1.zip) · [Media part 2](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.2/FK_YT_mpv-FFmpeg-part2.zip) · [Media checksums](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.2/SHA256SUMS.txt)

FK_YT code and documentation use MIT. Retain the original component licenses and notices that ship with the binary and source packages. Deno's applicable MPL sources are included in `notices/deno/third-party/sources/mpl/` in the runnable packages. When redistributing the binaries, keep the matching source accessible alongside them at no additional source-access fee. [MIT license](../LICENSE) · [Supplemental notices](https://github.com/mobsweeper/FK_YT/releases/download/v1.3.1/LICENSES-SUPPLEMENT.txt)
