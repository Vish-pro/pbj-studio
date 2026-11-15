using System.Diagnostics;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms;
using iText.Forms.Fields;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.Kernel.Pdf;
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

    private static SignRunResult Sign(
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
        var document = PrepareSignatureFields(options.InputPath, targets, results);

        using var signature = new TokenExternalSignature(certificate.Certificate);
        var chain = BuildChain(certificate.Certificate);
        var timestampClient = string.IsNullOrWhiteSpace(options.TimestampUrl)
            ? null
            : new TSAClientBouncyCastle(options.TimestampUrl.Trim());

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
                    timestampClient);

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

        using (var reader = new PdfReader(inputPath))
        using (var writer = new PdfWriter(buffer))
        using (var document = new PdfDocument(reader, writer))
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
        ITSAClient? timestampClient)
    {
        using var input = new MemoryStream(document, writable: false);
        using var output = new MemoryStream();

        using (var reader = new PdfReader(input))
        {
            var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());

            var properties = new SignerProperties()
                .SetFieldName(fieldName)
                .SetClaimedSignDate(DateTime.Now)
                .SetSignatureCreator("PB&J Studio");

            if (!string.IsNullOrWhiteSpace(options.Reason))
                properties.SetReason(options.Reason.Trim());

            if (!string.IsNullOrWhiteSpace(options.Location))
                properties.SetLocation(options.Location.Trim());

            if (!string.IsNullOrWhiteSpace(options.ContactInfo))
                properties.SetContact(options.ContactInfo.Trim());

            if (options.VisibleAppearance)
            {
                var appearanceText = new SignedAppearanceText()
                    .SetSignedBy(certificate.CommonName)
                    .SetSignDate(DateTime.Now)
                    .SetReasonLine(string.IsNullOrWhiteSpace(options.Reason)
                        ? string.Empty
                        : "Reason: " + options.Reason.Trim())
                    .SetLocationLine(string.IsNullOrWhiteSpace(options.Location)
                        ? string.Empty
                        : "Location: " + options.Location.Trim());

                properties.SetSignatureAppearance(
                    new SignatureFieldAppearance(SignerProperties.IGNORED_ID)
                        .SetContent(appearanceText));
            }

            signer.SetSignerProperties(properties);

            signer.SignDetached(
                signature,
                chain,
                crlList: null,
                ocspClient: null,
                tsaClient: timestampClient,
                estimatedSize: 0,
                sigtype: PdfSigner.CryptoStandard.CADES);
        }

        return output.ToArray();
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
    private static IX509Certificate[] BuildChain(SysX509.X509Certificate2 leaf)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        var encoded = new List<byte[]>();

        try
        {
            using var chain = new SysX509.X509Chain();
            chain.ChainPolicy.RevocationMode = SysX509.X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = SysX509.X509VerificationFlags.AllFlags;
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
        iText.Kernel.Exceptions.BadPasswordException =>
            "PDF is password protected, so it cannot be signed without the password.",
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
