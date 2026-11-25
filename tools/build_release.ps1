param(
    [Parameter(Mandatory = $true)]
    [string]$SnxBuildDir,
    [Parameter(Mandatory = $true)]
    [string]$SnxRoot,
    [string]$OutDir = "",
    [string]$DllSourceDir = "",
    [string]$IconPath = "",
    [string]$Strip = "llvm-strip",
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'release\XC2-PC-Edition' }

$snxExe = Join-Path $SnxBuildDir 'scarletnx.exe'
if (-not (Test-Path $snxExe)) { throw "Built scarletnx.exe not found at $snxExe" }

# 1. Build Launcher
Write-Host "==> Building Xenoblade2Launcher.exe..." -ForegroundColor Cyan
$launcherBuildArgs = @{}
if ($IconPath) { $launcherBuildArgs['IconPath'] = $IconPath }
& (Join-Path $repoRoot 'launcher\build.ps1') @launcherBuildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$launcherExe = Join-Path $repoRoot 'launcher\out\win-x64\Xenoblade2Launcher.exe'
if (-not (Test-Path $launcherExe)) { throw "Published launcher not found at $launcherExe" }

# 2. Build native extensions
$extOut = Join-Path $OutDir 'runtime\pc_ui'
New-Item -ItemType Directory -Path $extOut, (Join-Path $OutDir 'runtime\licenses') -Force | Out-Null

& (Join-Path $repoRoot 'extensions\native_ui\build.ps1') -SnxRoot $SnxRoot -OutDir $extOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $repoRoot 'extensions\presence\build.ps1') -SnxRoot $SnxRoot -OutDir $extOut
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

foreach ($lic in @('LICENSE', 'LICENSE.apache', 'NOTICE')) {
    Copy-Item (Join-Path $repoRoot $lic) (Join-Path $rtDir "licenses\$lic") -Force
    Copy-Item (Join-Path $repoRoot $lic) (Join-Path $OutDir $lic) -Force
}
Copy-Item (Join-Path $repoRoot 'README.md') (Join-Path $OutDir 'README.md') -Force

Write-Host "==> Portable release staged at $OutDir" -ForegroundColor Green
if ($Zip) {
    $zipPath = "$OutDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$OutDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "==> Created $zipPath" -ForegroundColor Green
}
