<p align="center"><img src="docs/logo.png" alt="PB&J Studio" width="420"></p>

# PB&J Studio

**Paper, Binder and Jurat.** A Windows PDF app for everyday office work: open, fill, comment, merge,
organise, edit, redact, protect, export and sign PDFs. Its star feature is **Multi-Sign (DSC)**:
sign one PDF in many places in a single run, using a Digital Signature Certificate on a USB token.

Black-and-white interface. All code and icons are original.

## What you will not find in most PDF readers

| Feature | What it does for you |
|---|---|
| **Multi-Sign (DSC)** | Sign one PDF in many places in one run with your USB token: one PIN prompt, every signature box found automatically. |
| **Scan Cleaner** | Turn phone photos of pages into clean, straight, white A4 PDF pages (edges found, shadows removed, colour, grey or black and white). |
| **OCR in Indian languages** | Make scanned PDFs searchable, including Hindi, Kannada, Marathi, Tamil, Telugu and more. Runs on your PC; nothing is uploaded. |
| **Signature check like Acrobat** | Banner, Signatures pane and properties: shows whether a signed PDF is valid, and who signed it. |
| **Multiple PDFs** | Pick pages from several PDFs and build one new PDF in one go. |
| **Suggestion bars** | Quiet hints when they help: scanned file, sideways pages, a password, big size, hidden information, before you send. |
| **Send check** | Checks a PDF before you email it to a bank or office: size, password, hidden information. |
| **Save a copy without password** | Remove a password from a PDF you own (you type REMOVE PASSWORD to confirm). |
| **Safe print and edit of signed files** | Warns before a change that would break signatures; refuses to print what the owner forbids. |

Everything runs offline on your PC, and the program is free and open source (MIT).

---

# Part 1 - For everyone: install and use

## What you need

- Windows 10 or 11, 64-bit.
- For signing: your DSC USB token and its driver installed (the same driver your other signing
  software uses), and the token's PIN.

## Install

1. Download `PBJStudio-Setup-1.3.2.exe` from the **Releases** page of this repository.
2. Your browser may warn that the file "isn't commonly downloaded". This is normal for new free software
   without a paid code-signing certificate. In **Edge**: open Downloads (Ctrl+J), click the **...** next to the
   file, choose **Keep**, then **Show more** > **Keep anyway**. In **Chrome**: click the warning, then **Keep**.
3. Double-click the file. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
4. Accept the licence and click **Install**. No administrator rights are needed.
5. Tick **Launch PB&J Studio** at the end, or start it from the Start menu.

To remove it, use **Settings > Apps > Installed apps > PB&J Studio > Uninstall**.

## Open a PDF

Use **File > Open**, drag a PDF onto the window, or pick one from the recent files list.
If PB&J Studio is already running, opening another PDF from Explorer adds it as a new tab in the same window.
The program works on a private copy. Your original file is not changed until you **Save**
(Ctrl+S) or **Save As** (Ctrl+Shift+S). Every change can be undone (Ctrl+Z) and redone (Ctrl+Y).

## What you can do

| Tool | What it does |
|---|---|
| **Multi-Sign (DSC)** | Sign one PDF at many places in one run with your USB token. The app finds the places for you (signature lines, text such as "Authorised Signatory", empty boxes), or you click the places by hand. |
| **Comment** | Highlight, underline, strike out, notes, text boxes, shapes, freehand and stamps. |
| **Fill Forms** | Fill form fields and flatten them. |
| **Organize Pages** | Rotate, delete, extract, reorder (drag, or Up, Down, To top, To bottom), insert and split pages of the open PDF. |
| **Multiple PDFs** | Pick specific pages from several PDFs and save them as one new PDF, in the order you want. See the how-to below. |
| **Combine Files** | Merge PDFs, or turn images into a PDF. |
| **Edit** | Add text, images, watermarks, headers, footers and page numbers. |
| **Redact** | Permanently remove content (the text is removed, not just covered). |
| **Protect** | Add or remove a password (AES-256) and set permissions. |
| **Export** | Pages to images, text, or a smaller optimised PDF. |
| **OCR** | Make a scanned PDF searchable: adds an invisible text layer (English, plus Indian languages you add). See below. |
| **Scan Cleaner** | Turn photos of pages into clean scanned PDF pages: edges found, straightened, shadows removed, colour, grey or black and white, optionally searchable. See below. |
| **Properties, Search, Read Aloud** | Document details, find text, and have a page read out. |

## Pick pages from several PDFs (Multiple PDFs)

Use this when you need one PDF made from chosen pages of several files, for example pages 3-5 of one report,
page 9 of another, and a covering letter.

1. Open the **Multiple PDFs** tool in the tools pane (or Tools > Multiple PDFs).
2. **Add PDFs...** (or drop PDF files on the file list, or **Add open document**). A password-protected file asks for its
   owner password, the one that also lets you change the file.
3. Click a file in the list. Its pages appear as thumbnails. Click the pages you want (Ctrl or Shift for several) and press
   **Add selected**, or double-click a page to add just that one. You can also type pages, such as `3-5, 9`, and press
   **Add range**; they are added in the order you type them (`5-3` counts down).
4. Repeat for the other files. The pages you add collect under **3. Your new PDF**, in the order you added them. Each
   thumbnail shows its place in the new file and the file name and original page number under it.
5. Arrange them: drag pages to a new place, or select pages and use the arrows, **To top**, **To bottom**; turn pages
   with the rotate buttons; take pages out with **Remove** (or the Delete key).
6. Press **Save new PDF...**. The new file opens in a new tab unless you untick the box.

Your original PDFs are never changed. Large PDFs stay quick because thumbnails are drawn only for the pages you are
looking at.

## Make a scanned PDF searchable (OCR)

A scanned PDF is a set of pictures: you cannot search it or copy its text. OCR (text recognition) reads the
pictures and puts an invisible layer of text on top. The pages look exactly the same, but Search (Ctrl+F),
selecting and copying now work. Everything runs on your PC; nothing is uploaded.

1. Open the scanned PDF. A bar usually says **This looks like a scan... Make it searchable (OCR)?**; click
   **Make searchable (OCR)...**, or open **OCR** in the tools pane (or Tools > OCR).
2. Choose the pages: **All pages**, **Current page**, or **Pages** such as `1-3, 5`.
3. Tick the languages the pages are written in. English is ticked at first; you can tick several together,
   for example Hindi and English.
4. Keep **Skip pages that already have text** ticked (typed pages and pages made searchable before are left alone).
5. Click **Make searchable**. A bar shows "Page 3 of 40"; **Cancel** stops after the current page and changes
   nothing. Each page takes one to three seconds. When it finishes, the panel says how many pages were read and
   how sure the program is. **Save** to keep it; Ctrl+Z undoes it.

Pages that were scanned sideways or upside down are read the right way up. A bar then offers to turn the pages
themselves (**N scanned pages look sideways or upside down. Straighten them?**); Ctrl+Z undoes that too.

**Languages.** English always comes with PB&J Studio. While installing you can choose **Choose scan languages**
and tick Hindi, Kannada, Marathi, Tamil, Telugu, Malayalam, Gujarati, Bengali, Punjabi or Urdu (0.5 to 5 MB each),
or keep **Skip for now (English only)**. To add one later: OCR tool > **Languages...** > **Add**. The program shows
the exact download address (the official Tesseract project on github.com) and size, asks first, downloads it, and
checks it before use. **Remove** takes away a language you added.

**PC without internet.** On a PC with internet, download the file shown in the Add message (for example
`hin.traineddata`), copy it over (USB stick), and use **Languages... > Add from a file...**. The file is checked
the same way; a wrong or damaged file is refused.

**What OCR cannot do well.** Clean printed pages scanned at 300 dpi read well. Handwriting, faint or blurred
phone photos, crooked pages, stamps and seals, signatures, and text in tables or over pictures read poorly or not
at all. Always check names, amounts and dates before you rely on them. English was tested on clean sample scans;
of the Indian languages only Hindi was tried (a clean sample page with Hindi and English, where 10 of 12 words came
out right); the others come from the same official source but have not been tested here.

OCR needs the free Microsoft Visual C++ Redistributable (x64), which most Windows PCs already have. If it is
missing, the OCR tool says so and tells you where to get it.

A PDF with a password can be made searchable only when you opened it with its owner password (the one that allows
changes), like any other change in the program. On a digitally signed PDF the program warns you first, because
adding text breaks the signatures.

## Turn photos into clean scanned PDFs (Scan Cleaner)

Photographed a page on your phone? Scan Cleaner turns the photos into neat, white, straight A4 pages in one PDF.

1. Open **Scan Cleaner** in the tools pane (Tools > Scan Cleaner). No PDF needs to be open.
2. **Add pictures...** (JPG, PNG, TIFF, BMP; or drop them on the list). Move them up or down to set the page order.
3. Click a picture: **Before and after** shows what the clean-up will do.
4. Choose the output: **Colour**, **Grey**, or **Black and white** (crisp text, smallest file).
5. Click **Make PDF...**, choose a name. One A4 page per picture. A **Cancel** button stops a long run.

What it does for each picture: finds the page edges and flattens a photo taken at an angle, straightens tilted text, whitens
the paper and removes shadows and uneven light, reduces noise and sharpens. Tick **Make searchable (OCR)** to also add a
text layer (slower; see the OCR section for languages and limits).

Photograph the page on a darker table, in even light, with the whole page in view. If no page edge can be found (white page
on a white table), the whole picture is straightened and cleaned instead; the result line tells you. Pictures that cannot be
read are skipped and named. Sizes: a 20-photo PDF is about 6 MB in colour and under 1 MB in black and white.

## Sign a PDF at many places

1. Open the PDF and choose **Multi-Sign (DSC)** in the tools pane.
2. Pick a detection mode, or click **place by hand**. The places are drawn on the pages.
3. Plug in your token, choose your certificate and click **Sign**.
4. Enter the token PIN when asked. The signed file opens in a new tab.

The visible signature looks like Acrobat's: your name large on the left, "Digitally signed by ..." and the date on the right,
plus Reason and Location lines when you fill them in. It uses the Myriad Pro font when Adobe Acrobat is installed on the PC (read from the PC, never shipped with this app), and otherwise the bundled Source Sans 3 (Adobe's open-source, Myriad-style font), so it looks right on every PC.

## Check that a signed PDF is valid

Open any signed PDF (signed here, in Acrobat, on a DSC portal or by a bank). A banner above the page tells you the result,
like Acrobat's:

- **Signed and all signatures are valid.** Nothing was changed after signing (or only later signatures, form filling or
  comments), and the signer's certificate leads to a certificate you trust.
- **Signature validity is unknown.** The document is unchanged, but the signer could not be confirmed: the certificate is
  not trusted on this PC, its revocation could not be checked (offline), or a change could not be classified.
- **At least one signature has problems.** The file was altered after signing, a signature is damaged, or the certificate
  was expired or revoked.

Click **Signature Panel** (or the shield tab in the left pane) to see every signature with its status. Double-click one for
its **Signature Properties**: signer, certificate, signing time (a trusted timestamp, or only the clock on the signer's
computer), the list of checks, **View certificate**, **Trust this certificate**, **Validate** and **Copy details**.
**Sign > Validate signatures** checks everything again, for example after you go online. Empty signature fields are listed
as "not signed yet", and a small tick, question mark or cross is drawn on each signature box.

**Why Indian DSC signatures show "unknown" at first.** Indian DSC certificates lead up to the CCA India root certificate,
which Windows usually does not include (Acrobat shows "unknown" for the same reason until you trust that root). To trust it in
PB&J Studio: download the CCA India root certificate from cca.gov.in, compare its thumbprint with the one published there,
then open **Sign > Trusted certificates...** (also in Preferences) and click **Add from file...**. Or open a DSC signature's
properties, pick the root at the end of the certificate chain and click **Trust this certificate**. The list is kept in
`%APPDATA%\KcsPdfStudio\trusted-certs.json` and is used by PB&J Studio only; Windows is not changed. Nothing is trusted
without you asking.

Good to know: revocation is checked online only when the certificate names a revocation service; offline it says
"revocation not checked". Revocation data embedded in a signature (LTV) is not evaluated.

## Suggestions

When the open PDF matches one of the situations below, a thin bar appears under the toolbar with one
message and one button (at most two bars at once). **Dismiss** hides it for that document until you close
the app; **Don't show this again** hides that kind of bar for every document. To turn all of them off,
untick **Show suggestions** in Preferences (Ctrl+K); **Reset suggestions** there brings hidden ones back.

| Bar | When it appears | The button |
|---|---|---|
| Password protected | You opened the PDF with its password. | Save a copy without password (asks you to type REMOVE PASSWORD; the original stays protected). |
| File is large | The file is larger than 5 MB (change the size in Preferences). | Opens Export, where Optimize shows the size before and after (Ctrl+Z undoes it). |
| Signature spots | An unsigned, unprotected PDF has phrases like "Authorised Signatory", "Partner", "Proprietor" or "Membership No". | Opens Multi-Sign (DSC) with those spots found, ready to review and sign. |
| Hidden information | The file names an author, has comments with author names, or carries attached files. | Lists exactly what will be removed (document details, XMP metadata, comment author names), asks separately about attachments, and removes it. Ctrl+Z undoes it. Nothing on the pages changes. |
| Owner restrictions | The owner has locked printing, copying, editing, comments, forms or page changes. | Information only. |
| Sideways pages | The text on a page runs sideways or upside down. | Turns those pages upright in one step (Ctrl+Z undoes it). |
| Scan | Most pages are pictures without text (a scan). | Opens the OCR tool to make it searchable. |
| Sideways scanned pages | Picture pages whose orientation the OCR engine reads, with high confidence, as sideways or upside down (checked in the background for up to 60 pages). | Turns those pages upright in one step (Ctrl+Z undoes it). |
| Damaged file | The file had a broken structure that was repaired while opening. | Rewrites it cleanly, then Save As to a new name. |
| Fillable form | The PDF has form fields to fill in (signature fields alone do not count). | Opens Fill Forms. |
| After merging | A PDF you just made with Multiple PDFs or Combine Files opens. | Opens Edit with "Page numbers" ticked: press "Add to every page". |
| Ready to send | A PDF you just signed with Multi-Sign opens. | Send check: signatures valid or intact, boxes print, file size, no password, not damaged, who signed. It ends with "Looks good to send" or "Check the items marked !". |

What the suggestions cannot see: signature spots are found from the text, so they are not found on a scanned
page (a picture) until it has been made searchable. Sideways scanned pages are found only when the page has
enough printed text for the OCR engine to be sure; a photo or a nearly empty page is never flagged. Hidden information does not cover names typed on the pages or form entries.
On a PDF whose owner has restricted it, bars that would change the file are not offered (it cannot be
changed without the owner password). The bars are checked again after every change; the after-merging and ready-to-send bars only show for the
file exactly as the tool produced it.

## Preferences (Ctrl+K)

Open **Edit > Preferences** or press Ctrl+K. Everything here takes effect as soon as you press OK.

| Setting | What it does |
|---|---|
| Open PDFs from the last session | Reopens the documents you had open when you last closed the app. |
| Ask before closing with several documents open | Shows a question before the window closes with two or more documents open. |
| Create links from web addresses | Web addresses (http, https, www) in the text become clickable; click one and confirm to open it in your browser. |
| Highlight form fields on open | Shades every form field when a document opens. |
| Single-key tool shortcuts | V = Select text, H = Hand, G = Snapshot. |
| Quick actions after selecting text | A small bar with Copy, Highlight and Search appears under your selection. |
| Mouse wheel zoom | The wheel alone zooms; Shift + wheel scrolls. |
| Snapshot options | Fixed resolution in pixels per inch, and "open each snapshot as a one-page PDF" so you can mark it up. |
| Touch mode | Auto, always on (125% larger interface) or off. Menus keep their normal size. |
| Scale for screen | Auto follows the Windows display scale; "No scaling" draws the interface at 100%. |
| Do not show edit warnings | Skips the question before redacting or flattening a form. Untick it (Reset all warnings) to see them again. |
| Show suggestions | Turns the suggestion bars (below) on or off. "Suggest a smaller file above ... MB" sets the size for the file-size bar (default 5 MB). "Reset suggestions" brings back the ones you hid with "Don't show this again". |

**Select tool (V):** drag over text; the selected lines are shaded. Ctrl+C copies, or use the quick actions.
**Snapshot tool (G):** drag an area; it is copied to the clipboard as a picture, ready to paste.
**File > Send to WhatsApp:** saves a copy of the open PDF (with your current changes), puts the file on the
clipboard and opens WhatsApp. In the chat, press Ctrl+V or drag the file in from the Explorer window that opens.

Settings are stored in `%APPDATA%\KcsPdfStudio\settings.json`.

**Not included, and why:** online storage options (the app has no cloud features), "read articles" and
image-first selection (the viewer has no article or image selection), and Explorer thumbnail previews
for PDFs (they need a native Windows shell extension that cannot be shipped safely from a .NET app).

## Keyboard shortcuts

Ctrl+O open - Ctrl+S save - Ctrl+Shift+S save as - Ctrl+P print - Ctrl+W close tab -
Ctrl+Z / Ctrl+Y undo / redo - Ctrl+F find - Ctrl+G go to page -
Ctrl+0 / 1 / 2 fit page / actual size / fit width - Ctrl+K preferences - F11 full screen.
With single-key shortcuts turned on: V select text - H hand - G snapshot.

## If something goes wrong

The app writes unexpected errors to `%APPDATA%\KcsPdfStudio\error.log`. Send that file when you ask for help.

---

# Part 2 - For developers: contribute

## Project layout

```
KcsPdfSigner.sln                   solution
src/KcsPdfSigner.Core              signing engine, detection, document operations (no UI)
src/KcsPdfSigner.App               WPF application (assembly PBJStudio.exe)
tools/SignerSmokeTest              end-to-end check of the engine and every document operation, no token needed
tools/UiSmokeTest                  builds the real windows in-process and checks Preferences, select, highlight, snapshot, form fields and touch mode
packaging/                         Inno Setup script, wizard images, installer licence
brand/                             logo sources and the splash-animation generator (Python)
Directory.Build.targets            release-only code protection (Obfuscar)
```

Namespaces and project names still say `KcsPdfSigner`; only the product name and the exe changed.

## Build and run

You need the .NET 8 SDK (Windows).

```bash
dotnet build KcsPdfSigner.sln
dotnet run --project src/KcsPdfSigner.App/KcsPdfSigner.App.csproj
```

## Test before every pull request

The smoke test builds PDFs in `%TEMP%`, signs them with a throwaway certificate, re-opens and
verifies them, and runs a numbered check for every document operation. It needs no token. Each check
prints PASS or FAIL and the exit code is 0 only when all pass.

```bash
dotnet run --project tools/SignerSmokeTest/SignerSmokeTest.csproj
```

Set the environment variable `PBJ_ALLOW_MULTIPLE=1` to run several copies of the app side by side (normally a second
start hands its PDFs to the running copy). The UI check sets it for itself, so it works even while the app is open.

Then run the UI check (it opens the real windows briefly, changes no screen settings, and restores your
`settings.json` afterwards; run the engine smoke test first because it creates the test PDFs):

```bash
dotnet run --project tools/UiSmokeTest/UiSmokeTest.csproj
```

Then start the app on `%LOCALAPPDATA%\Temp\KcsPdfSignerSmokeTest\unsigned.pdf` and check it stays open
and `%APPDATA%\KcsPdfStudio\error.log` gains no new entries.

## How the code is organised

- **Core** is stateless. Every operation takes an input path and writes a new output path (never in
  place), with an optional `password` for encrypted files, and throws a clear exception on failure.
  Signing lives in `MultiSignatureSigner`, `PlacementDetector`, `TextAnchorDetector`,
  `TokenExternalSignature` and `SigningCertificate`. Document operations live in `Documents/`.
- **App** is WPF with a monochrome theme (`Themes/Monochrome.xaml`, black and white only) and
  hand-drawn vector icons (`Themes/Icons.xaml`). `DocumentSession` keeps a working copy plus undo
  and redo stacks; each tool panel in `Tools/` acts on the active session through `Apply`, so every
  change is undoable. `Viewer/PdfViewer` renders visible pages lazily.
- **Preferences** live in `Services/AppSettings.cs` (stored in `settings.json`) and `Dialogs/PreferencesDialog`.
  The features they switch are in `MainWindow.Features.cs` (tools, links, form highlights, snapshot,
  Send to WhatsApp, session restore, touch and scaling). Text regions come from
  `Core/Documents/PdfTextRegions.cs`. Every setting must change real behaviour; do not add dead checkboxes.
- **Multiple PDFs** lives in `Tools/PageBasketPanel.xaml(.cs)`. It builds the output with the Core method
  `PdfPageOps.BuildFromSelection` (a list of `PageSelection(file, page, rotation, password)`), so no new PDF code is in the
  UI. Thumbnails render lazily on one background thread (`RenderLoop`), reading each source file once.
- **Splash**: the start-up animation is a zip of pre-rendered JPEG frames (`Assets/splash.zip`).
  `brand/generator/splashgen-v6/splash_v6.py` regenerates it (Python with Pillow, numpy, OpenCV and
  imageio-ffmpeg): `python -I splash_v6.py`.

## Rules for contributions

- Original code and original vector icons only. Do not copy code, assets, names or branding from other products.
- Keep the black-and-white theme: only `#000000`, `#FFFFFF` and their opacity.
- Source files in plain ASCII (no fancy quotes or dashes).
- Keep the signing engine working: the smoke test must pass.
- iText 9.7.0 gotchas: `DigestAlgorithms` is in `iText.Kernel.Crypto`; `PdfFormCreator` is in
  `iText.Forms.Fields`; `iText.Kernel.Geom.Path` clashes with `System.IO.Path` (alias it); disposing
  a layout `Document` closes its `PdfDocument`.
- Never commit certificates, keys, PINs, test documents with real data, or build output.
- Open an issue first for large changes; keep pull requests small and describe how you tested them.

## Building a protected release and the installer

The release build scrambles the assemblies (Obfuscar) before bundling them into one self-contained exe.
One-time setup:

```bash
dotnet tool install Obfuscar.GlobalTool --global
```

Then, from the repository root (adjust the runtime paths in `Directory.Build.targets` if your .NET
install is not in `D:\dotnet`):

```bash
dotnet publish src/KcsPdfSigner.App/KcsPdfSigner.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Obfuscate=true -o release
```

The publish also writes `release\x64\` (the Tesseract and Leptonica OCR engine DLLs) and `release\tessdata\`
next to the exe; the installer ships `x64\` from there. Fetch the OCR language files once (they are not in git):

```bash
powershell -ExecutionPolicy Bypass -File packaging/fetch-ocr-languages.ps1
```

It downloads English, orientation detection and the ten Indian languages from tessdata_fast into
`packaging/ocr-languages/` and checks each against the SHA-256 pinned in the script (the same values as
`Core/Documents/OcrLanguages.cs`). English and orientation are required; a missing Indian language is simply left
out of the installer's "Scan languages (OCR)" page.

Build the installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```bash
ISCC.exe packaging/setup.iss
```

The installer is written to `dist/PBJStudio-Setup-1.3.2.exe`. Upload it to a GitHub Release; do not
commit it. To change the version, edit `Version` in `KcsPdfSigner.App.csproj` and `AppVersion` in `setup.iss`.

## Credits

Signature font fallback: Source Sans 3 by Adobe, SIL Open Font License 1.1 (see `docs/licenses/SourceSans3-OFL.txt`).

OCR: Tesseract OCR engine and its tessdata_fast language data (Apache License 2.0), Leptonica (BSD 2-clause) and the
Tesseract for .NET wrapper by Charles Weld (Apache License 2.0); see `docs/licenses/Tesseract-NOTICE.txt`, which the
installer also places in the program's `licenses` folder.


Built by truevith.space and vistaar.space. Source code is released under the [MIT licence](LICENSE); the installer carries its own freeware licence.
