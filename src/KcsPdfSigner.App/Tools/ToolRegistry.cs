namespace KcsPdfSigner.App.Tools;

/// <summary>
/// The single registration point for tool panels. Order here is the order in the tools pane and the
/// Tools menu. The first entry is shown as the primary (inverted) tool. No tool opens at start-up.
/// </summary>
public static class ToolRegistry
{
    public static List<IToolPanel> CreateAll() =>
    [
        new MultiSignPanel(),

        // Phase C: add one line per tool, e.g.
        new CommentPanel(),
        new FormsPanel(),
        new OrganizePagesPanel(),
        new PageBasketPanel(),
        new CombinePanel(),
        new EditPanel(),
        new RedactPanel(),
        new ProtectPanel(),
        new ExportPanel(),
        new OcrPanel(),
        new ScanCleanerPanel(),
        new SearchPanel(),
        new PropertiesPanel(),
        new ReadAloudPanel(),
    ];
}
