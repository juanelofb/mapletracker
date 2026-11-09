param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    if (!(Test-Path 'data\tessdata\eng.traineddata')) { & "$PSScriptRoot\Get-OcrData.ps1" }
    dotnet build MapleTracker.sln -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/MapleTracker.Checks -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    dotnet run --project tests/MapleTracker.AppChecks -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'WPF regression checks failed.' }
    if ($Publish) {
        dotnet publish src/MapleTracker.App -c Release -r win-x64 --self-contained false -o artifacts/MapleTracker
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
} finally { Pop-Location }
