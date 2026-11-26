# Tools - PB&J Studio

| Tool | Where | When to use it |
|---|---|---|
| Protected release build | `Directory.Build.targets`, `src/*/obfuscar.body.xml` | Before shipping. `dotnet publish src/KcsPdfSigner.App/KcsPdfSigner.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Obfuscate=true -o release` (Obfuscar tool in `%USERPROFILE%\.dotnet\tools`). Also writes `release\x64\` (OCR engine DLLs) and `release\tessdata\` next to the exe. |
| OCR language files | `packaging/fetch-ocr-languages.ps1` | Once per PC before building, testing OCR or the installer: `powershell -ExecutionPolicy Bypass -File packaging/fetch-ocr-languages.ps1` downloads eng, osd and the 10 Indian languages (tessdata_fast 4.1.0) into `packaging/ocr-languages/` (git-ignored) and checks the pinned SHA-256; files already there are skipped. |
| Installer | `packaging/setup.iss` | After the protected build and the OCR language files: `ISCC.exe packaging/setup.iss` (Inno Setup at `%LOCALAPPDATA%\Programs\Inno Setup 6`) writes `dist/PBJStudio-Setup-1.3.3.exe`. Its "Scan languages (OCR)" page: "Skip for now (English only)" or "Choose scan languages" (one tick per language). Stops with a clear message if eng/osd or `release\x64` is missing. |
| Command-line OCR | `PBJStudio.exe --ocr in.pdf out.pdf [--lang hin+eng]` (`Services/CommandLineOcr.cs`) | Makes a scanned PDF searchable without a window; prints one result line in a console, exit code 0 ok, 1 failed, 2 wrong arguments. Used to prove OCR works in the published, obfuscated exe (set `PBJ_ALLOW_MULTIPLE=1` beside a running copy). No password support. |
| Splash generator | `brand/generator/splashgen-v6/splash_v6.py` | To change the start-up animation: `python -I splash_v6.py` writes `splash-v6.mp4` and `splash-v6.zip` (copy the zip to `src/.../Assets/splash.zip`). About 4 minutes. |
| UI smoke test | `tools/UiSmokeTest` | After any change to the App (Preferences, tools, viewer). Run the engine smoke test first. `dotnet run --project tools/UiSmokeTest/UiSmokeTest.csproj`; prints PASS/FAIL per check. With `-- --shots <folder>` it renders website screenshots instead (main window, Multiple PDFs, OCR tool, OCR languages). |
| Smoke test | `tools/SignerSmokeTest` | After any change to `src/KcsPdfSigner.Core` (signing engine or document operations), and before a release. Needs no DSC token. |
| Document advisor (suggestion bars) | `src/KcsPdfSigner.Core/Documents/DocumentAdvisor.cs`, `PdfSanitizeOps.cs`; App: `MainWindow.Suggestions.cs`, `Suggestions/` | Reads a PDF once and returns the suggestions shown as bars under the toolbar (ids in `SuggestionIds`). To add a bar for a new feature, implement `ISuggestionProvider` and register it in `SuggestionRegistry` (see the comment in `Suggestions/ISuggestionProvider.cs`); a provider with slow facts calls `SuggestionRegistry.RequestRefresh()` when they are ready. Checks 46-55 (engine) and [8] (UI). |
| OCR (make scans searchable) | Core: `Documents/PdfOcrOps.cs`, `Documents/OcrLanguages.cs`; App: `Tools/OcrPanel`, `Dialogs/OcrLanguagesDialog`, `Suggestions/ScannedSidewaysSuggestionProvider.cs` | Tesseract 5 in-process (NuGet `Tesseract` 5.2.0, natives in `x64\`). `PdfOcrOps.MakeSearchable` adds Tesseract's invisible text layer per page; `DetectOrientation` reads picture-page orientation (thresholds `TurnForReadingConfidence`, `SidewaysSuggestionConfidence`). Languages: program `tessdata\` plus `%APPDATA%\KcsPdfStudio\tessdata`. The "OCR" tool switches on the scan bar; the sideways-scan bar runs orientation detection in the background. Checks 56-65 (engine) and [9] (UI). |
| Scan Cleaner (photos to clean PDF pages) | Core: `Scanning/ScanImageOps.cs` (load, page-edge finding, perspective warp, skew, background levelling, denoise, sharpen, Otsu), `Scanning/ScanCleaner.cs` (pipeline, A4 PDF writer, optional OCR via `PdfOcrOps`); App: `Tools/ScanCleanerPanel` | Own algorithms on SkiaSharp, no OpenCV. `ScanCleaner.CreatePdf(pictures, output, options, progress, token)` is the one entry point; JPEG pages for colour/grey (200 dpi, 250 with OCR), 1-bit for black and white (300 dpi). TIFF/HEIC are read through Windows (`ScanImageOps.ExternalDecoder`). `dotnet run --project tools/SignerSmokeTest -- --scan` runs only checks 66-74. UI check [10]. |
| Store package (MSIX) | `packaging/msix/build-msix.ps1`, `identity.json`, `AppxManifest.template.xml` | After the protected release build: `powershell -ExecutionPolicy Bypass -File packaging/msix/build-msix.ps1` writes `dist/PBJStudio-<version>.msix` (unsigned; the Microsoft Store signs it) with all OCR languages and tiles from the seal. Put the Partner Center identity values in `identity.json` first. Needs Microsoft.Windows.SDK.BuildTools unpacked in `packaging/sdk-buildtools/pkg` (git-ignored). |

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
