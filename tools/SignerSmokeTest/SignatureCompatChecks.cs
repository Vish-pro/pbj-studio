using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Layout;
using iText.Layout.Element;
using iText.Signatures;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using SkiaSharp;
using Path = System.IO.Path;
using Rectangle = iText.Kernel.Geom.Rectangle;

/// <summary>
/// Checks 20 to 29: a signed PDF must look, print and validate elsewhere (other viewers, printers,
/// bank portals) the way the same document signed in Adobe Acrobat does.
/// </summary>
internal static class SignatureCompatChecks
{
    private static List<string> _failures = [];

    public static void Run(string workFolder, string signedPdf, List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "compat");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        var signer = Certificate("CN=KCS Compat Signer, O=Test");
        var plain = F("plain.pdf");
        BuildPdf(plain, null);

        Step("[20] Signature format matches Acrobat's default (PKCS#7 detached, SHA-256)", () =>
        {
            using var pdf = new PdfDocument(new PdfReader(signedPdf));
            var util = new SignatureUtil(pdf);
            foreach (var name in util.GetSignatureNames())
            {
                var dictionary = util.GetSignature(name);
                var data = util.ReadSignatureData(name);
                Console.WriteLine($"    [{name}] SubFilter={dictionary.GetSubFilter()} digest={data.GetDigestAlgorithmName()}");
                Check(PdfName.Adbe_pkcs7_detached.Equals(dictionary.GetSubFilter()),
                    $"format: {name} is {dictionary.GetSubFilter()}, not adbe.pkcs7.detached");
                Check(data.GetDigestAlgorithmName().Contains("SHA256"), $"format: {name} digest is {data.GetDigestAlgorithmName()}");
            }

            var acroForm = pdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
            Check(acroForm?.GetAsNumber(PdfName.SigFlags)?.IntValue() == 3, "format: AcroForm /SigFlags is not 3");
            Check(pdf.GetCatalog().GetPdfObject().Get(PdfName.Perms) is null, "format: document was certified (DocMDP) by accident");
        });

        Step("[21] Signing an already signed PDF keeps the earlier signatures valid", () =>
        {
            var before = SignatureInspector.Read(signedPdf).Count;
            var counter = F("counter-signed.pdf");
            var result = Sign(signer, signedPdf, counter, [Manual(signedPdf, 1, 300, 650, 170, 52)]);
            Check(!result.Aborted, "counter-sign: run aborted: " + string.Join("; ", result.Results.Select(r => r.Message)));
            if (result.Aborted) return;

            var all = SignatureInspector.Read(counter);
            Console.WriteLine($"    {before} existing + 1 new: " +
                              string.Join(", ", all.Select(s => $"{s.FieldName} ok={s.IntegrityOk}")));
            Check(all.Count == before + 1, $"counter-sign: expected {before + 1} signatures, found {all.Count}");
            Check(all.All(s => s.IntegrityOk), "counter-sign: an earlier signature was broken by the new one");
            Check(all.Count(s => s.CoversWholeDocument) == 1, "counter-sign: expected exactly one covering the whole file");
        });

        Step("[22] A long certificate chain (leaf + 3 CA certificates, about 7.7 KB) fits", () =>
        {
            var (leaf, cas) = Chain();
            var size = leaf.Certificate.RawData.Length + cas.Sum(c => c.RawData.Length);
            var output = F("long-chain.pdf");
            var result = Sign(leaf, plain, output, [Manual(plain, 1, 72, 600, 170, 52)], cas);
            Console.WriteLine($"    chain {size:N0} bytes: signed={result.Signed} {string.Join("; ", result.Results.Select(r => r.Message))}");
            Check(!result.Aborted, "chain: signing failed (not enough space reserved for the certificates)");
            if (result.Aborted) return;

            using var pdf = new PdfDocument(new PdfReader(output));
            var util = new SignatureUtil(pdf);
            var data = util.ReadSignatureData(util.GetSignatureNames()[0]);
            Check(data.GetCertificates().Length == 4, $"chain: expected 4 embedded certificates, found {data.GetCertificates().Length}");
            Check(data.VerifySignatureIntegrityAndAuthenticity(), "chain: signature does not verify");
        });

        Step("[23] Signer name read correctly from the certificate", () =>
        {
            var comma = Certificate(null, "SHARMA, RAVI KUMAR");
            var quotes = Certificate(null, "O'Brien & Sons \"Q\"");
            Console.WriteLine($"    '{comma.CommonName}' / '{quotes.CommonName}'");
            Check(comma.CommonName == "SHARMA, RAVI KUMAR", $"name: comma name read as '{comma.CommonName}'");
            Check(quotes.CommonName == "O'Brien & Sons \"Q\"", $"name: quoted name read as '{quotes.CommonName}'");
            Check(signer.CommonName == "KCS Compat Signer" && signer.IssuerName == "KCS Compat Signer",
                "name: plain name or issuer wrong");
        });

        Step("[24] Stamp lands upright inside the visible page (crop box, offset origin, all rotations)", () =>
        {
            var cases = new (string Name, Action<PdfPage> Setup)[]
            {
                ("crop", p => p.SetCropBox(new Rectangle(20, 30, 480, 760))),
                ("offset", p => { p.SetMediaBox(new Rectangle(-100, -100, 795, 1042)); p.SetCropBox(new Rectangle(-100, -100, 795, 1042)); }),
                ("rot90", p => p.SetRotation(90)),
                ("rot180", p => p.SetRotation(180)),
                ("rot270", p => p.SetRotation(270)),
                ("rot90-crop", p => { p.SetRotation(90); p.SetCropBox(new Rectangle(30, 50, 500, 760)); })
            };

            foreach (var (name, setup) in cases)
            {
                var input = F($"page-{name}.pdf");
                BuildPdf(input, setup);
                var page = PlacementDetector.ReadPages(input)[0];

                var fixedSpots = PlacementDetector.Detect(input, new DetectionSettings
                {
                    Mode = DetectionMode.FixedPosition, FixedScope = PageScope.AllPages, FixedCorner = StampCorner.BottomRight
                });
                var anchors = PlacementDetector.Detect(input, new DetectionSettings { Mode = DetectionMode.TextAnchors });
                var manual = PlacementDetector.CreateManual(page, page.Left + page.Width / 2, page.Bottom + page.Height / 2,
                    new DetectionSettings());
                var (displayWidth, displayHeight) = PageGeometry.DisplaySize(page);

                // The viewer lays the page out at this size and the renderer draws the crop box; when
                // the two disagree the page is shown stretched and every click lands off target.
                using (var image = SKBitmap.Decode(PdfPageRasterizer.RenderPng(File.ReadAllBytes(input), 1, 72)))
                {
                    Check(Math.Abs(image.Width - displayWidth) <= 1.5 && Math.Abs(image.Height - displayHeight) <= 1.5,
                        $"{name}: rendered page is {image.Width}x{image.Height} but laid out as {displayWidth:0}x{displayHeight:0}");
                }

                var spot = fixedSpots.Single();
                var shown = PageGeometry.UserToDisplay(page, spot.ToRectangle());
                Console.WriteLine($"    {name}: visible [{page.Left:0} {page.Bottom:0} {page.Width:0}x{page.Height:0}] /Rotate {page.Rotation}" +
                                  $" -> fixed at user ({spot.Left:0},{spot.Bottom:0} {spot.Width:0}x{spot.Height:0}), " +
                                  $"shown ({shown.GetLeft():0},{shown.GetBottom():0} {shown.GetWidth():0}x{shown.GetHeight():0}); anchors={anchors.Count}");

                Check(Inside(page, spot), $"{name}: fixed stamp is outside the visible page");
                Check(shown.GetWidth() > shown.GetHeight(), $"{name}: fixed stamp is not upright (shown taller than wide)");
                Check(shown.GetLeft() > displayWidth / 2 && shown.GetBottom() < displayHeight / 2,
                    $"{name}: 'bottom right' stamp is not at the bottom right of the page as displayed");

                var manualShown = PageGeometry.UserToDisplay(page, manual.ToRectangle());
                Check(Inside(page, manual) && manualShown.GetWidth() > manualShown.GetHeight(), $"{name}: hand-placed stamp not upright or outside");

                Check(anchors.Count == 1, $"{name}: expected 1 'Partner' anchor, found {anchors.Count}");
                if (anchors.Count == 1)
                {
                    var anchorShown = PageGeometry.UserToDisplay(page, anchors[0].ToRectangle());
                    Check(Inside(page, anchors[0]) && anchorShown.GetWidth() > anchorShown.GetHeight(),
                        $"{name}: anchor stamp not upright or outside the visible page");
                    var word = PdfTextSearch.Search(input, "Partner", caseSensitive: true, wholeWord: true).Single();
                    var wordShown = PageGeometry.UserToDisplay(page, new Rectangle(word.Left, word.Bottom, word.Width, word.Height));
                    var fits = wordShown.GetTop() + 4 + anchorShown.GetHeight() <= displayHeight - 2;
                    Check(!fits || anchorShown.GetBottom() >= wordShown.GetTop() - 1,
                        $"{name}: 'Above' anchor stamp is not above the word as displayed");
                }

                // Preview round trip on this page: the centre of the stamp comes back to itself.
                var preview = PreviewGeometry.ToPreview(spot, page, displayWidth * 1.5, displayHeight * 1.5);
                var (backX, backY) = PreviewGeometry.FromPreview(preview.Left + preview.Width / 2, preview.Top + preview.Height / 2,
                    page, displayWidth * 1.5, displayHeight * 1.5);
                Check(Math.Abs(backX - (spot.Left + spot.Width / 2)) < 0.5 && Math.Abs(backY - (spot.Bottom + spot.Height / 2)) < 0.5,
                    $"{name}: preview round trip missed ({backX:0.#},{backY:0.#})");
                Check(preview.Left >= 0 && preview.Top >= 0 && preview.Left + preview.Width <= displayWidth * 1.5 + 0.5 &&
                      preview.Top + preview.Height <= displayHeight * 1.5 + 0.5, $"{name}: preview box outside the rendered page");

                var output = F($"page-{name}-signed.pdf");
                var result = Sign(signer, input, output, [spot]);
                Check(!result.Aborted, $"{name}: signing failed");
                if (result.Aborted) continue;

                using var pdf = new PdfDocument(new PdfReader(output));
                var widget = pdf.GetPage(1).GetAnnotations().OfType<PdfWidgetAnnotation>().Single();
                var rect = widget.GetRectangle().ToRectangle();
                var visible = PageGeometry.VisibleBox(pdf.GetPage(1));
                Check(rect.GetLeft() >= visible.GetLeft() - 0.5 && rect.GetRight() <= visible.GetRight() + 0.5 &&
                      rect.GetBottom() >= visible.GetBottom() - 0.5 && rect.GetTop() <= visible.GetTop() + 0.5,
                    $"{name}: signed widget is outside the visible page");
            }
        });

        Step("[25] Exported page images include the signatures", () =>
        {
            var unsigned = Path.Combine(Path.GetDirectoryName(signedPdf)!, "unsigned.pdf");
            var a = PdfExportOps.ExportImages(signedPdf, F("export-signed"), "png", 50, [1]).Single();
            var b = PdfExportOps.ExportImages(unsigned, F("export-unsigned"), "png", 50, [1]).Single();
            Check(!File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)), "export: signed page exported without its signatures");

            var ra = PdfPageRasterizer.RenderPng(File.ReadAllBytes(signedPdf), 1, 50);
            var rb = PdfPageRasterizer.RenderPng(File.ReadAllBytes(unsigned), 1, 50);
            Check(!ra.SequenceEqual(rb), "rasterizer: signed page rendered without its signatures");
        });

        Step("[26] Blank box when 'draw name and date' is off; hidden prepared fields made visible", () =>
        {
            var blank = F("no-text.pdf");
            var result = Sign(signer, plain, blank, [Manual(plain, 1, 72, 600, 170, 52)], visible: false);
            Check(!result.Aborted, "no-text: signing failed");
            if (!result.Aborted)
            {
                var text = AppearanceText(blank);
                Console.WriteLine($"    no-text appearance has text operators: {text}");
                Check(!text, "no-text: the box still has text drawn in it");
            }

            var prepared = F("hidden-field.pdf");
            BuildPdf(prepared, null, hiddenField: true);
            var spots = PlacementDetector.Detect(prepared, new DetectionSettings { Mode = DetectionMode.ExistingFields });
            var output = F("hidden-field-signed.pdf");
            result = Sign(signer, prepared, output, spots);
            Check(spots.Count == 1 && !result.Aborted, "hidden field: not found or not signed");
            if (result.Aborted) return;

            using var pdf = new PdfDocument(new PdfReader(output));
            var flags = pdf.GetPage(1).GetAnnotations().OfType<PdfWidgetAnnotation>().Single().GetFlags();
            Console.WriteLine($"    prepared field /F 2 -> /F {flags}");
            Check((flags & PdfAnnotation.PRINT) != 0, "hidden field: Print flag missing");
            Check((flags & (PdfAnnotation.HIDDEN | PdfAnnotation.NO_VIEW | PdfAnnotation.INVISIBLE)) == 0,
                "hidden field: signature still hidden");
        });

        Step("[27] Names in Indian scripts are drawn, not shown as empty boxes", () =>
        {
            var nirmala = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "Nirmala.ttc")) ||
                          File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "Nirmala.ttf"));
            if (!nirmala)
            {
                Console.WriteLine("    SKIP: Nirmala UI is not installed on this PC");
                return;
            }

            // "Ramesh Kumar" in Devanagari, location in Kannada.
            var hindi = Certificate(null, "रमेश कुमार");
            var output = F("indic.pdf");
            var result = Sign(hindi, plain, output, [Manual(plain, 1, 72, 600, 170, 52)],
                location: "ಮಂಗಳೂರು");
            Check(!result.Aborted, "indic: signing failed");
            if (result.Aborted) return;

            var fonts = AppearanceFonts(output);
            Console.WriteLine("    fonts: " + string.Join(", ", fonts));
            Check(fonts.Any(f => f.Contains("Nirmala")), "indic: no font with Indian letters in the signature box");
            Check(!AppearanceFonts(signedPdf).Any(f => f.Contains("Nirmala")), "indic: Latin-only signature should keep its usual font");
        });

        Step("[28] Signed files are recognised (the app warns before an edit breaks them)", () =>
        {
            Check(SignatureInspector.HasSignatures(signedPdf), "detect: signed file not recognised");
            Check(!SignatureInspector.HasSignatures(plain), "detect: unsigned file reported as signed");

            // Why the warning exists: every edit rewrites the file and the signatures no longer match.
            var edited = F("signed-then-commented.pdf");
            PdfAnnotationOps.Add(signedPdf, edited, [new AnnotationSpec { Kind = AnnotationKind.Note, PageNumber = 1, Left = 72, Bottom = 72, Text = "x" }]);
            var after = SignatureInspector.Read(edited);
            Console.WriteLine($"    after adding a comment: {after.Count(s => s.IntegrityOk)} of {after.Count} signatures intact");
            Check(after.Any(s => !s.IntegrityOk) || after.Count == 0, "detect: expected the rewrite to break the signatures (test premise)");
        });

        Step("[29] Clear refusal for password-protected, restricted and certified PDFs", () =>
        {
            var userLocked = F("user-locked.pdf");
            PdfSecurityOps.Encrypt(plain, userLocked, "u1", "o1", true, true, true);
            var ownerLocked = F("owner-locked.pdf");
            PdfSecurityOps.Encrypt(plain, ownerLocked, null, "o2", true, false, false);
            var certified = F("certified.pdf");
            Certify(plain, certified, signer);

            foreach (var (file, expect) in new[] { (userLocked, "password"), (ownerLocked, "permissions"), (certified, "certified") })
            {
                var output = F(Path.GetFileNameWithoutExtension(file) + "-signed.pdf");
                string message;
                try
                {
                    var result = Sign(signer, file, output, [Manual(plain, 1, 72, 400, 170, 52)]);
                    message = result.Aborted ? string.Join("; ", result.Results.Select(r => r.Message)) : "(signed)";
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                }

                Console.WriteLine($"    {Path.GetFileName(file)}: {message}");
                Check(message.Contains(expect, StringComparison.OrdinalIgnoreCase), $"refusal: {Path.GetFileName(file)} gave '{message}'");
                Check(!File.Exists(output), $"refusal: {Path.GetFileName(file)} left an output file");
            }
        });

        Step("[32] Owner-restricted PDFs that allow signing can be signed, with their restrictions kept", () =>
        {
            var allows = F("owner-allows-signing.pdf");
            PdfSecurityOps.Encrypt(plain, allows, null, "o-allow", allowPrint: false, allowCopy: false, allowModify: true);
            var report = PdfPermissions.Inspect(allows);
            Console.WriteLine($"    permissions: restricted={report.IsRestricted} printing={report.AllowsPrinting} signing={report.AllowsSigning}");
            Check(report.IsRestricted && report.AllowsSigning && !report.AllowsPrinting, "restricted: permission bits read wrongly");

            var refused = F("owner-allows-refused.pdf");
            var wasRefused = false;
            try
            {
                wasRefused = Sign(signer, allows, refused, [Manual(plain, 1, 72, 400, 170, 52)]).Aborted;
            }
            catch (InvalidOperationException)
            {
                wasRefused = true;
            }

            Check(wasRefused && !File.Exists(refused), "restricted: signing must be refused unless the user agreed");

            var signed = F("owner-allows-signed.pdf");
            var options = new SigningOptions
            {
                InputPath = allows, OutputPath = signed, Reason = "Restricted check", Location = "Mangaluru",
                AllowRestrictedSigning = true
            };
            var result = new MultiSignatureSigner(null)
                .SignAsync(signer, options, [Manual(plain, 1, 72, 400, 170, 52)], null, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"    signed={result.Signed} {string.Join("; ", result.Results.Select(r => r.Message))}");
            Check(!result.Aborted && File.Exists(signed), "restricted: signing with the user's agreement failed");
            if (result.Aborted || !File.Exists(signed)) return;

            var after = PdfPermissions.Inspect(signed);
            Check(after.IsEncrypted && after.IsRestricted && !after.AllowsPrinting, "restricted: the owner's restrictions must still apply to the signed file");

            using var reader = new PdfReader(signed);
            reader.SetUnethicalReading(true);
            using var pdf = new PdfDocument(reader);
            var util = new SignatureUtil(pdf);
            Check(util.GetSignatureNames().Count == 1, "restricted: expected one signature");
            Check(util.ReadSignatureData(util.GetSignatureNames()[0]).VerifySignatureIntegrityAndAuthenticity(), "restricted: signature does not verify");

            var noSigning = F("owner-no-signing.pdf");
            PdfSecurityOps.Encrypt(plain, noSigning, null, "o-no", allowPrint: true, allowCopy: true, allowModify: false);
            // allowModify=false clears modify, annotations, fill-in and assembly.
            var blocked = PdfPermissions.Inspect(noSigning);
            Check(blocked.IsRestricted && !blocked.AllowsSigning, "restricted: a file that forbids modifying must not be reported as signable");
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static SignRunResult Sign(SigningCertificate certificate, string input, string output,
        List<SignaturePlacement> placements, IEnumerable<X509Certificate2>? extra = null, bool visible = true,
        string location = "Mangaluru")
    {
        var options = new SigningOptions
        {
            InputPath = input, OutputPath = output, Reason = "Compat check", Location = location, VisibleAppearance = visible
        };
        return new MultiSignatureSigner(extra).SignAsync(certificate, options, placements, null, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static SignaturePlacement Manual(string pdf, int pageNumber, float left, float bottom, float width, float height)
    {
        var page = PlacementDetector.ReadPages(pdf)[pageNumber - 1];
        return new SignaturePlacement
        {
            PageNumber = pageNumber, Left = left, Bottom = bottom, Width = width, Height = height,
            Source = PlacementSource.Manual, PageWidth = page.Width, PageHeight = page.Height,
            PageLeft = page.Left, PageBottom = page.Bottom, Rotation = page.Rotation
        };
    }

    private static bool Inside(PdfPageInfo page, SignaturePlacement spot) =>
        spot.Left >= page.Left - 0.5 && spot.Bottom >= page.Bottom - 0.5 &&
        spot.Left + spot.Width <= page.Left + page.Width + 0.5 && spot.Bottom + spot.Height <= page.Bottom + page.Height + 0.5;

    private static void BuildPdf(string path, Action<PdfPage>? setup, bool hiddenField = false)
    {
        using var pdf = new PdfDocument(new PdfWriter(path));
        var document = new Document(pdf);
        document.Add(new Paragraph("Compatibility check page."));
        document.Add(new Paragraph("\n\n\nFor KCS & Associates"));
        document.Add(new Paragraph("Partner"));
        document.Flush();

        if (hiddenField)
        {
            var form = PdfFormCreator.GetAcroForm(pdf, true);
            var field = new SignatureFormFieldBuilder(pdf, "Prepared").SetWidgetRectangle(new Rectangle(72, 120, 180, 60)).CreateSignature();
            form.AddField(field, pdf.GetPage(1));
            field.GetWidgets()[0].SetFlags(PdfAnnotation.HIDDEN);
        }

        setup?.Invoke(pdf.GetPage(1));
        document.Close();
    }

    private static void Certify(string input, string output, SigningCertificate certificate)
    {
        using var reader = new PdfReader(input);
        using var stream = File.Create(output);
        var signer = new PdfSigner(reader, stream, new StampingProperties());
        signer.SetSignerProperties(new SignerProperties()
            .SetFieldName("Author")
            .SetCertificationLevel(AccessPermissions.NO_CHANGES_PERMITTED));
        using var external = new TokenExternalSignature(certificate.Certificate);
        var factory = iText.Bouncycastleconnector.BouncyCastleFactoryCreator.GetFactory();
        signer.SignDetached(external, [factory.CreateX509Certificate(new MemoryStream(certificate.Certificate.RawData))],
            null, null, null, 0, PdfSigner.CryptoStandard.CMS);
    }

    /// <summary>True when the first widget's appearance (including nested forms) draws any text.</summary>
    private static bool AppearanceText(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var widget = pdf.GetPage(1).GetAnnotations().OfType<PdfWidgetAnnotation>().First();
        var found = false;
        Walk(widget.GetNormalAppearanceObject(), stream =>
        {
            var content = System.Text.Encoding.Latin1.GetString(stream.GetBytes());
            if (content.Contains("Tj") || content.Contains("TJ")) found = true;
        }, _ => { });
        return found;
    }

    private static List<string> AppearanceFonts(string path)
    {
        using var pdf = new PdfDocument(new PdfReader(path));
        var fonts = new List<string>();
        foreach (var widget in pdf.GetPage(1).GetAnnotations().OfType<PdfWidgetAnnotation>())
            Walk(widget.GetNormalAppearanceObject(), _ => { }, font => fonts.Add(font));
        return fonts.Distinct().ToList();
    }

    private static void Walk(PdfObject? obj, Action<PdfStream> onStream, Action<string> onFont, int depth = 0)
    {
        if (obj is not PdfStream stream || depth > 6) return;
        onStream(stream);
        var resources = stream.GetAsDictionary(PdfName.Resources);
        var fonts = resources?.GetAsDictionary(PdfName.Font);
        if (fonts is not null)
            foreach (var key in fonts.KeySet())
                onFont(fonts.GetAsDictionary(key)?.GetAsName(PdfName.BaseFont)?.GetValue() ?? "");
        var xobjects = resources?.GetAsDictionary(PdfName.XObject);
        if (xobjects is not null)
            foreach (var key in xobjects.KeySet())
                Walk(xobjects.Get(key), onStream, onFont, depth + 1);
    }

    private static SigningCertificate Certificate(string? distinguishedName, string? commonName = null)
    {
        using var rsa = RSA.Create(2048);
        X500DistinguishedName subject;
        if (commonName is not null)
        {
            var builder = new X500DistinguishedNameBuilder();
            builder.AddCommonName(commonName);
            builder.AddOrganizationName("Test");
            subject = builder.Build();
        }
        else
        {
            subject = new X500DistinguishedName(distinguishedName!);
        }

        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        var usable = new X509Certificate2(created.Export(X509ContentType.Pfx, "t"), "t",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        return new SigningCertificate(usable, StoreLocation.CurrentUser, "Smoke Test Provider");
    }

    /// <summary>
    /// A chain shaped like an Indian DSC: root (RSA 4096) -> CA (4096) -> sub-CA (4096) -> leaf (2048),
    /// padded with a private extension to the sizes real CA certificates reach with their policy,
    /// CRL and AIA extensions. Nothing is added to the Windows certificate store.
    /// </summary>
    private static (SigningCertificate Leaf, List<X509Certificate2> Authorities) Chain()
    {
        using var rootKey = RSA.Create(4096);
        using var caKey = RSA.Create(4096);
        using var subKey = RSA.Create(4096);
        var leafKey = RSA.Create(2048);

        X509Certificate2 Make(string name, RSA key, X509Certificate2? issuer, RSA? issuerKey, bool authority, int padding)
        {
            var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            if (!authority)
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            var filler = new byte[padding + 4];
            filler[0] = 0x04; filler[1] = 0x82; filler[2] = (byte)(padding >> 8); filler[3] = (byte)padding;
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.99999.1", filler, false));

            return issuer is null
                ? request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(5))
                : request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey!, RSASignaturePadding.Pkcs1),
                    DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2), BitConverter.GetBytes(Random.Shared.NextInt64()));
        }

        var root = Make("CN=Smoke Root CA 2022, O=Smoke PKI, C=IN", rootKey, null, null, true, 300);
        var ca = Make("CN=Smoke CA 2022, O=Smoke CA, C=IN", caKey, root, rootKey, true, 600);
        var sub = Make("CN=Smoke Sub CA for Class 3 Individual 2022, O=Smoke CA, C=IN", subKey, ca, caKey, true, 700);
        var leaf = Make("CN=KCS CHAIN SIGNER, O=Personal, C=IN", leafKey, sub, subKey, false, 900).CopyWithPrivateKey(leafKey);
        var usable = new X509Certificate2(leaf.Export(X509ContentType.Pfx, "t"), "t",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        return (new SigningCertificate(usable, StoreLocation.CurrentUser, "Smoke Test Provider"), [sub, ca, root]);
    }

    private static void Step(string title, Action body)
    {
        Console.WriteLine($"\n{title}");
        var before = _failures.Count;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _failures.Add($"{title} threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine(_failures.Count == before ? "    ok" : $"    {_failures.Count - before} problem(s)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) _failures.Add(message);
    }
}
