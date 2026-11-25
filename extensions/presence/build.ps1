param(
    [Parameter(Mandatory = $true)]
    [string]$SnxRoot,
    [string]$Clang = "clang",
    [string]$Strip = "llvm-strip",
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = Join-Path $here 'out' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$incNx = Join-Path $SnxRoot 'src\nx'
$incCommon = Join-Path $SnxRoot 'src\common'
if (-not (Test-Path (Join-Path $incNx 'runtime\native_ui.h'))) {
    throw "Header runtime/native_ui.h not found under $incNx"
}

$mapFlags = @("-ffile-prefix-map=$here=/src/extensions/presence", "-ffile-prefix-map=$SnxRoot=/src/snx")
$dllPath = Join-Path $OutDir 'presence.dll'
$testPath = Join-Path $OutDir 'model_test.exe'

Write-Host "==> Building presence.dll..." -ForegroundColor Cyan
& $Clang -shared -O2 -Wall -Wextra -Werror @mapFlags -I $incNx -I $incCommon (Join-Path $here 'presence.c') -o $dllPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Get-Command $Strip -ErrorAction SilentlyContinue) {
    & $Strip --strip-all $dllPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Copy-Item (Join-Path $here 'presence_names.tsv') (Join-Path $OutDir 'presence_names.tsv') -Force

Write-Host "==> Building and running model_test.exe..." -ForegroundColor Cyan
& $Clang -O2 -Wall -Wextra -Werror @mapFlags -I $incNx -I $incCommon (Join-Path $here 'model_test.c') -o $testPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Push-Location $OutDir
try {
    & $testPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}

$hash = (Get-FileHash $dllPath -Algorithm SHA256).Hash
Write-Host "==> Built $dllPath (SHA-256: $hash)" -ForegroundColor Green
