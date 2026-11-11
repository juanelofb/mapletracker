param([switch]$Publish, [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    if (!(Test-Path 'data\tessdata\eng.traineddata')) { & "$PSScriptRoot\Get-OcrData.ps1" }
    $env:DOTNET_HOST_PATH = (Get-Command $DotNet -ErrorAction Stop).Source
    & $DotNet msbuild MapleTracker.sln -t:Restore -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotNet msbuild MapleTracker.sln -t:Build -p:Configuration=Release -m:1 -p:UseSharedCompilation=false -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotNet exec tests/MapleTracker.Checks/bin/Release/net10.0/MapleTracker.Checks.dll
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    & $DotNet exec tests/MapleTracker.AppChecks/bin/Release/net10.0-windows/MapleTracker.AppChecks.dll
    if ($LASTEXITCODE -ne 0) { throw 'WPF regression checks failed.' }
    if ($Publish) {
        & $DotNet msbuild src/MapleTracker.App/MapleTracker.App.csproj -t:Restore -p:PublishProfile=Standalone -p:RuntimeIdentifier=win-x64 -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'Standalone runtime restore failed.' }
        & $DotNet msbuild src/MapleTracker.App/MapleTracker.App.csproj -t:Publish -p:Configuration=Release -p:PublishProfile=Standalone -p:DebugType=embedded -p:DebugSymbols=false -p:UseSharedCompilation=false -p:PublishDir="$repository/artifacts/standalone-publish/" -m:1 -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        New-Item -ItemType Directory -Path artifacts/MapleTracker-1.1 -Force | Out-Null
        Copy-Item artifacts/standalone-publish/MapleTracker.App.exe artifacts/MapleTracker-1.1/MapleTracker.exe
        Copy-Item README.md,THIRD-PARTY-NOTICES.txt artifacts/MapleTracker-1.1/
    }
} finally { Pop-Location }
