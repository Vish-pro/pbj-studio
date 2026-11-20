using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Signatures;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// One digital signature. IntegrityOk means the signed bytes are unchanged since signing (it says
/// nothing about whether the certificate is trusted). PageNumber is 0 for an invisible signature
/// whose widget is not on a page.
/// </summary>
public sealed class SignatureInfo
{
    public string FieldName { get; init; } = "";
    public string SignerName { get; init; } = "";
    public DateTime? SignDate { get; init; }
    public string Reason { get; init; } = "";
    public string Location { get; init; } = "";
    public bool IntegrityOk { get; init; }
    public bool CoversWholeDocument { get; init; }
    public int Revision { get; init; }
    public int PageNumber { get; init; }
    public float Left { get; init; }
    public float Bottom { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>Lists the digital signatures in a PDF and checks each one's integrity.</summary>
public static class SignatureInspector
{
    /// <summary>
    /// True when the PDF carries at least one applied digital signature. Cheap: no integrity check.
    /// Any change made by rewriting such a file (every edit tool does) invalidates those signatures.
    /// </summary>
    public static bool HasSignatures(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        return new SignatureUtil(document).GetSignatureNames().Count > 0;
    }

    public static List<SignatureInfo> Read(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var util = new SignatureUtil(document);
        var form = PdfFormCreator.GetAcroForm(document, false);
        var widgetPages = MapWidgetsToPages(document);
        var result = new List<SignatureInfo>();

        foreach (var name in util.GetSignatureNames())
        {
            var page = 0;
            iText.Kernel.Geom.Rectangle? rect = null;
            var widget = form?.GetField(name)?.GetWidgets().FirstOrDefault();
            if (widget is not null)
            {
                rect = widget.GetRectangle()?.ToRectangle();
                var reference = widget.GetPdfObject().GetIndirectReference();
                if (reference is not null) widgetPages.TryGetValue(reference, out page);
            }

            string signer = "", reason = "", location = "", error = "";
            DateTime? date = null;
            var intact = false;
            var covers = false;

            try
            {
                var dictionary = util.GetSignature(name);
                reason = dictionary?.GetReason() ?? "";
                location = dictionary?.GetLocation() ?? "";
                covers = util.SignatureCoversWholeDocument(name);

                var pkcs7 = util.ReadSignatureData(name);
                signer = pkcs7.GetSignName() ?? "";
                if (string.IsNullOrEmpty(signer))
                    signer = CertificateInfo.GetSubjectFields(pkcs7.GetSigningCertificate())?.GetField("CN") ?? "";
                var signDate = pkcs7.GetSignDate();
                if (signDate != DateTime.MinValue && signDate.Year > 1) date = signDate;
                intact = pkcs7.VerifySignatureIntegrityAndAuthenticity();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            result.Add(new SignatureInfo
            {
                FieldName = name,
                SignerName = signer,
                SignDate = date,
                Reason = reason,
                Location = location,
                IntegrityOk = intact,
                CoversWholeDocument = covers,
                Revision = util.GetRevision(name),
                PageNumber = page,
                Left = rect?.GetLeft() ?? 0,
                Bottom = rect?.GetBottom() ?? 0,
                Width = rect?.GetWidth() ?? 0,
                Height = rect?.GetHeight() ?? 0,
                Error = error
            });
        }

        return result;
    }

    private static Dictionary<PdfIndirectReference, int> MapWidgetsToPages(PdfDocument document)
    {
        var map = new Dictionary<PdfIndirectReference, int>();
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            foreach (var annotation in document.GetPage(pageNumber).GetAnnotations())
            {
                if (annotation is not PdfWidgetAnnotation) continue;
                var reference = annotation.GetPdfObject().GetIndirectReference();
                if (reference is not null) map[reference] = pageNumber;
            }
        }
        return map;
    }
}
