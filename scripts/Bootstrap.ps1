[CmdletBinding()]
param([ValidateSet('All', 'Dotnet', 'Runtime')][string]$Component = 'All', [switch]$InspectPythonDigest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-RegularBootstrapPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Bootstrap does not follow linked paths: $Path"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Assert-LockedBootstrapExecutable([string]$Path, [hashtable]$Entry) {
    if (-not $Entry -or -not $Entry.ContainsKey('installedExecutableSha256') -or
        $Entry.installedExecutableSha256 -isnot [string] -or
        $Entry.installedExecutableSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') {
        throw "Missing reviewed executable SHA256 pin: $Path"
    }
    Assert-RegularBootstrapPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Runtime executable is missing: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Entry.installedExecutableSha256) {
        throw "Existing executable disagrees with the locked hash: $Path"
    }
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $projectRoot 'dependencies.lock.json'
Assert-RegularBootstrapPath $lockPath
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
function Assert-ReviewedPythonLock {
    if (-not $lock.ContainsKey('components') -or $lock.components -isnot [hashtable] -or
        -not $lock.components.ContainsKey('python') -or $lock.components.python -isnot [hashtable] -or
        -not $lock.components.python.ContainsKey('sha256') -or
        $lock.components.python.sha256 -isnot [string] -or
        $lock.components.python.sha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') {
        throw 'A reviewed CPython lock entry with a pinned SHA256 is required. Restore dependencies.lock.json or prepare the pin through a separate publisher verification process; bootstrap will not derive a trusted hash from an unverified Sigstore bundle.'
    }
}
if ($InspectPythonDigest) {
    $response = Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.16/python-3.13.16-embed-amd64.zip.sigstore'
    Write-Host "Content type: $($response.Content.GetType().FullName)"
    Write-Host "Content length: $($response.Content.Length)"
    $contentText = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    $parsed = ConvertFrom-Json -InputObject $contentText -AsHashtable
    Write-Host "Parsed type: $($parsed.GetType().FullName)"
    Write-Host "Bundle fields: $($parsed.Keys -join ', ')"
    if ($parsed.ContainsKey('messageSignature')) { Write-Host "Digest algorithm: $($parsed.messageSignature.messageDigest.algorithm)" }
    return
}
if ($Component -ne 'Dotnet') { Assert-ReviewedPythonLock }
$downloadRoot = Join-Path $projectRoot 'tools/downloads'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
function Save-Lock {
    $lock.resolvedUtc = [DateTime]::UtcNow.ToString('o')
    $lock | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $lockPath -Encoding utf8
}
function Get-VerifiedDownload([hashtable]$Entry) {
    $cached = Join-Path $downloadRoot $Entry.fileName
    Assert-RegularBootstrapPath $cached
    $algorithm = if ($Entry.ContainsKey('sha512')) { 'SHA512' } else { 'SHA256' }
    $expected = if ($algorithm -eq 'SHA512') { $Entry.sha512 } else { $Entry.sha256 }
    if (-not $expected) { throw "Missing publisher hash for $($Entry.fileName)." }
    if (-not (Test-Path -LiteralPath $cached)) {
        $partialFile = "$cached.$([Guid]::NewGuid().ToString('N')).partial"
        Write-Host "Downloading $($Entry.fileName)..."
        Invoke-WebRequest -Uri $Entry.url -OutFile $partialFile -TimeoutSec 600
        if ((Get-FileHash -LiteralPath $partialFile -Algorithm $algorithm).Hash -ne $expected) { throw "Publisher $algorithm mismatch: $($Entry.fileName). Partial file is preserved." }
        Move-Item -LiteralPath $partialFile -Destination $cached
    }
    if ((Get-FileHash -LiteralPath $cached -Algorithm $algorithm).Hash -ne $expected) { throw "Cached $algorithm mismatch: $($Entry.fileName). Archive is preserved." }
    $Entry.downloadedSha256 = (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant()
    Save-Lock
    return $cached
}
function Get-GitHubEntry([object]$Release, [object]$Asset, [string]$InstallPath, [string]$HashSource) {
    if (-not $Asset) { throw 'Required GitHub release asset was not found.' }
    $digest = [string]$Asset.digest
    if ($digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "GitHub publisher SHA256 is unavailable for $($Asset.name)." }
    return @{ version = $Release.tag_name; fileName = $Asset.name; url = $Asset.browser_download_url; sha256 = $Matches[1]; hashSource = $HashSource; installPath = $InstallPath }
}
function Get-PyPiEntry([string]$Package, [string]$Version) {
    $metadataUrl = "https://pypi.org/pypi/$Package/$Version/json"
    $metadata = Invoke-RestMethod -Uri $metadataUrl
    $wheel = $metadata.urls | Where-Object { $_.filename -match 'py3-none-any\.whl$' } | Select-Object -First 1
    if (-not $wheel) { throw "Pure Python wheel was not found for $Package $Version." }
    return @{ version = $metadata.info.version; fileName = $wheel.filename; url = $wheel.url; sha256 = $wheel.digests.sha256; hashSource = $metadataUrl; installPath = 'runtime/python/Lib/site-packages'; requiresDist = @($metadata.info.requires_dist) }
}
function Resolve-RuntimeLock {
    Assert-ReviewedPythonLock
    if (-not $lock.components.ContainsKey('ytDlp')) {
        $releaseUrl = 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest'
        $release = Invoke-RestMethod -Uri $releaseUrl
        if ($release.prerelease -or $release.draft) { throw 'yt-dlp release must be stable.' }
        $lock.components.ytDlp = Get-GitHubEntry $release ($release.assets | Where-Object name -eq 'yt-dlp.exe') 'runtime/yt-dlp' $releaseUrl
        $lock.components.ytDlp.thirdPartyLicensesUrl = "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$($release.tag_name)/THIRD_PARTY_LICENSES.txt"
        $lock.components.ytDlp.licenseUrl = "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$($release.tag_name)/LICENSE"
        Save-Lock
    }
    if (-not $lock.components.ContainsKey('ytDlpPython')) {
        $lock.components.ytDlpPython = Get-PyPiEntry 'yt-dlp' $lock.components.ytDlp.version
        Save-Lock
    }
    if (-not $lock.components.ContainsKey('ytDlpEjs')) {
        $ejsRequirement = $lock.components.ytDlpPython.requiresDist | Where-Object { $_ -match '^yt-dlp-ejs' } | Select-Object -First 1
        if ($ejsRequirement -notmatch 'yt-dlp-ejs\s*==\s*([0-9.]+)') { throw "An exact yt-dlp-ejs dependency pin is required; found: $ejsRequirement" }
        $lock.components.ytDlpEjs = Get-PyPiEntry 'yt-dlp-ejs' $Matches[1]
        Save-Lock
    }
    if (-not $lock.components.ContainsKey('deno')) {
        $releaseUrl = 'https://api.github.com/repos/denoland/deno/releases/latest'
        $release = Invoke-RestMethod -Uri $releaseUrl
        $lock.components.deno = Get-GitHubEntry $release ($release.assets | Where-Object name -eq 'deno-x86_64-pc-windows-msvc.zip') 'runtime/deno' $releaseUrl
        $lock.components.deno.licenseUrl = "https://raw.githubusercontent.com/denoland/deno/$($release.tag_name)/LICENSE.md"
        Save-Lock
    }
    if (-not $lock.components.ContainsKey('mpv')) {
        $releaseUrl = 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
        $release = Invoke-RestMethod -Uri $releaseUrl
        $asset = $release.assets | Where-Object { $_.name -match '^mpv-x86_64-[0-9].*\.7z$' -and $_.name -notmatch '(v3|clang|dev)' } | Select-Object -First 1
        $lock.components.mpv = Get-GitHubEntry $release $asset 'runtime/mpv' $releaseUrl
        $lock.components.mpv.providerReference = 'https://mpv.io/installation/'
        $lock.components.mpv.licenseUrl = 'https://raw.githubusercontent.com/mpv-player/mpv/master/Copyright'
        Save-Lock
    }
    if (-not $lock.components.ContainsKey('sevenZipDecoder')) {
        $releaseUrl = 'https://api.github.com/repos/ip7z/7zip/releases/tags/26.04'
        $release = Invoke-RestMethod -Uri $releaseUrl
        $lock.components.sevenZipDecoder = Get-GitHubEntry $release ($release.assets | Where-Object name -eq '7zr.exe') 'tools/7zip' $releaseUrl
        $lock.components.sevenZipDecoder.providerReference = 'https://www.7-zip.org/download.html'
        Save-Lock
    }
}
function Install-Archive([hashtable]$Entry, [string]$Executable) {
    $archivePath = Get-VerifiedDownload $Entry
    $target = Join-Path $projectRoot $Entry.installPath
    $marker = Join-Path $target '.fk-toolchain.json'
    Assert-RegularBootstrapPath $target
    Assert-RegularBootstrapPath $marker
    if (Test-Path -LiteralPath $marker) {
        $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json -AsHashtable
        if ($record.archiveSha256 -ne $Entry.downloadedSha256) { throw "Different existing runtime is preserved: $target" }
        # The adjacent marker records installation state; only the reviewed lock
        # supplies the trusted executable pin for an existing installation.
        Assert-LockedBootstrapExecutable (Join-Path $target $Executable) $Entry
        if ($record.executableSha256 -ne $Entry.installedExecutableSha256) { throw "Existing runtime marker disagrees with the locked hash: $target" }
        return
    }
    if ((Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)) { throw "Existing untracked runtime folder is preserved: $target" }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if ($archivePath.EndsWith('.zip')) { [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $target) }
    elseif ($archivePath.EndsWith('.7z')) {
        $decoder = Join-Path $projectRoot 'tools/7zip/7zr.exe'
        Assert-LockedBootstrapExecutable $decoder $lock.components.sevenZipDecoder
        & $decoder x $archivePath "-o$target" -y
        if ($LASTEXITCODE -ne 0) { throw '7-Zip could not extract the mpv archive; partial folder is preserved.' }
    }
    else { Copy-Item -LiteralPath $archivePath -Destination (Join-Path $target $Executable) }
    $exePath = Join-Path $target $Executable
    if (-not (Test-Path -LiteralPath $exePath)) { throw "Archive lacks $Executable; partial folder is preserved." }
    Assert-RegularBootstrapPath $exePath
    # A new pin may be derived only during a fresh extraction of the verified archive.
    if (-not $Entry.ContainsKey('installedExecutableSha256')) {
        $Entry.installedExecutableSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Assert-LockedBootstrapExecutable $exePath $Entry
    @{ archiveSha256 = $Entry.downloadedSha256; executableSha256 = $Entry.installedExecutableSha256; version = $Entry.version } | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding utf8
    Save-Lock
}
function Save-License([hashtable]$Entry, [string]$Property, [string]$FileName) {
    if (-not $Entry.ContainsKey($Property)) { return }
    $target = Join-Path (Join-Path $projectRoot $Entry.installPath) $FileName
    Assert-RegularBootstrapPath $target
    $hashProperty = "${Property}Sha256"
    if ($Entry.ContainsKey($hashProperty) -and ($Entry[$hashProperty] -isnot [string] -or
        $Entry[$hashProperty] -cnotmatch '\A[0-9a-fA-F]{64}\z')) { throw "Invalid reviewed license SHA256 pin: $target" }
    if (Test-Path -LiteralPath $target) {
        if (-not $Entry.ContainsKey($hashProperty)) { throw "Existing unpinned license file is preserved: $target. Restore or review its license SHA256 pin separately." }
        if ($Entry.ContainsKey($hashProperty) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Entry[$hashProperty]) { throw "License file changed: $target" }
        return
    }
    $partial = "$target.$([Guid]::NewGuid().ToString('N')).partial"
    Invoke-WebRequest -Uri $Entry[$Property] -OutFile $partial -TimeoutSec 60
    $downloadedHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Entry.ContainsKey($hashProperty) -and $downloadedHash -ne $Entry[$hashProperty]) {
        throw "License SHA256 mismatch: $target. Partial file is preserved: $partial"
    }
    # A concurrent target must be preserved, too; the two-argument move never overwrites it.
    [IO.File]::Move($partial, $target)
    if (-not $Entry.ContainsKey($hashProperty)) {
        $Entry[$hashProperty] = $downloadedHash
        Save-Lock
    }
}
if ($Component -ne 'Runtime') {
$sdk = $lock.components.dotnet
$archive = Join-Path $downloadRoot $sdk.fileName
Assert-RegularBootstrapPath $archive
if (-not (Test-Path -LiteralPath $archive)) {
    Write-Host "Downloading .NET SDK $($sdk.version) from Microsoft..."
    $partial = "$archive.$([Guid]::NewGuid().ToString('N')).partial"
    Assert-RegularBootstrapPath $partial
    # Reserve only a new invocation-owned file; never consume an older partial.
    $partialStream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $partialStream.Dispose()
    Assert-RegularBootstrapPath $partial
    Invoke-WebRequest -Uri $sdk.url -OutFile $partial
    Assert-RegularBootstrapPath $partial
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA512).Hash -ne $sdk.sha512) { throw 'Microsoft SDK SHA512 mismatch.' }
    Assert-RegularBootstrapPath $archive
    [IO.File]::Move($partial, $archive)
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdk.sha512) { throw 'Cached Microsoft SDK SHA512 mismatch; preserve and inspect the archive.' }
$sdkRoot = Join-Path $projectRoot 'tools/dotnet'
$sdkExe = Join-Path $sdkRoot 'dotnet.exe'
Assert-RegularBootstrapPath $sdkRoot
$freshSdk = $false
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot "sdk/$($sdk.version)"))) {
    if ((Test-Path -LiteralPath $sdkRoot) -and (Get-ChildItem -LiteralPath $sdkRoot -Force | Select-Object -First 1)) { throw 'Existing SDK folder is preserved. Install the requested SDK into an empty tools/dotnet folder.' }
    New-Item -ItemType Directory -Path $sdkRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkRoot
    $freshSdk = $true
}
if ($freshSdk -and -not $sdk.ContainsKey('installedExecutableSha256')) {
    Assert-RegularBootstrapPath $sdkExe
    $sdk.installedExecutableSha256 = (Get-FileHash -LiteralPath $sdkExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Save-Lock
}
Assert-LockedBootstrapExecutable $sdkExe $sdk
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
& $sdkExe --version
if ($LASTEXITCODE -ne 0) { throw 'Local .NET SDK verification failed.' }
Write-Host "Verified SDK is ready: $sdkExe"
}
if ($Component -ne 'Dotnet') {
    Resolve-RuntimeLock
    Install-Archive $lock.components.python 'python.exe'
    $pythonRoot = Join-Path $projectRoot 'runtime/python'
    $pth = Join-Path $pythonRoot 'python313._pth'
    $pthBackup = "$pth.original"
    Assert-RegularBootstrapPath $pth
    Assert-RegularBootstrapPath $pthBackup
    $expectedPth = "python313.zip`n.`nLib/site-packages`n../../worker`n"
    $originalPth = "python313.zip`r`n.`r`n`r`n# Uncomment to run site.main() automatically`r`n#import site`r`n"
    $currentPth = Get-Content -LiteralPath $pth -Raw
    if ($currentPth.Replace("`r", '') -ne $expectedPth -and $currentPth.Replace("`r", '') -ne $originalPth.Replace("`r", '')) { throw 'Custom Python path file is preserved; inspect python313._pth.' }
    if ($currentPth.Replace("`r", '') -ne $expectedPth) {
        if (Test-Path -LiteralPath $pthBackup) { throw 'Existing Python path backup is preserved; inspect python313._pth.original before retrying.' }
        Assert-RegularBootstrapPath $pth
        Assert-RegularBootstrapPath $pthBackup
        [IO.File]::Copy($pth, $pthBackup, $false)
        Assert-RegularBootstrapPath $pth
        Assert-RegularBootstrapPath $pthBackup
        if ((Get-Content -LiteralPath $pth -Raw) -cne $currentPth) { throw 'Python path file changed during setup and is preserved; inspect the path file and its backup.' }
        Set-Content -LiteralPath $pth -Value $expectedPth -NoNewline -Encoding ascii
    }
    $sitePackages = Join-Path $pythonRoot 'Lib/site-packages'
    $packageMarker = Join-Path $sitePackages '.fk-python-packages.json'
    Assert-RegularBootstrapPath $sitePackages
    Assert-RegularBootstrapPath $packageMarker
    $wheelEntries = @($lock.components.ytDlpPython, $lock.components.ytDlpEjs)
    $wheelFiles = @($wheelEntries | ForEach-Object { Get-VerifiedDownload $_ })
    # Wheel verification may take time; recheck both paths and their ancestors
    # before inspecting or changing the package installation.
    Assert-RegularBootstrapPath $sitePackages
    Assert-RegularBootstrapPath $packageMarker
    if (-not (Test-Path -LiteralPath $packageMarker)) {
        Assert-RegularBootstrapPath $sitePackages
        if ((Test-Path -LiteralPath $sitePackages) -and (Get-ChildItem -LiteralPath $sitePackages -Force | Select-Object -First 1)) { throw 'Existing untracked Python packages are preserved.' }
        Assert-RegularBootstrapPath $sitePackages
        Assert-RegularBootstrapPath $packageMarker
        New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
        foreach ($wheelFile in $wheelFiles) {
            Assert-RegularBootstrapPath $sitePackages
            Assert-RegularBootstrapPath $packageMarker
            [IO.Compression.ZipFile]::ExtractToDirectory($wheelFile, $sitePackages)
        }
        Assert-RegularBootstrapPath $sitePackages
        Assert-RegularBootstrapPath $packageMarker
        if (Test-Path -LiteralPath $packageMarker) { throw 'Existing Python package marker is preserved; inspect .fk-python-packages.json before retrying.' }
        @{ ytDlpSha256 = $lock.components.ytDlpPython.downloadedSha256; ejsSha256 = $lock.components.ytDlpEjs.downloadedSha256 } | ConvertTo-Json | Set-Content -LiteralPath $packageMarker -Encoding utf8
    }
    else {
        Assert-RegularBootstrapPath $sitePackages
        Assert-RegularBootstrapPath $packageMarker
        $packageRecord = Get-Content -LiteralPath $packageMarker -Raw | ConvertFrom-Json -AsHashtable
        if ($packageRecord.ytDlpSha256 -ne $lock.components.ytDlpPython.downloadedSha256 -or $packageRecord.ejsSha256 -ne $lock.components.ytDlpEjs.downloadedSha256) { throw 'Existing Python packages differ from the lock and are preserved.' }
    }
    Install-Archive $lock.components.ytDlp 'yt-dlp.exe'
    Install-Archive $lock.components.deno 'deno.exe'
    Install-Archive $lock.components.sevenZipDecoder '7zr.exe'
    Install-Archive $lock.components.mpv 'mpv.exe'
    Save-License $lock.components.ytDlp 'licenseUrl' 'LICENSE'
    Save-License $lock.components.ytDlp 'thirdPartyLicensesUrl' 'THIRD_PARTY_LICENSES.txt'
    Save-License $lock.components.deno 'licenseUrl' 'LICENSE.md'
    Save-License $lock.components.mpv 'licenseUrl' 'Copyright.upstream'
    & (Join-Path $pythonRoot 'python.exe') -c 'import sys, yt_dlp.version, yt_dlp_ejs; print("CPython", sys.version.split()[0]); print("yt_dlp", yt_dlp.version.__version__); print("yt_dlp_ejs import OK")'
    if ($LASTEXITCODE -ne 0) { throw 'Embedded Python package import check failed.' }
    & (Join-Path $projectRoot 'runtime/yt-dlp/yt-dlp.exe') --ignore-config --version
    if ($LASTEXITCODE -ne 0) { throw 'yt-dlp CLI version check failed.' }
    & (Join-Path $projectRoot 'runtime/deno/deno.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Deno version check failed.' }
    & (Join-Path $projectRoot 'runtime/mpv/mpv.exe') --no-config --version
    if ($LASTEXITCODE -ne 0) { throw 'mpv version check failed.' }
    Write-Host 'All runtime executables and embedded Python packages are ready.'
}
