# Builds every Flotsam ModKit project and installs the result into the game.
#
#   .\build.ps1                 build + install
#   .\build.ps1 -NoInstall      build only
#   .\build.ps1 -GameDir D:\... other game folder
#
# Requires the .NET SDK (any recent one; the game is Unity 6 / Mono, so the assemblies
# target netstandard2.1). No NuGet packages are used — every reference points at DLLs
# that already exist on disk (game Managed/ + BepInEx core/).

param(
    [string]$GameDir = 'F:\Game\Flotsam',
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$src = Join-Path $PSScriptRoot 'src'
if (-not (Test-Path $src)) { throw "source folder not found: $src" }

$projects = Get-ChildItem $src -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'Probe.csproj' } |
    Sort-Object FullName

foreach ($p in $projects) {
    Write-Host "==> $($p.Name)" -ForegroundColor Cyan
    dotnet build $p.FullName -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed: $($p.FullName)" }
}

if ($NoInstall) { Write-Host 'build only (-NoInstall)'; return }

if (-not (Test-Path (Join-Path $GameDir 'Flotsam.exe'))) {
    throw "not a Flotsam folder: $GameDir"
}

# host + shared contracts: BepInEx loads every DLL in this folder, so the mods can
# resolve IFlotsamMod / GameApi against a single copy.
$hostOut = Join-Path $src 'FlotsamModKit.Host\bin\Debug'
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\FlotsamModKit'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
foreach ($f in 'FlotsamModKit.Host.dll', 'Flotsam.ModKit.Abstractions.dll', 'Flotsam.ModKit.Game.dll') {
    Copy-Item (Join-Path $hostOut $f) $pluginDir -Force
}

# mods: only the mod DLL is copied; mod.json is authored data and must not be overwritten.
$mods = [ordered]@{
    'FlotsamMod.KeyboardMove'   = 'flotsam.keyboardmove'
    'FlotsamMod.BuildingFinder' = 'flotsam.buildingfinder'
    'FlotsamMod.MaterialHelper' = 'flotsam.materialhelper'
    'FlotsamMod.MiniMap'        = 'flotsam.minimap'
    'FlotsamMod.GameplayTweaks' = 'flotsam.gameplaytweaks'
    'FlotsamMod.BatchManager'   = 'flotsam.batchmanager'
    'FlotsamMod.PowerLink'      = 'flotsam.powerlink'
    'FlotsamMod.AutoPriority'   = 'flotsam.autopriority'
    'FlotsamMod.AutoCrew'       = 'flotsam.autocrew'
    'FlotsamMod.PerfBoost'      = 'flotsam.perfboost'
}
foreach ($k in $mods.Keys) {
    $dst = Join-Path $GameDir ('Mods\' + $mods[$k])
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $src "mods\$k\bin\Debug\$k.dll") $dst -Force
    Write-Host "    -> Mods\$($mods[$k])\$k.dll"
}

Write-Host "installed into $GameDir" -ForegroundColor Green
Write-Host 'launch the game; the manager window opens with F10' -ForegroundColor Green