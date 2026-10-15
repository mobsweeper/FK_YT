@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "FKYT_ROOT=%~dp0"
set "FKYT_RELEASE=FK_YT-qualityreview-r4-20261016"
if exist "%FKYT_ROOT%dist\current-release.txt" set /p "FKYT_RELEASE="<"%FKYT_ROOT%dist\current-release.txt"
setlocal EnableDelayedExpansion
set "FKYT_INVALID="
for /f "eol=| delims=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-" %%A in ("!FKYT_RELEASE!") do set "FKYT_INVALID=1"
if defined FKYT_INVALID (
  echo Invalid release folder. Run scripts\Publish.ps1 again.
  pause
  exit /b 1
)
if not exist "!FKYT_ROOT!dist\!FKYT_RELEASE!\FK_YT.exe" (
  echo Build the portable app with scripts\Publish.ps1 first.
  pause
  exit /b 1
)
start "" "!FKYT_ROOT!dist\!FKYT_RELEASE!\FK_YT.exe" --root "!FKYT_ROOT!."
