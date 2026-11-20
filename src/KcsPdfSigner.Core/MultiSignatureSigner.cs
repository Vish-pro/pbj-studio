using System.Diagnostics;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms;
using iText.Forms.Fields;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.Kernel.Pdf;
using iText.Layout.Element;
using iText.Signatures;
using SysX509 = System.Security.Cryptography.X509Certificates;

namespace KcsPdfSigner.Core;

/// <summary>
/// Applies a genuine cryptographic signature at every enabled placement in one document.
/// </summary>
/// <remarks>
/// <para>
/// A PDF signature covers one field, so N spots means N signing operations layered as N
/// incremental updates. The order matters: every empty signature field is created up front in a
/// single write, and only then is each one signed. Creating fields after the first signature
/// would make each later field an unsigned change to an already-signed document, which readers
/// flag. Building them first means signature one already covers all the widgets, and every
/// subsequent pass only fills in a field that was there when the first signature was made.
/// </para>
/// <para>
/// The run is all-or-nothing: the document is assembled in memory and only written out once every
/// placement has succeeded, so a token failure half way through cannot leave a partly signed file.
/// </para>
/// </remarks>
public sealed class MultiSignatureSigner
{
    private const string FieldPrefix = "KcsSig";

    private readonly IReadOnlyList<SysX509.X509Certificate2> _extraChainCertificates;

    public MultiSignatureSigner()
        : this(null)
    {
    }

    /// <param name="extraChainCertificates">
    /// Intermediate (and root) certificates to use when building the chain that is embedded in each
    /// signature, in addition to the ones Windows already knows. Normally empty: DSC token drivers
    /// install their CA certificates into Windows.
    /// </param>
    public MultiSignatureSigner(IEnumerable<SysX509.X509Certificate2>? extraChainCertificates)
    {
        _extraChainCertificates = extraChainCertificates?.ToList() ?? [];
    }

    public Task<SignRunResult> SignAsync(
        SigningCertificate certificate,
        SigningOptions options,
        IReadOnlyList<SignaturePlacement> placements,
        IProgress<SignProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Sign(certificate, options, placements, progress, cancellationToken),
            cancellationToken);
    }

    private SignRunResult Sign(
        SigningCertificate certificate,
        SigningOptions options,
        IReadOnlyList<SignaturePlacement> placements,
        IProgress<SignProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var targets = placements.Where(p => p.Enabled).ToList();
        if (targets.Count == 0)
        {
            return new SignRunResult
            {
                Results = [],
                OutputPath = null,
                Elapsed = stopwatch.Elapsed
            };
        }

        var results = targets
            .Select(p => new PlacementResult { Placement = p })
            .ToList();

        // Pass 0: every signature field is created before any signature is applied.
        byte[] document;
        try
        {
            document = PrepareSignatureFields(options.InputPath, targets, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Describe(ex), ex);
        }

        using var signature = new TokenExternalSignature(certificate.Certificate);
        var chain = BuildChain(certificate.Certificate, _extraChainCertificates);
        var timestampClient = string.IsNullOrWhiteSpace(options.TimestampUrl)
            ? null
            : new TSAClientBouncyCastle(options.TimestampUrl.Trim());
        var estimatedSize = EstimateSignatureSize(chain, timestampClient is not null);

        for (var i = 0; i < results.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SignRunResult
                {
                    Results = results,
                    OutputPath = null,
                    Cancelled = true,
                    Elapsed = stopwatch.Elapsed
                };
            }

            var result = results[i];
            result.Status = SignStatus.Signing;

            try
            {
                document = SignOneField(
                    document, result.FieldName, certificate, options, signature, chain,
                    timestampClient, estimatedSize);

                result.Status = SignStatus.Signed;
                result.Message = "Signed";
            }
            catch (Exception ex)
            {
                result.Status = SignStatus.Failed;
                result.Message = Describe(ex);

                // Abandon the run rather than write a document signed in some places only.
                return new SignRunResult
                {
                    Results = results,
                    OutputPath = null,
                    Elapsed = stopwatch.Elapsed
                };
            }

            progress?.Report(new SignProgress(i + 1, results.Count, result));
        }

        WriteAtomically(options.OutputPath, document);

        stopwatch.Stop();
        return new SignRunResult
        {
            Results = results,
            OutputPath = options.OutputPath,
            Elapsed = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Adds an empty signature field for every placement that does not already point at one,
    /// recording the field name each result will later be signed into.
    /// </summary>
    private static byte[] PrepareSignatureFields(
        string inputPath, IReadOnlyList<SignaturePlacement> targets, List<PlacementResult> results)
    {
        using var buffer = new MemoryStream();

        // A document that already carries signatures (a client's, a co-signer's, or an earlier run)
        // must only be appended to: rewriting it, as a plain save does, breaks every one of them.
        // Unsigned documents are still rewritten here, which also repairs a slightly damaged file
        // before the signing passes (those always append).
        bool alreadySigned;
        using (var probe = new PdfDocument(new PdfReader(inputPath)))
        {
            alreadySigned = new SignatureUtil(probe).GetSignatureNames().Count > 0;
            if (ReadCertificationLevel(probe) == 1)
                throw new InvalidOperationException(
                    "This PDF was certified by its author with \"no changes allowed\", so any further " +
                    "signature would invalidate the certification. Ask the author for an uncertified copy.");
        }

        var stamping = new StampingProperties();
        if (alreadySigned) stamping.UseAppendMode();

        using (var reader = new PdfReader(inputPath))
        using (var writer = new PdfWriter(buffer))
        using (var document = new PdfDocument(reader, writer, stamping))
        {
            var form = PdfFormCreator.GetAcroForm(document, true);
            var usedNames = new HashSet<string>(form.GetAllFormFields().Keys, StringComparer.Ordinal);
            var counter = 1;

            for (var i = 0; i < targets.Count; i++)
            {
                var placement = targets[i];

                if (!string.IsNullOrEmpty(placement.ExistingFieldName))
                {
                    results[i].FieldName = placement.ExistingFieldName;
                    continue;
                }

                string name;
                do
                {
                    name = FieldPrefix + counter++;
                } while (!usedNames.Add(name));

                var field = new SignatureFormFieldBuilder(document, name)
                    .SetWidgetRectangle(placement.ToRectangle())
                    .CreateSignature();

                // Without the Print flag, printers and "Print to PDF" leave the signature box out
                // (Acrobat's own signature widgets carry Print + Locked).
                MarkPrintable(field);

                form.AddField(field, document.GetPage(placement.PageNumber));
                results[i].FieldName = name;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Signs one named field, returning the resulting document bytes.</summary>
    private static byte[] SignOneField(
        byte[] document,
        string fieldName,
        SigningCertificate certificate,
        SigningOptions options,
        TokenExternalSignature signature,
        IX509Certificate[] chain,
        ITSAClient? timestampClient,
        int estimatedSize)
    {
        using var input = new MemoryStream(document, writable: false);
        using var output = new MemoryStream();

        // One moment for both the signature's /M date and the date drawn in the box.
        var now = DateTimeOffset.Now;

        using (var reader = new PdfReader(input))
        {
            var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());
            EnsurePrintable(signer.GetDocument(), fieldName);

            var properties = new SignerProperties()
                .SetFieldName(fieldName)
                .SetClaimedSignDate(now.LocalDateTime)
                .SetSignatureCreator("PB&J Studio");

            if (!string.IsNullOrWhiteSpace(options.Reason))
                properties.SetReason(options.Reason.Trim());

            if (!string.IsNullOrWhiteSpace(options.Location))
                properties.SetLocation(options.Location.Trim());

            if (!string.IsNullOrWhiteSpace(options.ContactInfo))
                properties.SetContact(options.ContactInfo.Trim());

            if (options.VisibleAppearance)
            {
                // Same layout as Adobe Acrobat's default look: the signer's name large on the left,
                // "Digitally signed by ... / Date: ..." (plus Reason and Location when given) on the right.
                var description = new System.Text.StringBuilder()
                    .Append("Digitally signed by ").Append(certificate.CommonName).Append('\n')
                    .Append("Date: ").Append(FormatSignDate(now));
                if (!string.IsNullOrWhiteSpace(options.Reason))
                    description.Append("\nReason: ").Append(options.Reason.Trim());
                if (!string.IsNullOrWhiteSpace(options.Location))
                    description.Append("\nLocation: ").Append(options.Location.Trim());

                var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID)
                    .SetContent(certificate.CommonName, description.ToString());

                // Acrobat draws its signatures in Myriad Pro. When Myriad Pro is installed on this PC
                // (Adobe Acrobat puts it there) a subset of it is embedded for an exact match; otherwise
                // the bundled Source Sans 3 (Adobe's open-source Myriad-style face) is used. The Myriad
                // file is read from the PC and never shipped with this app.
                var font = LoadAppearanceFont();
                var fallback = Covers(font, certificate.CommonName + description) ? null : LoadFallbackFont();
                if (fallback is null)
                {
                    appearance.SetFont(font);
                }
                else
                {
                    // A name, reason or place in Hindi, Kannada or another Indian script: Myriad and
                    // Source Sans have no such letters, which would print as empty boxes. Those letters
                    // come from Windows' Nirmala UI; Latin text keeps the usual font.
                    var provider = new iText.Layout.Font.FontProvider();
                    provider.AddFont(font.GetFontProgram(), iText.IO.Font.PdfEncodings.IDENTITY_H);
                    provider.AddFont(fallback, iText.IO.Font.PdfEncodings.IDENTITY_H);
                    appearance.SetProperty(iText.Layout.Properties.Property.FONT_PROVIDER, provider);
                    appearance.SetFontFamily(FamilyName(font.GetFontProgram()), FamilyName(fallback));
                }

                properties.SetSignatureAppearance(appearance);
            }
            else
            {
                // "Draw name and date" switched off: the box stays blank. Without an appearance of
                // our own iText would draw its default text block there.
                properties.SetSignatureAppearance(
                    new SignatureFieldAppearance(SignerProperties.IGNORED_ID).SetContent(new Div()));
            }

            signer.SetSignerProperties(properties);

            // PKCS#7 detached (adbe.pkcs7.detached) is what Adobe Acrobat writes by default and what
            // every PDF reader and validator accepts; CAdES (ETSI.CAdES.detached) is unknown to older
            // readers and to many portal validators.
            signer.SignDetached(
                signature,
                chain,
                crlList: null,
                ocspClient: null,
                tsaClient: timestampClient,
                estimatedSize: estimatedSize,
                sigtype: PdfSigner.CryptoStandard.CMS);
        }

        return output.ToArray();
    }

    private static string? _fontPath;
    private static bool _fontSearched;

    /// <summary>True when the font has a glyph for every visible character of the text.</summary>
    internal static bool Covers(iText.Kernel.Font.PdfFont font, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = char.ConvertToUtf32(text, i);
            if (char.IsSurrogatePair(text, i)) i++;
            if (codePoint < 0x20 || char.IsWhiteSpace((char)Math.Min(codePoint, 0xFFFF))) continue;
            if (!font.ContainsGlyph(codePoint)) return false;
        }

        return true;
    }

    /// <summary>Windows' font for the Indian scripts (Nirmala UI); null when it is not there.</summary>
    internal static iText.IO.Font.FontProgram? LoadFallbackFont()
    {
        try
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var collection = System.IO.Path.Combine(fonts, "Nirmala.ttc");
            if (File.Exists(collection))
                return new iText.IO.Font.TrueTypeCollection(collection).GetFontByTccIndex(0);

            var single = System.IO.Path.Combine(fonts, "Nirmala.ttf");
            if (File.Exists(single)) return iText.IO.Font.FontProgramFactory.CreateFont(single);
        }
        catch
        {
            // Unreadable: the letters stay as they are.
        }

        return null;
    }

    private static string FamilyName(iText.IO.Font.FontProgram program)
    {
        var names = program.GetFontNames();
        var family = names.GetFamilyName();
        return family is { Length: > 0 } && family[0] is { Length: > 3 } && !string.IsNullOrEmpty(family[0][3])
            ? family[0][3]
            : names.GetFontName();
    }

    private static iText.Kernel.Font.PdfFont LoadAppearanceFont()
    {
        if (!_fontSearched)
        {
            _fontSearched = true;
            _fontPath = FindMyriadPro();
        }

        if (_fontPath is not null)
        {
            try
            {
                return iText.Kernel.Font.PdfFontFactory.CreateFont(
                    _fontPath, iText.IO.Font.PdfEncodings.IDENTITY_H,
                    iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
            }
            catch
            {
                _fontPath = null; // unreadable: use the bundled font
            }
        }

        var assembly = typeof(MultiSignatureSigner).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("SourceSans3-Regular.otf", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return iText.Kernel.Font.PdfFontFactory.CreateFont(
            memory.ToArray(), iText.IO.Font.PdfEncodings.IDENTITY_H,
            iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
    }

    private static string? FindMyriadPro()
    {
        // PBJ_NO_MYRIAD=1 forces the bundled font (used to test the fallback).
        if (Environment.GetEnvironmentVariable("PBJ_NO_MYRIAD") == "1") return null;

        try
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var local = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            foreach (var folder in new[] { fonts, local })
            {
                foreach (var name in new[] { "MyriadPro-Regular.otf", "MyriadPro-Regular.ttf" })
                {
                    var candidate = System.IO.Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var adobe = System.IO.Path.Combine(Environment.GetFolderPath(root), "Adobe");
                if (!Directory.Exists(adobe)) continue;

                var found = Directory.EnumerateFiles(adobe, "MyriadPro-Regular.otf", SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
        }
        catch
        {
            // No access or no Adobe folder: Helvetica is used.
        }

        return null;
    }

    /// <summary>Sets the Print flag on every widget of a signature field.</summary>
    private static void MarkPrintable(iText.Forms.Fields.PdfFormField field)
    {
        foreach (var annotation in field.GetWidgets())
        {
            annotation.SetFlag(iText.Kernel.Pdf.Annot.PdfAnnotation.PRINT);
        }
    }

    private static void EnsurePrintable(iText.Kernel.Pdf.PdfDocument document, string fieldName)
    {
        var form = iText.Forms.Fields.PdfFormCreator.GetAcroForm(document, false);
        var field = form?.GetField(fieldName);
        if (field is null) return;

        // A blank field prepared by someone else may also be marked hidden or screen-only; a signature
        // drawn into it would then be invisible or vanish on paper.
        const int hiding = iText.Kernel.Pdf.Annot.PdfAnnotation.INVISIBLE |
                           iText.Kernel.Pdf.Annot.PdfAnnotation.HIDDEN |
                           iText.Kernel.Pdf.Annot.PdfAnnotation.NO_VIEW |
                           iText.Kernel.Pdf.Annot.PdfAnnotation.TOGGLE_NO_VIEW;

        foreach (var widget in field.GetWidgets())
        {
            var flags = widget.GetFlags();
            var wanted = (flags & ~hiding) | iText.Kernel.Pdf.Annot.PdfAnnotation.PRINT;
            if (wanted != flags) widget.SetFlags(wanted);
        }
    }

    /// <summary>The DocMDP permission level (1, 2 or 3) of a certified document; null when not certified.</summary>
    private static int? ReadCertificationLevel(PdfDocument document)
    {
        var docMdp = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms)?.GetAsDictionary(PdfName.DocMDP);
        var references = docMdp?.GetAsArray(PdfName.Reference);
        if (references is null) return null;

        for (var i = 0; i < references.Size(); i++)
        {
            var reference = references.GetAsDictionary(i);
            if (!PdfName.DocMDP.Equals(reference?.GetAsName(PdfName.TransformMethod))) continue;
            return reference!.GetAsDictionary(PdfName.TransformParams)?.GetAsNumber(PdfName.P)?.IntValue() ?? 2;
        }

        return null;
    }

    /// <summary>
    /// Space reserved for each signature. iText's automatic figure (8 KB, plus 4 KB for a timestamp)
    /// ignores the certificate chain, and Indian DSC chains (leaf, sub-CA, CA, CCA root) run to 6 to 8 KB
    /// on their own, which made signing fail with "Not enough space". Reserving too much costs only padding.
    /// </summary>
    internal static int EstimateSignatureSize(IReadOnlyCollection<IX509Certificate> chain, bool withTimestamp)
    {
        var chainBytes = chain.Sum(certificate => certificate.GetEncoded().Length);
        return 8192 + chainBytes + (withTimestamp ? 12288 : 0);
    }

    /// <summary>Acrobat's date style: 2026.11.12 12:13:01 +05'30'.</summary>
    internal static string FormatSignDate(DateTimeOffset moment)
    {
        var offset = moment.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{moment:yyyy.MM.dd HH:mm:ss} {sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }

    private static void WriteAtomically(string outputPath, byte[] document)
    {
        var directory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = outputPath + ".signing.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, document);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch (IOException) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Builds the full issuing chain so a reader can walk up to the root. Falls back to the leaf
    /// alone if the chain cannot be built: the signature is still valid, it just shows as an
    /// unknown identity until the reader locates the intermediates itself.
    /// </summary>
    private static IX509Certificate[] BuildChain(
        SysX509.X509Certificate2 leaf, IReadOnlyList<SysX509.X509Certificate2> extraCertificates)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        var encoded = new List<byte[]>();

        try
        {
            using var chain = new SysX509.X509Chain();
            chain.ChainPolicy.RevocationMode = SysX509.X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = SysX509.X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(extraCertificates.ToArray());
            chain.Build(leaf);

            foreach (var element in chain.ChainElements)
            {
                encoded.Add(element.Certificate.RawData);
            }
        }
        catch (Exception)
        {
            encoded.Clear();
        }

        if (encoded.Count == 0) encoded.Add(leaf.RawData);

        return encoded
            .Select(bytes => factory.CreateX509Certificate(new MemoryStream(bytes)))
            .ToArray();
    }

    private static string Describe(Exception ex) => ex switch
    {
        iText.Kernel.Exceptions.BadPasswordException bad when bad.Message.Contains("owner", StringComparison.OrdinalIgnoreCase) =>
            "This PDF restricts changes with a permissions (owner) password, so it cannot be signed as it is. " +
            "Remove the restriction in Protect (it needs that password), then sign.",
        iText.Kernel.Exceptions.BadPasswordException =>
            "PDF is password protected, so it cannot be signed without the password.",
        iText.Kernel.Exceptions.PdfException append when append.Message.Contains("Append mode", StringComparison.OrdinalIgnoreCase) =>
            "This PDF already carries signatures but its internal structure is damaged, so another signature " +
            "cannot be added without breaking the existing ones.",
        InvalidOperationException invalid => invalid.Message,
        UnauthorizedAccessException =>
            "Access denied. Check the output folder is writable.",
        IOException io when io.Message.Contains("being used by another process",
            StringComparison.OrdinalIgnoreCase) =>
            "The file is open in another program. Close it and retry.",
        System.Security.Cryptography.CryptographicException crypto =>
            "Token reported: " + crypto.Message +
            " (a wrong PIN, a cancelled PIN prompt, or an unplugged token all land here)",
        _ => ex.Message
    };
}
