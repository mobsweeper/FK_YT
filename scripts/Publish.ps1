[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-RegularPublishPath([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if(Test-Path -LiteralPath $current){
            if(((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Publish does not follow linked input or output paths.'}
        }
        $current=[IO.Path]::GetDirectoryName($current)
    }
}
function Assert-RuntimeExecutables([string]$Root,[hashtable]$Lock) {
    $runtimeRoot=[IO.Path]::GetFullPath((Join-Path $Root 'runtime'))+[IO.Path]::DirectorySeparatorChar
    $executables=@{python='python.exe';ytDlp='yt-dlp.exe';mpv='mpv.exe';deno='deno.exe';ffmpeg='ffmpeg.exe'}
    foreach($name in $executables.Keys){
        $component=$Lock.components[$name]
        if(-not $component -or $component.installedExecutableSha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw "Missing installed runtime hash: $name"}
        $path=[IO.Path]::GetFullPath((Join-Path (Join-Path $Root $component.installPath) $executables[$name]))
        if(-not $path.StartsWith($runtimeRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime lock path escaped the runtime folder.'}
        Assert-RegularPublishPath $path
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $component.installedExecutableSha256){throw "Installed runtime disagrees with the locked hash: $name"}
    }
}
function Assert-LockedPublishTool([string]$Path, [hashtable]$Entry) {
    if (-not $Entry -or -not $Entry.ContainsKey('installedExecutableSha256') -or
        $Entry.installedExecutableSha256 -isnot [string] -or
        $Entry.installedExecutableSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'Missing reviewed dotnet executable SHA256 pin.' }
    Assert-RegularPublishPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Run scripts/Bootstrap.ps1 first.' }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Entry.installedExecutableSha256) { throw 'dotnet executable disagrees with the locked hash.' }
}
function Assert-RuntimeNotices([string]$Root, [hashtable]$Lock) {
    $notices = @(
        @{ path='python/LICENSE.txt' },
        @{ path='yt-dlp/LICENSE'; component='ytDlp'; pin='licenseUrlSha256' },
        @{ path='yt-dlp/THIRD_PARTY_LICENSES.txt'; component='ytDlp'; pin='thirdPartyLicensesUrlSha256' },
        @{ path='deno/LICENSE.md'; component='deno'; pin='licenseUrlSha256' },
        @{ path='mpv/Copyright.upstream'; component='mpv'; pin='licenseUrlSha256' },
        @{ path='ffmpeg/LICENSE' }
    )
    foreach ($notice in $notices) {
        $path = Join-Path $Root "runtime/$($notice.path)"
        Assert-RegularPublishPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime license notice: $($notice.path). Complete Bootstrap first." }
        if (-not $notice.ContainsKey('pin')) { continue }
        $entry = $Lock.components[$notice.component]
        # Validate existing reviewed pins; do not derive new trust from installed bytes.
        if (-not $entry.ContainsKey($notice.pin)) { continue }
        $expected = $entry[$notice.pin]
        if ($expected -isnot [string] -or $expected -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw "Invalid reviewed license SHA256 pin: $($notice.path)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw "Runtime license notice disagrees with the locked SHA256 pin: $($notice.path)" }
    }
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $projectRoot 'tools/dotnet/dotnet.exe'
$lockPath = Join-Path $projectRoot 'dependencies.lock.json'
Assert-RegularPublishPath $lockPath
$runtimeLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
Assert-LockedPublishTool $dotnet $runtimeLock.components.dotnet
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist/FK_YT' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
if (-not $output.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must be a child of the project dist folder.' }
if([IO.Path]::GetDirectoryName($output) -ne $distRoot -or [IO.Path]::GetFileName($output) -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,100}$'){throw 'Use one release folder directly under dist, with letters, digits, underscores or hyphens.'}
Assert-RegularPublishPath $output
if (Test-Path -LiteralPath $output) {
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) { throw 'Output folder is not empty. Preserve it and choose a new dist output folder.' }
}
foreach ($tool in @('python/python.exe', 'yt-dlp/yt-dlp.exe', 'mpv/mpv.exe', 'deno/deno.exe', 'ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "runtime/$tool"))) { throw "Missing runtime/$tool. Run Bootstrap first." }
}
Assert-RuntimeExecutables $projectRoot $runtimeLock
foreach ($workerFile in @('feed_worker.py', 'export_session.py', 'youtube_session.py','selected_ytdlp.py', 'shorts_feed.py', 'recommendation_actions.py', 'related_feed.py', 'home_feed.py', 'channel_feed.py', 'channel_metadata.py')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "worker/$workerFile"))) { throw "Missing worker file: $workerFile" }
}
Assert-RuntimeNotices $projectRoot $runtimeLock
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'tools/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot 'tools/nuget/http-cache'
$nugetLock = Join-Path $projectRoot 'src/FkYouTube/packages.lock.json'
if (-not (Test-Path -LiteralPath $nugetLock -PathType Leaf)) { throw 'Missing reviewed NuGet lock. Restore explicitly, review the new lock, then publish again.' }
# Publish never updates the reviewed dependency graph implicitly. To update it,
# run restore --force-evaluate -p:RestoreLockedMode=false explicitly and review the lock.
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $dotnet publish (Join-Path $projectRoot 'src/FkYouTube/FkYouTube.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false --nologo -o $output
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE; incomplete output is preserved." }
function Copy-RuntimeTree([string]$Source, [string]$Destination) {
    Assert-RegularPublishPath $Source
    Assert-RegularPublishPath $Destination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Publish does not follow links inside runtime folders.'}
        if ($entry.PSIsContainer) {
            if ($entry.Name -in @('__pycache__', 'data', '.test-temp', 'cache', 'logs', 'temp')) { continue }
            Copy-RuntimeTree $entry.FullName (Join-Path $Destination $entry.Name)
        }
        elseif ($entry.Extension -notin @('.pyc', '.pyo', '.partial')) {
            Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $Destination $entry.Name)
        }
    }
}
Copy-RuntimeTree (Join-Path $projectRoot 'runtime') (Join-Path $output 'runtime')
$workerOutput = Join-Path $output 'worker'
New-Item -ItemType Directory -Path $workerOutput -Force | Out-Null
foreach ($workerFile in @('feed_worker.py', 'export_session.py', 'youtube_session.py','selected_ytdlp.py', 'shorts_feed.py', 'recommendation_actions.py', 'related_feed.py', 'home_feed.py', 'channel_feed.py', 'channel_metadata.py')) {
    Assert-RegularPublishPath (Join-Path $projectRoot "worker/$workerFile")
    Copy-Item -LiteralPath (Join-Path $projectRoot "worker/$workerFile") -Destination $workerOutput
}
foreach ($document in @('LICENSE', 'README.md', 'VERIFICATION.md', 'TOOLCHAIN.md', 'CHANGES_AFTER_PROFILE.txt', 'worker/README.md')) {
    $documentPath = Join-Path $projectRoot $document
    if (Test-Path -LiteralPath $documentPath -PathType Leaf) {
        Assert-RegularPublishPath $documentPath
        Copy-Item -LiteralPath $documentPath -Destination (Join-Path $output $document)
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'dependencies.lock.json') -Destination $output
Copy-Item -LiteralPath $nugetLock -Destination (Join-Path $output 'nuget-packages.lock.json')
$dotnetNotices = Join-Path $output 'notices/dotnet'
New-Item -ItemType Directory -Path $dotnetNotices -Force | Out-Null
foreach ($noticeName in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $env:DOTNET_ROOT $noticeName) -Destination $dotnetNotices
}
$managedDependencies = Get-Content -LiteralPath $nugetLock -Raw | ConvertFrom-Json -AsHashtable
$copiedPackages = @{}
foreach ($framework in $managedDependencies.dependencies.Values) {
    foreach ($package in $framework.GetEnumerator()) {
        if (-not $package.Value.ContainsKey('resolved')) { continue }
        $packageKey = "$($package.Key.ToLowerInvariant())/$($package.Value.resolved)"
        if ($copiedPackages.ContainsKey($packageKey)) { continue }
        $copiedPackages[$packageKey] = $true
        $packageRoot = Join-Path $env:NUGET_PACKAGES $packageKey
        $noticeTarget = Join-Path (Join-Path $output 'notices/nuget') $packageKey
        New-Item -ItemType Directory -Path $noticeTarget -Force | Out-Null
        foreach ($notice in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
            if ($notice.Extension -eq '.nuspec' -or $notice.Name -match '(?i)^(license|copying|notice|third-party-notices)(\..*)?$') {
                $relativeNotice = [IO.Path]::GetRelativePath($packageRoot, $notice.FullName)
                $noticeDestination = Join-Path $noticeTarget $relativeNotice
                New-Item -ItemType Directory -Path (Split-Path -Parent $noticeDestination) -Force | Out-Null
                Copy-Item -LiteralPath $notice.FullName -Destination $noticeDestination
            }
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $output 'data')) { throw 'Unexpected data directory in publish output.' }
$manifest = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
@{ createdUtc = [DateTime]::UtcNow.ToString('o'); files = @($manifest) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding utf8
$releasePointer=Join-Path $distRoot 'current-release.txt'
Assert-RegularPublishPath $releasePointer
Assert-RegularPublishPath ($releasePointer+'.new')
[IO.File]::WriteAllText($releasePointer+'.new',[IO.Path]::GetFileName($output),[Text.Encoding]::ASCII)
[IO.File]::Move($releasePointer+'.new',$releasePointer,$true)
Write-Host "Published win-x64 self-contained application: $output"
