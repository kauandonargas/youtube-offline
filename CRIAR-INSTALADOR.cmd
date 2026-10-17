@echo off
setlocal
cd /d "%~dp0"
title YouTube Offline - Criar instalador

echo ==========================================
echo   YouTube Offline 0.5.0 - Criar Setup.exe
echo ==========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
)

dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo O .NET 8 SDK nao foi encontrado.
  where winget >nul 2>nul
  if errorlevel 1 (
    echo Instale o .NET 8 SDK e execute este arquivo novamente.
    echo https://aka.ms/dotnet/download
    pause
    exit /b 1
  )
  echo.
  choice /C SN /N /M "Instalar o .NET 8 SDK automaticamente com winget? [S/N]: "
  if errorlevel 2 exit /b 1
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (
    echo Falha ao instalar o .NET 8 SDK.
    pause
    exit /b 1
  )
  set "PATH=%ProgramFiles%\dotnet;%PATH%"
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\build-installer.ps1"
if errorlevel 1 (
  echo.
  echo Ocorreu um erro. Copie o texto desta janela e envie para analise.
  pause
  exit /b 1
)

echo.
echo Instalador criado com sucesso.
pause
