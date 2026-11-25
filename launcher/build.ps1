# Builds, tests and publishes Xenoblade2Launcher.exe (single self-contained net10.0-windows exe)
param(
    [switch]$SkipPublish,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

Write-Host "==> Checking launcher/ui/app.js syntax..." -ForegroundColor Cyan
node --check (Join-Path $here 'ui\app.js')
if ($LASTEXITCODE -ne 0) { throw "app.js syntax error" }

if (-not $SkipTests) {
    Write-Host "==> Running XC2.Core.Tests..." -ForegroundColor Cyan
    dotnet run --project (Join-Path $here 'Tests\XC2.Core.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)" }
}

if ($SkipPublish) {
    Write-Host "==> Building XC2.Launcher (Release)..." -ForegroundColor Cyan
    dotnet build (Join-Path $here 'App\XC2.Launcher.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
    return
}

$pubDir = Join-Path $here 'out\win-x64'
Write-Host "==> Publishing self-contained Xenoblade2Launcher.exe..." -ForegroundColor Cyan
dotnet publish (Join-Path $here 'App\XC2.Launcher.csproj') -c Release -r win-x64 -o $pubDir -v minimal
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

$builtExe = Join-Path $pubDir 'Xenoblade2Launcher.exe'
if (-not (Test-Path $builtExe)) { throw "Published exe not found at $builtExe" }

$destExe = Join-Path $root 'Xenoblade2Launcher.exe'
$tmpExe = "$destExe.tmp.$PID"
Copy-Item $builtExe $tmpExe -Force
Move-Item $tmpExe $destExe -Force

$fi = Get-Item $destExe
$hash = (Get-FileHash $destExe -Algorithm SHA256).Hash
Write-Host ("==> Published {0} ({1:N1} MB) SHA-256: {2}" -f $fi.FullName, ($fi.Length / 1MB), $hash) -ForegroundColor Green
