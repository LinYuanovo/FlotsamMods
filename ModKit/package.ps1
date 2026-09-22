# Packs the installed Flotsam ModKit (Doorstop + BepInEx + host + every mod)
# into a redistributable zip: players extract the contents into their game
# folder (next to Flotsam.exe) and just launch the game.
#
#   .\package.ps1                    pack the currently installed state
#   .\package.ps1 -Rebuild           rebuild + reinstall from source first
#                                     (the game must be closed)
#   .\package.ps1 -GameDir D:\...    pack from another game folder
#   .\package.ps1 -Label beta        zip name becomes FlotsamModKit-beta.zip
#
# Zip layout (exactly these, nothing else — no game files, no personal data):
#   winhttp.dll / doorstop_config.ini / .doorstop_version   Doorstop injector
#   BepInEx\core\*                             BepInEx 5.4.23.5 loader
#   BepInEx\config\BepInEx.cfg
#   BepInEx\plugins\FlotsamModKit\*.dll        host + contracts + game layer
#   Mods\<id>\{mod.json,*.dll}                 every mod
#   安装说明.txt                                generated player README
#
# Per-mod config.json / keybinds.json / trace.log and Mods\ModKit.json are
# intentionally EXCLUDED: the host regenerates them from code defaults on
# first launch (newly discovered mods start enabled — DiscoverMods passes
# default:true), so recipients get a clean install instead of the author's
# window positions and tweak values.

param(
    [string]$GameDir = 'F:\Game\Flotsam',
    [string]$OutDir,
    [switch]$Rebuild,
    [string]$Label
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }

if ($Rebuild) {
    if (Get-Process -Name Flotsam -ErrorAction SilentlyContinue) {
        throw 'Flotsam is running — close the game before -Rebuild (game DLLs are locked).'
    }
    & (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir
}

# ------------------------------------------------------------ validation
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Flotsam.exe'))) {
    throw "not a Flotsam folder: $GameDir"
}
$coreDir   = Join-Path $GameDir 'BepInEx\core'
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\FlotsamModKit'
$modsDir   = Join-Path $GameDir 'Mods'
foreach ($d in $coreDir, $pluginDir, $modsDir) {
    if (-not (Test-Path -LiteralPath $d)) { throw "missing: $d (run build.ps1 first)" }
}

# Host version straight from source (single source of truth).
$hostVersion = '(?)'
$runtimeCs = Join-Path $PSScriptRoot 'src\FlotsamModKit.Host\ModKitRuntime.cs'
$hit = Select-String -LiteralPath $runtimeCs -Pattern 'HostVersion\s*=\s*"([^"]+)"' -ErrorAction SilentlyContinue
if ($hit) { $hostVersion = $hit.Matches[0].Groups[1].Value }

if (-not $Label) { $Label = "$hostVersion-$(Get-Date -Format 'yyyy-MM-dd')" }
$zipPath = Join-Path $OutDir "FlotsamModKit-$Label.zip"

# ---------------------------------------------------------------- staging
$stage = Join-Path ([IO.Path]::GetTempPath()) "flotsam-modkit-package-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    New-Item -ItemType Directory -Path (Join-Path $stage 'BepInEx\core') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'BepInEx\config') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'BepInEx\plugins\FlotsamModKit') -Force | Out-Null

    # Doorstop injector (game-root files; paths inside doorstop_config.ini are relative — portable).
    Copy-Item -LiteralPath (Join-Path $GameDir 'winhttp.dll') $stage -Force
    Copy-Item -LiteralPath (Join-Path $GameDir 'doorstop_config.ini') $stage -Force
    if (Test-Path -LiteralPath (Join-Path $GameDir '.doorstop_version')) {
        Copy-Item -LiteralPath (Join-Path $GameDir '.doorstop_version') $stage -Force
    } else {
        Write-Warning '.doorstop_version not found — skipped (informational file, not required).'
    }

    # BepInEx loader + config.
    Copy-Item -Path (Join-Path $coreDir '*') -Destination (Join-Path $stage 'BepInEx\core') -Force
    $bepConfig = Join-Path $GameDir 'BepInEx\config\BepInEx.cfg'
    if (Test-Path -LiteralPath $bepConfig) {
        Copy-Item -LiteralPath $bepConfig (Join-Path $stage 'BepInEx\config') -Force
    } else {
        Write-Warning 'BepInEx\config\BepInEx.cfg not found — BepInEx will generate a default on first run.'
    }

    # Host + shared contracts.
    foreach ($dll in (Get-ChildItem -LiteralPath $pluginDir -Filter *.dll)) {
        Copy-Item -LiteralPath $dll.FullName (Join-Path $stage 'BepInEx\plugins\FlotsamModKit') -Force
    }

    # Mods: manifest + entry assembly only. Runtime files (config.json,
    # keybinds.json, trace.log) stay out — regenerated with defaults.
    $friendlyKey = @{
        'Alpha0' = '0'; 'Alpha1' = '1'; 'Alpha2' = '2'; 'Alpha3' = '3'; 'Alpha4' = '4'
        'Alpha5' = '5'; 'Alpha6' = '6'; 'Alpha7' = '7'; 'Alpha8' = '8'; 'Alpha9' = '9'
        'Return' = 'Enter'; 'KeypadEnter' = '小键盘Enter'
    }
    $mods = @()
    $modDirs = Get-ChildItem -LiteralPath $modsDir -Directory |
        Where-Object { -not $_.Name.StartsWith('.') } | Sort-Object Name
    foreach ($dir in $modDirs) {
        $manifestPath = Join-Path $dir.FullName 'mod.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            Write-Warning "skip $($dir.Name): no mod.json (host would ignore it too)"
            continue
        }
        $m = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $dllPath = Join-Path $dir.FullName $m.entryAssembly
        if (-not (Test-Path -LiteralPath $dllPath)) {
            throw "$($dir.Name): entry assembly missing: $($m.entryAssembly) — run build.ps1 first"
        }
        $dst = Join-Path $stage "Mods\$($dir.Name)"
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item -LiteralPath $manifestPath $dst -Force
        Copy-Item -LiteralPath $dllPath $dst -Force
        $mods += $m
        Write-Host "    + $($m.id) v$($m.version)" -ForegroundColor DarkCyan
    }
    if ($mods.Count -eq 0) { throw 'no mods found in Mods\ — run build.ps1 first' }

    # ---------------------------------------------------- 安装说明.txt
    $keyText = {
        param($bind)
        $k = if ($friendlyKey.ContainsKey("$($bind.default)")) { $friendlyKey["$($bind.default)"] } else { "$($bind.default)" }
        if ("$($bind.display)".Contains($k)) { "$($bind.display)" } else { "$($bind.display) [默认 $k]" }
    }
    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine("Flotsam ModKit 玩家整合包（host v$hostVersion，api 1.1）")
    [void]$sb.AppendLine("内含 BepInEx 5.4.23.5 模组加载器 + FlotsamModKit 管理器 + $($mods.Count) 个模组。")
    [void]$sb.AppendLine("适配游戏：Flotsam 1.0.1f7（Steam / Windows / Mono）。其他游戏版本不保证可用。")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【安装】')
    [void]$sb.AppendLine('1. 找到游戏安装文件夹（Flotsam.exe 所在的文件夹）。')
    [void]$sb.AppendLine('   Steam：库中右键 Flotsam → 管理 → 浏览本地文件。')
    [void]$sb.AppendLine('2. 打开本压缩包，全选（Ctrl+A）所有内容，解压到该文件夹；')
    [void]$sb.AppendLine('   提示合并文件夹时选「是」。本包不修改、不覆盖任何游戏本体文件。')
    [void]$sb.AppendLine('3. 正常启动游戏。首次启动会生成 BepInEx 缓存，主菜单可能慢 1~2 秒，属正常。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【安装成功的标志】')
    [void]$sb.AppendLine('- 主菜单出现提示「Flotsam ModKit 就绪：N/N 个模组已启用（F10 打开管理器）」。')
    [void]$sb.AppendLine('- 按 F10 打开模组管理器，可启停各模组、改键、查看日志。')
    [void]$sb.AppendLine('- 排错日志：BepInEx\LogOutput.log（每次启动覆盖）。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【模组清单】')
    foreach ($m in $mods) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("■ $($m.name)（v$($m.version)）")
        # Long descriptions are split at circled-number markers (①②③…) so each
        # item gets its own line — layout agreed with the hand-edited 安装说明.txt.
        $desc = "$($m.description)" -replace '(?<!^)(?=[①②③④⑤⑥⑦⑧⑨⑩])', "`r`n"
        [void]$sb.AppendLine($desc)
        if ($m.keybinds -and $m.keybinds.Count -gt 0) {
            $keys = ($m.keybinds | ForEach-Object { & $keyText $_ }) -join '；'
            [void]$sb.AppendLine("  按键：$keys（均可在 F10 管理器改键）")
        }
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【卸载】')
    [void]$sb.AppendLine('删除游戏文件夹内的以下五项即可（不影响游戏本体与存档）：')
    [void]$sb.AppendLine('  BepInEx\  Mods\  winhttp.dll  doorstop_config.ini  .doorstop_version')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【常见问题】')
    [void]$sb.AppendLine('- 杀毒软件报毒 / 游戏无法启动：winhttp.dll 是 BepInEx 的注入组件（Doorstop 代理），')
    [void]$sb.AppendLine('  属常见误报，将其加入白名单/信任区即可。')
    [void]$sb.AppendLine('- 已经装过其他 BepInEx 或其他版本 ModKit：请先卸载（删除 BepInEx\ 等），再解压本包。')
    [void]$sb.AppendLine('- 改键：F10 管理器 →「按键」页，点改绑后按任意键，自动保存。')
    [void]$sb.AppendLine('- 各模组的详细设置存于 Mods\<模组id>\config.json，首次运行自动生成。')
    [void]$sb.AppendLine('- 游戏更新到新版本后模组可能不兼容，请等待整合包更新。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("打包时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $readme = Join-Path $stage '安装说明.txt'
    [IO.File]::WriteAllText($readme, $sb.ToString(), [Text.UTF8Encoding]::new($true)) # BOM for legacy Notepad

    # ------------------------------------------------------------------ zip
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $top = Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName }
    Compress-Archive -Path $top -DestinationPath $zipPath -Force

    # ------------------------------------------------------------ verify
    $required = @(
        'winhttp.dll', 'doorstop_config.ini', '安装说明.txt',
        'BepInEx/core/BepInEx.dll', 'BepInEx/core/BepInEx.Preloader.dll', 'BepInEx/core/0Harmony.dll',
        'BepInEx/config/BepInEx.cfg',
        'BepInEx/plugins/FlotsamModKit/FlotsamModKit.Host.dll',
        'BepInEx/plugins/FlotsamModKit/Flotsam.ModKit.Abstractions.dll',
        'BepInEx/plugins/FlotsamModKit/Flotsam.ModKit.Game.dll'
    )
    foreach ($dir in $modDirs) {
        $m2 = $mods | Where-Object { $_.id -eq $dir.Name }
        if (-not $m2) { continue }
        $required += "Mods/$($dir.Name)/mod.json"
        $required += "Mods/$($dir.Name)/$($m2.entryAssembly)"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
        $missing = $required | Where-Object { $names -notcontains $_ }
        if ($missing) { throw "zip incomplete, missing: $($missing -join ', ')" }
        $fileCount = $zip.Entries.Count
    }
    finally { $zip.Dispose() }

    $size = (Get-Item -LiteralPath $zipPath).Length
    Write-Host ''
    Write-Host "packaged $($mods.Count) mod(s), $fileCount file(s), $([math]::Round($size / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host "zip: $zipPath" -ForegroundColor Green
    Write-Host 'players extract the zip contents into their Flotsam game folder (next to Flotsam.exe)' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
