@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================================
rem  CaptureCanva one-click release build
rem  Publishes a self-contained win-x64 ZIP (same layout as the CI release),
rem  including the pinned FFmpeg bundle. No push, no tag: CI remains the
rem  official release channel - run it again with a v* tag when ready.
rem
rem  Usage:
rem    scripts\release.bat            -> dist\CaptureCanva-<version>-win-x64.zip
rem    scripts\release.bat -Run       -> ... and launch the published exe afterwards
rem ============================================================================

set "ROOT=%~dp0.."
rem Normalize (resolve "scripts\.." to a clean absolute path) - xcopy/robocopy
rem reject ".." segments in the middle of a source path.
for %%I in ("%ROOT%") do set "ROOT=%%~fI"
set "PUBLISH_DIR=%ROOT%\publish"
set "DIST_DIR=%ROOT%\dist"
set "PROJECT=%ROOT%\src\CaptureCanva\CaptureCanva.csproj"

if not exist "%PROJECT%" (
    echo [ERROR] Project not found: %PROJECT%
    goto :fail
)

rem --- read version from the csproj (first ^<Version^> tag) --------------------
set "VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command ^
    "Select-Xml -Path '%PROJECT%' -XPath '/Project/PropertyGroup/Version/text()' | Select-Object -First 1 -ExpandProperty Node | ForEach-Object Value"`) do set "VERSION=%%V"
if not defined VERSION (
    echo [ERROR] Could not read ^<Version^> from CaptureCanva.csproj
    goto :fail
)

echo.
echo ============================================================
echo  CaptureCanva %VERSION% - release build
echo   publish : %PUBLISH_DIR%
echo   dist    : %DIST_DIR%
echo ============================================================
echo.

rem --- clean previous output ---------------------------------------------------
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
if errorlevel 1 (
    echo [ERROR] Failed to clean previous publish/dist output. Close any running CaptureCanva.exe and retry.
    goto :fail
)
rem Compress-Archive fails if the destination directory does not exist yet.
mkdir "%DIST_DIR%" 2>nul

rem --- publish self-contained single file (mirrors CI release job) --------------
echo [1/4] Publishing self-contained single file...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:DebugType=none
if errorlevel 1 (
    echo [ERROR] dotnet publish failed.
    goto :fail
)

rem dotnet -o is omitted on purpose: default output is
rem src\CaptureCanva\bin\Release\net10.0-windows10.0.22621.0\win-x64\publish\
rem (the runtime identifier adds a win-x64 segment for RID-specific publish)
set "PUBLISH_OUTPUT=%ROOT%\src\CaptureCanva\bin\Release\net10.0-windows10.0.22621.0\win-x64\publish"
if not exist "%PUBLISH_OUTPUT%\CaptureCanva.exe" (
    echo [ERROR] Published exe not found: %PUBLISH_OUTPUT%\CaptureCanva.exe
    goto :fail
)

rem --- bundle FFmpeg + licenses (same pinned script CI uses) --------------------
echo [2/4] Bundling FFmpeg 9.0.2 (checksum-verified)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\Bundle-Ffmpeg.ps1" -OutputDirectory "%PUBLISH_OUTPUT%"
if errorlevel 1 (
    echo [ERROR] FFmpeg bundling failed.
    goto :fail
)

rem --- collect into publish\ (CI layout: exe + ffmpeg + licenses) ---------------
echo [3/4] Collecting portable layout...
rem Unquoted xcopy: cmd's quoted-source xcopy mis-parses some absolute paths
rem ("Invalid number of parameters"); none of these paths contain spaces.
xcopy "%PUBLISH_OUTPUT%\*" "%PUBLISH_DIR%\" /e /i /y /q >nul 2>&1 || xcopy %PUBLISH_OUTPUT%\* %PUBLISH_DIR%\ /e /i /y /q >nul
if errorlevel 1 (
    echo [ERROR] Failed to collect publish output.
    goto :fail
)

rem --- zip -----------------------------------------------------------------------
echo [4/4] Creating ZIP...
set "ZIP_PATH=%DIST_DIR%\CaptureCanva-%VERSION%-win-x64.zip"
rem -Path folder itself: Compress-Archive's "folder\*" glob fails when the
rem parent dir was freshly recreated ("ArchiveCmdletPathNotFound"). Work from
rem inside the folder so the ZIP root holds the files directly (CI layout).
powershell -NoProfile -Command "Set-Location '%PUBLISH_DIR%'; Compress-Archive -Path '*' -DestinationPath '%ZIP_PATH%' -Force"
if errorlevel 1 (
    echo [ERROR] Failed to create ZIP: %ZIP_PATH%
    goto :fail
)

rem --- summary -------------------------------------------------------------------
for %%S in ("%ZIP_PATH%") do set "ZIP_SIZE_MB="
for /f %%A in ('powershell -NoProfile -Command "[math]::Round((Get-Item '%ZIP_PATH%').Length/1MB,1)"') do set "ZIP_SIZE_MB=%%A"

echo.
echo ============================================================
echo  DONE  CaptureCanva %VERSION%
echo   ZIP  : %ZIP_PATH%  (%ZIP_SIZE_MB% MB)
echo   Folder: %PUBLISH_DIR%
echo ============================================================

if /i "%~1"=="-Run" (
    echo.
    echo Launching published app...
    start "" "%PUBLISH_DIR%\CaptureCanva.exe"
)

endlocal & exit /b 0

:fail
echo.
echo [FAILED] Release build did not complete. See messages above.
endlocal
exit /b 1
