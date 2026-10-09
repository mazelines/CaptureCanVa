[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,
    [string] $CacheDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'CaptureCanva-ffmpeg-9.0.2')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pin both the version and checksum so a changed upstream download cannot enter a release.
$version = '9.0.2'
$archiveName = "ffmpeg-$version-essentials_build.zip"
$downloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$expectedHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$sourceUrl = 'https://github.com/FFmpeg/FFmpeg/commit/946fcce07b'

New-Item -ItemType Directory -Path $OutputDirectory, $CacheDirectory -Force | Out-Null
$archivePath = Join-Path $CacheDirectory $archiveName
if (-not (Test-Path -LiteralPath $archivePath) -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 600 --output $archivePath $downloadUrl
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg download failed (exit $LASTEXITCODE)." }
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'FFmpeg checksum mismatch; refusing to package the download.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $files = @{
        'bin/ffmpeg.exe' = 'ffmpeg.exe'
        'LICENSE' = 'licenses/ffmpeg/LICENSE.txt'
        'README.txt' = 'licenses/ffmpeg/README.txt'
    }
    foreach ($file in $files.GetEnumerator()) {
        $entry = $archive.GetEntry("ffmpeg-$version-essentials_build/$($file.Key)")
        if ($null -eq $entry) { throw "Missing FFmpeg archive entry: $($file.Key)" }
        $destination = Join-Path $OutputDirectory $file.Value
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    }
}
finally {
    $archive.Dispose()
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
$sourceNotice = @"
FFmpeg $version (Gyan release essentials, Windows x64 static build)
Binary package: $downloadUrl
Package SHA-256: $expectedHash
FFmpeg source: $sourceUrl
FFmpeg source archive: https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.tar.gz
Upstream build information: https://www.gyan.dev/ffmpeg/builds/

This is an unmodified upstream executable, licensed under GPLv3.
LICENSE.txt contains the upstream license. README.txt preserves the build
configuration and the exact versions of the external libraries in this build.
See THIRD-PARTY-NOTICES.md for source locations.
"@
Set-Content -LiteralPath (Join-Path $OutputDirectory 'licenses/ffmpeg/SOURCE.txt') -Value $sourceNotice -Encoding UTF8

$ffmpegPath = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'ffmpeg.exe'
& $ffmpegPath -version
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg cannot run.' }
# Exercise software H.264, AAC, and MP4 muxing without requiring a GPU or audio device.
& $ffmpegPath -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=256x256:r=30' `
    -f lavfi -i 'anullsrc=r=48000:cl=stereo' -t 0.2 `
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -f mp4 -movflags frag_keyframe+empty_moov -y NUL
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg H.264/AAC smoke test failed.' }

Write-Host "Bundled FFmpeg $version and its notices in $OutputDirectory."
