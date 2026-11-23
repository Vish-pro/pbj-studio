# Tools - PB&J Studio

| Tool | Where | When to use it |
|---|---|---|
| Protected release build | `Directory.Build.targets`, `src/*/obfuscar.body.xml` | Before shipping. `dotnet publish src/KcsPdfSigner.App/KcsPdfSigner.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Obfuscate=true -o release` (Obfuscar tool in `%USERPROFILE%\.dotnet	ools`). |
| Installer | `packaging/setup.iss` | After the protected build: `ISCC.exe packaging/setup.iss` (Inno Setup at `%LOCALAPPDATA%\Programs\Inno Setup 6`) writes `dist/PBJStudio-Setup-1.0.0.exe`. |
| Splash generator | `brand/generator/splashgen-v6/splash_v6.py` | To change the start-up animation: `python -I splash_v6.py` writes `splash-v6.mp4` and `splash-v6.zip` (copy the zip to `src/.../Assets/splash.zip`). About 4 minutes. |
| UI smoke test | `tools/UiSmokeTest` | After any change to the App (Preferences, tools, viewer). Run the engine smoke test first. `dotnet run --project tools/UiSmokeTest/UiSmokeTest.csproj`; prints PASS/FAIL per check. |
| Smoke test | `tools/SignerSmokeTest` | After any change to `src/KcsPdfSigner.Core` (signing engine or document operations), and before a release. Needs no DSC token. |
| Document advisor (suggestion bars) | `src/KcsPdfSigner.Core/Documents/DocumentAdvisor.cs`, `PdfSanitizeOps.cs`; App: `MainWindow.Suggestions.cs`, `Suggestions/` | Reads a PDF once and returns the suggestions shown as bars under the toolbar (ids in `SuggestionIds`). To add a bar for a new feature, implement `ISuggestionProvider` and register it in `SuggestionRegistry` (see the comment in `Suggestions/ISuggestionProvider.cs`); the framework is not touched. Checks 46-55 (engine) and [8] (UI). |
| OCR hook | `Suggestions/ISuggestionProvider.cs` (`OcrSuggestionProvider`) | Scans are already detected (`DocumentAdvisor.DetectScanned`, `DocumentAnalysis.Scan`). The "Make it searchable (OCR)?" bar appears by itself once a tool panel titled "OCR" is added to `ToolRegistry`. Image-only sideways pages: an OCR provider reads `Analysis.Scan.ScannedPages` and straightens with `DocumentAdvisor.StraightenPages`. |

## Smoke test

Builds test PDFs in `%TEMP%\KcsPdfSignerSmokeTest\`, runs every detection mode, signs them with a
throwaway self-signed certificate and verifies the signatures (including all four page rotations),
then runs a numbered check for every document operation (info and metadata, text search and
extraction, page operations, annotations, forms, content and redaction, security, export, signature
inspection). Each check prints PASS or FAIL; the exit code is 0 only when every check passes.

```bash
D:\dotnet\dotnet.exe run --project "E:\PDF Signer Application\tools\SignerSmokeTest\SignerSmokeTest.csproj"
```

It also leaves `unsigned.pdf` in `%TEMP%\KcsPdfSignerSmokeTest\`, handy for a quick launch check
of the app:

```bash
"E:\PDF Signer Application\src\KcsPdfSigner.App\bin\Debug\net8.0-windows\KcsPdfSigner.exe" "%LOCALAPPDATA%\Temp\KcsPdfSignerSmokeTest\unsigned.pdf"
```

The app should stay open and responsive, and `%APPDATA%\KcsPdfStudio\error.log` should gain no new
entries.

- **User guide PDF** - `node packaging/guide/build-guide.mjs` builds `dist/User-Guide.pdf` from README Part 1 (Edge headless; run `npm i` once in `packaging/guide`). Copy the PDF to truevith-space `public/pbj/downloads/`.
