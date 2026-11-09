$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $repository 'data\tessdata'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Invoke-WebRequest 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata' -OutFile (Join-Path $destination 'eng.traineddata')
Invoke-WebRequest 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/LICENSE' -OutFile (Join-Path $destination 'LICENSE')
Write-Host 'English Tesseract data ready. Rebuild the app to copy it to the output.'
