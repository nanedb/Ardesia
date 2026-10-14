# Crea l'eseguibile unico (publish\Ardesia.exe) e l'installazione per utente (dist\ArdesiaSetup.exe).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish src\Ardesia -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 non trovato: installalo per creare ArdesiaSetup.exe" }

New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Q installer\Ardesia.iss
Copy-Item publish\Ardesia.exe dist\Ardesia-portatile.exe -Force
Get-ChildItem dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
