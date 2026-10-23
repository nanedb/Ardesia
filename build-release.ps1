# Crea l'eseguibile unico (publish\Ardesia.exe) e, con il numero di versione nel nome,
# l'installazione per utente (dist\ArdesiaSetup-X.Y.Z.exe) e la versione portatile (dist\Ardesia-portatile-X.Y.Z.exe).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# versione da Ardesia.csproj, da tenere uguale ad AppVersion in installer\Ardesia.iss
$version = ([xml](Get-Content src\Ardesia\Ardesia.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$issVersion = (Select-String -Path installer\Ardesia.iss -Pattern '#define AppVersion "(.+)"').Matches[0].Groups[1].Value
if ($version -ne $issVersion) { throw "Versione diversa: $version in Ardesia.csproj, $issVersion in installer\Ardesia.iss" }

# client Google col secret, fuori dal repository: senza, Ardesia non potrebbe collegarsi a Drive
if (-not (Test-Path google-client.json)) { throw "Manca google-client.json: salva qui il client OAuth scaricato dalla console di Google Cloud" }

dotnet publish src\Ardesia -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 non trovato: installalo per creare ArdesiaSetup-$version.exe" }

New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Q installer\Ardesia.iss
Copy-Item publish\Ardesia.exe "dist\Ardesia-portatile-$version.exe" -Force
Get-ChildItem dist | Sort-Object LastWriteTime | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
