using System.Reflection;
using iText.IO.Source;
using iText.Kernel.Pdf;

namespace KcsPdfSigner.Core.Documents;

/// <summary>What one change made after a signature was.</summary>
internal enum RevisionChangeKind
{
    /// <summary>A signature was applied (a new signature dictionary).</summary>
    SignatureAdded,

    /// <summary>A document timestamp (ETSI.RFC3161) was added.</summary>
    TimestampAdded,

    /// <summary>An empty signature field was added.</summary>
    SignatureFieldAdded,

    /// <summary>A form field value (and its appearance) changed.</summary>
    FormFilled,

    /// <summary>A comment (annotation other than a form field) was added, changed or removed.</summary>
    CommentChanged,

    /// <summary>Validation data (DSS: certificates, CRLs, OCSP responses) was added.</summary>
    ValidationData,

    /// <summary>A change that never matters (file information, structure objects).</summary>
    Benign,

    /// <summary>A change that is never allowed after signing (page content, pages, signed fields).</summary>
    NotAllowed,

    /// <summary>A change that could not be classified.</summary>
    Unclassified
}

internal sealed record RevisionChange(RevisionChangeKind Kind, string Text, string? FieldName = null);

/// <summary>
/// Classifies the incremental updates made after each signature, the way Acrobat separates
/// "later signatures, form fill-in and comments" from changes to the signed content.
/// <para>
/// Revisions are compared by where each object is stored in the file: an incremental update
/// appends new copies of the objects it changes, so an object whose xref entry points to the
/// same place in both revisions is byte-for-byte the signed one. Only the objects that moved are
/// loaded and classified, which keeps a 100-signature file fast.
/// </para>
/// </summary>
internal static class SignatureRevisionAnalyzer
{
    private static readonly MethodInfo? GetXrefMethod =
        typeof(PdfDocument).GetMethod("GetXref", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly PdfName DocTimeStamp = new("DocTimeStamp");
    private static readonly PdfName ObjStm = new("ObjStm");
    private static readonly PdfName XRef = new("XRef");
    private static readonly PdfName EtsiRfc3161 = new("ETSI.RFC3161");

    /// <summary>
    /// For each boundary (ascending end offsets of signed revisions) returns the changes made
    /// between that boundary and the next one (or the end of the file for the last).
    /// </summary>
    public static List<List<RevisionChange>> AnalyzeSteps(byte[] file, string? password, IReadOnlyList<long> boundaries,
        CancellationToken cancellation)
    {
        var steps = new List<List<RevisionChange>>();
        if (boundaries.Count == 0) return steps;

        PdfDocument? previous = null;
        Dictionary<int, int>? contentSet = null;

        try
        {
            for (var i = 0; i < boundaries.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var start = boundaries[i];
                var end = i + 1 < boundaries.Count ? boundaries[i + 1] : file.LongLength;
                if (end <= start)
                {
                    steps.Add([]);
                    continue;
                }

                try
                {
                    previous ??= Open(file, start, password);
                    var next = Open(file, end, password);
                    List<RevisionChange> changes;
                    try
                    {
                        contentSet ??= ContentObjects(previous);
                        changes = Diff(previous, next, contentSet);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        changes = [new RevisionChange(RevisionChangeKind.Unclassified,
                            "The changes made after this point could not be analysed (" + ex.Message + ").")];
                    }

                    // A clean step leaves every page's content objects where they were, so the
                    // set stays valid; otherwise rebuild it from the newer revision.
                    if (changes.Any(c => c.Kind is RevisionChangeKind.NotAllowed or RevisionChangeKind.Unclassified))
                        contentSet = null;

                    previous.Close();
                    previous = next;
                    steps.Add(changes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try { previous?.Close(); } catch { /* reading failed already */ }
                    previous = null;
                    contentSet = null;
                    steps.Add([new RevisionChange(RevisionChangeKind.Unclassified,
                        "A later revision of the file could not be read (" + ex.Message + ").")]);
                }
            }
        }
        finally
        {
            try { previous?.Close(); } catch { /* ignore */ }
        }

        return steps;
    }

    private static PdfDocument Open(byte[] file, long length, string? password)
    {
        var source = new WindowRandomAccessSource(new RandomAccessSourceFactory().CreateSource(file), 0, length);
        var reader = new PdfReader(source, PdfIo.ReaderProperties(password));
        reader.SetUnethicalReading(true);
        return new PdfDocument(reader);
    }

    // ---- Object locations ---------------------------------------------------------------------

    private sealed class XrefView
    {
        private readonly PdfDocument _document;
        private readonly PdfXrefTable? _table;

        public XrefView(PdfDocument document)
        {
            _document = document;
            _table = GetXrefMethod?.Invoke(document, null) as PdfXrefTable;
            Size = _table?.Size() ?? document.GetNumberOfPdfObjects();
        }

        public int Size { get; }

        public PdfIndirectReference? Get(int number)
        {
            if (number <= 0 || number >= Size) return null;
            if (_table is not null) return _table.Get(number);
            return _document.GetPdfObject(number)?.GetIndirectReference();
        }
    }

    private static bool SameLocation(PdfIndirectReference a, PdfIndirectReference b, XrefView xa, XrefView xb, int depth = 0)
    {
        if (a.GetGenNumber() != b.GetGenNumber()) return false;
        if (a.GetObjStreamNumber() != b.GetObjStreamNumber()) return false;
        if (a.GetObjStreamNumber() == 0) return a.GetOffset() == b.GetOffset();
        if (a.GetIndex() != b.GetIndex() || depth > 2) return false;

        var streamA = xa.Get(a.GetObjStreamNumber());
        var streamB = xb.Get(b.GetObjStreamNumber());
        return streamA is not null && streamB is not null && SameLocation(streamA, streamB, xa, xb, depth + 1);
    }

    // ---- The diff -----------------------------------------------------------------------------

    private sealed class Structure
    {
        public int Catalog;
        public int AcroForm;
        public readonly List<int> PageObjects = [];
        public readonly HashSet<int> PageTreeNodes = [];
        public readonly Dictionary<int, int> AnnotationPage = [];
        public readonly Dictionary<int, int> AnnotsArrayPage = [];
        public readonly Dictionary<int, PdfDictionary> Fields = [];
        public readonly Dictionary<int, int> FieldValueObjects = [];
        public readonly Dictionary<int, int> AppearanceOwner = [];
        public readonly HashSet<int> DssObjects = [];
        public readonly HashSet<int> InfoObjects = [];
        public readonly List<string> DirectAnnotations = [];
    }

    private static List<RevisionChange> Diff(PdfDocument before, PdfDocument after, Dictionary<int, int> contentBefore)
    {
        var changes = new List<RevisionChange>();
        var xa = new XrefView(before);
        var xb = new XrefView(after);
        var a = ReadStructure(before);
        var b = ReadStructure(after);

        // Pages: count, order and identity.
        if (before.GetNumberOfPages() != after.GetNumberOfPages())
            changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed,
                $"Pages were added or removed ({before.GetNumberOfPages()} before, {after.GetNumberOfPages()} after)."));
        else if (!a.PageObjects.SequenceEqual(b.PageObjects))
            changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, "Pages were replaced or put in a different order."));

        if (!a.DirectAnnotations.SequenceEqual(b.DirectAnnotations))
            changes.Add(new RevisionChange(RevisionChangeKind.Unclassified, "Annotations stored inside a page were changed."));

        var changed = new List<(int Number, bool IsNew)>();
        for (var number = 1; number < xb.Size; number++)
        {
            var refB = xb.Get(number);
            if (refB is null || refB.IsFree()) continue;
            var refA = xa.Get(number);
            if (refA is null || refA.IsFree()) changed.Add((number, true));
            else if (!SameLocation(refA, refB, xa, xb)) changed.Add((number, false));
        }

        // Objects that existed and are now gone: harmless unless a page drew them.
        for (var number = 1; number < xa.Size; number++)
        {
            var refA = xa.Get(number);
            if (refA is null || refA.IsFree()) continue;
            var refB = xb.Get(number);
            if ((refB is null || refB.IsFree()) && contentBefore.TryGetValue(number, out var page))
                changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"Content of page {page} was removed."));
        }

        // Annotations removed from pages.
        foreach (var (number, page) in a.AnnotationPage)
        {
            if (b.AnnotationPage.ContainsKey(number)) continue;
            var annotation = SafeObject(xa.Get(number)) as PdfDictionary;
            if (PdfName.Widget.Equals(annotation?.GetAsName(PdfName.Subtype)))
                changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"A form field was removed from page {page}."));
            else
                changes.Add(new RevisionChange(RevisionChangeKind.CommentChanged, $"A comment was removed from page {page}."));
        }

        // Fields removed from the form.
        foreach (var (number, field) in a.Fields)
        {
            if (!b.Fields.ContainsKey(number))
                changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"Form field '{FullName(field)}' was removed."));
        }

        var roots = new List<int>();
        var unexplained = new List<(int Number, bool IsNew)>();

        foreach (var (number, isNew) in changed)
        {
            var objectB = SafeObject(xb.Get(number));
            var objectA = isNew ? null : SafeObject(xa.Get(number));

            if (objectB is PdfStream { } stream &&
                (ObjStm.Equals(stream.GetAsName(PdfName.Type)) || XRef.Equals(stream.GetAsName(PdfName.Type))))
                continue;

            if (!isNew && contentBefore.TryGetValue(number, out var contentPage))
            {
                changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"The content of page {contentPage} was changed."));
                continue;
            }

            if (number == b.Catalog)
            {
                CompareCatalog(objectA as PdfDictionary, objectB as PdfDictionary, changes);
                continue;
            }

            var pageIndex = b.PageObjects.IndexOf(number);
            if (pageIndex >= 0 || a.PageObjects.Contains(number))
            {
                if (objectA is PdfDictionary pageA && objectB is PdfDictionary pageB &&
                    !SameDictionary(pageA, pageB, PdfName.Annots))
                    changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"Page {pageIndex + 1} was changed."));
                continue;
            }

            if (b.PageTreeNodes.Contains(number) || a.PageTreeNodes.Contains(number))
            {
                if (objectA is not PdfDictionary nodeA || objectB is not PdfDictionary nodeB || !SameDictionary(nodeA, nodeB))
                    changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, "The page structure was changed."));
                continue;
            }

            if (number == b.AcroForm && b.AcroForm != 0)
            {
                CompareAcroForm(objectA as PdfDictionary, objectB as PdfDictionary, changes);
                continue;
            }

            if (b.AnnotsArrayPage.ContainsKey(number)) continue; // the annotations in it are classified one by one

            if (b.DssObjects.Contains(number))
            {
                changes.Add(new RevisionChange(RevisionChangeKind.ValidationData,
                    "Validation data (certificates and revocation information) was added."));
                continue;
            }

            if (b.InfoObjects.Contains(number)) continue; // document information and XMP metadata

            if (objectB is PdfDictionary dictionary && IsSignatureDictionary(dictionary))
            {
                if (isNew)
                {
                    var timestamp = DocTimeStamp.Equals(dictionary.GetAsName(PdfName.Type)) ||
                                    EtsiRfc3161.Equals(dictionary.GetAsName(PdfName.SubFilter));
                    var owner = b.Fields.FirstOrDefault(f => RefersTo(f.Value.Get(PdfName.V, false), number)).Value;
                    var name = owner is null ? "" : $" (field '{FullName(owner)}')";
                    changes.Add(timestamp
                        ? new RevisionChange(RevisionChangeKind.TimestampAdded, "A document timestamp was added" + name + ".")
                        : new RevisionChange(RevisionChangeKind.SignatureAdded, "Signed by a later signature" + name + "."));
                    roots.Add(number);
                }
                else
                {
                    changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, "An existing signature was altered."));
                }

                continue;
            }

            var isField = b.Fields.ContainsKey(number) || a.Fields.ContainsKey(number);
            var isAnnotation = b.AnnotationPage.ContainsKey(number) || a.AnnotationPage.ContainsKey(number);
            if (isField || (isAnnotation && PdfName.Widget.Equals((objectB as PdfDictionary)?.GetAsName(PdfName.Subtype))))
            {
                var change = ClassifyField(number, objectA as PdfDictionary, objectB as PdfDictionary, b);
                changes.Add(change);
                if (change.Kind != RevisionChangeKind.NotAllowed && change.Kind != RevisionChangeKind.Unclassified) roots.Add(number);
                continue;
            }

            if (isAnnotation)
            {
                var page = b.AnnotationPage.TryGetValue(number, out var p) ? p : a.AnnotationPage.GetValueOrDefault(number);
                var subtype = (objectB as PdfDictionary)?.GetAsName(PdfName.Subtype);
                if (PdfName.Link.Equals(subtype))
                {
                    changes.Add(new RevisionChange(RevisionChangeKind.Unclassified, $"A link was added or changed on page {page}."));
                }
                else
                {
                    changes.Add(new RevisionChange(RevisionChangeKind.CommentChanged,
                        $"A comment was {(isNew ? "added" : "changed")} on page {page}."));
                    roots.Add(number);
                }

                continue;
            }

            if (b.FieldValueObjects.TryGetValue(number, out var valueOwner) && b.Fields.TryGetValue(valueOwner, out var valueField))
            {
                if (IsSignatureField(valueField))
                    changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed, $"The value of signature field '{FullName(valueField)}' was altered."));
                else
                    changes.Add(new RevisionChange(RevisionChangeKind.FormFilled, $"Form field '{FullName(valueField)}' was filled in.", FullName(valueField)));
                continue;
            }

            unexplained.Add((number, isNew));
        }

        if (unexplained.Count > 0)
        {
            var reach = Reachable(after, roots);
            foreach (var (number, isNew) in unexplained)
            {
                if (reach.Contains(number) || isNew) continue; // part of an allowed change, or an unused new object

                if (b.AppearanceOwner.TryGetValue(number, out var owner))
                {
                    var ownerObject = SafeObject(xb.Get(owner)) as PdfDictionary;
                    var field = ownerObject is null ? null : FieldOf(ownerObject);
                    if (field is not null && IsSignatureField(field) && field.Get(PdfName.V) is not null)
                        changes.Add(new RevisionChange(RevisionChangeKind.NotAllowed,
                            $"The appearance of signed field '{FullName(field)}' was changed."));
                    else if (field is not null)
                        changes.Add(new RevisionChange(RevisionChangeKind.FormFilled,
                            $"The appearance of form field '{FullName(field)}' was updated.", FullName(field)));
                    else
                        changes.Add(new RevisionChange(RevisionChangeKind.CommentChanged, "The appearance of a comment was changed."));
                    continue;
                }

                changes.Add(new RevisionChange(RevisionChangeKind.Unclassified,
                    $"A part of the file that is not page content was changed (object {number})."));
            }
        }

        return changes;
    }

    private static RevisionChange ClassifyField(int number, PdfDictionary? before, PdfDictionary? after, Structure structure)
    {
        if (after is null) return new RevisionChange(RevisionChangeKind.Unclassified, "A form field could not be read.");

        var field = FieldOf(after);
        var name = FullName(field);
        var signature = IsSignatureField(field);

        if (before is null)
        {
            if (signature)
            {
                return field.Get(PdfName.V) is not null
                    ? new RevisionChange(RevisionChangeKind.Benign, $"Signature field '{name}' was added and signed.")
                    : new RevisionChange(RevisionChangeKind.SignatureFieldAdded, $"An empty signature field '{name}' was added.");
            }

            return new RevisionChange(RevisionChangeKind.NotAllowed, $"Form field '{name}' was added.");
        }

        var fieldBefore = FieldOf(before);
        if (signature)
        {
            var valueBefore = fieldBefore.Get(PdfName.V);
            var valueAfter = field.Get(PdfName.V);
            if (valueBefore is null && valueAfter is not null)
                return new RevisionChange(RevisionChangeKind.Benign, $"Signature field '{name}' was signed.");
            if (valueBefore is not null)
                return new RevisionChange(RevisionChangeKind.NotAllowed, $"Signed field '{name}' was changed.");
            return SameDictionary(before, after, PdfName.AP, PdfName.MK, PdfName.F, PdfName.DA, PdfName.AS)
                ? new RevisionChange(RevisionChangeKind.Benign, $"The look of empty signature field '{name}' was changed.")
                : new RevisionChange(RevisionChangeKind.Unclassified, $"Empty signature field '{name}' was changed.");
        }

        if (!SameDictionary(before, after, PdfName.V, PdfName.AP, PdfName.AS, PdfName.RV))
            return new RevisionChange(RevisionChangeKind.NotAllowed, $"The properties of form field '{name}' were changed.");

        return new RevisionChange(RevisionChangeKind.FormFilled, $"Form field '{name}' was filled in.", name);
    }

    private static void CompareCatalog(PdfDictionary? before, PdfDictionary? after, List<RevisionChange> changes)
    {
        if (before is null || after is null)
        {
            changes.Add(new RevisionChange(RevisionChangeKind.Unclassified, "The document catalog could not be compared."));
            return;
        }

        PdfName[] harmless = [PdfName.AcroForm, PdfName.DSS, PdfName.Extensions, PdfName.Metadata, PdfName.Version];
        PdfName[] forbidden = [PdfName.Pages, PdfName.OpenAction, PdfName.AA, PdfName.Names, PdfName.OCProperties, PdfName.Perms, PdfName.Collection];

        // A form dictionary stored inside the catalog is compared here; an indirect one on its own.
        if (before.Get(PdfName.AcroForm, false) is PdfDictionary formBefore && after.Get(PdfName.AcroForm, false) is PdfDictionary formAfter)
            CompareAcroForm(formBefore, formAfter, changes);

        foreach (var key in before.KeySet().Union(after.KeySet()).Distinct())
        {
            if (harmless.Contains(key)) continue;
            if (SameValue(before.Get(key, false), after.Get(key, false))) continue;

            changes.Add(forbidden.Contains(key)
                ? new RevisionChange(RevisionChangeKind.NotAllowed, $"The document's {Describe(key)} was changed.")
                : new RevisionChange(RevisionChangeKind.Unclassified, $"The document's {Describe(key)} was changed."));
        }
    }

    private static string Describe(PdfName key)
    {
        if (PdfName.OpenAction.Equals(key) || PdfName.AA.Equals(key)) return "automatic actions (scripts run on opening)";
        if (PdfName.Names.Equals(key)) return "name tree (scripts, attachments or destinations)";
        if (PdfName.OCProperties.Equals(key)) return "layer visibility";
        if (PdfName.Pages.Equals(key)) return "page tree";
        if (PdfName.Perms.Equals(key)) return "certification permissions";
        if (PdfName.Outlines.Equals(key)) return "bookmarks";
        return "/" + key.GetValue() + " entry";
    }

    private static void CompareAcroForm(PdfDictionary? before, PdfDictionary? after, List<RevisionChange> changes)
    {
        if (before is null || after is null) return; // a new form dictionary: its fields are classified one by one

        PdfName[] harmless = [PdfName.Fields, PdfName.SigFlags, PdfName.DR, PdfName.DA, PdfName.NeedAppearances];
        foreach (var key in before.KeySet().Union(after.KeySet()).Distinct())
        {
            if (harmless.Contains(key) || SameValue(before.Get(key, false), after.Get(key, false))) continue;
            changes.Add(PdfName.XFA.Equals(key)
                ? new RevisionChange(RevisionChangeKind.NotAllowed, "The XFA form data was changed.")
                : new RevisionChange(RevisionChangeKind.Unclassified, $"The form's /{key.GetValue()} entry was changed."));
        }
    }

    // ---- Structure of one revision -------------------------------------------------------------

    private static Structure ReadStructure(PdfDocument document)
    {
        var structure = new Structure();
        var catalog = document.GetCatalog().GetPdfObject();
        structure.Catalog = catalog.GetIndirectReference()?.GetObjNumber() ?? 0;

        var info = document.GetTrailer().Get(PdfName.Info, false);
        if (info is PdfIndirectReference infoRef) structure.InfoObjects.Add(infoRef.GetObjNumber());
        if (catalog.Get(PdfName.Metadata, false) is PdfIndirectReference metadataRef) structure.InfoObjects.Add(metadataRef.GetObjNumber());

        // Page tree nodes and pages.
        var pagesRoot = catalog.Get(PdfName.Pages, false);
        WalkPageTree(pagesRoot, structure, new HashSet<int>(), 0);
        for (var page = 1; page <= document.GetNumberOfPages(); page++)
        {
            var dictionary = document.GetPage(page).GetPdfObject();
            structure.PageObjects.Add(dictionary.GetIndirectReference()?.GetObjNumber() ?? -page);

            var annotsValue = dictionary.Get(PdfName.Annots, false);
            if (annotsValue is PdfIndirectReference annotsRef) structure.AnnotsArrayPage[annotsRef.GetObjNumber()] = page;
            if (dictionary.GetAsArray(PdfName.Annots) is not { } annots) continue;

            for (var i = 0; i < annots.Size(); i++)
            {
                var entry = annots.Get(i, false);
                if (entry is PdfIndirectReference annotationRef)
                {
                    structure.AnnotationPage.TryAdd(annotationRef.GetObjNumber(), page);
                    if (annotationRef.GetRefersTo() is PdfDictionary annotation) NoteAppearance(annotation, annotationRef.GetObjNumber(), structure);
                }
                else if (entry is PdfDictionary direct)
                {
                    structure.DirectAnnotations.Add(page + ":" + direct.GetAsName(PdfName.Subtype)?.GetValue() + ":" + direct.Size());
                }
            }
        }

        // Form fields.
        var acroFormValue = catalog.Get(PdfName.AcroForm, false);
        if (acroFormValue is PdfIndirectReference acroRef) structure.AcroForm = acroRef.GetObjNumber();
        if (catalog.GetAsDictionary(PdfName.AcroForm)?.GetAsArray(PdfName.Fields) is { } fields)
            WalkFields(fields, structure, new HashSet<int>(), 0);

        // Validation data.
        if (catalog.Get(PdfName.DSS, false) is { } dss)
        {
            if (dss is PdfIndirectReference dssRef) structure.DssObjects.Add(dssRef.GetObjNumber());
            Collect(dss, structure.DssObjects, 0);
        }

        return structure;
    }

    private static void WalkPageTree(PdfObject? node, Structure structure, HashSet<int> seen, int depth)
    {
        if (node is not PdfIndirectReference reference || depth > 64 || !seen.Add(reference.GetObjNumber())) return;
        if (reference.GetRefersTo() is not PdfDictionary dictionary) return;
        if (!PdfName.Pages.Equals(dictionary.GetAsName(PdfName.Type)) && dictionary.Get(PdfName.Kids) is null) return;

        structure.PageTreeNodes.Add(reference.GetObjNumber());
        var kids = dictionary.Get(PdfName.Kids, false);
        if (kids is PdfIndirectReference kidsRef) structure.PageTreeNodes.Add(kidsRef.GetObjNumber());
        if (dictionary.GetAsArray(PdfName.Kids) is not { } array) return;
        for (var i = 0; i < array.Size(); i++) WalkPageTree(array.Get(i, false), structure, seen, depth + 1);
    }

    private static void WalkFields(PdfArray fields, Structure structure, HashSet<int> seen, int depth)
    {
        if (depth > 32) return;
        for (var i = 0; i < fields.Size(); i++)
        {
            if (fields.Get(i, false) is not PdfIndirectReference reference || !seen.Add(reference.GetObjNumber())) continue;
            if (reference.GetRefersTo() is not PdfDictionary field) continue;

            structure.Fields[reference.GetObjNumber()] = field;
            if (field.Get(PdfName.V, false) is PdfIndirectReference valueRef)
                structure.FieldValueObjects[valueRef.GetObjNumber()] = reference.GetObjNumber();
            NoteAppearance(field, reference.GetObjNumber(), structure);
            if (field.GetAsArray(PdfName.Kids) is { } kids) WalkFields(kids, structure, seen, depth + 1);
        }
    }

    private static void NoteAppearance(PdfDictionary annotation, int owner, Structure structure)
    {
        if (annotation.Get(PdfName.AP, false) is PdfIndirectReference apRef) structure.AppearanceOwner.TryAdd(apRef.GetObjNumber(), owner);
        if (annotation.GetAsDictionary(PdfName.AP) is not { } appearance) return;
        foreach (var key in appearance.KeySet())
        {
            var value = appearance.Get(key, false);
            if (value is PdfIndirectReference streamRef) structure.AppearanceOwner.TryAdd(streamRef.GetObjNumber(), owner);
            else if (value is PdfDictionary states)
                foreach (var state in states.KeySet())
                    if (states.Get(state, false) is PdfIndirectReference stateRef) structure.AppearanceOwner.TryAdd(stateRef.GetObjNumber(), owner);
        }
    }

    /// <summary>Object number -> first page that draws it, for every object a page's content uses.</summary>
    private static Dictionary<int, int> ContentObjects(PdfDocument document)
    {
        var map = new Dictionary<int, int>();
        for (var page = 1; page <= document.GetNumberOfPages(); page++)
        {
            var dictionary = document.GetPage(page).GetPdfObject();
            var contents = dictionary.Get(PdfName.Contents, false);
            if (contents is PdfIndirectReference contentsRef) map.TryAdd(contentsRef.GetObjNumber(), page);
            CollectFor(contents, map, page);

            // Resources may be inherited from a parent Pages node.
            PdfDictionary? node = dictionary;
            for (var depth = 0; node is not null && depth < 64; depth++)
            {
                var resources = node.Get(PdfName.Resources, false);
                if (resources is not null)
                {
                    if (resources is PdfIndirectReference resourcesRef) map.TryAdd(resourcesRef.GetObjNumber(), page);
                    CollectFor(resources, map, page);
                    break;
                }

                node = node.GetAsDictionary(PdfName.Parent);
            }
        }

        return map;
    }

    private static void CollectFor(PdfObject? value, Dictionary<int, int> map, int page)
    {
        var set = new HashSet<int>();
        Collect(value, set, 0);
        foreach (var number in set) map.TryAdd(number, page);
    }

    /// <summary>Every indirect object reachable from <paramref name="value"/> (not through /Parent or /P).</summary>
    private static void Collect(PdfObject? value, HashSet<int> into, int depth)
    {
        var stack = new Stack<PdfObject>();
        if (value is not null) stack.Push(value);
        var guard = 0;

        while (stack.Count > 0 && guard++ < 500_000)
        {
            var current = stack.Pop();
            if (current is PdfIndirectReference reference)
            {
                if (reference.IsFree() || !into.Add(reference.GetObjNumber())) continue;
                var target = reference.GetRefersTo();
                if (target is not null) stack.Push(target);
                continue;
            }

            if (current is PdfDictionary dictionary) // includes streams
            {
                foreach (var key in dictionary.KeySet())
                {
                    if (PdfName.Parent.Equals(key) || PdfName.P.Equals(key)) continue;
                    var child = dictionary.Get(key, false);
                    if (child is not null) stack.Push(child);
                }
            }
            else if (current is PdfArray array)
            {
                for (var i = 0; i < array.Size(); i++)
                {
                    var child = array.Get(i, false);
                    if (child is not null) stack.Push(child);
                }
            }
        }
    }

    private static HashSet<int> Reachable(PdfDocument document, List<int> roots)
    {
        var set = new HashSet<int>();
        foreach (var root in roots)
        {
            var reference = document.GetPdfObject(root)?.GetIndirectReference();
            if (reference is null) continue;
            if (reference.GetRefersTo() is PdfDictionary dictionary && PdfName.Page.Equals(dictionary.GetAsName(PdfName.Type))) continue;
            Collect(reference, set, 0);
        }

        return set;
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static PdfObject? SafeObject(PdfIndirectReference? reference)
    {
        if (reference is null) return null;
        try
        {
            return reference.GetRefersTo();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSignatureDictionary(PdfDictionary dictionary)
    {
        var type = dictionary.GetAsName(PdfName.Type);
        if (PdfName.Sig.Equals(type) || DocTimeStamp.Equals(type)) return true;
        return type is null && dictionary.Get(PdfName.ByteRange) is not null && dictionary.Get(PdfName.Contents) is not null &&
               dictionary.Get(PdfName.Filter) is not null;
    }

    /// <summary>The field a widget belongs to (itself when it carries /T).</summary>
    private static PdfDictionary FieldOf(PdfDictionary dictionary)
    {
        if (dictionary.Get(PdfName.T) is null && dictionary.GetAsDictionary(PdfName.Parent) is { } parent &&
            PdfName.Widget.Equals(dictionary.GetAsName(PdfName.Subtype)))
            return parent;
        return dictionary;
    }

    private static bool IsSignatureField(PdfDictionary field)
    {
        PdfDictionary? node = field;
        for (var depth = 0; node is not null && depth < 32; depth++)
        {
            var type = node.GetAsName(PdfName.FT);
            if (type is not null) return PdfName.Sig.Equals(type);
            node = node.GetAsDictionary(PdfName.Parent);
        }

        return false;
    }

    private static string FullName(PdfDictionary field)
    {
        var parts = new List<string>();
        PdfDictionary? node = field;
        for (var depth = 0; node is not null && depth < 32; depth++)
        {
            var part = node.GetAsString(PdfName.T)?.ToUnicodeString();
            if (!string.IsNullOrEmpty(part)) parts.Insert(0, part);
            node = node.GetAsDictionary(PdfName.Parent);
        }

        return parts.Count == 0 ? "(unnamed)" : string.Join(".", parts);
    }

    private static bool RefersTo(PdfObject? value, int number) =>
        value is PdfIndirectReference reference && reference.GetObjNumber() == number;

    private static bool SameDictionary(PdfDictionary a, PdfDictionary b, params PdfName[] ignore)
    {
        foreach (var key in a.KeySet().Union(b.KeySet()).Distinct())
        {
            if (ignore.Contains(key)) continue;
            if (!SameValue(a.Get(key, false), b.Get(key, false))) return false;
        }

        return true;
    }

    /// <summary>
    /// Compares two values without following references: a reference is equal when it names the
    /// same object (the object's own change, if any, is classified separately).
    /// </summary>
    private static bool SameValue(PdfObject? a, PdfObject? b, int depth = 0)
    {
        if (a is null || b is null) return a is null && b is null;
        if (depth > 32) return false;

        if (a is PdfIndirectReference ra || b is PdfIndirectReference)
        {
            return a is PdfIndirectReference x && b is PdfIndirectReference y &&
                   x.GetObjNumber() == y.GetObjNumber() && x.GetGenNumber() == y.GetGenNumber();
        }

        if (a.GetObjectType() != b.GetObjectType()) return false;

        switch (a)
        {
            case PdfStream sa when b is PdfStream sb:
                return SameDictionaryValues(sa, sb, depth) && (sa.GetBytes(false) ?? []).AsSpan().SequenceEqual(sb.GetBytes(false) ?? []);
            case PdfDictionary da when b is PdfDictionary db:
                return SameDictionaryValues(da, db, depth);
            case PdfArray aa when b is PdfArray ab:
                if (aa.Size() != ab.Size()) return false;
                for (var i = 0; i < aa.Size(); i++)
                    if (!SameValue(aa.Get(i, false), ab.Get(i, false), depth + 1)) return false;
                return true;
            case PdfString sa2 when b is PdfString sb2:
                return sa2.GetValueBytes().AsSpan().SequenceEqual(sb2.GetValueBytes());
            case PdfNumber na when b is PdfNumber nb:
                return na.GetValue().Equals(nb.GetValue());
            default:
                return a.Equals(b) || a.ToString() == b.ToString();
        }
    }

    private static bool SameDictionaryValues(PdfDictionary a, PdfDictionary b, int depth)
    {
        if (a.Size() != b.Size()) return false;
        foreach (var key in a.KeySet())
            if (!SameValue(a.Get(key, false), b.Get(key, false), depth + 1)) return false;
        return true;
    }
}
