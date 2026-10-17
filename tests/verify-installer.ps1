param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$WorkDir
)

$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
$installDir = Join-Path $WorkDir 'installed'
$logsDir = Join-Path $WorkDir 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

function Close-TestApp {
    # O script roda apenas no runner Windows efemero do CI.
    Get-Process -Name 'YouTubeOffline' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Invoke-TestInstallation([string]$label) {
    $log = Join-Path $logsDir "$label.log"
    $arguments = @(
        '/SP-',
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/CLOSEAPPLICATIONS',
        ('/DIR="{0}"' -f $installDir),
        ('/LOG="{0}"' -f $log)
    )
    $setup = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru
    try {
        if (-not $setup.WaitForExit(180000)) {
            try { $setup.Kill() } catch {}
            throw "$label excedeu 180 segundos."
        }
        if ($setup.ExitCode -ne 0) {
            throw "$label retornou codigo $($setup.ExitCode). Veja $log."
        }
    }
    finally {
        $setup.Dispose()
        Close-TestApp
    }
}

try {
    Invoke-TestInstallation 'initial'
    $ytdlp = Join-Path $installDir 'tools\yt-dlp.exe'
    if (-not (Test-Path -LiteralPath $ytdlp -PathType Leaf)) {
        throw "yt-dlp nao foi instalado em: $ytdlp"
    }
    $before = (Get-FileHash -LiteralPath $ytdlp -Algorithm SHA256).Hash

    # Reproduz DeleteFile failed; codigo 5: mantem o binario aberto sem
    # compartilhamento para escrita durante uma segunda instalacao.
    $stream = [System.IO.File]::Open(
        $ytdlp,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None
    )
    try {
        Invoke-TestInstallation 'locked-upgrade'
    }
    finally {
        $stream.Dispose()
    }

    $after = (Get-FileHash -LiteralPath $ytdlp -Algorithm SHA256).Hash
    if ($before -ne $after) {
        throw 'O instalador alterou o yt-dlp bloqueado; esperava preservar o binario.'
    }
    Write-Host 'Instalacao e reinstalacao com yt-dlp bloqueado: PASSOU.'
}
finally {
    Close-TestApp
}
