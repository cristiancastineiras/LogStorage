; Instalador de LogStorage para Windows.
;
; Instala tres cosas en Program Files:
;   LogStorage.Worker.exe   el servicio que archiva y poda
;   logtool.exe             el CLI de consultas
;   LogStorage.Tray.exe     el panel de control
;
; Y crea C:\ProgramData\LogStorage con el appsettings.json del worker. La carpeta del
; almacén se pregunta durante la instalación: es ahí donde el usuario decide qué carpeta
; usa LogStorage, sin tener que editar JSON.
;
; Para compilarlo:  installer\build.ps1        (publica y llama a ISCC)
; O a mano:         ISCC.exe installer\LogStorage.iss

#define AppName        "LogStorage"
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif
#define AppPublisher   "LogStorage"
#define ServiceName    "LogStorageMaintenance"
#define ServiceDisplay "LogStorage Maintenance"
#define DataDir        "{commonappdata}\LogStorage"

[Setup]
AppId={{6C0F2B41-9F4A-4E1B-9E0F-3B7C9A2D5E88}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
ChangesEnvironment=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "..\artifacts\publish\worker\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\artifacts\publish\cli\*";    DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\artifacts\publish\tray\*";   DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Dirs]
; users-modify para que el panel pueda guardar la configuración sin pedir elevación.
Name: "{#DataDir}"; Permissions: users-modify

[Icons]
Name: "{group}\Panel de LogStorage"; Filename: "{app}\LogStorage.Tray.exe"
Name: "{group}\Consola de LogStorage"; Filename: "{sys}\cmd.exe"; Parameters: "/k ""{app}\logtool.exe"" --help"; WorkingDir: "{app}"
Name: "{autodesktop}\LogStorage"; Filename: "{app}\LogStorage.Tray.exe"; Tasks: desktopicon
Name: "{userstartup}\LogStorage"; Filename: "{app}\LogStorage.Tray.exe"; Parameters: "--tray"; Tasks: startupicon

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked
Name: "startupicon"; Description: "Mostrar el panel en la bandeja al iniciar sesión"
Name: "addtopath";   Description: "Añadir logtool al PATH del sistema"
Name: "startservice"; Description: "Iniciar el servicio al terminar"

[Run]
; Una entrada por linea: Inno Setup no admite continuacion de linea en las secciones.
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\LogStorage.Worker.exe\"" --contentRoot \""{#DataDir}\"""" start= auto DisplayName= ""{#ServiceDisplay}"""; StatusMsg: "Registrando el servicio de mantenimiento..."; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Archiva a Parquet y poda el almacen de logs de LogStorage."""; Flags: runhidden
; Que un ciclo fallido no deje el servicio muerto: reintentos escalonados.
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/60000/restart/120000/restart/300000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; StatusMsg: "Arrancando el servicio..."; Flags: runhidden; Tasks: startservice
Filename: "{app}\LogStorage.Tray.exe"; Description: "Abrir el panel de LogStorage"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
var
  StorePage: TInputDirWizardPage;

{ ── Utilidades ───────────────────────────────────────────────────────────────── }

function EscapeJson(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function ServiceExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '',
                 SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure StopAndDeleteService;
var
  Code: Integer;
begin
  if not ServiceExists then Exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
end;

procedure StopTray;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM LogStorage.Tray.exe', '',
       SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ ── PATH del sistema ─────────────────────────────────────────────────────────── }

const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure AddToPath(const Directory: string);
var
  Current: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Current) then Exit;
  if Pos(';' + Uppercase(Directory) + ';', ';' + Uppercase(Current) + ';') > 0 then Exit;

  if (Current <> '') and (Current[Length(Current)] <> ';') then Current := Current + ';';
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Current + Directory);
end;

procedure RemoveFromPath(const Directory: string);
var
  Current: string;
  Position: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Current) then Exit;

  { La busqueda va sobre la cadena con ';' delante, asi que el indice real en Current es
    Position - 1, salvo cuando el directorio es el primero de todos. }
  Position := Pos(';' + Uppercase(Directory) + ';', ';' + Uppercase(Current) + ';');
  if Position = 0 then Exit;

  if Position = 1 then
    Delete(Current, 1, Length(Directory) + 1)
  else
    Delete(Current, Position - 1, Length(Directory) + 1);
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Current);
end;

{ ── Configuración del worker ─────────────────────────────────────────────────── }

procedure WriteWorkerConfig(const StorePath: string);
var
  Config: string;
  Lines: TArrayOfString;
begin
  Config := ExpandConstant('{#DataDir}\appsettings.json');

  { Si ya existe (reinstalación o actualización) no se toca: el usuario pudo haberla
    ajustado desde el panel y machacarla sería perder su configuración. }
  if FileExists(Config) then Exit;

  SetArrayLength(Lines, 15);
  Lines[0]  := '{';
  Lines[1]  := '  "Logging": {';
  Lines[2]  := '    "LogLevel": { "Default": "Information" }';
  Lines[3]  := '  },';
  Lines[4]  := '  "LogStorage": {';
  Lines[5]  := '    "RootPath": "' + EscapeJson(StorePath) + '",';
  Lines[6]  := '    "Interval": "00:15:00",';
  Lines[7]  := '    "InitialDelay": "00:00:10",';
  Lines[8]  := '    "CompressionLevel": 3,';
  Lines[9]  := '    "ArchiveAfterDays": 2,';
  Lines[10] := '    "SegmentRetentionDays": 0,';
  Lines[11] := '    "ArchiveRetentionDays": 1825,';
  Lines[12] := '    "Reindex": true,';
  Lines[13] := '    "Verify": false';
  Lines[14] := '  }' + #13#10 + '}';

  SaveStringsToUTF8File(Config, Lines, False);
end;

{ ── Asistente ────────────────────────────────────────────────────────────────── }

procedure InitializeWizard;
begin
  StorePage := CreateInputDirPage(wpSelectDir,
    'Carpeta del almacén',
    '¿Dónde se guardan los logs?',
    'Elige una carpeta en un disco local con espacio. Es la carpeta que vigilará el ' +
    'servicio de mantenimiento y en la que escribirán tus aplicaciones.' + #13#10 + #13#10 +
    'Evita Program Files (un servicio no puede escribir ahí) y las unidades de red.',
    False, '');
  StorePage.Add('');
  StorePage.Values[0] := ExpandConstant('{#DataDir}\logs');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Chosen: string;
begin
  Result := True;
  if CurPageID <> StorePage.ID then Exit;

  Chosen := Trim(StorePage.Values[0]);

  if (Length(Chosen) < 3) or (Chosen[2] <> ':') then
  begin
    MsgBox('La carpeta del almacén tiene que ser una ruta absoluta con unidad, ' +
           'por ejemplo C:\ProgramData\LogStorage\logs.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Pos(Uppercase(ExpandConstant('{autopf}')), Uppercase(Chosen)) = 1 then
  begin
    MsgBox('Esa carpeta está dentro de Program Files y un servicio de Windows no puede ' +
           'escribir ahí. Elige otra, por ejemplo C:\ProgramData\LogStorage\logs.',
           mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  case CurStep of
    ssInstall:
      begin
        { Actualización sobre una instalación existente: hay que soltar los ficheros
          antes de copiarlos encima. }
        StopAndDeleteService;
        StopTray;
      end;

    ssPostInstall:
      begin
        ForceDirectories(StorePage.Values[0]);
        WriteWorkerConfig(StorePage.Values[0]);
        if WizardIsTaskSelected('addtopath') then AddToPath(ExpandConstant('{app}'));
      end;
  end;
end;

{ Saca el RootPath del appsettings.json sin parsear JSON de verdad: el fichero lo escriben
  este instalador o el panel, y ambos dejan la clave y su valor en la misma linea. Si no se
  encuentra, se devuelve cadena vacia y el desinstalador no borra nada. }
function ReadConfiguredRootPath: string;
var
  Lines: TArrayOfString;
  Line: string;
  I, Colon, First, Last: Integer;
begin
  Result := '';
  if not LoadStringsFromUTF8File(ExpandConstant('{#DataDir}\appsettings.json'), Lines) then Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    if Pos('"RootPath"', Line) = 0 then Continue;

    Colon := Pos(':', Copy(Line, Pos('"RootPath"', Line) + 10, Length(Line)));
    if Colon = 0 then Exit;

    Line := Copy(Line, Pos('"RootPath"', Line) + 10 + Colon, Length(Line));

    First := Pos('"', Line);
    if First = 0 then Exit;

    Line := Copy(Line, First + 1, Length(Line));
    Last := Pos('"', Line);
    if Last = 0 then Exit;

    Result := Copy(Line, 1, Last - 1);
    StringChangeEx(Result, '\\', '\', True);
    Exit;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StorePath, Question: string;
begin
  if CurUninstallStep <> usUninstall then Exit;

  StopTray;
  RemoveFromPath(ExpandConstant('{app}'));

  StorePath := ReadConfiguredRootPath;

  { Los logs NO se borran nunca sin preguntar, la pregunta dice exactamente que carpeta se
    va a borrar, y la respuesta por defecto es conservarla: tirar anios de historico por un
    clic de mas no tiene arreglo. }
  Question := 'Se va a borrar la configuracion de LogStorage.' + #13#10 + #13#10;

  if (StorePath <> '') and DirExists(StorePath) then
    Question := Question + '¿Borrar también los logs guardados en' + #13#10 +
                '    ' + StorePath + #13#10 + 'y toda su historia?' + #13#10 + #13#10 +
                'Si eliges No (recomendado) esa carpeta se queda intacta y podrás seguir ' +
                'consultándola con logtool o reinstalar sin perder nada.'
  else
    Question := Question + '¿Borrar también la carpeta de configuración?';

  if MsgBox(Question, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;

  DeleteFile(ExpandConstant('{#DataDir}\appsettings.json'));
  DeleteFile(ExpandConstant('{#DataDir}\status.json'));

  if (StorePath <> '') and DirExists(StorePath) then
    DelTree(StorePath, True, True, True);

  RemoveDir(ExpandConstant('{#DataDir}'));
end;
