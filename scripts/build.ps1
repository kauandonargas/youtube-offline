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

# Nao embute yt-dlp/FFmpeg nas releases por padrao. Essas ferramentas ficam em
# %LOCALAPPDATA%\YouTube Offline\tools, sao preparadas no primeiro download e
# sobrevivem as atualizacoes do aplicativo. Isso reduz muito o tempo e o tamanho
# de cada instalador. Use -EmbedTools apenas quando quiser um pacote totalmente
# pre-carregado.
if ($EmbedTools) {
  $tools = Join-Path $out 'tools'
  New-Item -ItemType Directory -Path $tools -Force | Out-Null
  try {
    & (Join-Path $PSScriptRoot 'fetch-tools.ps1') -TargetDir $tools
  }
  catch {
    Write-Host 'Aviso: nao foi possivel embutir yt-dlp/FFmpeg no instalador.' -ForegroundColor Yellow
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host 'O aplicativo tentara prepara-los automaticamente no primeiro download.' -ForegroundColor Yellow
  }
}
else {
  Write-Host 'Build rapido: yt-dlp/FFmpeg serao mantidos no perfil do usuario e preparados sob demanda.' -ForegroundColor Cyan
}

Write-Host "Build $Version concluido em: $out" -ForegroundColor Green
