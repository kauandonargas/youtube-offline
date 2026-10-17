param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$ExpectedVersion = "",
    [int]$Port = 43127,
    [int]$StartupTimeoutSeconds = 60,
    [int]$IdleSeconds = 12,
    [Parameter(Mandatory = $true)][string]$MetricsPath
)

$ErrorActionPreference = 'Stop'
if ($IdleSeconds -lt 5) { throw 'IdleSeconds deve ser pelo menos 5.' }
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) { throw "Executavel nao encontrado: $AppPath" }

$app = (Resolve-Path -LiteralPath $AppPath).Path
$metrics = [ordered]@{
    success = $false
    expectedVersion = $ExpectedVersion
    reportedVersion = $null
    startupMs = $null
    idleSeconds = $IdleSeconds
    idleCpuSeconds = $null
    idleCpuPercentOfOneCore = $null
    idleStartWorkingSetMiB = $null
    idleEndWorkingSetMiB = $null
    hasMainWindow = $null
    failure = $null
}
$process = $null
$timer = [System.Diagnostics.Stopwatch]::StartNew()

try {
    $process = Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app) -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $baseUrl = "http://127.0.0.1:$Port"
    $settings = $null

    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "Aplicativo encerrou antes de responder a API (exit code: $($process.ExitCode))." }

        try {
            $settings = Invoke-RestMethod -Uri "$baseUrl/api/settings" -TimeoutSec 2 -ErrorAction Stop
            break
        }
        catch {
            Start-Sleep -Milliseconds 350
        }
    }

    if ($null -eq $settings) { throw "API local nao respondeu em $StartupTimeoutSeconds segundos." }
    $metrics.startupMs = [math]::Round($timer.Elapsed.TotalMilliseconds, 0)
    $metrics.reportedVersion = [string]$settings.update.currentVersion
    if ($ExpectedVersion -and $metrics.reportedVersion -ne $ExpectedVersion) {
        throw "Versao inesperada: $($metrics.reportedVersion); esperada $ExpectedVersion."
    }

    # API de leitura sem acesso ao YouTube; confirma que o backend segue acessivel.
    $null = Invoke-RestMethod -Uri "$baseUrl/api/download/jobs" -TimeoutSec 3 -ErrorAction Stop

    # Descarta o aquecimento inicial e a consulta ao feed de atualizacoes.
    Start-Sleep -Seconds 8
    $process.Refresh()
    if ($process.HasExited) { throw 'Aplicativo fechou durante o aquecimento.' }
    $initial = Get-Process -Id $process.Id -ErrorAction Stop
    $cpuBefore = [double]$initial.CPU
    $metrics.idleStartWorkingSetMiB = [math]::Round($initial.WorkingSet64 / 1MB, 2)

    Start-Sleep -Seconds $IdleSeconds
    $process.Refresh()
    if ($process.HasExited) { throw 'Aplicativo fechou durante a medicao em repouso.' }

    $last = Get-Process -Id $process.Id -ErrorAction Stop
    $cpuDelta = [math]::Max(0, [double]$last.CPU - $cpuBefore)
    $metrics.idleCpuSeconds = [math]::Round($cpuDelta, 3)
    $metrics.idleCpuPercentOfOneCore = [math]::Round(100.0 * $cpuDelta / $IdleSeconds, 2)
    $metrics.idleEndWorkingSetMiB = [math]::Round($last.WorkingSet64 / 1MB, 2)
    $metrics.hasMainWindow = ($last.MainWindowHandle -ne [IntPtr]::Zero)
    $metrics.success = $true
    Write-Host "Startup: $($metrics.startupMs) ms; CPU ociosa: $($metrics.idleCpuPercentOfOneCore)% de 1 nucleo; RAM: $($metrics.idleEndWorkingSetMiB) MiB."
}
catch {
    $metrics.failure = $_.Exception.Message
    throw
}
finally {
    $directory = Split-Path -Parent $MetricsPath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $metrics | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $MetricsPath -Encoding utf8
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
        catch {}
        $process.Dispose()
    }
}
