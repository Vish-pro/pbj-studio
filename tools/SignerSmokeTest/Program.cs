using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Signatures;
using KcsPdfSigner.Core;
using Path = System.IO.Path;

// Exercises detection, preview geometry and multi-signature signing end to end with a throwaway
// self-signed certificate, so the engine can be verified without a DSC token plugged in.

var workFolder = Path.Combine(Path.GetTempPath(), "KcsPdfSignerSmokeTest");
Directory.CreateDirectory(workFolder);

var sourcePdf = Path.Combine(workFolder, "unsigned.pdf");
var fieldPdf = Path.Combine(workFolder, "with-blank-field.pdf");
var signedPdf = Path.Combine(workFolder, "signed.pdf");

var failures = new List<string>();

// "--scan" runs only the Scan Cleaner checks (66 and up) while that tool is being worked on.
if (args.Contains("--scan"))
{
    ScanChecks.Run(workFolder, failures);
    foreach (var f in failures) Console.WriteLine("  - " + f);
    return failures.Count == 0 ? 0 : 1;
}

BuildTestPdf(sourcePdf);
BuildPdfWithBlankSignatureField(fieldPdf);
Console.WriteLine($"Built test PDFs in {workFolder}");

// ---- 1. Text anchor detection ------------------------------------------------

var settings = new DetectionSettings { Mode = DetectionMode.TextAnchors };
var placements = PlacementDetector.Detect(sourcePdf, settings);

Console.WriteLine($"\n[1] Text anchors found {placements.Count} spot(s):");
foreach (var placement in placements) Console.WriteLine("    " + placement.Describe());

Check(placements.Count == 4, $"expected 4 anchor placements, got {placements.Count}");

Check(!placements.Any(p => p.Label.Contains("Partnership", StringComparison.OrdinalIgnoreCase)),
    "word-boundary matching leaked: 'Partnership' was treated as a signing spot");

Check(placements.Any(p => p.PageNumber == 1) && placements.Any(p => p.PageNumber == 2),
    "expected placements on both pages");

// ---- 2. Existing blank signature fields --------------------------------------

var fieldPlacements = PlacementDetector.Detect(
    fieldPdf, new DetectionSettings { Mode = DetectionMode.ExistingFields });

Console.WriteLine($"\n[2] Existing-field mode found {fieldPlacements.Count} spot(s):");
foreach (var placement in fieldPlacements) Console.WriteLine("    " + placement.Describe());

Check(fieldPlacements.Count == 1, $"expected 1 blank field, got {fieldPlacements.Count}");
Check(fieldPlacements.All(p => p.ExistingFieldName == "PreparedSig"),
    "blank field was not reported under its own field name");

// ---- 3. Fixed position -------------------------------------------------------

var fixedPlacements = PlacementDetector.Detect(sourcePdf, new DetectionSettings
{
    Mode = DetectionMode.FixedPosition,
    FixedScope = PageScope.AllPages,
    FixedCorner = StampCorner.BottomRight
});

Console.WriteLine($"\n[3] Fixed-position mode produced {fixedPlacements.Count} spot(s).");
Check(fixedPlacements.Count == 2, $"expected one per page (2), got {fixedPlacements.Count}");

// ---- 4. Preview rasterising and coordinate round-trip -------------------------

var pdfBytes = File.ReadAllBytes(sourcePdf);
var png = PdfPageRasterizer.RenderPng(pdfBytes, 1);
var isPng = png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;

Console.WriteLine($"\n[4] Rendered page 1 preview: {png.Length:N0} bytes, valid PNG = {isPng}");
Check(isPng, "preview renderer did not return a PNG");

Console.WriteLine("    Rotation round-trip (PDF point -> preview pixel -> PDF point):");
foreach (var rotation in new[] { 0, 90, 180, 270 })
{
    var page = new PdfPageInfo(1, 595f, 842f, rotation);
    var probe = new SignaturePlacement
    {
        PageNumber = 1, Left = 100f, Bottom = 200f, Width = 170f, Height = 52f,
        Source = PlacementSource.Manual, PageWidth = 595f, PageHeight = 842f, Rotation = rotation
    };

    var (rotatedWidth, rotatedHeight) = PreviewGeometry.RotatedSize(page);
    double renderedWidth = rotatedWidth * 2, renderedHeight = rotatedHeight * 2;

    var box = PreviewGeometry.ToPreview(probe, page, renderedWidth, renderedHeight);
    var (backX, backY) = PreviewGeometry.FromPreview(
        box.Left + box.Width / 2, box.Top + box.Height / 2, page, renderedWidth, renderedHeight);

    var expectedX = probe.Left + probe.Width / 2;
    var expectedY = probe.Bottom + probe.Height / 2;
    var accurate = Math.Abs(backX - expectedX) < 0.5 && Math.Abs(backY - expectedY) < 0.5;

    Console.WriteLine($"      /Rotate {rotation,3}: centre ({expectedX:0.#}, {expectedY:0.#}) " +
                      $"-> ({backX:0.#}, {backY:0.#})  {(accurate ? "ok" : "MISMATCH")}");

    Check(accurate, $"preview geometry round-trip failed at /Rotate {rotation}");
}

// ---- 5. Sign every detected spot ---------------------------------------------

var options = new SigningOptions
{
    InputPath = sourcePdf,
    OutputPath = signedPdf,
    Reason = "Smoke test",
    Location = "Mangaluru",
    VisibleAppearance = true
};

var certificate = CreateSelfSignedCertificate();
var runResult = await new MultiSignatureSigner()
    .SignAsync(certificate, options, placements, null, CancellationToken.None);

Console.WriteLine($"\n[5] Signed {runResult.Signed}, failed {runResult.Failed}, " +
                  $"elapsed {runResult.Elapsed.TotalSeconds:0.0}s");

foreach (var result in runResult.Results.Where(r => r.Status != SignStatus.Signed))
    Console.WriteLine($"    NOT SIGNED [{result.FieldName}]: {result.Message}");

Check(!runResult.Aborted, "signing run aborted, nothing written");

if (!runResult.Aborted) VerifySignatures(signedPdf, placements.Count, failures);

// ---- 7 to 16. Document operations (KcsPdfSigner.Core.Documents) -------------

DocumentOpsChecks.Run(workFolder, sourcePdf, signedPdf, placements.Count, !runResult.Aborted, failures);

// ---- 20 to 29. Signed PDFs behave like Acrobat's elsewhere (viewers, printers, portals) --

if (!runResult.Aborted) SignatureCompatChecks.Run(workFolder, signedPdf, failures);

// ---- 30 and up. Printing and page tools match Acrobat ------------------------------

if (!runResult.Aborted) PrintAndKeepChecks.Run(workFolder, failures);

// ---- 33 and up. Signature verification reports what Acrobat reports -----------------

if (!runResult.Aborted) SignatureVerifyChecks.Run(workFolder, signedPdf, failures);

// ---- 46 and up. Document advisor (suggestion bars) ------------------------------------

if (!runResult.Aborted) AdvisorChecks.Run(workFolder, sourcePdf, signedPdf, failures);

// ---- 56 and up. OCR: making scanned PDFs searchable ----------------------------------

OcrChecks.Run(workFolder, failures);

// ---- 66 and up. Scan Cleaner: pictures to clean scanned pages ----------------------------

ScanChecks.Run(workFolder, failures);

// ---- Result ------------------------------------------------------------------

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("PASS - all checks succeeded.");
    return 0;
}

Console.WriteLine($"FAIL - {failures.Count} check(s) failed:");
foreach (var failure in failures) Console.WriteLine("  - " + failure);
return 1;

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

static void BuildTestPdf(string path)
{
    using var writer = new PdfWriter(path);
    using var pdf = new PdfDocument(writer);
    using var document = new Document(pdf);

    document.Add(new Paragraph("Independent Auditor's Report")
        .SetFontSize(16).SetTextAlignment(TextAlignment.CENTER));
    document.Add(new Paragraph("We have audited the accompanying financial statements. " +
                               "This paragraph mentions a Partnership firm and partnerships " +
                               "generally, which must not be mistaken for a signing spot."));
    document.Add(new Paragraph("\n\n\nFor KCS & Associates"));
    document.Add(new Paragraph("Partner"));
    document.Add(new Paragraph("Membership No. 123456"));

    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));

    document.Add(new Paragraph("Annexure to the Auditor's Report").SetFontSize(14));
    document.Add(new Paragraph("Details continue on this page."));
    document.Add(new Paragraph("\n\n\nSignature"));
    document.Add(new Paragraph("Proprietor"));
}

static void BuildPdfWithBlankSignatureField(string path)
{
    using var writer = new PdfWriter(path);
    using var pdf = new PdfDocument(writer);

    // Not disposed on purpose: closing a Document also closes the PdfDocument underneath it,
    // and the signature field still has to be added afterwards.
    var document = new Document(pdf);
    document.Add(new Paragraph("Agreement prepared with a signature field already in place."));
    document.Flush();

    var form = PdfFormCreator.GetAcroForm(pdf, true);
    var field = new SignatureFormFieldBuilder(pdf, "PreparedSig")
        .SetWidgetRectangle(new Rectangle(72, 120, 180, 60))
        .CreateSignature();

    form.AddField(field, pdf.GetPage(1));
}

static SigningCertificate CreateSelfSignedCertificate()
{
    using var rsa = RSA.Create(2048);

    var request = new CertificateRequest(
        "CN=KCS Smoke Test Signer, O=Test", rsa, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

    var certificate = request.CreateSelfSigned(
        DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

    // Round-trip through PFX so the private key is bound in a form GetRSAPrivateKey can use.
    var exported = certificate.Export(X509ContentType.Pfx, "test");
    var usable = new X509Certificate2(exported, "test",
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

    return new SigningCertificate(usable, StoreLocation.CurrentUser, "Smoke Test Provider");
}

static void VerifySignatures(string signedPdf, int expectedCount, List<string> failures)
{
    using var reader = new PdfReader(signedPdf);
    using var pdf = new PdfDocument(reader);

    var util = new SignatureUtil(pdf);
    var names = util.GetSignatureNames();

    Console.WriteLine($"\n[6] Verifying {signedPdf}");
    Console.WriteLine($"    Signature fields: {names.Count} (expected {expectedCount})");

    if (names.Count != expectedCount)
        failures.Add($"expected {expectedCount} signatures in the output, found {names.Count}");

    foreach (var name in names)
    {
        var signature = util.ReadSignatureData(name);
        var intact = signature.VerifySignatureIntegrityAndAuthenticity();
        var coversWhole = util.SignatureCoversWholeDocument(name);

        Console.WriteLine($"    [{name}] integrity={intact} coversWholeDocument={coversWhole}");

        if (!intact) failures.Add($"signature {name} failed its integrity check");
    }

    // Only the final signature covers the whole file; each earlier one covers its own revision.
    // That is how layered PDF signatures work, so exactly one must cover everything.
    var covering = names.Count(util.SignatureCoversWholeDocument);
    if (covering != 1)
        failures.Add($"expected exactly 1 signature covering the whole document, found {covering}");

    if (util.GetBlankSignatureNames().Count != 0)
        failures.Add("output still contains an unsigned signature field");
}
