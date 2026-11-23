using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.Signatures;
using iText.Signatures.Validation;
using iText.Signatures.Validation.Context;
using iText.Signatures.Validation.Report;
using KcsPdfSigner.Core;
using KcsPdfSigner.Core.Documents;
using Path = System.IO.Path;
using Rectangle = iText.Kernel.Geom.Rectangle;

/// <summary>
/// Checks 33 and up: signature verification reports what Adobe Acrobat reports (valid, unknown,
/// invalid; allowed and disallowed changes after signing; certification; trust).
/// Throwaway certificates only; nothing is added to the Windows certificate store.
/// </summary>
internal static class SignatureVerifyChecks
{
    private static List<string> _failures = [];

    public static void Run(string workFolder, string signedPdf, List<string> failures)
    {
        _failures = failures;
        var folder = Path.Combine(workFolder, "verify");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);

        var (root, ca, leaf) = Chain("Verify");
        var trustRoot = new VerificationOptions { TrustedCertificates = [root], CheckRevocationOnline = false };
        var noTrust = new VerificationOptions { CheckRevocationOnline = false };

        var plain = F("plain.pdf");
        BuildPdf(plain);
        var signed = F("signed.pdf");
        Sign(plain, signed, leaf, [ca, root], "Sig1", new Rectangle(72, 640, 170, 52));

        Step("[33] A good signature is Valid when its root is trusted, Unknown when not", () =>
        {
            var unknown = SignatureVerifier.Verify(signed, null, noTrust);
            var valid = SignatureVerifier.Verify(signed, null, trustRoot);
            var byIssuer = SignatureVerifier.Verify(signed, null, new VerificationOptions { TrustedCertificates = [ca], CheckRevocationOnline = false });
            var s = valid.Signatures.Single();
            Show(unknown);
            Show(valid);
            Check(unknown.Overall == SignatureValidity.Unknown && unknown.Signatures[0].IntegrityOk, "trust: untrusted root should be Unknown with integrity intact");
            Check(unknown.BannerText == "Signature validity is unknown.", "trust: banner text for unknown");
            Check(valid.Overall == SignatureValidity.Valid && valid.BannerText == "Signed and all signatures are valid.", "trust: trusted root should be Valid");
            Check(byIssuer.Overall == SignatureValidity.Valid, "trust: trusting the issuing CA should also make it Valid");
            Check(s.Changes == ChangesAfterSigning.None && s.CoversWholeDocument, "trust: expected no later changes");
            Check(s.Chain.Count == 3 && s.Chain[^1].Thumbprint == root.Thumbprint, "trust: chain leaf -> CA -> root not reported");
            Check(s.Checks.Any(c => c.Text.Contains("clock on the signer's computer")), "trust: missing 'signer's computer clock' line");
            Check(s.Checks.Any(c => c.Text.Contains("does not name a revocation service")), "trust: missing revocation line");
            Check(s.PageNumber == 1 && Math.Abs(s.Left - 72) < 1 && s.SignerName == "VERIFY SIGNER", $"trust: page/position/signer wrong ({s.PageNumber} {s.Left} '{s.SignerName}')");

            // CAdES and other digests.
            foreach (var (standard, hash) in new[] { (PdfSigner.CryptoStandard.CADES, "SHA-256"), (PdfSigner.CryptoStandard.CMS, "SHA-384"),
                         (PdfSigner.CryptoStandard.CMS, "SHA-512"), (PdfSigner.CryptoStandard.CMS, "SHA-1") })
            {
                var output = F($"digest-{standard}-{hash}.pdf");
                Sign(plain, output, leaf, [ca, root], "Sig1", new Rectangle(72, 640, 170, 52), standard: standard, hash: hash);
                var r = SignatureVerifier.Verify(output, null, trustRoot).Signatures.Single();
                Console.WriteLine($"    {standard} {hash}: {r.SubFilter} {r.DigestAlgorithm} -> {r.Status}");
                Check(r.Status == SignatureValidity.Valid && r.IntegrityOk, $"format: {standard} {hash} not Valid ({r.Summary})");
                if (hash == "SHA-1") Check(r.Checks.Any(c => c.Text.Contains("SHA-1")), "format: SHA-1 warning missing");
            }
        });

        Step("[34] A file changed inside the signed bytes is Invalid", () =>
        {
            var bytes = File.ReadAllBytes(signed);
            var tampered = F("tampered.pdf");
            // Flip a character in the document text area: find "Verification page" and change one letter.
            var marker = System.Text.Encoding.ASCII.GetBytes("/Producer");
            var at = IndexOf(bytes, marker);
            var position = at > 0 ? at + 12 : 12;
            bytes[position] = (byte)(bytes[position] == (byte)'x' ? 'y' : 'x');
            File.WriteAllBytes(tampered, bytes);
            var result = SignatureVerifier.Verify(tampered, null, trustRoot);
            Show(result);
            Check(result.Overall == SignatureValidity.Invalid && !result.Signatures[0].IntegrityOk, "tamper: changed byte not reported as Invalid");
            Check(result.BannerText == "At least one signature has problems.", "tamper: banner text");
        });

        Step("[35] Content added by a later update is reported as a not-allowed change", () =>
        {
            var content = F("content-added.pdf");
            Append(signed, content, pdf =>
            {
                var canvas = new PdfCanvas(pdf.GetPage(1).NewContentStreamAfter(), pdf.GetPage(1).GetResources(), pdf);
                canvas.Rectangle(300, 300, 100, 30).Fill();
            });
            var newPage = F("page-added.pdf");
            Append(signed, newPage, pdf => pdf.AddNewPage());
            var font = F("resources-changed.pdf");
            Append(signed, font, pdf =>
            {
                // A new image in the page's resources: what the page can draw has changed.
                var resources = pdf.GetPage(1).GetResources().GetPdfObject();
                var xobjects = resources.GetAsDictionary(PdfName.XObject) ?? new PdfDictionary();
                var image = new PdfStream(new byte[] { 0, 0, 0 });
                image.Put(PdfName.Type, PdfName.XObject);
                image.Put(PdfName.Subtype, PdfName.Image);
                image.Put(PdfName.Width, new PdfNumber(1));
                image.Put(PdfName.Height, new PdfNumber(1));
                image.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
                image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
                image.MakeIndirect(pdf);
                xobjects.Put(new PdfName("Im99"), image);
                resources.Put(PdfName.XObject, xobjects);
                resources.SetModified();
                pdf.GetPage(1).GetPdfObject().SetModified();
            });

            foreach (var file in new[] { content, newPage, font })
            {
                var result = SignatureVerifier.Verify(file, null, trustRoot);
                Show(result);
                var s = result.Signatures.Single();
                Check(s.IntegrityOk, $"{Path.GetFileName(file)}: the signed revision itself is intact");
                Check(s.Status == SignatureValidity.Invalid && s.Changes == ChangesAfterSigning.NotAllowed,
                    $"{Path.GetFileName(file)}: expected Invalid with not-allowed changes, got {s.Status}/{s.Changes}");
            }
        });

        var counter = F("counter-signed.pdf");
        Step("[36] A second signature added later leaves the first one valid", () =>
        {
            Sign(signed, counter, leaf, [ca, root], "Sig2", new Rectangle(300, 640, 170, 52));
            var result = SignatureVerifier.Verify(counter, null, trustRoot);
            Show(result);
            Check(result.Signatures.Count == 2 && result.Overall == SignatureValidity.Valid, "counter: both signatures should be valid");
            var first = result.Signatures.First(s => s.FieldName == "Sig1");
            Check(first.Changes == ChangesAfterSigning.AllowedOnly && first.ChangeDescriptions.Any(d => d.Contains("later signature")),
                "counter: first signature should report 'signed by a later signature'");
            Check(result.Signatures.First(s => s.FieldName == "Sig2").Changes == ChangesAfterSigning.None, "counter: last signature covers everything");
        });

        var withField = F("with-text-field.pdf");
        Step("[37] Form fill-in and comments after signing are allowed changes", () =>
        {
            BuildPdf(withField, textField: true);
            var signedForm = F("form-signed.pdf");
            Sign(withField, signedForm, leaf, [ca, root], "Sig1", new Rectangle(72, 500, 170, 52));
            var filled = F("form-filled.pdf");
            Append(signedForm, filled, pdf => PdfFormCreator.GetAcroForm(pdf, false).GetField("Name").SetValue("Ravi Kumar"));
            var commented = F("commented.pdf");
            Append(signedForm, commented, pdf =>
            {
                var note = new PdfTextAnnotation(new Rectangle(400, 700, 20, 20));
                note.SetContents("Checked");
                pdf.GetPage(1).AddAnnotation(note);
            });

            foreach (var (file, expect) in new[] { (filled, "filled in"), (commented, "comment") })
            {
                var result = SignatureVerifier.Verify(file, null, trustRoot);
                Show(result);
                var s = result.Signatures.Single();
                Check(s.Status == SignatureValidity.Valid && s.Changes == ChangesAfterSigning.AllowedOnly &&
                      s.ChangeDescriptions.Any(d => d.Contains(expect, StringComparison.OrdinalIgnoreCase)),
                    $"{Path.GetFileName(file)}: expected Valid with allowed change '{expect}', got {s.Status}/{s.Changes}: {string.Join(" | ", s.ChangeDescriptions)}");
            }

            // iText's own DocMDP/FieldMDP validator must agree on allowed versus not allowed.
            foreach (var (file, allowed) in new[] { (filled, true), (commented, true), (counter, true), (F("content-added.pdf"), false), (F("page-added.pdf"), false) })
            {
                var report = ITextRevisionCheck(file);
                Console.WriteLine($"    iText cross-check {Path.GetFileName(file)}: {report}");
                Check(report == (allowed ? "VALID" : "INVALID"), $"cross-check: iText says {report} for {Path.GetFileName(file)}");
            }
        });

        Step("[38] Certified documents: permitted and forbidden changes follow the certification level", () =>
        {
            var noChanges = F("certified-p1.pdf");
            Sign(withField, noChanges, leaf, [ca, root], "Author", new Rectangle(72, 500, 170, 52), level: AccessPermissions.NO_CHANGES_PERMITTED);
            var formsOnly = F("certified-p2.pdf");
            Sign(withField, formsOnly, leaf, [ca, root], "Author", new Rectangle(72, 500, 170, 52), level: AccessPermissions.FORM_FIELDS_MODIFICATION);

            var p1Filled = F("certified-p1-filled.pdf");
            Append(noChanges, p1Filled, pdf => PdfFormCreator.GetAcroForm(pdf, false).GetField("Name").SetValue("Changed"));
            var p2Filled = F("certified-p2-filled.pdf");
            Append(formsOnly, p2Filled, pdf => PdfFormCreator.GetAcroForm(pdf, false).GetField("Name").SetValue("Allowed"));
            var p2Signed = F("certified-p2-signed.pdf");
            Sign(formsOnly, p2Signed, leaf, [ca, root], "Approval", new Rectangle(300, 500, 170, 52));
            var p2Comment = F("certified-p2-comment.pdf");
            Append(formsOnly, p2Comment, pdf => pdf.GetPage(1).AddAnnotation(new PdfTextAnnotation(new Rectangle(400, 700, 20, 20)).SetContents("x")));

            var clean = SignatureVerifier.Verify(noChanges, null, trustRoot);
            Show(clean);
            Check(clean.CertificationLevel == 1 && clean.Overall == SignatureValidity.Valid && clean.BannerDetail.Contains("Certified by"),
                "certified: P=1 document not reported as certified and valid");

            foreach (var (file, expected) in new[] { (p1Filled, SignatureValidity.Invalid), (p2Filled, SignatureValidity.Valid),
                         (p2Signed, SignatureValidity.Valid), (p2Comment, SignatureValidity.Invalid) })
            {
                var result = SignatureVerifier.Verify(file, null, trustRoot);
                Show(result);
                var author = result.Signatures.First(s => s.FieldName == "Author");
                Check(author.Status == expected, $"certified: {Path.GetFileName(file)} expected {expected}, got {author.Status} ({string.Join(" | ", author.ChangeDescriptions)})");
            }
        });

        Step("[39] A certificate that had expired when signing makes the signature Invalid", () =>
        {
            var expired = Certificate("CN=EXPIRED SIGNER, O=Test", DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-2));
            var output = F("expired.pdf");
            Sign(plain, output, expired, [], "Sig1", new Rectangle(72, 640, 170, 52));
            var result = SignatureVerifier.Verify(output, null, new VerificationOptions { TrustedCertificates = [expired], CheckRevocationOnline = false });
            Show(result);
            var s = result.Signatures.Single();
            Check(s.IntegrityOk && s.Status == SignatureValidity.Invalid && s.Checks.Any(c => c.Text.Contains("not valid at the signing time")),
                "expired: expected Invalid because the certificate had expired at signing time");
        });

        Step("[40] Empty signature fields are listed as not signed yet", () =>
        {
            var blank = Path.Combine(workFolder, "with-blank-field.pdf");
            var result = SignatureVerifier.Verify(blank, null, noTrust);
            Console.WriteLine($"    {Path.GetFileName(blank)}: signatures={result.Signatures.Count} unsigned={string.Join(",", result.UnsignedFields.Select(u => u.FieldName + "@p" + u.PageNumber))} banner='{result.BannerText}'");
            Check(result.Overall is null && result.UnsignedFields.Count == 1 && result.UnsignedFields[0].FieldName == "PreparedSig" &&
                  result.UnsignedFields[0].PageNumber == 1, "unsigned: blank field not reported");

            var partly = F("signed-plus-blank.pdf");
            Sign(blank, partly, leaf, [ca, root], "Other", new Rectangle(300, 640, 170, 52));
            var mixed = SignatureVerifier.Verify(partly, null, trustRoot);
            Show(mixed);
            Check(mixed.Signatures.Count == 1 && mixed.UnsignedFields.Count == 1 && mixed.BannerDetail.Contains("not signed yet"),
                "unsigned: signed file with an empty field not reported");
        });

        Step("[41] Password-protected signed files are checked with the password", () =>
        {
            var locked = F("locked.pdf");
            PdfSecurityOps.Encrypt(plain, locked, "user-pw", "owner-pw", true, true, true);
            var lockedSigned = F("locked-signed.pdf");
            Sign(locked, lockedSigned, leaf, [ca, root], "Sig1", new Rectangle(72, 640, 170, 52), password: "owner-pw");
            var withPassword = SignatureVerifier.Verify(lockedSigned, "user-pw", trustRoot);
            Show(withPassword);
            Check(withPassword.Overall == SignatureValidity.Valid, "encrypted: signed file with user password should be Valid");
            var without = SignatureVerifier.Verify(lockedSigned, null, trustRoot);
            Console.WriteLine($"    without password: '{without.BannerText}' {without.DocumentError}");
            Check(without.DocumentError.Contains("password", StringComparison.OrdinalIgnoreCase), "encrypted: missing password not explained");

            var ownerOnly = Path.Combine(workFolder, "compat", "owner-allows-signed.pdf");
            if (File.Exists(ownerOnly))
            {
                var restricted = SignatureVerifier.Verify(ownerOnly, null, noTrust);
                Show(restricted);
                Check(restricted.Signatures.Count == 1 && restricted.Signatures[0].IntegrityOk, "encrypted: owner-restricted signed file not readable");
            }
        });

        Step("[42] The 4-signature file from check [5]: all intact, earlier ones changed only by later signatures", () =>
        {
            var untrusted = SignatureVerifier.Verify(signedPdf, null, noTrust);
            Show(untrusted);
            Check(untrusted.Signatures.Count == 4 && untrusted.Signatures.All(s => s.IntegrityOk), "four: not all intact");
            Check(untrusted.Overall == SignatureValidity.Unknown, "four: self-signed and untrusted should be Unknown");

            var signerCertificate = new X509Certificate2(untrusted.Signatures[0].Certificate!.RawData);
            var trusted = SignatureVerifier.Verify(signedPdf, null, new VerificationOptions { TrustedCertificates = [signerCertificate], CheckRevocationOnline = false });
            Show(trusted);
            Check(trusted.Overall == SignatureValidity.Valid, "four: trusting the signer's own certificate should make all Valid");
            Check(trusted.Signatures.Count(s => s.Changes == ChangesAfterSigning.AllowedOnly) == 3 &&
                  trusted.Signatures.Count(s => s.Changes == ChangesAfterSigning.None) == 1, "four: expected 3 'later changes allowed' + 1 covering all");
        });

        Step("[43] Damaged signatures and damaged files never crash and are never Valid", () =>
        {
            var bogus = F("bogus-signature.pdf");
            BuildBogusSignature(plain, bogus, garbage: true);
            var noContents = F("no-contents.pdf");
            BuildBogusSignature(plain, noContents, garbage: false);
            foreach (var file in new[] { bogus, noContents })
            {
                var result = SignatureVerifier.Verify(file, null, trustRoot);
                Show(result);
                Check(result.Signatures.Count == 1 && result.Signatures[0].Status == SignatureValidity.Invalid,
                    $"{Path.GetFileName(file)}: malformed signature should be Invalid");
            }

            // Wrong startxref: the file can only be read by rebuilding its structure.
            var bytes = File.ReadAllBytes(counter);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            var at = text.LastIndexOf("startxref", StringComparison.Ordinal);
            var damaged = F("damaged-xref.pdf");
            var head = text[..(at + "startxref".Length)];
            File.WriteAllBytes(damaged, System.Text.Encoding.Latin1.GetBytes(head + "\n12345\n%%EOF\n"));
            var repaired = SignatureVerifier.Verify(damaged, null, trustRoot);
            Show(repaired);
            Check(repaired.Overall != SignatureValidity.Valid, "damaged: a repaired file must not be reported Valid");
            Check(repaired.StructureRepaired || !string.IsNullOrEmpty(repaired.DocumentError), "damaged: the damage was not reported");

            var notPdf = F("not-a-pdf.pdf");
            File.WriteAllText(notPdf, "hello");
            var junk = SignatureVerifier.Verify(notPdf, null, noTrust);
            Console.WriteLine($"    not a PDF: '{junk.BannerText}' {junk.DocumentError}");
            Check(!string.IsNullOrEmpty(junk.DocumentError), "damaged: a non-PDF should give a clear error");
        });

        Step("[44] 100 signatures are checked in under 30 seconds", () =>
        {
            var many = plain;
            var build = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                var next = F($"many-{i % 2}.pdf");
                if (File.Exists(next) && next != many) File.Delete(next);
                Sign(many, next, leaf, [ca, root], "M" + i, new Rectangle(20 + (i % 10) * 55, 20 + (i / 10) * 40, 50, 30));
                many = next;
            }

            Console.WriteLine($"    built in {build.Elapsed.TotalSeconds:0.0}s, {new FileInfo(many).Length:N0} bytes");
            var timer = Stopwatch.StartNew();
            var result = SignatureVerifier.Verify(many, null, trustRoot);
            timer.Stop();
            Console.WriteLine($"    verified {result.Signatures.Count} signatures in {timer.Elapsed.TotalSeconds:0.0}s: {result.BannerText} {result.BannerDetail}");
            Check(timer.Elapsed.TotalSeconds < 30, $"performance: 100 signatures took {timer.Elapsed.TotalSeconds:0.0}s");
            Check(result.Signatures.Count == 100 && result.Overall == SignatureValidity.Valid, "performance: expected 100 valid signatures");
            Check(result.Signatures.Count(s => s.Changes == ChangesAfterSigning.AllowedOnly) == 99, "performance: expected 99 with later signatures");
        });

        Step("[45] An RFC 3161 timestamp is verified and its authority's trust is reported", () =>
        {
            var (tsaRoot, tsa, tsaKey) = TimestampAuthority();
            var stamped = F("timestamped.pdf");
            Sign(plain, stamped, leaf, [ca, root], "Sig1", new Rectangle(72, 640, 170, 52), tsa: new TestTsaClient(tsa, tsaRoot, tsaKey));

            var trustedTsa = SignatureVerifier.Verify(stamped, null, new VerificationOptions { TrustedCertificates = [root, tsaRoot], CheckRevocationOnline = false });
            Show(trustedTsa);
            var s = trustedTsa.Signatures.Single();
            Check(s.TimestampTime is not null && s.TimestampVerified && s.TimestampTrusted && s.Status == SignatureValidity.Valid &&
                  s.Checks.Any(c => c.Text.Contains("RFC 3161") && c.Outcome == CheckOutcome.Pass), "timestamp: trusted timestamp not reported");

            var untrustedTsa = SignatureVerifier.Verify(stamped, null, trustRoot).Signatures.Single();
            Check(untrustedTsa.TimestampVerified && !untrustedTsa.TimestampTrusted &&
                  untrustedTsa.Checks.Any(c => c.Text.Contains("not trusted, so the time is not relied on")), "timestamp: untrusted authority not reported");
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static void Show(DocumentVerification result)
    {
        Console.WriteLine($"    -> {result.BannerText} {result.BannerDetail}");
        foreach (var s in result.Signatures)
            Console.WriteLine($"       [{s.FieldName}] {s.Status} integrity={s.IntegrityOk} changes={s.Changes} {s.Summary}");
    }

    private static string ITextRevisionCheck(string file)
    {
        using var pdf = new PdfDocument(new PdfReader(file));
        var context = new ValidationContext(ValidatorContext.DOCUMENT_REVISIONS_VALIDATOR, CertificateSource.SIGNER_CERT, TimeBasedContext.PRESENT);
        var report = new ValidatorChainBuilder().BuildDocumentRevisionsValidator().ValidateAllDocumentRevisions(context, pdf);
        return report.GetValidationResult().ToString();
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }

        return -1;
    }

    private static void BuildPdf(string path, bool textField = false)
    {
        using var pdf = new PdfDocument(new PdfWriter(path));
        var document = new Document(pdf);
        document.Add(new Paragraph("Verification page."));
        document.Add(new Paragraph("For KCS & Associates"));
        document.Flush();
        if (textField)
        {
            var form = PdfFormCreator.GetAcroForm(pdf, true);
            var field = new TextFormFieldBuilder(pdf, "Name").SetWidgetRectangle(new Rectangle(72, 600, 200, 20)).CreateText();
            field.SetValue("");
            form.AddField(field, pdf.GetPage(1));
        }

        document.Close();
    }

    private static void Append(string input, string output, Action<PdfDocument> edit)
    {
        using var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(output), new StampingProperties().UseAppendMode());
        edit(pdf);
    }

    private static void Sign(string input, string output, X509Certificate2 certificate, IEnumerable<X509Certificate2> issuers, string field,
        Rectangle rect, AccessPermissions? level = null, string? password = null, PdfSigner.CryptoStandard standard = PdfSigner.CryptoStandard.CMS,
        string hash = "SHA-256", ITSAClient? tsa = null)
    {
        var readerProperties = new ReaderProperties();
        if (password is not null) readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        using var reader = new PdfReader(input, readerProperties);
        using var stream = File.Create(output);
        var signer = new PdfSigner(reader, stream, new StampingProperties().UseAppendMode());
        var properties = new SignerProperties().SetFieldName(field).SetPageNumber(1).SetPageRect(rect).SetReason("Verify check").SetLocation("Mangaluru");
        if (level is { } permissions) properties.SetCertificationLevel(permissions);
        signer.SetSignerProperties(properties);
        var factory = BouncyCastleFactoryCreator.GetFactory();
        var chain = new[] { certificate }.Concat(issuers).Select(c => factory.CreateX509Certificate(new MemoryStream(c.RawData))).ToArray();
        signer.SignDetached(new HashSigner(certificate, hash), chain, null, null, tsa, 0, standard);
    }

    /// <summary>A file whose signature field holds a broken signature dictionary.</summary>
    private static void BuildBogusSignature(string input, string output, bool garbage)
    {
        using var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(output));
        var signature = new PdfDictionary();
        signature.Put(PdfName.Type, PdfName.Sig);
        signature.Put(PdfName.Filter, PdfName.Adobe_PPKLite);
        signature.Put(PdfName.SubFilter, PdfName.Adbe_pkcs7_detached);
        signature.Put(PdfName.ByteRange, new PdfArray(new[] { 0, 10, 20, 30 }));
        if (garbage) signature.Put(PdfName.Contents, new PdfString(new byte[] { 0x30, 0x03, 0x01, 0x02, 0x03 }).SetHexWriting(true));
        signature.MakeIndirect(pdf);

        var form = PdfFormCreator.GetAcroForm(pdf, true);
        var field = new SignatureFormFieldBuilder(pdf, "Broken").SetWidgetRectangle(new Rectangle(72, 400, 150, 40)).CreateSignature();
        field.Put(PdfName.V, signature);
        form.AddField(field, pdf.GetPage(1));
        form.SetSignatureFlags(PdfAcroForm.SIGNATURE_EXIST | PdfAcroForm.APPEND_ONLY);
    }

    private static X509Certificate2 Certificate(string name, DateTimeOffset from, DateTimeOffset to)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var created = request.CreateSelfSigned(from, to);
        return new X509Certificate2(created.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>Root CA -> issuing CA -> signer, shaped like an Indian DSC chain (no revocation addresses).</summary>
    private static (X509Certificate2 Root, X509Certificate2 Ca, X509Certificate2 Leaf) Chain(string tag)
    {
        using var rootKey = RSA.Create(2048);
        using var caKey = RSA.Create(2048);
        var leafKey = RSA.Create(2048);

        X509Certificate2 Make(string name, RSA key, X509Certificate2? issuer, RSA? issuerKey, bool authority)
        {
            var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(authority
                ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
                : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            return issuer is null
                ? request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddYears(5))
                : request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey!, RSASignaturePadding.Pkcs1),
                    DateTimeOffset.Now.AddDays(-5), DateTimeOffset.Now.AddYears(2), BitConverter.GetBytes(Random.Shared.NextInt64()));
        }

        var root = Make($"CN={tag} Root CA, O=Smoke PKI, C=IN", rootKey, null, null, true);
        var ca = Make($"CN={tag} Sub CA, O=Smoke PKI, C=IN", caKey, root, rootKey, true);
        var leaf = Make("CN=VERIFY SIGNER, O=Personal, C=IN", leafKey, ca, caKey, false).CopyWithPrivateKey(leafKey);
        var usable = new X509Certificate2(leaf.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        return (root, ca, usable);
    }

    /// <summary>A throwaway timestamp authority: root and a TSA certificate with the timeStamping usage.</summary>
    private static (X509Certificate2 Root, X509Certificate2 Tsa, RSA Key) TimestampAuthority()
    {
        using var rootKey = RSA.Create(2048);
        var tsaKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Smoke TSA Root, O=Smoke PKI", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddYears(5));

        var request = new CertificateRequest("CN=Smoke Timestamp Authority, O=Smoke PKI", tsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var tsa = request.Create(root.SubjectName, X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.Now.AddDays(-5), DateTimeOffset.Now.AddYears(2), BitConverter.GetBytes(Random.Shared.NextInt64()));
        return (new X509Certificate2(root.RawData), tsa, tsaKey);
    }

    /// <summary>RSA signer with a chosen digest (the app's own signer always uses SHA-256).</summary>
    private sealed class HashSigner(X509Certificate2 certificate, string hash) : IExternalSignature
    {
        public string GetDigestAlgorithmName() => hash.Replace("-", "");
        public string GetSignatureAlgorithmName() => "RSA";
        public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

        public byte[] Sign(byte[] message)
        {
            using var rsa = certificate.GetRSAPrivateKey()!;
            var name = hash switch
            {
                "SHA-1" => HashAlgorithmName.SHA1,
                "SHA-384" => HashAlgorithmName.SHA384,
                "SHA-512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256
            };
            return rsa.SignData(message, name, RSASignaturePadding.Pkcs1);
        }
    }

    /// <summary>Answers timestamp requests locally with BouncyCastle (no network).</summary>
    private sealed class TestTsaClient(X509Certificate2 tsa, X509Certificate2 root, RSA tsaKey) : ITSAClient
    {
        public int GetTokenSizeEstimate() => 8192;

        public iText.Commons.Digest.IMessageDigest GetMessageDigest() => new BouncyCastleDigest().GetMessageDigest("SHA256");

        public byte[] GetTimeStampToken(byte[] imprint)
        {
            var key = Org.BouncyCastle.Security.DotNetUtilities.GetRsaKeyPair(tsaKey.ExportParameters(true)).Private;
            var certificate = Org.BouncyCastle.Security.DotNetUtilities.FromX509Certificate(tsa);
            var rootCertificate = Org.BouncyCastle.Security.DotNetUtilities.FromX509Certificate(root);
            var generator = new Org.BouncyCastle.Tsp.TimeStampTokenGenerator(key, certificate,
                Org.BouncyCastle.Tsp.TspAlgorithms.Sha256, "1.3.6.1.4.1.99999.2");
            generator.SetCertificates(Org.BouncyCastle.Utilities.Collections.CollectionUtilities.CreateStore(new[] { certificate, rootCertificate }));
            var request = new Org.BouncyCastle.Tsp.TimeStampRequestGenerator();
            request.SetCertReq(true);
            var tsq = request.Generate(Org.BouncyCastle.Tsp.TspAlgorithms.Sha256, imprint, Org.BouncyCastle.Math.BigInteger.ValueOf(Random.Shared.NextInt64(1, long.MaxValue)));
            var token = generator.Generate(tsq, Org.BouncyCastle.Math.BigInteger.ValueOf(Random.Shared.NextInt64(1, long.MaxValue)), DateTime.UtcNow);
            return token.GetEncoded();
        }
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
            Console.WriteLine("    " + ex);
        }

        Console.WriteLine(_failures.Count == before ? "    ok" : $"    {_failures.Count - before} problem(s)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failures.Add(message);
            Console.WriteLine("    FAIL: " + message);
        }
    }
}
