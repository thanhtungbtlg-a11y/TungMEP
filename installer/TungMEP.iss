#define MyAppName "Tung MEP for Revit 2024"
#define MyAppVersion "1.8.3"
#define MyAppPublisher "Tung MEP"

[Setup]
AppId={{4F91432D-1E96-4F56-91F4-0DA38DFF54F1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Tung MEP add-in installer for Autodesk Revit 2024
DefaultDirName={localappdata}\TungMEP\Revit2024
DefaultGroupName=Tung MEP
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=TungMEP_Setup_1.8.3_Revit2024
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Revit.exe
RestartApplications=no
UninstallDisplayName={#MyAppName}
    UninstallDisplayIcon={app}\TungMEP.Loader.dll
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "stage\TungMEP.Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\payload\TungMEP.Payload.*.dll"; DestDir: "{app}\payload"; Flags: ignoreversion
Source: "stage\payload\current.txt"; DestDir: "{app}\payload"; Flags: ignoreversion
Source: "stage\content\AutoSprinkler\TungMEP Test Sprinkler.rfa"; DestDir: "{app}\content\AutoSprinkler"; Flags: ignoreversion
Source: "stage\pyrevit\TungMEP.extension\*"; DestDir: "{userappdata}\pyRevit\Extensions\TungMEP.extension"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsPyRevitInstalled
Source: "INSTALL_VI.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\TungMEP.Loader.*.dll"
Type: files; Name: "{app}\payload\TungMEP.Payload.*.dll"
Type: filesandordirs; Name: "{userappdata}\pyRevit\Extensions\TungMEP.extension"

[UninstallDelete]
Type: files; Name: "{app}\TungMEP.Loader.*.dll"
Type: files; Name: "{app}\payload\TungMEP.Payload.*.dll"

[Icons]
Name: "{group}\Go cai dat Tung MEP"; Filename: "{uninstallexe}"

[Code]
const
  RevitAddinId = '82718F2F-609B-47AF-AD9E-3D40604DD102';

function XmlEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
  StringChangeEx(Result, '"', '&quot;', True);
end;

function AddinManifestPath(): String;
begin
  Result := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2024\TungMEP.addin');
end;

function IsPyRevitInstalled(): Boolean;
begin
  Result :=
    DirExists(ExpandConstant('{autopf}\pyRevit-Master')) or
    DirExists(ExpandConstant('{userappdata}\pyRevit'));
end;

procedure WriteAddinManifest();
var
  AddinDirectory: String;
  AssemblyPath: String;
  Manifest: String;
begin
  AddinDirectory := ExtractFileDir(AddinManifestPath());
  if not ForceDirectories(AddinDirectory) then
    RaiseException('Khong the tao thu muc Revit Addins: ' + AddinDirectory);

  AssemblyPath := XmlEscape(ExpandConstant('{app}\TungMEP.Loader.dll'));
  Manifest :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>TungMEP</Name>' + #13#10 +
    '    <Assembly>' + AssemblyPath + '</Assembly>' + #13#10 +
    '    <AddInId>' + RevitAddinId + '</AddInId>' + #13#10 +
    '    <FullClassName>TungMEP.Loader.Application</FullClassName>' + #13#10 +
    '    <VendorId>TUNG</VendorId>' + #13#10 +
    '    <VendorDescription>Tung MEP tools for Autodesk Revit</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>' + #13#10;

  if not SaveStringToFile(AddinManifestPath(), Manifest, False) then
    RaiseException('Khong the ghi Revit add-in manifest: ' + AddinManifestPath());
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not DirExists(ExpandConstant('{autopf}\Autodesk\Revit 2024')) then
  begin
    Result := MsgBox(
      'Khong tim thay Autodesk Revit 2024 tren may nay.' + #13#10 +
      'Ban van muon cai Tung MEP de su dung sau?',
      mbConfirmation,
      MB_YESNO) = IDYES;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteAddinManifest();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteFile(AddinManifestPath());
end;
