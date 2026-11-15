using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace KcsPdfSigner.Core.Documents;

/// <summary>
/// One fillable field. FieldType is "text", "checkbox", "radio", "choice", "button" or
/// "signature". For checkbox and radio, Options lists the "on" values; for choice fields, the
/// selectable values. The rectangle (unrotated PDF points) is that of the first widget.
/// </summary>
public sealed class FormFieldInfo
{
    public string Name { get; init; } = "";
    public string FieldType { get; init; } = "";
    public string Value { get; init; } = "";
    public List<string> Options { get; init; } = [];
    public int PageNumber { get; init; }
    public float Left { get; init; }
    public float Bottom { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public bool IsReadOnly { get; init; }
}

/// <summary>Reads, fills and flattens AcroForm fields.</summary>
public static class PdfFormOps
{
    public static List<FormFieldInfo> ReadFields(string path, string? password = null)
    {
        using var document = PdfIo.OpenRead(path, password);
        var form = PdfFormCreator.GetAcroForm(document, false);
        if (form is null) return [];

        var widgetPages = MapWidgetsToPages(document);
        var result = new List<FormFieldInfo>();

        foreach (var (name, field) in form.GetAllFormFields())
        {
            // Parent nodes only group fields by name; the terminal fields carry values.
            if (field.GetChildFormFields().Count > 0) continue;

            var type = TypeOf(field);
            var widget = field.GetWidgets().FirstOrDefault();
            var rect = widget?.GetRectangle()?.ToRectangle();
            var page = 0;
            var reference = widget?.GetPdfObject().GetIndirectReference();
            if (reference is not null) widgetPages.TryGetValue(reference, out page);

            result.Add(new FormFieldInfo
            {
                Name = name,
                FieldType = type,
                Value = field.GetValueAsString() ?? "",
                Options = OptionsOf(field, type),
                PageNumber = page,
                Left = rect?.GetLeft() ?? 0,
                Bottom = rect?.GetBottom() ?? 0,
                Width = rect?.GetWidth() ?? 0,
                Height = rect?.GetHeight() ?? 0,
                IsReadOnly = field.IsReadOnly()
            });
        }

        return result;
    }

    /// <summary>
    /// Sets field values by full name. For checkboxes, "true", "yes", "on" or "1" ticks the box
    /// and "false", "no", "off", "0" or "" clears it; any other value must be one of its on-states.
    /// Throws if a name does not exist or the field is read-only or a signature.
    /// </summary>
    public static void Fill(string input, string output, IDictionary<string, string> values, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var form = PdfFormCreator.GetAcroForm(document, false)
                       ?? throw new InvalidOperationException("This PDF has no form fields.");

            var missing = values.Keys.Where(k => form.GetField(k) is null).ToList();
            if (missing.Count > 0)
                throw new ArgumentException("No such field(s): " + string.Join(", ", missing));

            foreach (var (name, value) in values)
            {
                var field = form.GetField(name);
                var type = TypeOf(field);
                if (type == "signature") throw new InvalidOperationException($"'{name}' is a signature field and cannot be filled.");
                if (type == "button") throw new InvalidOperationException($"'{name}' is a push button and has no value.");
                if (field.IsReadOnly()) throw new InvalidOperationException($"'{name}' is read-only.");

                field.SetValue(type is "checkbox" or "radio" ? ButtonState(field, name, value ?? "") : value ?? "");
            }
        });
    }

    /// <summary>Burns all field values into the page and removes the form.</summary>
    public static void Flatten(string input, string output, string? password = null)
    {
        PdfIo.Edit(input, output, password, document =>
        {
            var form = PdfFormCreator.GetAcroForm(document, false)
                       ?? throw new InvalidOperationException("This PDF has no form fields.");
            form.FlattenFields();
        });
    }

    private static string TypeOf(PdfFormField field)
    {
        var type = field.GetFormType();
        if (PdfName.Tx.Equals(type)) return "text";
        if (PdfName.Ch.Equals(type)) return "choice";
        if (PdfName.Sig.Equals(type)) return "signature";
        if (PdfName.Btn.Equals(type))
        {
            if (field is PdfButtonFormField button)
            {
                if (button.IsPushButton()) return "button";
                if (button.IsRadio()) return "radio";
            }
            return "checkbox";
        }
        return "text";
    }

    private static List<string> OptionsOf(PdfFormField field, string type)
    {
        var options = new List<string>();

        if (type is "checkbox" or "radio")
        {
            options.AddRange(field.GetAppearanceStates().Where(s => s != "Off").Distinct());
        }
        else if (type == "choice")
        {
            var array = field.GetPdfObject().GetAsArray(PdfName.Opt);
            if (array is null) return options;
            foreach (var item in array)
            {
                var entry = item is PdfArray pair && pair.Size() > 0 ? pair.Get(0) : item;
                if (entry is PdfString text) options.Add(text.ToUnicodeString());
            }
        }

        return options;
    }

    private static string ButtonState(PdfFormField field, string name, string value)
    {
        var states = field.GetAppearanceStates().Where(s => s != "Off").Distinct().ToList();
        var normalized = value.Trim().ToLowerInvariant();

        if (normalized is "" or "off" or "false" or "no" or "0") return "Off";
        if (states.Contains(value)) return value;
        if (normalized is "true" or "yes" or "on" or "1" && states.Count > 0) return states[0];

        throw new ArgumentException($"'{value}' is not a valid state for '{name}'. Valid: Off, {string.Join(", ", states)}.");
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
