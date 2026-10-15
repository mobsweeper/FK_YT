[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-RegularFfmpegPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "FFmpeg bootstrap does not follow linked paths: $Path"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $root 'dependencies.lock.json'
Assert-RegularFfmpegPath $lockPath
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$decoder = Join-Path $root 'tools/7zip/7zr.exe'
$decoderEntry = $lock.components.sevenZipDecoder
if (-not $decoderEntry.ContainsKey('installedExecutableSha256') -or $decoderEntry.installedExecutableSha256 -isnot [string] -or
    $decoderEntry.installedExecutableSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'Missing reviewed 7zr executable SHA256 pin.' }
Assert-RegularFfmpegPath $decoder
if (-not (Test-Path -LiteralPath $decoder -PathType Leaf)) { throw 'Missing 7zr executable. Complete Bootstrap first.' }
if ((Get-FileHash -LiteralPath $decoder -Algorithm SHA256).Hash -ne $decoderEntry.installedExecutableSha256) { throw '7zr executable disagrees with the locked hash.' }
$version = '9.0.2'
$url = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.7z'
$expected = '4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077'
$archive = Join-Path $root 'tools/downloads/ffmpeg-9.0.2-essentials_build.7z'
$target = Join-Path $root 'runtime/ffmpeg'
Assert-RegularFfmpegPath $archive
Assert-RegularFfmpegPath $target
function Install-FfmpegFiles([string]$Package, [string]$Destination) {
    Assert-RegularFfmpegPath $Destination
    $files = @(
        @{ name = 'ffmpeg.exe'; source = (Join-Path $Package 'bin/ffmpeg.exe') },
        @{ name = 'ffprobe.exe'; source = (Join-Path $Package 'bin/ffprobe.exe') },
        @{ name = 'LICENSE'; source = (Join-Path $Package 'LICENSE') },
        @{ name = 'README.txt'; source = (Join-Path $Package 'README.txt') }
    )
    # Check every file before copying any missing file. A differing existing
    # executable or notice is preserved together with the rest of the target.
    $missing = @()
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.source -PathType Leaf)) { throw "FFmpeg archive lacks $($file.name)." }
        $installed = Join-Path $Destination $file.name
        Assert-RegularFfmpegPath $installed
        if (Test-Path -LiteralPath $installed) {
            if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or
                (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $file.source -Algorithm SHA256).Hash) {
                throw "Different existing FFmpeg file is preserved: $installed"
            }
        }
        else { $missing += @{ source = $file.source; destination = $installed } }
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in $missing) {
        Assert-RegularFfmpegPath $file.destination
        # File.Copy without overwrite also protects a file created after the
        # preflight checks instead of silently replacing a concurrent change.
        [IO.File]::Copy($file.source, $file.destination, $false)
    }
}
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    $partial = "$archive.$([Guid]::NewGuid().ToString('N')).partial"
    Invoke-WebRequest -Uri $url -OutFile $partial -TimeoutSec 600
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "FFmpeg archive checksum mismatch; partial download is preserved: $partial" }
    Move-Item -LiteralPath $partial -Destination $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'FFmpeg archive checksum does not match the publisher checksum.' }
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $root 'tools'))
$unpack = Join-Path $toolsRoot ('ffmpeg-staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $unpack | Out-Null
try {
    & $decoder x $archive "-o$unpack" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg extraction failed.' }
    $package = Join-Path $unpack 'ffmpeg-9.0.2-essentials_build'
    Install-FfmpegFiles $package $target
    Assert-RegularFfmpegPath (Join-Path $target 'ffmpeg.exe')
    # Reload immediately before the scoped update so unrelated lock entries
    # changed during extraction are retained.
    Assert-RegularFfmpegPath $lockPath
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
    $lock.components.ffmpeg = @{ version=$version; installPath='runtime/ffmpeg'; url=$url; sha256=$expected; downloadedSha256=$expected; providerReference='https://ffmpeg.org/download.html#build-windows'; hashSource=$url+'.sha256'; sourceUrl='https://github.com/FFmpeg/FFmpeg/commit/946fcce07b'; installedExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $target 'ffmpeg.exe') -Algorithm SHA256).Hash.ToLowerInvariant() }
    $lock | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $lockPath -Encoding utf8
    Assert-RegularFfmpegPath (Join-Path $target 'ffmpeg.exe')
    & (Join-Path $target 'ffmpeg.exe') -version | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg version verification failed.' }
}
catch {
    Write-Warning "FFmpeg staging is preserved for inspection: $unpack"
    throw
}
# Only remove this invocation's fresh staging directory after success.
if ([IO.Path]::GetFullPath((Split-Path -Parent $unpack)) -ne $toolsRoot -or
    (Split-Path -Leaf $unpack) -notmatch '^ffmpeg-staging-[0-9a-f]{32}$') { throw 'Unexpected FFmpeg staging cleanup path; folder is preserved.' }
Remove-Item -LiteralPath $unpack -Recurse -Force
