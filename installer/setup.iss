; Script de Inno Setup para empaquetar CastDesktop HD como Instalador Único (.exe)
; Requiere Inno Setup 6.x (https://jrsoftware.org/isdl.php)

#define MyAppName "CastDesktop HD"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CastDesktop"
#define MyAppExeName "CastDesktop.exe"
#define PublishDir "..\src\CastDesktop\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{D37E942B-4A1C-4B9E-8628-9844C719F861}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=CastDesktop_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Archivo principal ejecutable auto-contenido de WPF C#
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Transcodificador FFmpeg empaquetado si está presente en la carpeta de publish
Source: "{#PublishDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#MyAppExeName}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
