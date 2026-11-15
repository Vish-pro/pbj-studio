; PB&J Studio installer (Inno Setup 6). Build from the packaging folder:  ISCC.exe setup.iss
; Expects the protected release build in ..\release\PBJStudio.exe (see README, "Building a protected release").

#define AppName "PB&J Studio"
#define AppShort "PB&J Studio"
#define AppVersion "1.0.0"
#define AppExe "PBJStudio.exe"
#define Publisher "truevith.space and vistaar.space"
#define PublisherURL "https://www.vistaar.space/"

[Setup]
AppId={{B7D3E5A2-4C1F-4E8A-9A62-5F0C7D2B9E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#PublisherURL}
AppSupportURL={#PublisherURL}
AppCopyright=(c) 2026 {#Publisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Per-user install: no administrator rights needed. (Admins can still pass /ALLUSERS.)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\PBJ Studio
DefaultGroupName={#AppShort}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

LicenseFile=LICENSE.txt
SetupIconFile=..\src\KcsPdfSigner.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

WizardStyle=modern
WizardImageFile=installer\wizard-100.bmp,installer\wizard-200.bmp
WizardSmallImageFile=installer\wizard-small-100.bmp,installer\wizard-small-200.bmp
WizardImageBackColor=#FFFFFF

OutputDir=..\dist
OutputBaseFilename=PBJStudio-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=force
RestartApplications=no

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nPaper, Binder and Jurat: view, fill, comment, merge, protect and sign PDFs, including signing one PDF in many places with a USB DSC token.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

Name: "pdfassoc"; Description: "Offer {#AppShort} for opening PDF files (Windows lets you pick the default app)"; GroupDescription: "PDF files:"

[Registry]
Root: HKA; Subkey: "Software\Classes\PBJStudio.pdf"; ValueType: string; ValueData: "PDF Document (PB&J Studio)"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\PBJStudio.pdf\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\PBJStudio.pdf\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "PBJStudio.pdf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc
Root: HKA; Subkey: "Software\PBJStudio\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\PBJStudio\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Paper, Binder and Jurat: view, fill, merge, protect and sign PDFs"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\PBJStudio\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "PBJStudio.pdf"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PBJStudio"; ValueData: "Software\PBJStudio\Capabilities"; Flags: uninsdeletevalue; Tasks: pdfassoc

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppShort}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"
Name: "{autodesktop}\{#AppShort}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppShort}"; Flags: postinstall nowait skipifsilent

Filename: "ms-settings:defaultapps"; Description: "Open Windows Settings to choose {#AppShort} as the default PDF app"; Flags: postinstall shellexec nowait skipifsilent; Tasks: pdfassoc

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""{#AppExe}"""; Flags: runhidden; RunOnceId: "StopPBJ"
