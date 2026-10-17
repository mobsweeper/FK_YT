[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-RegularBuildPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Build does not follow linked tool or lock paths: $Path"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $projectRoot 'tools/dotnet/dotnet.exe'
Assert-RegularBuildPath $dotnet
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'Run scripts/Bootstrap.ps1 -Component Dotnet first.' }
$lockPath = Join-Path $projectRoot 'dependencies.lock.json'
Assert-RegularBuildPath $lockPath
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$sdk = $lock.components.dotnet
if (-not $sdk.ContainsKey('installedExecutableSha256') -or $sdk.installedExecutableSha256 -isnot [string] -or
    $sdk.installedExecutableSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'Missing reviewed dotnet executable SHA256 pin.' }
if ((Get-FileHash -LiteralPath $dotnet -Algorithm SHA256).Hash -ne $sdk.installedExecutableSha256) { throw 'dotnet executable disagrees with the locked hash.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'tools/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot 'tools/nuget/http-cache'
$project = Join-Path $projectRoot 'src/FkYouTube/FkYouTube.csproj'
$nugetLock = Join-Path $projectRoot 'src/FkYouTube/packages.lock.json'
if (-not (Test-Path -LiteralPath $nugetLock -PathType Leaf)) { throw 'Missing reviewed NuGet lock. Restore explicitly, review the new lock, then build again.' }
# Dependency updates are explicit: restore --force-evaluate -p:RestoreLockedMode=false,
# then review packages.lock.json before using the default locked build again.
# Preserve the lock's win-x64 restore graph without changing non-RID build output paths.
$buildArgs = @('build', $project, '-c', $Configuration, '--nologo', '-p:RestoreLockedMode=true', '-p:RuntimeIdentifiers=win-x64')
if ($NoRestore) {
    # This skips graph/lock validation; use only after a verified locked restore.
    $buildArgs += '--no-restore'
}
& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
