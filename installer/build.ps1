<#
.SYNOPSIS
    Publica LogStorage y compila el instalador de Windows.

.DESCRIPTION
    Publica el worker, el CLI y el panel de control en artifacts\publish y, si encuentra
    Inno Setup, compila artifacts\installer\LogStorage-<version>-setup.exe.

    Todo se publica self-contained: el instalador no exige tener .NET 10 en la maquina de
    destino. A cambio el instalador ronda los 90 MB. Con -FrameworkDependent baja a ~5 MB
    pero la maquina necesita el runtime de .NET 10.

.PARAMETER Version
    Version del producto. Se escribe en el .iss antes de compilar.

.PARAMETER FrameworkDependent
    Publica sin runtime incluido.

.PARAMETER SkipInstaller
    Solo publica; no llama a Inno Setup.

.EXAMPLE
    .\installer\build.ps1
    .\installer\build.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [switch] $FrameworkDependent,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish   = Join-Path $artifacts 'publish'

Write-Host "LogStorage $Version" -ForegroundColor Cyan
Write-Host "Repositorio: $root"

# ── Publicar ────────────────────────────────────────────────────────────────────

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

$projects = @(
    @{ Name = 'worker'; Path = 'LogStorage.Worker\LogStorage.Worker.csproj' },
    @{ Name = 'cli';    Path = 'LogStorage.CLI\LogStorage.CLI.csproj' },
    @{ Name = 'tray';   Path = 'LogStorage.Tray\LogStorage.Tray.csproj' }
)

foreach ($project in $projects) {
    $out = Join-Path $publish $project.Name
    Write-Host "`nPublicando $($project.Name)..." -ForegroundColor Yellow

    $arguments = @(
        'publish', (Join-Path $root $project.Path),
        '-c', 'Release',
        '-r', 'win-x64',
        '-o', $out,
        "-p:Version=$Version",
        '--nologo'
    )

    if ($FrameworkDependent) {
        $arguments += '--self-contained'; $arguments += 'false'
    } else {
        $arguments += '--self-contained'; $arguments += 'true'
        $arguments += '-p:PublishTrimmed=false'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $($project.Name)." }
}

# Los .pdb no pintan nada en una instalación y son la mitad del peso.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force

$size = [math]::Round((Get-ChildItem $publish -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "`nPublicado en $publish ($size MB)" -ForegroundColor Green

foreach ($expected in 'worker\LogStorage.Worker.exe', 'cli\logtool.exe', 'tray\LogStorage.Tray.exe') {
    $file = Join-Path $publish $expected
    if (-not (Test-Path $file)) { throw "Falta $expected. Revisa el AssemblyName del proyecto." }
}

if ($SkipInstaller) { Write-Host 'Instalador omitido (-SkipInstaller).'; return }

# ── Compilar el instalador ──────────────────────────────────────────────────────

$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning @"
No se ha encontrado Inno Setup 6 (ISCC.exe).

Instalalo con:   winget install -e --id JRSoftware.InnoSetup
Y vuelve a ejecutar este script. Hace falta la version 6.3 o superior
(el script usa ArchitecturesAllowed=x64compatible).

Los binarios publicados estan listos en:
  $publish
"@
    return
}

Write-Host "`nCompilando el instalador con $iscc..." -ForegroundColor Yellow

New-Item -ItemType Directory -Force -Path (Join-Path $artifacts 'installer') | Out-Null

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'LogStorage.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC ha fallado.' }

$setup = Join-Path $artifacts "installer\LogStorage-$Version-setup.exe"
$setupSize = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host "`nListo: $setup ($setupSize MB)" -ForegroundColor Green
