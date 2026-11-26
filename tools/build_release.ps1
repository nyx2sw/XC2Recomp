param(
    [Parameter(Mandatory = $true)]
    [string]$SnxBuildDir,
    [Parameter(Mandatory = $true)]
    [string]$SnxRoot,
    [string]$OutDir = "",
    [string]$DllSourceDir = "",
    [string]$IconPath = "",
    [string]$LocalizationSourceDir = "",
    [string]$GameRomfsDir = "",
    [string]$PcUiAssetsDir = "",
    [string]$RuntimeTestLog = "",
    [string]$ThirdPartyLicenseDir = "",
    [string[]]$PrivateRoot = @(),
    [string]$Strip = "llvm-strip",
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'release\XC2-PC-Edition' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path -LiteralPath $OutDir) { throw "Use uma pasta de saída nova: $OutDir" }
if ([bool]$GameRomfsDir -ne [bool]$PcUiAssetsDir) { throw 'Use -GameRomfsDir e -PcUiAssetsDir juntos.' }

function Get-SourceState([string]$Root) {
    $resolved = (Resolve-Path -LiteralPath $Root).Path
    $revision = & git -C $resolved rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw "Falha ao identificar o commit em $resolved" }
    $tracked = & git -C $resolved diff --name-only HEAD
    if ($LASTEXITCODE -ne 0) { throw "Falha ao listar as alterações em $resolved" }
    $untracked = & git -C $resolved ls-files --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw "Falha ao listar arquivos novos em $resolved" }
    $changed = @($tracked; $untracked) | Where-Object { $_ } | Sort-Object -Unique
    [ordered]@{
        commit = $revision
        dirty = [bool]$changed
        changes = @(foreach ($name in $changed) {
            $file = Join-Path $resolved $name
            [ordered]@{ path = $name; sha256 = if (Test-Path -LiteralPath $file -PathType Leaf) { (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() } }
        })
    }
}

$sources = [ordered]@{ launcher = (Get-SourceState $repoRoot); runtime = (Get-SourceState $SnxRoot) }

$snxExe = Join-Path $SnxBuildDir 'scarletnx.exe'
if (-not (Test-Path $snxExe)) { throw "Built scarletnx.exe not found at $snxExe" }

$extOut = Join-Path $OutDir 'runtime\pc_ui'
New-Item -ItemType Directory -Path $extOut, (Join-Path $OutDir 'runtime\licenses') -Force | Out-Null

# Build the overlay before the launcher so legacy updaters can carry it in the EXE.
$originalMenuOverlayDir = ''
if ($GameRomfsDir) {
    $originalMenuOverlayDir = Join-Path $extOut 'original_menu'
    & python -B (Join-Path $repoRoot 'tools/build_pc_ui_overlay.py') $GameRomfsDir $PcUiAssetsDir $originalMenuOverlayDir
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao preparar o overlay do menu de PC.' }
}

# 1. Build Launcher
Write-Host "==> Building Xenoblade2Launcher.exe..." -ForegroundColor Cyan
$launcherBuildArgs = @{}
if ($IconPath) { $launcherBuildArgs['IconPath'] = $IconPath }
if ($originalMenuOverlayDir) { $launcherBuildArgs['OriginalMenuOverlayDir'] = $originalMenuOverlayDir }
& (Join-Path $repoRoot 'launcher\build.ps1') @launcherBuildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$launcherExe = Join-Path $repoRoot 'launcher\out\win-x64\Xenoblade2Launcher.exe'
if (-not (Test-Path $launcherExe)) { throw "Published launcher not found at $launcherExe" }

# 2. Build native extensions

& (Join-Path $repoRoot 'extensions\native_ui\build.ps1') -SnxRoot $SnxRoot -OutDir $extOut -Strip $Strip
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $repoRoot 'extensions\presence\build.ps1') -SnxRoot $SnxRoot -OutDir $extOut -Strip $Strip
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item (Join-Path $extOut 'test_model.exe'), (Join-Path $extOut 'model_test.exe') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repoRoot 'extensions\presence\presence_names.tsv') (Join-Path $extOut 'presence_names.tsv') -Force

# 3. Stage Launcher + Stripped Runtime + Open-source Runtime DLLs
$rtDir = Join-Path $OutDir 'runtime'
Copy-Item $launcherExe (Join-Path $OutDir 'Xenoblade2Launcher.exe') -Force

$dstExe = Join-Path $rtDir 'xenoblade2.exe'
Copy-Item $snxExe $dstExe -Force

$openSourceDlls = @(
    'libc++.dll', 'libffi-8.dll', 'libiconv-2.dll', 'libLLVM-23.dll',
    'libunwind.dll', 'libxml2-16.dll', 'libzstd.dll', 'zlib1.dll', 'snx_dlss.dll'
)
$dllDirs = @($DllSourceDir, $SnxBuildDir, (Join-Path $SnxBuildDir 'tests')) | Where-Object { $_ -and (Test-Path $_) }
foreach ($dll in $openSourceDlls) {
    $found = $null
    foreach ($d in $dllDirs) {
        $cand = Join-Path $d $dll
        if (Test-Path $cand) { $found = $cand; break }
    }
    if (-not $found) {
        Write-Warning "Optional/runtime DLL not found in search dirs: $dll"
    } else {
        Copy-Item $found (Join-Path $rtDir $dll) -Force
    }
}

$hintsSrc = Join-Path $repoRoot 'tools\xenoblade2.hints'
if (Test-Path $hintsSrc) { Copy-Item $hintsSrc (Join-Path $rtDir 'xenoblade2.hints') -Force }

if ($LocalizationSourceDir -and (Test-Path $LocalizationSourceDir)) {
    foreach ($pack in @('pt-br', 'pt-br-jp')) {
        $srcPack = Join-Path $LocalizationSourceDir $pack
        if (Test-Path (Join-Path $srcPack 'romfs\bf2.arh')) {
            $dstPack = Join-Path $rtDir "localization\$pack"
            New-Item -ItemType Directory -Path (Join-Path $dstPack 'romfs') -Force | Out-Null
            foreach ($rel in @('manifest.json', 'romfs\bf2.arh', 'romfs\bf2.ard.append')) {
                $sf = Join-Path $srcPack $rel
                if (Test-Path $sf) { Copy-Item $sf (Join-Path $dstPack $rel) -Force }
            }
            Write-Host "    Bundled localization pack: $pack" -ForegroundColor DarkCyan
        }
    }
}

foreach ($lic in @('LICENSE', 'LICENSE.apache', 'NOTICE')) {
    Copy-Item (Join-Path $repoRoot $lic) (Join-Path $rtDir "licenses\$lic") -Force
    Copy-Item (Join-Path $repoRoot $lic) (Join-Path $OutDir $lic) -Force
}
if ($ThirdPartyLicenseDir) {
    $licenses = Resolve-Path -LiteralPath $ThirdPartyLicenseDir
    Copy-Item -LiteralPath $licenses.Path -Destination (Join-Path $rtDir 'licenses/third-party') -Recurse
}
Copy-Item (Join-Path $repoRoot 'README.md') (Join-Path $OutDir 'README.md') -Force

$version = ((Get-Item -LiteralPath $launcherExe).VersionInfo.ProductVersion -split '\+', 2)[0]
if (!$version) { throw 'Versão do launcher ausente.' }
$verification = @(
    [ordered]@{ name = 'launcher and JavaScript'; result = 'passed' }
    [ordered]@{ name = 'native_ui model'; result = 'passed' }
    [ordered]@{ name = 'presence model'; result = 'passed' }
)
if ($RuntimeTestLog) {
    $log = Get-Item -LiteralPath $RuntimeTestLog
    $summary = @(Get-Content -LiteralPath $log.FullName | Where-Object { $_ -match '^\s*\d+% tests passed(?:, \d+ tests failed)? out of \d+\s*$' })
    if (!$summary -or $summary[-1] -notmatch '^\s*100% tests passed(?:, 0 tests failed)? out of \d+\s*$') { throw 'O log do CTest não registra uma suíte aprovada.' }
    $verification += [ordered]@{ name = 'runtime CTest'; result = 'passed'; summary = $summary[-1].Trim(); logSha256 = (Get-FileHash -LiteralPath $log.FullName).Hash.ToLowerInvariant() }
}
$artifacts = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = ($_.FullName.Substring($OutDir.TrimEnd('\', '/').Length + 1) -replace '\\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{ schema = 1; version = $version; sources = $sources; verification = $verification; files = $artifacts }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir 'release-manifest.json') -Encoding UTF8

Write-Host "==> Portable release staged at $OutDir" -ForegroundColor Green
if ($Zip) {
    $zipPath = "$OutDir.zip"
    if (Test-Path -LiteralPath $zipPath) { throw "O ZIP já existe: $zipPath" }
    Compress-Archive -Path "$OutDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    $line = '{0}  {1}' -f (Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant(), (Split-Path -Leaf $zipPath)
    Set-Content -LiteralPath "$zipPath.sha256" -Value $line -Encoding ASCII
    $verifyArgs = @($zipPath)
    foreach ($root in $PrivateRoot) { $verifyArgs += @('--private-root', $root) }
    & python -B (Join-Path $repoRoot 'tools/verify_portable_release.py') @verifyArgs
    if ($LASTEXITCODE -ne 0) { throw "Portable archive failed verification: $zipPath" }
    Write-Host "==> Created $zipPath" -ForegroundColor Green
}
