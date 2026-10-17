$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$program = Get-Content (Join-Path $root 'src\LocalTube.App\Program.cs') -Raw
$updater = Get-Content (Join-Path $root 'src\LocalTube.App\UpdateService.cs') -Raw
$installer = Get-Content (Join-Path $root 'installer\LocalTube.iss') -Raw

# Uma versao futura nao pode reintroduzir a instalacao silenciosa na abertura.
if ($program.Contains('TryInstallAvailableUpdateAsync')) {
    throw 'Regressao: inicializacao ainda chama atualizacao automatica com instalacao.'
}
if ($updater.Contains('TryInstallAvailableUpdateAsync')) {
    throw 'Regressao: metodo de instalacao automatica obsoleto foi reintroduzido.'
}
$match = [regex]::Match($program, 'form\.Shown\s*\+=.*?(?=Application\.Run\(form\))', 'Singleline')
if (-not $match.Success -or -not $match.Value.Contains('await updater.CheckAsync()')) {
    throw 'Abertura deve consultar atualizacoes sem instalar.'
}
if ($match.Value.Contains('Application.Exit') -or $match.Value.Contains('LaunchInstaller')) {
    throw 'Regressao: startup encerra aplicativo ou inicia instalador.'
}

# Verificacao estrutural da politica de sobrescrita de ferramentas do instalador.
if ($installer -notmatch 'Excludes:\s*"tools\\\*"') {
    throw 'O instalador deixou de excluir tools do grupo de arquivos atualizaveis.'
}
if ($installer -notmatch 'DestDir:\s*"\{app\}\\tools".*onlyifdoesntexist') {
    throw 'O instalador pode voltar a sobrescrever ferramentas em uso.'
}
Write-Host 'Guardas de atualizacao inicial e preservacao de ferramentas passaram.'
