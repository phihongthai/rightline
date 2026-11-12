; Inno Setup 6 script. Build with build-installer.ps1 (publishes into ..\publish first).
; Silent/scripted install:  KidSafeSetup.exe /VERYSILENT /BACKEND=https://api.example.com
#define AppName "KidSafe Clone"

[Setup]
AppId={{7B0F3E52-5C0D-4B52-9C61-6B0D3C6A91A4}
AppName={#AppName}
AppVersion=0.1.0
DefaultDirName={commonpf64}\KidSafeClone
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=KidSafeSetup
Compression=lzma2
SolidCompression=yes
UninstallDisplayName={#AppName}
; The agent runs from this folder; keep it out of the "repair/modify" flow.
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Run]
; Order matters: create -> harden (recovery + ACLs) -> start.
Filename: "{sys}\sc.exe"; Parameters: "create KidSafeAgent binPath= ""{app}\Agent.Service.exe"" start= auto DisplayName= ""KidSafe Agent"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description KidSafeAgent ""Windows protection service"""; Flags: runhidden
Filename: "{app}\Agent.Service.exe"; Parameters: "harden"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start KidSafeAgent"; Flags: runhidden

[UninstallRun]
; The service ACL lets Administrators stop it; the PIN/password gate (InitializeUninstall) has already passed.
Filename: "{sys}\sc.exe"; Parameters: "stop KidSafeAgent"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{app}\Agent.Service.exe"; Parameters: "cleanup"; Flags: runhidden; RunOnceId: "Cleanup"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Agent.Helper.exe"; Flags: runhidden; RunOnceId: "KillHelper"
Filename: "{sys}\sc.exe"; Parameters: "delete KidSafeAgent"; Flags: runhidden; RunOnceId: "DelSvc"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\KidSafeClone"

[Code]
var
  ResultCode: Integer;

function GetBackend(Param: String): String;
begin
  Result := ExpandConstant('{param:BACKEND|http://localhost:3000}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Reinstall over a running agent.
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop KidSafeAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\appsettings.json'),
      '{ "Agent": { "BackendUrl": "' + GetBackend('') + '" }, "Logging": { "LogLevel": { "Default": "Information" } } }',
      False);
end;

// ---- Uninstall gate: parent's daily PIN (works offline) or account password ----

function AskSecret(var Secret: String): Boolean;
var
  F: TSetupForm;
  L: TNewStaticText;
  E: TPasswordEdit;
  Ok, Cancel: TNewButton;
begin
  F := CreateCustomForm(ScaleX(380), ScaleY(150), False, True);
  try
    F.Caption := 'Xác nhận gỡ cài đặt';
    L := TNewStaticText.Create(F);
    L.Parent := F;
    L.Left := ScaleX(16); L.Top := ScaleY(16); L.Width := ScaleX(348);
    L.AutoSize := False; L.WordWrap := True; L.Height := ScaleY(48);
    L.Caption := 'Nhập mã PIN gỡ cài đặt 8 số (xem trong ứng dụng phụ huynh) hoặc mật khẩu tài khoản phụ huynh.';
    E := TPasswordEdit.Create(F);
    E.Parent := F;
    E.Left := ScaleX(16); E.Top := ScaleY(70); E.Width := ScaleX(348);
    Ok := TNewButton.Create(F);
    Ok.Parent := F; Ok.Caption := 'OK'; Ok.ModalResult := mrOk; Ok.Default := True;
    Ok.Left := ScaleX(204); Ok.Top := ScaleY(108); Ok.Width := ScaleX(75); Ok.Height := ScaleY(24);
    Cancel := TNewButton.Create(F);
    Cancel.Parent := F; Cancel.Caption := 'Hủy'; Cancel.ModalResult := mrCancel; Cancel.Cancel := True;
    Cancel.Left := ScaleX(289); Cancel.Top := ScaleY(108); Cancel.Width := ScaleX(75); Cancel.Height := ScaleY(24);
    F.ActiveControl := E;
    Result := F.ShowModal = mrOk;
    Secret := E.Text;
  finally
    F.Free;
  end;
end;

function InitializeUninstall(): Boolean;
var
  Secret: String;
  Attempt, Code: Integer;
begin
  Result := False;
  // No scripted bypass of the prompt.
  if UninstallSilent then Exit;
  if not FileExists(ExpandConstant('{app}\Agent.Service.exe')) then begin Result := True; Exit; end;

  for Attempt := 1 to 3 do
  begin
    if not AskSecret(Secret) then Exit;
    if (Secret = '') or (Pos('"', Secret) > 0) then
    begin
      MsgBox('Giá trị không hợp lệ.', mbError, MB_OK);
      Continue;
    end;
    if Exec(ExpandConstant('{app}\Agent.Service.exe'), 'verify-uninstall "' + Secret + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      if Code = 0 then begin Result := True; Exit; end;
      if Code = 2 then
        MsgBox('Không kiểm tra được mật khẩu vì máy đang offline. Hãy nhập mã PIN gỡ cài đặt 8 số từ ứng dụng phụ huynh.', mbInformation, MB_OK)
      else
        MsgBox('Mã PIN hoặc mật khẩu không đúng.', mbError, MB_OK);
    end
    else
      MsgBox('Không chạy được bước xác thực.', mbError, MB_OK);
  end;
end;
