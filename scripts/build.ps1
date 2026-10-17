param(
  [switch]$FrameworkDependent,
  [switch]$EmbedTools,
  [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LocalTube.App\LocalTube.App.csproj'
$out = Join-Path $root 'dist\YouTubeOffline'

# Materializa o icone a partir de texto versionado no GitHub.
$iconB64 = Join-Path $root 'build\YouTubeOffline.ico.b64'
$iconPath = Join-Path $root 'src\LocalTube.App\Assets\YouTubeOffline.ico'
$faviconPath = Join-Path $root 'src\LocalTube.App\wwwroot\favicon.ico'
if (Test-Path $iconB64) {
  $iconBytes = [Convert]::FromBase64String((Get-Content $iconB64 -Raw).Trim())
  New-Item -ItemType Directory -Path (Split-Path -Parent $iconPath) -Force | Out-Null
  [IO.File]::WriteAllBytes($iconPath, $iconBytes)
  [IO.File]::WriteAllBytes($faviconPath, $iconBytes)
}

if ([string]::IsNullOrWhiteSpace($Version)) {
  $projectText = Get-Content $project -Raw
  $m = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
  if (-not $m.Success) { throw 'Versao nao encontrada no .csproj.' }
  $Version = $m.Groups[1].Value.Trim()
}

$dotnet = $null
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnetCommand) { $dotnet = $dotnetCommand.Source }
if (-not $dotnet) {
  $fallback = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
  if (Test-Path $fallback) { $dotnet = $fallback }
}
if (-not $dotnet) { throw '.NET SDK nao encontrado. Instale o .NET 8 SDK.' }

$sdk8 = & $dotnet --list-sdks | Where-Object { $_ -match '^8\.' } | Select-Object -First 1
if (-not $sdk8) { throw '.NET 8 SDK nao encontrado.' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$args = @('publish', $project, '-c', 'Release', '-o', $out, "-p:Version=$Version")
if (-not $FrameworkDependent) {
  $args += @('-r','win-x64','--self-contained','true')
}

& $dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }

# Releases oficiais voltam a incluir yt-dlp e FFmpeg para que o primeiro download
# comece imediatamente, como nas primeiras versoes do aplicativo. No CI, as
# ferramentas sao mantidas em cache entre builds para evitar baixa-las de novo.
$shouldEmbedTools = $EmbedTools -or ($env:YOUTUBE_OFFLINE_EMBED_TOOLS -eq '1')
if ($shouldEmbedTools) {
  $tools = Join-Path $out 'tools'
  New-Item -ItemType Directory -Path $tools -Force | Out-Null

  $cacheDir = $env:YOUTUBE_OFFLINE_TOOL_CACHE
  if ([string]::IsNullOrWhiteSpace($cacheDir)) { $cacheDir = $tools }
  New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

  try {
    & (Join-Path $PSScriptRoot 'fetch-tools.ps1') -TargetDir $cacheDir

    $cacheFull = [IO.Path]::GetFullPath($cacheDir).TrimEnd('\')
    $toolsFull = [IO.Path]::GetFullPath($tools).TrimEnd('\')
    if (-not $cacheFull.Equals($toolsFull, [StringComparison]::OrdinalIgnoreCase)) {
      Copy-Item (Join-Path $cacheDir 'yt-dlp.exe') (Join-Path $tools 'yt-dlp.exe') -Force
      Copy-Item (Join-Path $cacheDir 'ffmpeg.exe') (Join-Path $tools 'ffmpeg.exe') -Force
      Copy-Item (Join-Path $cacheDir 'ffprobe.exe') (Join-Path $tools 'ffprobe.exe') -Force
    }

    foreach ($required in @('yt-dlp.exe','ffmpeg.exe','ffprobe.exe')) {
      if (-not (Test-Path (Join-Path $tools $required))) { throw "$required nao foi incluido no build." }
    }
    Write-Host 'yt-dlp e FFmpeg incluidos no instalador.' -ForegroundColor Green
  }
  catch {
    if ($env:YOUTUBE_OFFLINE_EMBED_TOOLS -eq '1') { throw }
    Write-Host 'Aviso: nao foi possivel embutir yt-dlp/FFmpeg no instalador.' -ForegroundColor Yellow
    Write-Host $_.Exception.Message -ForegroundColor Yellow
  }
}
else {
  Write-Host 'Build sem ferramentas embutidas. Use -EmbedTools para pacote completo.' -ForegroundColor Cyan
}

Write-Host "Build $Version concluido em: $out" -ForegroundColor Green
