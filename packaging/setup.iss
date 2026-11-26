; PB&J Studio installer (Inno Setup 6). Build from the packaging folder:  ISCC.exe setup.iss
; Expects the protected release build in ..\release\PBJStudio.exe (see README, "Building a protected release").

#define AppName "PB&J Studio"
#define AppShort "PB&J Studio"
#define AppVersion "1.3.3"
#define AppExe "PBJStudio.exe"
#define Publisher "truevith.space and vistaar.space"
#define PublisherURL "https://www.vistaar.space/"

; OCR: the engine (release\x64, from the protected release build) and the language files
; (packaging\ocr-languages, from fetch-ocr-languages.ps1). English and orientation are required;
; each Indian language is offered only when its file is there.
#define Here AddBackslash(SourcePath)
#define OcrDir Here + "ocr-languages\"
#if !FileExists(OcrDir + "eng.traineddata") || !FileExists(OcrDir + "osd.traineddata")
  #error OCR language files are missing. Run: powershell -ExecutionPolicy Bypass -File packaging\fetch-ocr-languages.ps1
#endif
#if !FileExists(Here + "..\release\x64\tesseract50.dll") || !FileExists(Here + "..\release\x64\leptonica-1.82.0.dll")
  #error release\x64 (OCR engine) is missing. Run the protected release build first (see README).
#endif

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
WizardSelectComponents=Scan languages (OCR)
SelectComponentsDesc=Which languages should PB&J Studio read in scanned PDFs?
SelectComponentsLabel2=The OCR tool makes scanned PDFs searchable. English is always included. Keep "Skip for now" for English only, or choose "Choose scan languages" and tick the languages you need. You can add languages later in the OCR tool (Languages...).

[Types]
Name: "english"; Description: "Skip for now (English only)"
Name: "choose"; Description: "Choose scan languages"; Flags: iscustom

[Components]
Name: "main"; Description: "PB&J Studio with English scanning (OCR)"; Types: english choose; Flags: fixed
#if FileExists(OcrDir + "hin.traineddata")
Name: "lang_hin"; Description: "Hindi"
#endif
#if FileExists(OcrDir + "kan.traineddata")
Name: "lang_kan"; Description: "Kannada"
#endif
#if FileExists(OcrDir + "mar.traineddata")
Name: "lang_mar"; Description: "Marathi"
#endif
#if FileExists(OcrDir + "tam.traineddata")
Name: "lang_tam"; Description: "Tamil"
#endif
#if FileExists(OcrDir + "tel.traineddata")
Name: "lang_tel"; Description: "Telugu"
#endif
#if FileExists(OcrDir + "mal.traineddata")
Name: "lang_mal"; Description: "Malayalam"
#endif
#if FileExists(OcrDir + "guj.traineddata")
Name: "lang_guj"; Description: "Gujarati"
#endif
#if FileExists(OcrDir + "ben.traineddata")
Name: "lang_ben"; Description: "Bengali"
#endif
#if FileExists(OcrDir + "pan.traineddata")
Name: "lang_pan"; Description: "Punjabi"
#endif
#if FileExists(OcrDir + "urd.traineddata")
Name: "lang_urd"; Description: "Urdu"
#endif

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
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PBJStudio"; ValueData: "Software\PBJStudio\Capabilities"; Flags: uninsdeletevalue; Tasks: pdfassoc

[InstallDelete]
; Scan languages are installed fresh each time, so only the ticked ones stay (languages the user
; added in the app live in %APPDATA%\KcsPdfStudio\tessdata and are not touched).
Type: files; Name: "{app}\tessdata\*.traineddata"

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\licenses\Tesseract-NOTICE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\docs\licenses\SourceSans3-OFL.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
; OCR engine (Tesseract and Leptonica) and the always-included languages.
Source: "..\release\x64\tesseract50.dll"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "..\release\x64\leptonica-1.82.0.dll"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "ocr-languages\eng.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion
Source: "ocr-languages\osd.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion
#if FileExists(OcrDir + "hin.traineddata")
Source: "ocr-languages\hin.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_hin
#endif
#if FileExists(OcrDir + "kan.traineddata")
Source: "ocr-languages\kan.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_kan
#endif
#if FileExists(OcrDir + "mar.traineddata")
Source: "ocr-languages\mar.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_mar
#endif
#if FileExists(OcrDir + "tam.traineddata")
Source: "ocr-languages\tam.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_tam
#endif
#if FileExists(OcrDir + "tel.traineddata")
Source: "ocr-languages\tel.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_tel
#endif
#if FileExists(OcrDir + "mal.traineddata")
Source: "ocr-languages\mal.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_mal
#endif
#if FileExists(OcrDir + "guj.traineddata")
Source: "ocr-languages\guj.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_guj
#endif
#if FileExists(OcrDir + "ben.traineddata")
Source: "ocr-languages\ben.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_ben
#endif
#if FileExists(OcrDir + "pan.traineddata")
Source: "ocr-languages\pan.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_pan
#endif
#if FileExists(OcrDir + "urd.traineddata")
Source: "ocr-languages\urd.traineddata"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: lang_urd
#endif

[Icons]
Name: "{autoprograms}\{#AppShort}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"
Name: "{autodesktop}\{#AppShort}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppShort}"; Flags: postinstall nowait skipifsilent

Filename: "ms-settings:defaultapps"; Description: "Open Windows Settings to choose {#AppShort} as the default PDF app"; Flags: postinstall shellexec nowait skipifsilent; Tasks: pdfassoc

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""{#AppExe}"""; Flags: runhidden; RunOnceId: "StopPBJ"
