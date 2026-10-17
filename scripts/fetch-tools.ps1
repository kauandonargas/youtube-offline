param(
  [Parameter(Mandatory=$true)][string]$TargetDir
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$yt = Join-Path $TargetDir 'yt-dlp.exe'
if (-not (Test-Path $yt)) {
  Write-Host 'Baixando yt-dlp para incluir no instalador...' -ForegroundColor Cyan
  Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $yt
}

$ffmpeg = Join-Path $TargetDir 'ffmpeg.exe'
$ffprobe = Join-Path $TargetDir 'ffprobe.exe'
if (-not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) {
  Write-Host 'Baixando FFmpeg Essentials para incluir no instalador...' -ForegroundColor Cyan
  $tempRoot = Join-Path $env:TEMP ('youtube-offline-ffmpeg-' + [guid]::NewGuid().ToString('N'))
  $zip = Join-Path $tempRoot 'ffmpeg.zip'
  $extract = Join-Path $tempRoot 'extract'
  New-Item -ItemType Directory -Path $extract -Force | Out-Null
  try {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $ffmpegSource = Get-ChildItem -Path $extract -Filter 'ffmpeg.exe' -Recurse -File | Select-Object -First 1
    $ffprobeSource = Get-ChildItem -Path $extract -Filter 'ffprobe.exe' -Recurse -File | Select-Object -First 1
    if (-not $ffmpegSource) { throw 'ffmpeg.exe nao encontrado no pacote baixado.' }
    if (-not $ffprobeSource) { throw 'ffprobe.exe nao encontrado no pacote baixado.' }
    Copy-Item $ffmpegSource.FullName $ffmpeg -Force
    Copy-Item $ffprobeSource.FullName $ffprobe -Force
  }
  finally {
    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
  }
}

Write-Host 'Ferramentas de download prontas.' -ForegroundColor Green
