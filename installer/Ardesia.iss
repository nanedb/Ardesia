; Installazione di Ardesia per il solo utente corrente: NON servono le credenziali di amministratore.
; Compilare con:  ISCC.exe installer\Ardesia.iss   (dopo "dotnet publish", vedi build-release.ps1)

#define AppName "Ardesia"
#define AppVersion "0.8.2"
#define AppExe "Ardesia.exe"

[Setup]
AppId={{8D3F1C52-7A4E-4B19-9E26-5C1A0B7D3E41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Giovanni Dal Bo
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ArdesiaSetup-{#AppVersion}
SetupIconFile=..\src\Ardesia\Assets\Ardesia.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "Crea un'icona sul desktop"; GroupDescription: "Icone aggiuntive:"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userprograms}\{#AppName} – Prova tocco LIM"; Filename: "{app}\{#AppExe}"; Parameters: "--prova-tocco"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Associazione dei file .ardesia (solo per l'utente corrente)
Root: HKCU; Subkey: "Software\Classes\.ardesia"; ValueType: string; ValueName: ""; ValueData: "Ardesia.Notebook"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Ardesia.Notebook"; ValueType: string; ValueName: ""; ValueData: "Blocco appunti Ardesia"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Ardesia.Notebook\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\Ardesia.Notebook\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "Avvia Ardesia"; Flags: nowait postinstall skipifsilent
; Aggiornamento avviato da Ardesia («Aggiorna adesso», /SILENT /AGGIORNA=1): alla fine la si riapre
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsUpdateFromArdesia

[Code]
function IsUpdateFromArdesia: Boolean;
begin
  Result := ExpandConstant('{param:AGGIORNA|0}') = '1';
end;

// Con /ATTENDI=<mutex> (vedi Services/SingleInstance.cs) si aspetta, al massimo un minuto, che l'Ardesia che ha
// scaricato l'aggiornamento abbia finito di chiudersi: salvataggio, PDF per la classe e caricamenti su Drive.
function InitializeSetup: Boolean;
var
  Mutex: String;
  I: Integer;
begin
  Mutex := ExpandConstant('{param:ATTENDI|}');
  if Mutex <> '' then
  begin
    I := 0;
    while CheckForMutexes(Mutex) and (I < 120) do
    begin
      Sleep(500);
      I := I + 1;
    end;
  end;
  Result := True;
end;
