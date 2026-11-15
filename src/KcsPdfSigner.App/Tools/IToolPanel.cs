using System.Windows;
using KcsPdfSigner.App.Documents;
using KcsPdfSigner.App.Viewer;

namespace KcsPdfSigner.App.Tools;

/*
 * How to add a tool panel (Phase C)
 * ---------------------------------
 * 1. Create a UserControl under Tools/, e.g. Tools/CommentPanel.xaml(.cs), and implement IToolPanel.
 *    - Title and IconKey (a Geometry key from Themes/Icons.xaml, e.g. "Icon.Comment") fill the
 *      entry in the right-hand tools pane and the Tools menu.
 *    - View returns the control itself (`public FrameworkElement View => this;`).
 * 2. Register it in ToolRegistry.CreateAll(), in the order it should appear.
 * 3. Lifecycle, driven by MainWindow:
 *    - Initialize(host) once, right after creation.
 *    - Attach(session, viewer) whenever the panel is opened, or the active document changes while it
 *      is open. session and viewer are null when no document is open.
 *    - Detach() when the panel is closed, another tool is opened, or before attaching to a different
 *      document. Undo everything Attach did: unsubscribe viewer events, set
 *      viewer.InteractionMode back to Browse, and viewer.ClearOverlay(this).
 * 4. Change documents only through session.Apply / ApplyAsync so every edit is undoable. Draw
 *    on viewer.GetOverlay(page) with Tag = this, and redraw on viewer.OverlayLayoutChanged.
 */

/// <summary>A tool shown in the right-hand tools pane.</summary>
public interface IToolPanel
{
    /// <summary>Name in the tools list, e.g. "Comment".</summary>
    string Title { get; }

    /// <summary>One-line description shown under the title.</summary>
    string Description { get; }

    /// <summary>Key of a Geometry in Themes/Icons.xaml.</summary>
    string IconKey { get; }

    /// <summary>The panel's UI (normally the user control itself).</summary>
    FrameworkElement View { get; }

    /// <summary>Called once after construction.</summary>
    void Initialize(IStudioHost host);

    /// <summary>The panel is visible and should work on this document (both null when none is open).</summary>
    void Attach(DocumentSession? session, PdfViewer? viewer);

    /// <summary>Release the document and viewer taken in <see cref="Attach"/>.</summary>
    void Detach();
}

/// <summary>What the main window offers to tool panels.</summary>
public interface IStudioHost
{
    /// <summary>Owner for dialogs.</summary>
    Window Window { get; }

    /// <summary>Opens a PDF in a new tab (or activates it if already open) and returns its session.</summary>
    DocumentSession? OpenDocument(string path);

    /// <summary>Shows a short message in the status bar.</summary>
    void SetStatus(string message);

    /// <summary>Opens a tool panel by title, e.g. "Search". Returns false when not registered.</summary>
    bool OpenTool(string title);

    /// <summary>Raised when the user submits the toolbar find box (Enter) or presses Ctrl+F then Enter.</summary>
    event EventHandler<string>? FindRequested;

    /// <summary>Raised when the active document changes (tab switch, open, close); argument may be null.</summary>
    event EventHandler<DocumentSession?>? ActiveDocumentChanged;

    /// <summary>Raised after a document tab is closed and its session disposed. Drop any state for it.</summary>
    event EventHandler<DocumentSession>? DocumentClosed;

    /// <summary>The active document and its viewer, or null when none is open.</summary>
    DocumentSession? ActiveSession { get; }

    PdfViewer? ActiveViewer { get; }
}
