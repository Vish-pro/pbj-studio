using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace KcsPdfSigner.Core.Documents;

/// <summary>Overall result for one signature, as Acrobat reports it.</summary>
public enum SignatureValidity
{
    /// <summary>Unchanged since signing (or only allowed changes) and the signer is trusted.</summary>
    Valid,

    /// <summary>Unchanged, but the signer could not be confirmed (not trusted, revocation unknown, ...).</summary>
    Unknown,

    /// <summary>Altered after signing, damaged signature, or a certificate that was not valid.</summary>
    Invalid
}

/// <summary>Outcome of one line in the list of checks.</summary>
public enum CheckOutcome
{
    Pass,
    Fail,
    Warning,
    Info
}

/// <summary>What happened to the document after a signature was applied.</summary>
public enum ChangesAfterSigning
{
    /// <summary>The signature covers the whole file.</summary>
    None,

    /// <summary>Only later signatures, form fill-in, comments or validation data.</summary>
    AllowedOnly,

    /// <summary>Changes that the signature (or the certification) does not allow.</summary>
    NotAllowed,

    /// <summary>Changes that could not be classified.</summary>
    Unclassified
}

public sealed class SignatureCheck
{
    public CheckOutcome Outcome { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>Readable facts about one certificate (DER bytes kept for viewing and trusting).</summary>
public sealed class CertificateSummary
{
    public string Subject { get; init; } = "";
    public string CommonName { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string IssuerCommonName { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Thumbprint { get; init; } = "";
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public bool IsSelfSigned { get; init; }
    public byte[] RawData { get; init; } = [];

    public static CertificateSummary From(X509Certificate2 certificate) => new()
    {
        Subject = certificate.Subject,
        CommonName = Cn(certificate.SubjectName) ?? certificate.GetNameInfo(X509NameType.SimpleName, false),
        Issuer = certificate.Issuer,
        IssuerCommonName = Cn(certificate.IssuerName) ?? certificate.GetNameInfo(X509NameType.SimpleName, true),
        SerialNumber = certificate.SerialNumber,
        Thumbprint = certificate.Thumbprint,
        NotBefore = certificate.NotBefore,
        NotAfter = certificate.NotAfter,
        IsSelfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData),
        RawData = certificate.RawData
    };

    private static string? Cn(X500DistinguishedName name)
    {
        foreach (var part in name.EnumerateRelativeDistinguishedNames())
        {
            if (part.GetSingleElementType().Value == "2.5.4.3") return part.GetSingleElementValue();
        }

        return null;
    }
}

/// <summary>Everything found out about one signature.</summary>
public sealed class SignatureVerification
{
    public string FieldName { get; init; } = "";
    public bool IsDocumentTimestamp { get; set; }
    public string SignerName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Location { get; set; } = "";
    public string ContactInfo { get; set; } = "";
    public string SubFilter { get; set; } = "";
    public string DigestAlgorithm { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";

    /// <summary>Time the signer's computer claimed (signing-time attribute or /M).</summary>
    public DateTime? ClaimedSigningTime { get; set; }

    /// <summary>Time in an RFC 3161 timestamp, when one is embedded.</summary>
    public DateTime? TimestampTime { get; set; }

    public string TimestampAuthority { get; set; } = "";

    /// <summary>True when a timestamp is embedded, matches this signature and its token verifies.</summary>
    public bool TimestampVerified { get; set; }

    /// <summary>True when the timestamp authority's certificate is trusted, so its time can be relied on.</summary>
    public bool TimestampTrusted { get; set; }

    /// <summary>The time the certificate checks used.</summary>
    public DateTime? ValidationTime { get; set; }

    public bool IntegrityOk { get; set; }
    public bool CoversWholeDocument { get; set; }
    public int Revision { get; set; }
    public int TotalRevisions { get; set; }
    public int PageNumber { get; set; }
    public float Left { get; set; }
    public float Bottom { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public CertificateSummary? Certificate { get; set; }

    /// <summary>Signer certificate first, then each issuer as far as the chain could be built.</summary>
    public List<CertificateSummary> Chain { get; set; } = [];

    public bool IdentityTrusted { get; set; }

    /// <summary>"Windows certificate store", "your trusted certificates", or "" when not trusted.</summary>
    public string TrustSource { get; set; } = "";

    public string RevocationText { get; set; } = "";
    public ChangesAfterSigning Changes { get; set; }
    public List<string> ChangeDescriptions { get; set; } = [];

    /// <summary>1 to 3 when this is the certification (DocMDP) signature, otherwise 0.</summary>
    public int CertificationLevel { get; set; }

    public SignatureValidity Status { get; set; }
    public string Summary { get; set; } = "";
    public List<SignatureCheck> Checks { get; set; } = [];
    public string Error { get; set; } = "";
}

/// <summary>A signature field that nobody has signed yet.</summary>
public sealed class UnsignedSignatureField
{
    public string FieldName { get; init; } = "";
    public int PageNumber { get; init; }
    public float Left { get; init; }
    public float Bottom { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

/// <summary>The result of checking every signature in a PDF.</summary>
public sealed class DocumentVerification
{
    public List<SignatureVerification> Signatures { get; init; } = [];
    public List<UnsignedSignatureField> UnsignedFields { get; init; } = [];

    /// <summary>0 when not certified, else the DocMDP level 1, 2 or 3.</summary>
    public int CertificationLevel { get; set; }

    public string CertifiedBy { get; set; } = "";
    public bool StructureRepaired { get; set; }

    /// <summary>Set when the file could not be read at all.</summary>
    public string DocumentError { get; set; } = "";

    /// <summary>Worst status over all signatures; null when the file has no signatures.</summary>
    public SignatureValidity? Overall { get; set; }

    public string BannerText { get; set; } = "";
    public string BannerDetail { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;

    public bool IsSigned => Signatures.Count > 0;

    public static string CertificationDescription(int level) => level switch
    {
        1 => "no changes are allowed",
        2 => "only filling in forms and signing are allowed",
        3 => "filling in forms, signing and commenting are allowed",
        _ => ""
    };
}

public sealed class VerificationOptions
{
    /// <summary>Certificates the user chose to trust (roots, issuers or signers). Never added to Windows.</summary>
    public IReadOnlyCollection<X509Certificate2> TrustedCertificates { get; init; } = [];

    /// <summary>Ask the certificate authority online whether a certificate was revoked.</summary>
    public bool CheckRevocationOnline { get; init; } = true;

    public TimeSpan RevocationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Validates the digital signatures in a PDF the way Adobe Acrobat does: the signed bytes are
/// checked cryptographically, later changes are classified (allowed or not), and the signer's
/// certificate is checked against the Windows certificate store plus a list of certificates
/// the user chose to trust. Nothing is trusted silently and the Windows store is never changed.
/// </summary>
public static class SignatureVerifier
{
    private static readonly ConcurrentDictionary<string, (DateTime When, RevocationResult Result)> RevocationCache = new();
    private static readonly TimeSpan RevocationCacheLifetime = TimeSpan.FromMinutes(15);

    private enum RevocationState
    {
        Good,
        Revoked,
        NotChecked,
        NoAddress,
        Skipped
    }

    private sealed record RevocationResult(RevocationState State, string Text);

    public static DocumentVerification Verify(string path, string? password, VerificationOptions? options = null,
        CancellationToken cancellation = default)
    {
        options ??= new VerificationOptions();
        var result = new DocumentVerification();

        byte[] bytes;
        try
        {
            PdfIo.RequireFile(path);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            result.DocumentError = ex.Message;
            Finish(result);
            return result;
        }

        PdfDocument document;
        PdfReader reader;
        try
        {
            reader = new PdfReader(new MemoryStream(bytes, false), PdfIo.ReaderProperties(password));
            reader.SetUnethicalReading(true);
            document = new PdfDocument(reader);
        }
        catch (Exception ex)
        {
            result.DocumentError = ex is iText.Kernel.Exceptions.BadPasswordException
                ? "The file is password protected; the password is needed to check its signatures."
                : "The file could not be read as a PDF (" + ex.Message + ").";
            Finish(result);
            return result;
        }

        using (document)
        {
            result.StructureRepaired = reader.HasRebuiltXref() || reader.HasFixedXref();

            SignatureUtil util;
            List<string> names;
            try
            {
                util = new SignatureUtil(document);
                names = util.GetSignatureNames().ToList();
            }
            catch (Exception ex)
            {
                result.DocumentError = "The signature fields could not be read (" + ex.Message + ").";
                Finish(result);
                return result;
            }

            var widgets = WidgetPlaces(document);
            var form = SafeForm(document);
            var certification = ReadCertification(document);

            int totalRevisions;
            try { totalRevisions = util.GetTotalRevisions(); } catch { totalRevisions = 0; }

            // Read each signature: one bad signature must never stop the others being checked.
            var boundaries = new Dictionary<string, long>();
            var fieldLocks = new Dictionary<string, (string Action, HashSet<string> Fields)?>();
            foreach (var name in names)
            {
                cancellation.ThrowIfCancellationRequested();
                var verification = new SignatureVerification { FieldName = name, TotalRevisions = totalRevisions };
                Place(verification, name, form, widgets);
                try
                {
                    var end = ReadSignature(verification, name, util, bytes, options, certification);
                    if (end > 0) boundaries[name] = end;
                    fieldLocks[name] = ReadFieldLock(util, name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    verification.IntegrityOk = false;
                    verification.Error = "The signature could not be read: " + ex.Message;
                }

                result.Signatures.Add(verification);
            }

            // What changed after each signature.
            var ordered = boundaries.Values.Where(v => v < bytes.LongLength).Distinct().OrderBy(v => v).ToList();
            List<List<RevisionChange>> steps;
            try
            {
                // In a repaired file the object locations cannot be trusted, so revisions are not compared.
                steps = result.StructureRepaired
                    ? ordered.Select(_ => new List<RevisionChange>
                    {
                        new(RevisionChangeKind.Unclassified, "The file structure is damaged, so the changes made after signing could not be analysed.")
                    }).ToList()
                    : SignatureRevisionAnalyzer.AnalyzeSteps(bytes, password, ordered, cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                steps = ordered.Select(_ => new List<RevisionChange>
                {
                    new(RevisionChangeKind.Unclassified, "The changes made after signing could not be analysed (" + ex.Message + ").")
                }).ToList();
            }

            long certificationEnd = certification.FieldName is not null && boundaries.TryGetValue(certification.FieldName, out var ce) ? ce : -1;

            foreach (var verification in result.Signatures)
            {
                if (!boundaries.TryGetValue(verification.FieldName, out var end)) continue;
                if (end >= bytes.LongLength)
                {
                    verification.Changes = ChangesAfterSigning.None;
                    continue;
                }

                var index = ordered.IndexOf(end);
                var changes = new List<(RevisionChange Change, bool UnderCertification)>();
                for (var i = Math.Max(0, index); i < steps.Count; i++)
                    foreach (var change in steps[i])
                        changes.Add((change, certificationEnd >= 0 && ordered[i] >= certificationEnd));

                Judge(verification, changes, certification.Level, fieldLocks.GetValueOrDefault(verification.FieldName));
            }

            // Unsigned signature fields.
            try
            {
                foreach (var blank in util.GetBlankSignatureNames())
                {
                    var place = new SignatureVerification { FieldName = blank };
                    Place(place, blank, form, widgets);

                    // A value without the signature bytes is a damaged signature, not an empty field.
                    if (form?.GetField(blank)?.GetPdfObject().Get(PdfName.V) is PdfDictionary)
                    {
                        place.TotalRevisions = totalRevisions;
                        place.IntegrityOk = false;
                        place.Error = "The field holds a signature dictionary without a usable signature value (damaged or incomplete signature).";
                        result.Signatures.Add(place);
                        continue;
                    }

                    result.UnsignedFields.Add(new UnsignedSignatureField
                    {
                        FieldName = blank, PageNumber = place.PageNumber, Left = place.Left, Bottom = place.Bottom,
                        Width = place.Width, Height = place.Height
                    });
                }
            }
            catch
            {
                // Listing empty fields is extra information only.
            }

            result.CertificationLevel = certification.Level;
            if (certification.FieldName is not null)
            {
                var certifier = result.Signatures.FirstOrDefault(s => s.FieldName == certification.FieldName);
                if (certifier is not null)
                {
                    certifier.CertificationLevel = certification.Level;
                    result.CertifiedBy = certifier.SignerName;
                }
            }

            foreach (var verification in result.Signatures) Decide(verification, result.StructureRepaired);
        }

        Finish(result);
        return result;
    }

    // ---- One signature ---------------------------------------------------------------------------

    /// <summary>Fills the cryptographic and certificate facts. Returns the end of the signed range.</summary>
    private static long ReadSignature(SignatureVerification v, string name, SignatureUtil util, byte[] bytes,
        VerificationOptions options, Certification certification)
    {
        var signature = util.GetSignature(name);
        var dictionary = signature.GetPdfObject();
        v.SubFilter = signature.GetSubFilter()?.GetValue() ?? "";
        v.Reason = signature.GetReason() ?? "";
        v.Location = signature.GetLocation() ?? "";
        v.ContactInfo = dictionary.GetAsString(PdfName.ContactInfo)?.ToUnicodeString() ?? "";
        try { v.Revision = util.GetRevision(name); } catch { /* informational */ }

        // The byte range must be the whole file minus exactly the signature value.
        var rangeProblem = CheckByteRange(signature.GetByteRange(), bytes, out var end);
        if (rangeProblem is not null)
        {
            v.Error = rangeProblem;
            v.IntegrityOk = false;
            return 0;
        }

        try { v.CoversWholeDocument = util.SignatureCoversWholeDocument(name); } catch { v.CoversWholeDocument = false; }

        var pkcs7 = util.ReadSignatureData(name);
        v.IsDocumentTimestamp = pkcs7.IsTsp();
        v.DigestAlgorithm = pkcs7.GetDigestAlgorithmName() ?? "";
        try { v.SignatureAlgorithm = pkcs7.GetSignatureAlgorithmName() ?? ""; } catch { v.SignatureAlgorithm = ""; }

        try
        {
            v.IntegrityOk = pkcs7.VerifySignatureIntegrityAndAuthenticity();
        }
        catch (Exception ex)
        {
            v.IntegrityOk = false;
            v.Error = "The signature value could not be verified: " + ex.Message;
        }

        // Times.
        var claimed = Defined(pkcs7.GetSignDate());
        if (claimed is null && signature.GetDate() is { } m)
        {
            try { claimed = PdfDate.Decode(m.ToUnicodeString()); } catch { /* no usable /M */ }
        }

        v.ClaimedSigningTime = claimed;

        // Certificates.
        var signingCertificate = pkcs7.GetSigningCertificate();
        X509Certificate2? leaf = signingCertificate is null ? null : new X509Certificate2(signingCertificate.GetEncoded());
        var embedded = new List<X509Certificate2>();
        foreach (var certificate in pkcs7.GetCertificates() ?? [])
        {
            try { embedded.Add(new X509Certificate2(certificate.GetEncoded())); } catch { /* skip a broken one */ }
        }

        var signer = pkcs7.GetSignName();
        if (string.IsNullOrWhiteSpace(signer) && leaf is not null) signer = CertificateSummary.From(leaf).CommonName;
        if (string.IsNullOrWhiteSpace(signer)) signer = signature.GetName() ?? "";
        v.SignerName = signer ?? "";

        if (v.IsDocumentTimestamp)
        {
            v.TimestampTime = claimed ?? Defined(pkcs7.GetTimeStampDate());
            v.TimestampAuthority = v.SignerName;
        }
        else
        {
            ReadTimestamp(v, pkcs7, options);
        }

        if (leaf is null)
        {
            v.Error = string.IsNullOrEmpty(v.Error) ? "The signature does not contain the signer's certificate." : v.Error;
            return end;
        }

        v.Certificate = CertificateSummary.From(leaf);
        var at = v.TimestampTrusted && v.TimestampTime is { } ts ? ts : claimed ?? DateTime.Now;
        v.ValidationTime = at;

        CheckCertificate(v, leaf, embedded, options, at);
        _ = certification; // certification facts are attached after all signatures are read
        return end;
    }

    private static void ReadTimestamp(SignatureVerification v, PdfPKCS7 pkcs7, VerificationOptions options)
    {
        var time = Defined(pkcs7.GetTimeStampDate());
        if (time is null) return;

        v.TimestampTime = time;
        var imprint = false;
        try { imprint = pkcs7.VerifyTimestampImprint(); } catch { imprint = false; }

        var tokenOk = false;
        X509Certificate2? tsa = null;
        var tsaCertificates = new List<X509Certificate2>();
        try
        {
            var container = pkcs7.GetTimestampSignatureContainer();
            if (container is not null)
            {
                tokenOk = container.VerifySignatureIntegrityAndAuthenticity();
                var signing = container.GetSigningCertificate();
                if (signing is not null) tsa = new X509Certificate2(signing.GetEncoded());
                foreach (var certificate in container.GetCertificates() ?? [])
                {
                    try { tsaCertificates.Add(new X509Certificate2(certificate.GetEncoded())); } catch { /* skip */ }
                }
            }
        }
        catch
        {
            tokenOk = false;
        }

        v.TimestampVerified = imprint && tokenOk;
        if (tsa is not null)
        {
            v.TimestampAuthority = CertificateSummary.From(tsa).CommonName;
            if (v.TimestampVerified)
            {
                var chain = BuildChain(tsa, tsaCertificates, options.TrustedCertificates, time.Value);
                v.TimestampTrusted = chain.Trusted && chain.Fatal.Count == 0;
            }
        }

        if (!imprint)
            v.Checks.Add(new SignatureCheck { Outcome = CheckOutcome.Fail, Text = "The embedded timestamp does not belong to this signature." });
    }

    private static void CheckCertificate(SignatureVerification v, X509Certificate2 leaf, List<X509Certificate2> embedded,
        VerificationOptions options, DateTime at)
    {
        var chain = BuildChain(leaf, embedded, options.TrustedCertificates, at);
        v.Chain = chain.Elements.Select(CertificateSummary.From).ToList();
        if (v.Chain.Count == 0) v.Chain.Add(CertificateSummary.From(leaf));

        v.IdentityTrusted = chain.Trusted && chain.Fatal.Count == 0 && chain.Problems.Count == 0;
        v.TrustSource = v.IdentityTrusted ? chain.TrustSource : "";

        foreach (var fatal in chain.Fatal) v.Checks.Add(new SignatureCheck { Outcome = CheckOutcome.Fail, Text = fatal });
        foreach (var problem in chain.Problems) v.Checks.Add(new SignatureCheck { Outcome = CheckOutcome.Warning, Text = problem });

        // Key usage: a certificate meant for something else (e.g. only encryption) is not a signing certificate.
        var usage = leaf.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            v.IdentityTrusted = false;
            v.Checks.Add(new SignatureCheck { Outcome = CheckOutcome.Warning, Text = "The certificate is not marked for digital signatures (key usage)." });
        }

        // Revocation is asked once per signer certificate.
        var revocation = Revocation(chain.Elements.Count > 0 ? chain.Elements : [leaf], options);
        v.RevocationText = revocation.Text;
        v.Checks.Add(new SignatureCheck
        {
            Outcome = revocation.State switch
            {
                RevocationState.Good => CheckOutcome.Pass,
                RevocationState.Revoked => CheckOutcome.Fail,
                RevocationState.NotChecked => CheckOutcome.Warning,
                _ => CheckOutcome.Info
            },
            Text = revocation.Text
        });
        if (revocation.State == RevocationState.Revoked) v.Error = string.IsNullOrEmpty(v.Error) ? "The signer's certificate has been revoked." : v.Error;
        if (revocation.State == RevocationState.NotChecked) v.IdentityTrusted = false;

        // An expired certificate: only a trusted timestamp proves the signing happened before expiry.
        if (leaf.NotAfter < DateTime.Now && !v.TimestampTrusted && chain.Fatal.Count == 0)
        {
            v.IdentityTrusted = false;
            v.Checks.Add(new SignatureCheck
            {
                Outcome = CheckOutcome.Warning,
                Text = $"The certificate expired on {leaf.NotAfter:dd MMM yyyy}. Without a trusted timestamp the signing time " +
                       "comes only from the signer's computer, so it cannot be proved the signature was made before expiry."
            });
        }
    }

    // ---- Certificate chain -------------------------------------------------------------------------

    private sealed class ChainResult
    {
        public List<X509Certificate2> Elements { get; } = [];
        public bool Trusted { get; set; }
        public string TrustSource { get; set; } = "";
        public List<string> Fatal { get; } = [];
        public List<string> Problems { get; } = [];
    }

    private static ChainResult BuildChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> embedded,
        IReadOnlyCollection<X509Certificate2> userTrusted, DateTime at)
    {
        var result = new ChainResult();
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.VerificationTime = at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at;
        policy.DisableCertificateDownloads = true;
        foreach (var certificate in embedded) policy.ExtraStore.Add(certificate);
        foreach (var certificate in userTrusted) policy.ExtraStore.Add(certificate);

        try
        {
            chain.Build(leaf);
        }
        catch (Exception ex)
        {
            result.Problems.Add("The certificate chain could not be built: " + ex.Message);
            return result;
        }

        foreach (var element in chain.ChainElements) result.Elements.Add(new X509Certificate2(element.Certificate.RawData));

        const X509ChainStatusFlags trustFlags = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
        const X509ChainStatusFlags ignored = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation |
                                             X509ChainStatusFlags.NoError | X509ChainStatusFlags.NotTimeNested;
        const X509ChainStatusFlags fatalFlags = X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.Revoked |
                                                X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.ExplicitDistrust |
                                                X509ChainStatusFlags.Cyclic | X509ChainStatusFlags.InvalidBasicConstraints;

        var all = X509ChainStatusFlags.NoError;
        foreach (var status in chain.ChainStatus) all |= status.Status;
        for (var i = 0; i < chain.ChainElements.Count; i++)
        {
            var element = chain.ChainElements[i];
            var name = CertificateSummary.From(element.Certificate).CommonName;
            foreach (var status in element.ChainElementStatus)
            {
                if ((status.Status & X509ChainStatusFlags.NotTimeValid) != 0)
                    result.Fatal.Add(i == 0
                        ? $"The signer's certificate was not valid at the signing time ({at:dd MMM yyyy HH:mm}); it is valid " +
                          $"from {element.Certificate.NotBefore:dd MMM yyyy} to {element.Certificate.NotAfter:dd MMM yyyy}."
                        : $"Issuer certificate '{name}' was not valid at the signing time.");
                if ((status.Status & X509ChainStatusFlags.NotSignatureValid) != 0)
                    result.Fatal.Add($"Certificate '{name}' does not carry a valid signature from its issuer (forged or damaged chain).");
                if ((status.Status & X509ChainStatusFlags.ExplicitDistrust) != 0)
                    result.Fatal.Add($"Certificate '{name}' is marked as not trusted in Windows.");
                if ((status.Status & (X509ChainStatusFlags.Cyclic | X509ChainStatusFlags.InvalidBasicConstraints)) != 0)
                    result.Fatal.Add($"Certificate '{name}' is not allowed to issue certificates (invalid chain).");
            }
        }

        var other = all & ~(trustFlags | ignored | fatalFlags);
        if (other != X509ChainStatusFlags.NoError)
            result.Problems.Add("The certificate chain has problems: " + other + ".");

        if ((all & trustFlags) == 0)
        {
            result.Trusted = true;
            result.TrustSource = "Windows certificate store";
            return result;
        }

        var thumbprints = new HashSet<string>(userTrusted.Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);
        var anchor = result.Elements.FirstOrDefault(e => thumbprints.Contains(e.Thumbprint));
        if (anchor is not null)
        {
            result.Trusted = true;
            result.TrustSource = "your trusted certificates (" + CertificateSummary.From(anchor).CommonName + ")";
        }

        return result;
    }

    private static RevocationResult Revocation(IReadOnlyList<X509Certificate2> chainElements, VerificationOptions options)
    {
        // The root is trusted (or not) as a whole; revocation applies to the certificates below it.
        var checkable = chainElements.Count > 1 ? chainElements.Take(chainElements.Count - 1).ToList() : chainElements.ToList();
        if (!checkable.Any(HasRevocationAddress))
            return new RevocationResult(RevocationState.NoAddress,
                "Revocation was not checked: the certificate does not name a revocation service (CRL or OCSP address).");

        if (!options.CheckRevocationOnline)
            return new RevocationResult(RevocationState.Skipped, "Revocation was not checked (online checking is turned off).");

        var key = string.Join("|", chainElements.Select(c => c.Thumbprint));
        if (RevocationCache.TryGetValue(key, out var cached) && DateTime.Now - cached.When < RevocationCacheLifetime)
            return cached.Result;

        RevocationResult result;
        try
        {
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.RevocationMode = X509RevocationMode.Online;
            policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            policy.UrlRetrievalTimeout = options.RevocationTimeout;
            policy.VerificationTime = DateTime.Now;
            foreach (var certificate in chainElements.Skip(1)) policy.ExtraStore.Add(certificate);
            chain.Build(chainElements[0]);

            var all = X509ChainStatusFlags.NoError;
            foreach (var element in chain.ChainElements)
                foreach (var status in element.ChainElementStatus) all |= status.Status;

            if ((all & X509ChainStatusFlags.Revoked) != 0)
                result = new RevocationResult(RevocationState.Revoked,
                    "The certificate has been REVOKED by its certificate authority (checked online now; the revocation date was not compared with the signing time).");
            else if ((all & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
                result = new RevocationResult(RevocationState.NotChecked,
                    "Revocation could not be checked (offline, or the certificate authority did not answer).");
            else
                result = new RevocationResult(RevocationState.Good, "Revocation checked online: the certificate is not revoked.");
        }
        catch (Exception ex)
        {
            result = new RevocationResult(RevocationState.NotChecked, "Revocation could not be checked (" + ex.Message + ").");
        }

        RevocationCache[key] = (DateTime.Now, result);
        return result;
    }

    private static bool HasRevocationAddress(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == "2.5.29.31") return true; // CRL distribution points
            if (extension.Oid?.Value == "1.3.6.1.5.5.7.1.1" && extension.RawData.Length > 0)
            {
                // Authority information access: look for the OCSP method (1.3.6.1.5.5.7.48.1).
                var ocsp = new byte[] { 0x06, 0x08, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x07, 0x30, 0x01 };
                if (extension.RawData.AsSpan().IndexOf(ocsp) >= 0) return true;
            }
        }

        return false;
    }

    // ---- Changes after signing ---------------------------------------------------------------------

    private static void Judge(SignatureVerification v, List<(RevisionChange Change, bool UnderCertification)> changes, int certificationLevel,
        (string Action, HashSet<string> Fields)? fieldLock)
    {
        var notAllowed = new List<string>();
        var unclassified = new List<string>();
        var allowed = new List<string>();

        foreach (var (change, certified) in changes)
        {
            var level = certified ? certificationLevel : 0;
            var text = change.Text;
            switch (change.Kind)
            {
                case RevisionChangeKind.Benign:
                    break;
                case RevisionChangeKind.ValidationData:
                case RevisionChangeKind.TimestampAdded:
                    allowed.Add(text);
                    break;
                case RevisionChangeKind.SignatureAdded:
                    if (level == 1) notAllowed.Add(text + " The certification allows no changes.");
                    else allowed.Add(text);
                    break;
                case RevisionChangeKind.SignatureFieldAdded:
                    if (level is 1 or 2) notAllowed.Add(text + " The certification does not allow adding fields.");
                    else allowed.Add(text);
                    break;
                case RevisionChangeKind.FormFilled:
                    if (level == 1) notAllowed.Add(text + " The certification allows no changes.");
                    else if (IsLocked(fieldLock, change.FieldName)) notAllowed.Add(text + " This signature locked that field.");
                    else allowed.Add(text);
                    break;
                case RevisionChangeKind.CommentChanged:
                    if (level is 1 or 2) notAllowed.Add(text + " The certification does not allow comments.");
                    else allowed.Add(text);
                    break;
                case RevisionChangeKind.NotAllowed:
                    notAllowed.Add(text);
                    break;
                default:
                    unclassified.Add(text);
                    break;
            }
        }

        if (allowed.Count == 0 && notAllowed.Count == 0 && unclassified.Count == 0)
            allowed.Add("Data was added to the end of the file after signing, but no part of the document was changed.");

        v.ChangeDescriptions = notAllowed.Concat(unclassified).Concat(allowed).Distinct().ToList();
        v.Changes = notAllowed.Count > 0 ? ChangesAfterSigning.NotAllowed
            : unclassified.Count > 0 ? ChangesAfterSigning.Unclassified
            : ChangesAfterSigning.AllowedOnly;
    }

    private static bool IsLocked((string Action, HashSet<string> Fields)? fieldLock, string? field)
    {
        if (fieldLock is not { } locked || field is null) return false;
        return locked.Action switch
        {
            "All" => true,
            "Include" => locked.Fields.Contains(field),
            "Exclude" => !locked.Fields.Contains(field),
            _ => false
        };
    }

    // ---- The verdict -----------------------------------------------------------------------------

    private static void Decide(SignatureVerification v, bool structureRepaired)
    {
        var checks = new List<SignatureCheck>();
        var invalid = false;
        var unknown = false;
        string? reason = null;

        void Add(CheckOutcome outcome, string text) => checks.Add(new SignatureCheck { Outcome = outcome, Text = text });

        // 1. The signed bytes.
        if (!string.IsNullOrEmpty(v.Error) && !v.IntegrityOk)
        {
            invalid = true;
            Add(CheckOutcome.Fail, v.Error);
        }
        else if (!v.IntegrityOk)
        {
            invalid = true;
            Add(CheckOutcome.Fail, "The document has been altered or corrupted since this signature was applied (the signed bytes do not match the signature).");
        }
        else
        {
            switch (v.Changes)
            {
                case ChangesAfterSigning.None:
                    Add(CheckOutcome.Pass, "The document has not been modified since this signature was applied.");
                    break;
                case ChangesAfterSigning.AllowedOnly:
                    Add(CheckOutcome.Pass, "The signed version is intact. The document was changed afterwards, but only in ways this signature allows:");
                    foreach (var line in v.ChangeDescriptions) Add(CheckOutcome.Info, "  " + line);
                    break;
                case ChangesAfterSigning.NotAllowed:
                    invalid = true;
                    reason = "The document was changed after signing in a way that is not allowed: " + v.ChangeDescriptions.FirstOrDefault();
                    Add(CheckOutcome.Fail, "The document has been changed after this signature was applied, in ways that are not allowed:");
                    foreach (var line in v.ChangeDescriptions) Add(CheckOutcome.Info, "  " + line);
                    break;
                default:
                    unknown = true;
                    reason = "Changes were made after this signature that could not be classified: " + v.ChangeDescriptions.FirstOrDefault();
                    Add(CheckOutcome.Warning, "Changes were made after this signature was applied that could not be classified as allowed:");
                    foreach (var line in v.ChangeDescriptions) Add(CheckOutcome.Info, "  " + line);
                    break;
            }

            if (!string.IsNullOrEmpty(v.Error))
            {
                invalid = true;
                Add(CheckOutcome.Fail, v.Error);
            }
        }

        if (structureRepaired)
        {
            unknown = true;
            Add(CheckOutcome.Warning, "The file's internal structure is damaged and had to be repaired to be read, so changes cannot be checked reliably.");
        }

        // 2. Algorithms.
        var digest = v.DigestAlgorithm.ToUpperInvariant();
        if (digest.Contains("MD5") || digest.Contains("MD2") || digest.Contains("RIPEMD"))
        {
            invalid = true;
            Add(CheckOutcome.Fail, $"The signature uses the {v.DigestAlgorithm} digest algorithm, which is not accepted for signatures.");
        }
        else if (digest.Contains("SHA1") || digest == "SHA-1")
        {
            Add(CheckOutcome.Warning, "The signature uses SHA-1, which is considered weak. Adobe Acrobat may also warn about it.");
        }

        // 3. Identity.
        if (v.Certificate is null)
        {
            unknown = true;
            if (v.IntegrityOk) Add(CheckOutcome.Warning, "The signer's certificate is missing, so the signer's identity is unknown.");
        }
        else
        {
            var certificateFailures = v.Checks.Where(c => c.Outcome == CheckOutcome.Fail).ToList();
            if (certificateFailures.Count > 0) invalid = true;

            if (v.IdentityTrusted)
                Add(CheckOutcome.Pass, $"The signer's identity is valid: the certificate is trusted through {v.TrustSource}.");
            else if (certificateFailures.Count == 0)
            {
                unknown = true;
                Add(CheckOutcome.Warning, "The signer's identity is unknown: the certificate does not lead to a certificate you trust " +
                                          "(Windows certificate store or your trusted certificates).");
            }

            checks.AddRange(v.Checks);
            Add(CheckOutcome.Info, "Certificate chain: " + string.Join(" -> ", v.Chain.Select(c => c.CommonName)) +
                                   (v.Chain.Count > 0 && !v.Chain[^1].IsSelfSigned ? " (incomplete: the root certificate is not available)" : ""));
        }

        // 4. Time.
        if (v.IsDocumentTimestamp)
        {
            Add(CheckOutcome.Info, "This is a document timestamp from a timestamp authority (RFC 3161), not a personal signature.");
        }
        else if (v.TimestampTime is { } ts)
        {
            if (v.TimestampVerified && v.TimestampTrusted)
                Add(CheckOutcome.Pass, $"The signature is timestamped by {v.TimestampAuthority} (RFC 3161) at {ts:dd MMM yyyy HH:mm:ss}; the timestamp is valid and trusted.");
            else if (v.TimestampVerified)
                Add(CheckOutcome.Warning, $"The signature is timestamped by {v.TimestampAuthority} at {ts:dd MMM yyyy HH:mm:ss}, but the timestamp authority is not trusted, so the time is not relied on.");
            else
            {
                invalid = true;
                Add(CheckOutcome.Fail, "The embedded timestamp could not be verified.");
            }
        }
        else
        {
            Add(CheckOutcome.Info, "The signature is not timestamped. The signing time is from the clock on the signer's computer.");
        }

        v.Checks = checks.GroupBy(c => c.Text).Select(g => g.First()).ToList();
        v.Status = invalid ? SignatureValidity.Invalid : unknown ? SignatureValidity.Unknown : SignatureValidity.Valid;
        v.Summary = v.Status switch
        {
            SignatureValidity.Valid => v.Changes == ChangesAfterSigning.AllowedOnly
                ? "Signature is valid. The document was changed afterwards only in allowed ways."
                : "Signature is valid.",
            SignatureValidity.Invalid => "Signature is INVALID. " + (invalid && reason is not null && v.Changes == ChangesAfterSigning.NotAllowed
                ? reason
                : FirstFailure(checks)),
            _ => "Signature validity is unknown. " + (reason ?? FirstWarning(checks))
        };
    }

    private static string FirstFailure(List<SignatureCheck> checks) =>
        checks.FirstOrDefault(c => c.Outcome == CheckOutcome.Fail)?.Text ?? "";

    private static string FirstWarning(List<SignatureCheck> checks) =>
        checks.FirstOrDefault(c => c.Outcome == CheckOutcome.Warning)?.Text ?? "";

    private static void Finish(DocumentVerification result)
    {
        result.CheckedAt = DateTime.Now;
        if (!string.IsNullOrEmpty(result.DocumentError))
        {
            result.Overall = SignatureValidity.Unknown;
            result.BannerText = "The signatures in this file could not be checked.";
            result.BannerDetail = result.DocumentError;
            return;
        }

        if (result.Signatures.Count == 0)
        {
            result.Overall = null;
            result.BannerText = result.UnsignedFields.Count > 0 ? "This document has signature fields that are not signed yet." : "";
            result.BannerDetail = result.UnsignedFields.Count > 0 ? $"{result.UnsignedFields.Count} empty signature field(s)." : "";
            return;
        }

        result.Overall = result.Signatures.Any(s => s.Status == SignatureValidity.Invalid) ? SignatureValidity.Invalid
            : result.Signatures.Any(s => s.Status == SignatureValidity.Unknown) ? SignatureValidity.Unknown
            : SignatureValidity.Valid;

        result.BannerText = result.Overall switch
        {
            SignatureValidity.Valid => "Signed and all signatures are valid.",
            SignatureValidity.Invalid => "At least one signature has problems.",
            _ => "Signature validity is unknown."
        };

        var parts = new List<string>();
        if (result.CertificationLevel > 0)
            parts.Add($"Certified by {(string.IsNullOrWhiteSpace(result.CertifiedBy) ? "an unknown signer" : result.CertifiedBy)}: " +
                      DocumentVerification.CertificationDescription(result.CertificationLevel) + ".");
        var valid = result.Signatures.Count(s => s.Status == SignatureValidity.Valid);
        var unknown = result.Signatures.Count(s => s.Status == SignatureValidity.Unknown);
        var invalid = result.Signatures.Count(s => s.Status == SignatureValidity.Invalid);
        var counts = $"{result.Signatures.Count} signature(s)";
        var detail = new List<string>();
        if (valid > 0) detail.Add($"{valid} valid");
        if (unknown > 0) detail.Add($"{unknown} unknown");
        if (invalid > 0) detail.Add($"{invalid} with problems");
        parts.Add(counts + ": " + string.Join(", ", detail) + ".");
        if (result.UnsignedFields.Count > 0) parts.Add($"{result.UnsignedFields.Count} signature field(s) not signed yet.");
        if (result.StructureRepaired) parts.Add("The file structure is damaged.");
        result.BannerDetail = string.Join(" ", parts);
    }

    // ---- Plain-text report -------------------------------------------------------------------------

    /// <summary>Everything about one signature as plain text (for "Copy details").</summary>
    public static string Describe(SignatureVerification v)
    {
        var text = new StringBuilder();
        text.AppendLine((v.IsDocumentTimestamp ? "Document timestamp" : "Signature") + $" '{v.FieldName}'");
        text.AppendLine("Status: " + v.Summary);
        text.AppendLine("Signer: " + v.SignerName);
        if (v.ClaimedSigningTime is { } claimed) text.AppendLine($"Signing time (signer's computer): {claimed:dd MMM yyyy HH:mm:ss zzz}");
        if (v.TimestampTime is { } ts) text.AppendLine($"Timestamp: {ts:dd MMM yyyy HH:mm:ss zzz} by {v.TimestampAuthority}" +
                                                         (v.TimestampVerified ? (v.TimestampTrusted ? " (verified, trusted)" : " (verified, authority not trusted)") : " (not verified)"));
        if (!string.IsNullOrWhiteSpace(v.Reason)) text.AppendLine("Reason: " + v.Reason);
        if (!string.IsNullOrWhiteSpace(v.Location)) text.AppendLine("Location: " + v.Location);
        if (!string.IsNullOrWhiteSpace(v.ContactInfo)) text.AppendLine("Contact: " + v.ContactInfo);
        text.AppendLine($"Type: {v.SubFilter}, digest {v.DigestAlgorithm}" + (string.IsNullOrEmpty(v.SignatureAlgorithm) ? "" : $", {v.SignatureAlgorithm}"));
        text.AppendLine($"Revision {v.Revision} of {v.TotalRevisions}; covers the whole document: {(v.CoversWholeDocument ? "yes" : "no")}");
        text.AppendLine(v.PageNumber > 0 ? $"Page {v.PageNumber}, at ({v.Left:0}, {v.Bottom:0}) size {v.Width:0} x {v.Height:0} pt" : "Invisible signature (not on a page)");
        if (v.CertificationLevel > 0) text.AppendLine("Certification signature: " + DocumentVerification.CertificationDescription(v.CertificationLevel));
        if (v.Certificate is { } c)
        {
            text.AppendLine("Certificate subject: " + c.Subject);
            text.AppendLine("Certificate issuer: " + c.Issuer);
            text.AppendLine("Serial number: " + c.SerialNumber);
            text.AppendLine($"Valid from {c.NotBefore:dd MMM yyyy HH:mm} to {c.NotAfter:dd MMM yyyy HH:mm}");
            text.AppendLine("SHA-1 thumbprint: " + c.Thumbprint);
        }

        text.AppendLine("Checks:");
        foreach (var check in v.Checks) text.AppendLine($"  [{check.Outcome}] {check.Text}");
        return text.ToString();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static string? CheckByteRange(PdfArray? range, byte[] bytes, out long end)
    {
        end = 0;
        if (range is null || range.Size() != 4) return "The signature's byte range is missing or malformed.";
        var values = new long[4];
        for (var i = 0; i < 4; i++)
        {
            if (range.GetAsNumber(i) is not { } number) return "The signature's byte range is malformed.";
            values[i] = number.LongValue();
        }

        long a = values[0], b = values[1], c = values[2], d = values[3];
        if (a != 0 || b <= 0 || c <= b || d < 0 || c + d > bytes.LongLength)
            return "The signature's byte range does not cover the file correctly (it may have been manipulated).";

        // The gap must hold exactly the hex signature value: "<...>".
        if (bytes[b] != (byte)'<' || bytes[c - 1] != (byte)'>')
            return "The signature's byte range leaves out more than the signature value (it may have been manipulated).";
        for (var i = b + 1; i < c - 1; i++)
        {
            var ch = bytes[i];
            var hex = ch is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
            if (!hex && ch is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
                return "The signature's byte range leaves out more than the signature value (it may have been manipulated).";
        }

        end = c + d;
        return null;
    }

    private static DateTime? Defined(DateTime value)
    {
        if (value == TimestampConstants.UNDEFINED_TIMESTAMP_DATE || value == DateTime.MinValue || value == DateTime.MaxValue || value.Year < 1950)
            return null;
        return value;
    }

    private sealed record Certification(string? FieldName, int Level);

    private static Certification ReadCertification(PdfDocument document)
    {
        try
        {
            var perms = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
            var docMdp = perms?.Get(PdfName.DocMDP, false);
            if (docMdp is null) return new Certification(null, 0);
            var number = (docMdp as PdfIndirectReference)?.GetObjNumber() ?? docMdp.GetIndirectReference()?.GetObjNumber();
            var dictionary = perms!.GetAsDictionary(PdfName.DocMDP);
            var level = 2;
            if (dictionary?.GetAsArray(PdfName.Reference) is { } references)
            {
                for (var i = 0; i < references.Size(); i++)
                {
                    var reference = references.GetAsDictionary(i);
                    if (!PdfName.DocMDP.Equals(reference?.GetAsName(PdfName.TransformMethod))) continue;
                    var p = reference!.GetAsDictionary(PdfName.TransformParams)?.GetAsNumber(PdfName.P)?.IntValue();
                    if (p is >= 1 and <= 3) level = p.Value;
                }
            }

            string? field = null;
            var form = SafeForm(document);
            if (form is not null && number is not null)
            {
                foreach (var (name, candidate) in form.GetAllFormFields())
                {
                    if (candidate.GetPdfObject().Get(PdfName.V, false) is PdfIndirectReference value && value.GetObjNumber() == number)
                    {
                        field = name;
                        break;
                    }
                }
            }

            return new Certification(field, level);
        }
        catch
        {
            return new Certification(null, 0);
        }
    }

    private static (string Action, HashSet<string> Fields)? ReadFieldLock(SignatureUtil util, string name)
    {
        try
        {
            var dictionary = util.GetSignatureDictionary(name);
            if (dictionary?.GetAsArray(PdfName.Reference) is not { } references) return null;
            for (var i = 0; i < references.Size(); i++)
            {
                var reference = references.GetAsDictionary(i);
                if (!PdfName.FieldMDP.Equals(reference?.GetAsName(PdfName.TransformMethod))) continue;
                var parameters = reference!.GetAsDictionary(PdfName.TransformParams);
                var action = parameters?.GetAsName(PdfName.Action)?.GetValue() ?? "";
                var fields = new HashSet<string>();
                if (parameters?.GetAsArray(PdfName.Fields) is { } list)
                    for (var j = 0; j < list.Size(); j++)
                        if (list.GetAsString(j) is { } field) fields.Add(field.ToUnicodeString());
                return (action, fields);
            }
        }
        catch
        {
            // No lock information.
        }

        return null;
    }

    private static PdfAcroForm? SafeForm(PdfDocument document)
    {
        try { return PdfFormCreator.GetAcroForm(document, false); } catch { return null; }
    }

    private static Dictionary<int, int> WidgetPlaces(PdfDocument document)
    {
        var map = new Dictionary<int, int>();
        try
        {
            for (var page = 1; page <= document.GetNumberOfPages(); page++)
            {
                var annots = document.GetPage(page).GetPdfObject().GetAsArray(PdfName.Annots);
                if (annots is null) continue;
                for (var i = 0; i < annots.Size(); i++)
                    if (annots.Get(i, false) is PdfIndirectReference reference) map.TryAdd(reference.GetObjNumber(), page);
            }
        }
        catch
        {
            // Positions are extra information.
        }

        return map;
    }

    private static void Place(SignatureVerification v, string name, PdfAcroForm? form, Dictionary<int, int> widgets)
    {
        try
        {
            var widget = form?.GetField(name)?.GetWidgets().FirstOrDefault();
            if (widget is null) return;
            var rect = widget.GetRectangle()?.ToRectangle();
            var number = widget.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? 0;
            if (widgets.TryGetValue(number, out var page)) v.PageNumber = page;
            if (rect is null) return;
            v.Left = rect.GetLeft();
            v.Bottom = rect.GetBottom();
            v.Width = rect.GetWidth();
            v.Height = rect.GetHeight();
        }
        catch
        {
            // Position is extra information.
        }
    }
}
