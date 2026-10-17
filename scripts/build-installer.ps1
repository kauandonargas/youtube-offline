param([string]$Version = '', [switch]$NoOpen)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LocalTube.App\LocalTube.App.csproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
  $projectText = Get-Content $project -Raw
  $m = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
  if (-not $m.Success) { throw 'Versao nao encontrada no .csproj.' }
  $Version = $m.Groups[1].Value.Trim()
}

& (Join-Path $PSScriptRoot 'build.ps1') -Version $Version

$possible = @()
if ($env:LOCALAPPDATA) { $possible += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if ($programFilesX86) { $possible += (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe') }
if ($env:ProgramFiles) { $possible += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
$iscc = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Inno Setup nao encontrado e winget indisponivel. Instale o Inno Setup 6 e rode novamente.'
  }
  Write-Host 'Instalando Inno Setup 6 para gerar o Setup.exe...' -ForegroundColor Cyan
  winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
  $iscc = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Nao foi possivel localizar ISCC.exe apos a instalacao.' }

$iss = Join-Path $root 'installer\LocalTube.iss'
& $iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador.' }

$setup = Join-Path $root "dist\installer\YouTube-Offline-Setup-$Version-x64.exe"
if (-not (Test-Path $setup)) { throw "Instalador esperado nao encontrado: $setup" }
Write-Host "`nInstalador criado:" -ForegroundColor Green
Write-Host $setup -ForegroundColor Green
if (-not $NoOpen -and -not $env:CI) { Start-Process explorer.exe -ArgumentList "/select,`"$setup`"" }
