using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KcsPdfSigner.App.Dialogs;
using KcsPdfSigner.App.Services;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;
using KcsPdfSigner.Core.Documents;

namespace KcsPdfSigner.App.Tools;

/// <summary>One field in the Fill Forms list with the value being edited.</summary>
public sealed class FormFieldRow
{
    public required FormFieldInfo Info { get; init; }

    /// <summary>The value as typed or picked; compared with Info.Value to find changes.</summary>
    public string Value { get; set; } = "";

    public bool IsEditable => !Info.IsReadOnly && Info.FieldType is "text" or "checkbox" or "radio" or "choice";

    public bool IsChanged => IsEditable && !string.Equals(Normalize(Value), Normalize(Info.Value), StringComparison.Ordinal);

    internal Border? Frame { get; set; }

    private string Normalize(string value)
        => Info.FieldType is "checkbox" or "radio" && (value.Length == 0 || value == "Off") ? "Off" : value;
}

/// <summary>
/// Fill Forms: lists the AcroForm fields with an editor suited to each type, writes the values
/// with PdfFormOps.Fill and flattens with PdfFormOps.Flatten.
/// </summary>
public partial class FormsPanel : UserControl, IToolPanel
{
    private IStudioHost? _host;
    private DocumentSession? _session;
    private PdfViewer? _viewer;
    private List<FormFieldRow> _rows = [];
    private FormFieldRow? _selected;
    private int _generation;

    public FormsPanel()
    {
        InitializeComponent();
    }

    // ---- IToolPanel --------------------------------------------------------

    public string Title => "Fill Forms";

    public string Description => "Fill in form fields, then flatten.";

    public string IconKey => "Icon.Form";

    public FrameworkElement View => this;

    public void Initialize(IStudioHost host) => _host = host;

    public void Attach(DocumentSession? session, PdfViewer? viewer)
    {
        Detach();
        _session = session;
        _viewer = viewer;

        if (_session is not null) _session.DocumentChanged += OnDocumentChanged;
        if (_viewer is not null) _viewer.OverlayLayoutChanged += OnOverlayLayoutChanged;

        _ = LoadFieldsAsync();
    }

    public void Detach()
    {
        if (_session is not null) _session.DocumentChanged -= OnDocumentChanged;
        if (_viewer is not null)
        {
            _viewer.OverlayLayoutChanged -= OnOverlayLayoutChanged;
            _viewer.InteractionMode = InteractionMode.Browse;
            _viewer.ClearOverlay(this);
        }

        _session = null;
        _viewer = null;
        _generation++;
    }

    // ---- Public for tests and automation ----------------------------------

    public IReadOnlyList<FormFieldRow> Rows => _rows;

    /// <summary>Reads the fields of the working file off the UI thread and rebuilds the editors.</summary>
    public async Task LoadFieldsAsync()
    {
        var generation = ++_generation;
        _rows = [];
        _selected = null;
        FieldHost.Children.Clear();
        UpdateButtons();

        var session = _session;
        if (session is null)
        {
            StatusText.Text = "Open a PDF to fill its form.";
            return;
        }

        StatusText.Text = "Reading form fields...";
        var file = session.CurrentFile;
        var password = session.Password;

        try
        {
            var fields = await Task.Run(() => PdfFormOps.ReadFields(file, password));
            if (generation != _generation) return;

            _rows = fields
                .OrderBy(f => f.PageNumber == 0 ? int.MaxValue : f.PageNumber)
                .ThenByDescending(f => f.Bottom + f.Height)
                .ThenBy(f => f.Left)
                .Select(f => new FormFieldRow { Info = f, Value = f.Value })
                .ToList();

            foreach (var row in _rows) FieldHost.Children.Add(BuildRow(row));

            StatusText.Text = _rows.Count == 0
                ? "This document has no form fields."
                : $"{_rows.Count} field(s). Click a field's name to find it on the page.";
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            App.LogError("Read form fields", ex);
            StatusText.Text = "Could not read the form: " + ex.Message;
        }

        UpdateButtons();
        RedrawOverlay();
    }

    /// <summary>Writes every changed value into the document (undoable). Returns false when nothing changed or it failed.</summary>
    public async Task<bool> ApplyValuesAsync()
    {
        var session = _session;
        if (session is null) return false;

        var changes = _rows.Where(r => r.IsChanged).ToDictionary(r => r.Info.Name, r => r.Value);
        if (changes.Count == 0)
        {
            _host?.SetStatus("No field values were changed.");
            return false;
        }

        var password = session.Password;
        return await ToolUi.ApplyAsync(_host, this, session,
            (input, output) => PdfFormOps.Fill(input, output, changes, password),
            changes.Count == 1 ? "Fill 1 form field" : $"Fill {changes.Count} form fields");
    }

    // ---- Editors -----------------------------------------------------------

    private FrameworkElement BuildRow(FormFieldRow row)
    {
        var info = row.Info;
        var panel = new StackPanel();

        var title = new TextBlock
        {
            Text = info.Name,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Cursor = Cursors.Hand
        };
        var detail = new TextBlock
        {
            Text = TypeLabel(info) + (info.PageNumber > 0 ? " - page " + info.PageNumber : "") +
                   (info.IsReadOnly ? " - read-only" : ""),
            Style = (Style)FindResource("Hint")
        };
        panel.Children.Add(title);
        panel.Children.Add(detail);

        FrameworkElement? editor = null;

        switch (info.FieldType)
        {
            case "text":
            {
                var box = new TextBox { Text = row.Value, IsReadOnly = info.IsReadOnly, Margin = new Thickness(0, 4, 0, 0) };
                if (info.Value.Contains('\n') || info.Height > 30)
                {
                    box.AcceptsReturn = true;
                    box.TextWrapping = TextWrapping.Wrap;
                    box.MinHeight = 48;
                }
                box.TextChanged += (_, _) => { row.Value = box.Text; UpdateButtons(); };
                editor = box;
                break;
            }

            case "checkbox":
            {
                var onState = info.Options.FirstOrDefault() ?? "Yes";
                var check = new CheckBox
                {
                    Content = info.Options.Count > 0 ? "Ticked (" + onState + ")" : "Ticked",
                    IsChecked = IsOn(info.Value),
                    IsEnabled = !info.IsReadOnly,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                check.Click += (_, _) => { row.Value = check.IsChecked == true ? onState : "Off"; UpdateButtons(); };
                editor = check;
                break;
            }

            case "radio":
            case "choice":
            {
                var combo = new ComboBox
                {
                    IsEnabled = !info.IsReadOnly,
                    IsEditable = info.FieldType == "choice",
                    Margin = new Thickness(0, 4, 0, 0)
                };
                if (info.FieldType == "radio") combo.Items.Add("Off");
                foreach (var option in info.Options) combo.Items.Add(option);

                if (info.FieldType == "radio")
                    combo.SelectedItem = IsOn(info.Value) && info.Options.Contains(info.Value) ? info.Value : "Off";
                else
                    combo.Text = info.Value;

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string s) row.Value = s;
                    UpdateButtons();
                };
                if (combo.IsEditable)
                {
                    combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                        new TextChangedEventHandler((_, _) => { row.Value = combo.Text; UpdateButtons(); }));
                }
                editor = combo;
                break;
            }

            case "signature":
            {
                var hint = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                hint.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(info.Value) ? "Empty signature field." : "Signed.",
                    TextWrapping = TextWrapping.Wrap
                });
                hint.Children.Add(new TextBlock
                {
                    Text = "Signature fields cannot be typed in. Use Multi-Sign (DSC) to sign them.",
                    Style = (Style)FindResource("Hint"),
                    TextWrapping = TextWrapping.Wrap
                });
                var open = new Button { Content = "Open Multi-Sign", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                open.Click += (_, _) => _host?.OpenTool("Multi-Sign (DSC)");
                hint.Children.Add(open);
                editor = hint;
                break;
            }

            default:
                panel.Children.Add(new TextBlock { Text = "Push button (no value).", Style = (Style)FindResource("Hint") });
                break;
        }

        if (editor is not null)
        {
            editor.GotKeyboardFocus += (_, _) => Select(row, scroll: true);
            panel.Children.Add(editor);
        }

        var frame = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = panel
        };
        frame.MouseLeftButtonDown += (_, _) => Select(row, scroll: true);
        row.Frame = frame;
        return frame;
    }

    private static bool IsOn(string value) => value.Length > 0 && value != "Off";

    private static string TypeLabel(FormFieldInfo info) => info.FieldType switch
    {
        "text" => "Text",
        "checkbox" => "Check box",
        "radio" => "Radio buttons",
        "choice" => "List",
        "signature" => "Signature",
        "button" => "Button",
        _ => info.FieldType
    };

    /// <summary>Marks a field as selected, outlines it on the page and scrolls the viewer to it.</summary>
    public void Select(FormFieldRow row, bool scroll)
    {
        if (!ReferenceEquals(_selected, row))
        {
            _selected = row;
            foreach (var r in _rows)
            {
                if (r.Frame is null) continue;
                var isSelected = ReferenceEquals(r, row);
                r.Frame.BorderBrush = (System.Windows.Media.Brush)FindResource(isSelected ? "Brush.Line" : "Brush.Subtle");
                r.Frame.BorderThickness = new Thickness(isSelected ? 2 : 1);
            }
            RedrawOverlay();
        }

        if (scroll && _viewer is not null && row.Info.PageNumber > 0)
        {
            _viewer.BringIntoView(row.Info.PageNumber, row.Info.Left, row.Info.Bottom + row.Info.Height);
        }
    }

    private void RedrawOverlay()
    {
        if (_viewer is null) return;
        _viewer.ClearOverlay(this);
        if (_selected is { Info: { PageNumber: > 0 } info })
        {
            ToolUi.DrawBox(_viewer, this, info.PageNumber, info.Left - 2, info.Bottom - 2, info.Width + 4, info.Height + 4);
        }
    }

    private void UpdateButtons()
    {
        var hasDocument = _session is not null;
        ApplyButton.IsEnabled = hasDocument && _rows.Any(r => r.IsChanged);
        ResetButton.IsEnabled = ApplyButton.IsEnabled;
        FlattenButton.IsEnabled = hasDocument && _rows.Count > 0;
    }

    // ---- Events ------------------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e) => _ = LoadFieldsAsync();

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) => RedrawOverlay();

    private async void OnApplyClicked(object sender, RoutedEventArgs e) => await ApplyValuesAsync();

    private void OnResetClicked(object sender, RoutedEventArgs e) => _ = LoadFieldsAsync();

    private async void OnFlattenClicked(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        var pending = _rows.Any(r => r.IsChanged);
        var message = "Flatten the form?\n\nThe field values become part of the page and the fields can no longer be edited. " +
                      "Undo (Ctrl+Z) still brings the fields back while the document is open.";
        if (pending) message += "\n\nValues you changed but did not apply are applied first.";

        if (!AppSettings.ConfirmEdit(_host?.Window, message, "Flatten form")) return;

        if (pending && !await ApplyValuesAsync()) return;

        var password = session.Password;
        await ToolUi.ApplyAsync(_host, this, session,
            (input, output) => PdfFormOps.Flatten(input, output, password), "Flatten form");
    }
}
