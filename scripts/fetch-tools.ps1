param(
  [Parameter(Mandatory=$true)][string]$TargetDir
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

# yt-dlp muda com frequência conforme o YouTube altera extração e URLs de mídia.
# Nunca reutilize a cópia antiga do cache no pacote final: baixe o nightly atual.
$yt = Join-Path $TargetDir 'yt-dlp.exe'
$ytTemp = Join-Path $TargetDir ('yt-dlp-' + [guid]::NewGuid().ToString('N') + '.download')
try {
  Write-Host 'Atualizando yt-dlp nightly para o instalador...' -ForegroundColor Cyan
  Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe' -OutFile $ytTemp
  if ((Get-Item $ytTemp).Length -lt 1MB) { throw 'Download do yt-dlp parece incompleto.' }
  Move-Item $ytTemp $yt -Force
  $ytVersion = & $yt --version
  Write-Host "yt-dlp: $ytVersion" -ForegroundColor Green
}
finally {
  if (Test-Path $ytTemp) { Remove-Item $ytTemp -Force -ErrorAction SilentlyContinue }
}

# O yt-dlp atual usa um runtime JavaScript para resolver desafios do YouTube.
$deno = Join-Path $TargetDir 'deno.exe'
if (-not (Test-Path $deno)) {
  Write-Host 'Baixando Deno para os desafios JavaScript do YouTube...' -ForegroundColor Cyan
  $tempRoot = Join-Path $env:TEMP ('youtube-offline-deno-' + [guid]::NewGuid().ToString('N'))
  $zip = Join-Path $tempRoot 'deno.zip'
  $extract = Join-Path $tempRoot 'extract'
  New-Item -ItemType Directory -Path $extract -Force | Out-Null
  try {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $denoSource = Get-ChildItem -Path $extract -Filter 'deno.exe' -Recurse -File | Select-Object -First 1
    if (-not $denoSource) { throw 'deno.exe nao encontrado no pacote baixado.' }
    Copy-Item $denoSource.FullName $deno -Force
  }
  finally {
    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
  }
}
$denoVersion = & $deno --version | Select-Object -First 1
Write-Host "Deno: $denoVersion" -ForegroundColor Green

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
