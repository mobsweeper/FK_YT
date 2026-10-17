# Building FK_YT 1.1.1

Use Windows x64, PowerShell 7, Python 3.10 or later for packaging, and the pinned **.NET SDK 10.0.402**. The WPF project targets `net10.0-windows`. The public source package contains production/build sources and public documentation; FK_YT tests and internal work records are intentionally excluded. The commands below do not depend on absent test projects or scripts.

Compiling/publishing the application and assembling the complete licensed runtime package are separate operations. The portable assembler needs the unchanged, pinned 1.0 portable archive. Installer construction uses the separate installer-source ZIP.

## Prepare the build inputs

Extract `FK_YT-1.1.1-source.zip` and work from its source root. The existing online preparation command for the SDK is:

```powershell
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
```

This downloads/verifies the locked SDK into `tools/dotnet`; skip it when that exact verified SDK is already prepared. `dependencies.lock.json` pins its archive digest and executable SHA-256. The embedded Python in the runnable app cannot replace this SDK.

For the offline publish below, prepare `tools/nuget/packages` with **all** packages required by `src/FkYouTube/packages.lock.json` and the win-x64 self-contained .NETCore/WindowsDesktop `10.0.13` runtime packs. A fresh checkout does not include this cache. Obtain and verify these inputs while online or supply a preserved trusted cache before going offline. An empty package source cannot fetch missing inputs; failures must be resolved without changing the reviewed lock or bypassing hash checks.

## Direct offline self-contained publish

The following recipe uses only the prepared SDK/cache and a new NuGet configuration with no remote sources. `NuGetAudit=false` prevents an online vulnerability-data lookup during this offline build; it does not disable the locked package graph. Use a new publish directory. This is the direct publish path used for the 1.1.1 release preparation.

```powershell
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = Join-Path $PWD 'tools/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'tools/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PWD 'tools/nuget/http-cache'

New-Item -ItemType Directory -Path './dist' -Force | Out-Null
if (Test-Path -LiteralPath './dist/app-1.1.1') { throw 'Choose a fresh publish directory.' }
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /></packageSources></configuration>
'@ | Set-Content -LiteralPath './dist/NuGet.offline.config' -Encoding utf8

& ./tools/dotnet/dotnet.exe publish ./src/FkYouTube/FkYouTube.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:RestoreLockedMode=true -p:NuGetAudit=false `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -p:ContinuousIntegrationBuild=true `
  --configfile ./dist/NuGet.offline.config --nologo -o ./dist/app-1.1.1
if ($LASTEXITCODE -ne 0) { throw 'Offline publish failed.' }
```

This command performs a locked restore/publish; it does not rely on an old `obj/project.assets.json` or use `--no-restore`. Debug symbols are disabled to avoid distributing PDBs and a CodeView reference to a local build path. A successful application publish does not create the playback-tool/notices tree or installer.

The portable assembler consumes only `FK_YT.exe`, `FK_YT.dll`, `FK_YT.deps.json` and `FK_YT.runtimeconfig.json` from this directory. It requires application `1.1.1`, file/assembly version `1.1.1.0`, and self-contained .NETCore/WindowsDesktop `10.0.13` configuration. It preserves the reviewed base runtime bytes rather than importing a different runtime set from the publish folder.

## Assemble the licensed portable ZIP

Prepare these exact inputs before invoking `scripts/Package.py`:

- The public **1.0 portable ZIP**, saved as `inputs/1.0.portable.zip`, with SHA-256 `05fd94e0901e22903db33d3bc4ee9f57bfb0509b93fb5010ef1aa6ad5df25d72`. The recipe refuses a different base, including a 1.1/preview archive.
- The fresh publish directory above.
- The completed, matching **`inputs/FK_YT-1.1.1-source.zip`**. It must have one safe top-level directory, the exact `scripts/Package.py`, production sources/locks and public documents. Exclude `.git`, user data, tests, internal work records, `tools`, `runtime`, `artifacts`, `bin`, `obj`, Python caches and executable/build payloads. Do not archive a working tree wholesale after building it.
- A new output filename in an existing output directory.

For an unchanged release rebuild, keep the downloaded matching app-source archive and extract that same source to build it. For modified source, prepare a fresh matching source archive before packaging; supplying the original release archive with changed inputs will fail selected-source checks and would not describe the changed build.

```powershell
python -B ./scripts/Package.py `
  --base ./inputs/1.0.portable.zip `
  --publish ./dist/app-1.1.1 `
  --app-source ./inputs/FK_YT-1.1.1-source.zip `
  --output ./dist/FK_YT-1.1.1-portable.zip
if ($LASTEXITCODE -ne 0) { throw 'Portable packaging failed.' }
```

The recipe never builds, launches, downloads or publishes. It verifies the pinned base, selected source/archive identity, four application files and version metadata; overlays eleven production workers and the selected public documents; regenerates `file-manifest.json` and `distribution-provenance.json`; and checks all protected base files for exact byte preservation. The resulting JSON reports output size/hash, file count and preservation results. Use a new filename on a retry; existing outputs are refused.

The complete runtime remains .NET/Windows Desktop `10.0.13`, Python `3.13.16`, standalone yt-dlp `2026.08.19`, Python yt-dlp `2026.8.19`, EJS `0.8.0`, Deno `2.9.7`, pinned mpv `0.41.0-dev-gec38361c56-fkyt`, and FFmpeg/ffprobe `9.0.2`. Original runtime notices and copyright/license texts are retained.

## Other build paths

`scripts/Build.ps1` compiles the app using the prepared pinned SDK and locked NuGet graph. `scripts/Publish.ps1` expects a separately prepared **development** runtime that matches its historical supplier pins. Full `Bootstrap.ps1` and `Bootstrap-Ffmpeg.ps1` acquire historical supplier mpv/FFmpeg inputs; they do not recreate the final licensed media distribution used here. Keep their lock failures visible instead of bypassing them or relabelling a different runtime set as the matching release.

The final assembler therefore uses the direct publish and pinned base recipe above. A self-contained publish does not establish a complete independent rebuild of every bundled external component, or a byte-identical rebuild across arbitrary machines.

## Build the installer separately

Use **`FK_YT-1.1.1-installer-source.zip`** and its `docs/BUILDING.md`. Its input lock identifies the exact portable ZIP, common app-source ZIP and unchanged external corresponding-source ZIPs. Its toolchain lock identifies NSIS 3.13 and original notices. Keep those exact inputs and use fresh payload/stage/output directories.

The installer adapts the exact portable payload and adds `FK_YT.install-mode` containing `machine-v1` plus LF, installed-mode guidance and installer/NSIS notices. The app DLL is shared: the marker selects each user's `%LOCALAPPDATA%\FK_YT\data`; without it the portable app uses adjacent `data/`. A separate machine-app source ZIP or rebuild is unnecessary.

The installer uses NSIS Unicode/MUI2, English pages and zlib compression, requests administrator permission, defaults to native 64-bit Program Files and preserves user data on removal. A successful NSIS compile is not an actual installation/removal test. No new manual 1.1.1 installer or real YouTube test is claimed here.

Final asset hashes belong in the v1.1.1 release `SHA256SUMS.txt`; source archives do not embed their own archive hash. See [RELEASE-1.1.1.md](RELEASE-1.1.1.md#verification-scope), [SOURCE-ACCESS-1.1.1.txt](SOURCE-ACCESS-1.1.1.txt) and [LICENSING-1.1.1.md](LICENSING-1.1.1.md) for publication, corresponding-source and verification boundaries.
