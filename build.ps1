# Builds everything and runs the tests.
#
# This exists because "I edited Core, ran the tests, and then tested against a stale app" happened three
# times in one afternoon. The test project builds its OWN copy of Core, so a green test run says nothing
# about whether BloomFreezeDoctor.exe contains your change. Use this before testing the app by hand.
#
#   .\build.ps1              # build everything, run the tests
#   .\build.ps1 -SkipTests   # build only
#
# Note it stops any running Doctor first: a running instance holds its own DLLs open and the build fails
# with MSB3027, which reads as a mysterious error rather than as "your app is still running".

param(
    [switch]$SkipTests,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$running = Get-Process -Name BloomFreezeDoctor -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping $($running.Count) running Doctor instance(s) so their DLLs are not locked..."
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Write-Host "Building ($Configuration)..."
# .slnx, not .sln: this repo's solution was created by the .NET 10 SDK, which produces the newer XML format.
dotnet build BloomFreezeDoctor.slnx --configuration $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (-not $SkipTests) {
    Write-Host "Testing..."
    dotnet test tests/BloomFreezeDoctor.Core.Tests --configuration $Configuration --no-build --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

# Print what was actually produced, with timestamps, because the whole point of this script is to make
# "am I running the code I just wrote?" answerable at a glance.
$exe = "src/BloomFreezeDoctor/bin/$Configuration/net8.0-windows/BloomFreezeDoctor.exe"
if (Test-Path $exe) {
    $built = (Get-Item $exe).LastWriteTime
    Write-Host ""
    Write-Host "BloomFreezeDoctor.exe built at $built"
    Write-Host "  $((Resolve-Path $exe).Path)"
}
