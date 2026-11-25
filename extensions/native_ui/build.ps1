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

$mapFlags = @("-ffile-prefix-map=$here=/src/extensions/native_ui", "-ffile-prefix-map=$SnxRoot=/src/snx")
$dllPath = Join-Path $OutDir 'native_ui.dll'
$testPath = Join-Path $OutDir 'test_model.exe'

Write-Host "==> Building native_ui.dll..." -ForegroundColor Cyan
& $Clang -shared -O2 -Wall -Wextra -Werror @mapFlags -I $incNx -I $incCommon (Join-Path $here 'native_menu.c') -o $dllPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Get-Command $Strip -ErrorAction SilentlyContinue) {
    & $Strip --strip-all $dllPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "==> Building and running test_model.exe..." -ForegroundColor Cyan
& $Clang -O2 -Wall -Wextra -Werror @mapFlags -I $incNx -I $incCommon (Join-Path $here 'test_model.c') -o $testPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $testPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$hash = (Get-FileHash $dllPath -Algorithm SHA256).Hash
Write-Host "==> Built $dllPath (SHA-256: $hash)" -ForegroundColor Green
