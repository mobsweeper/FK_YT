# Building FK_YT 1.1

Use Windows x64 and PowerShell 7. The application targets `net10.0-windows` and uses the pinned **.NET SDK 10.0.402**. The public 1.1 source contains product/build sources and public documentation; test projects and internal verification records are deliberately excluded. No test command is needed to compile it.

There are three different outputs: compiling the app, publishing with a prepared development runtime, and assembling the final release with its preserved licensed runtime. The latter also needs the pinned base portable archive. Installer compilation is a separate step.

## Compile the application

From the extracted `FK_YT-1.1-source.zip` source root, or from the `v1.1` repository checkout:

```powershell
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
pwsh -NoProfile -File .\scripts\Build.ps1
```

The bootstrap downloads the pinned SDK into `tools/dotnet`; Build uses the reviewed NuGet lock. Internet access is needed unless the exact SDK/packages are already cached. Keep hash and locked-restore failures visible; do not disable verification to continue.

This compiles the WPF app. It does not install the application or prepare the playback/runtime folder. The embedded Python included in the runnable app is not a substitute for the .NET SDK.

## Publish application files for final assembly

For a new normal release publish folder, use the pinned SDK and the reviewed lock:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'tools/nuget/packages'
./tools/dotnet/dotnet.exe publish src/FkYouTube/FkYouTube.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o ./dist/app-1.1
```

Use a fresh output directory. `DebugType=None` and `DebugSymbols=false` avoid distributing PDBs or a CodeView reference to a local build path. The final assembler consumes the four application files (`FK_YT.exe`, `FK_YT.dll`, `FK_YT.deps.json`, `FK_YT.runtimeconfig.json`) from this folder. It preserves the base package's reviewed runtime and notices instead of copying newly obtained runtime files from the publish output.

The bundled .NETCore/WindowsDesktop runtime remains **10.0.13**; Python remains **3.13.16**. External playback tools and their original legal notices remain the versions listed in the README. Publishing the app with an SDK does not, on its own, recreate that complete runtime set.

## Assemble the licensed portable package

Use Python 3.10 or later, the completed common application-source ZIP, and the public 1.0 portable archive pinned by `scripts/Package.py`. Its hash must match the recipe's expected base; do not substitute a preview ZIP or a newly bootstrapped runtime. Choose a new output path:

```powershell
python -B ./scripts/Package.py --base '<inputs>/1.0.portable.zip' --publish './dist/app-1.1' --app-source '<inputs>/FK_YT-1.1-source.zip' --output '<output>/FK_YT-1.1-portable.zip'
```

Create the common source ZIP before this step; `--app-source` records that exact source alongside the payload. The recipe preserves the verified base runtime/licensing tree and overlays only the four application files, the product worker allowlist and public documents. It regenerates the package inventory/provenance. Tests, internal review records, user data, build tools and local verification outputs do not belong in the runnable package. The app source is supplied separately as `FK_YT-1.1-source.zip`.

The legacy `dependencies.lock.json` records development tool and historical supplier-runtime pins. Full `Bootstrap.ps1` and `Bootstrap-Ffmpeg.ps1` acquire older supplier mpv/FFmpeg binaries; **they do not restore the final FK_YT media distribution**. Do not replace the preserved media binaries and describe the result as the matching release.

## Ordinary development Publish

`scripts/Publish.ps1` expects the runtime files and original notices required by its development lock. It validates those inputs and writes a self-contained runnable directory beneath `dist/`. It is useful with that prepared development environment, but it does not create the final licensed portable ZIP, an NSIS installer, corresponding-source collections, or a GitHub release. Do not point it at the final preserved media set and bypass a lock mismatch.

## Build the installer

Use the separate **`FK_YT-1.1-installer-source.zip`** and follow its `docs/BUILDING.md`. Its active input lock identifies the exact 1.1 portable ZIP, common app-source ZIP, and unchanged external corresponding-source ZIPs. Its toolchain lock identifies NSIS 3.13 and required source/notices. Keep those inputs available and use fresh payload/stage/output folders.

The installer adapts the exact portable payload and adds `FK_YT.install-mode` with `machine-v1` plus LF, installed-mode guidance, and NSIS/source-access notices. The same application DLL chooses `%LOCALAPPDATA%\FK_YT\data` for each Windows user when that marker is present. Without it, the portable app uses its local `data/` folder. A separate installed-app rebuild or source ZIP is unnecessary for 1.1.

The installer uses NSIS Unicode/MUI2, English pages and zlib compression. It requests administrator permission, defaults to native 64-bit Program Files and preserves user data on removal. One complete fixed-payload SHA-256 pass remains in protected staging; path/type/length, ownership, rollback and removal checks remain. Actual UAC, installation time and final 1.1 installation/update/removal have not been verified by compilation alone.

## Sources and verification

Final download hashes are in [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1/SHA256SUMS.txt), without duplicating a source archive's own hash inside its inputs. The two media source ZIPs are independent archives; extract part1 then part2 into the same folder. They, and `FK_YT_yt-dlp.zip`, describe the unchanged bundled external binaries. A manually downloaded experimental tool overlay needs its own notices and corresponding-source review before redistribution.

See [RELEASE-1.1.md](RELEASE-1.1.md#verification-scope) for the executed-check boundaries and [LICENSING-1.1.md](LICENSING-1.1.md) for source/license limits. A successful compile or archive comparison is not proof of actual YouTube playback, account compatibility, install behavior, or long-duration reliability.
