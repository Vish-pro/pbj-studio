using System.Windows;
using System.Windows.Input;

namespace KcsPdfSigner.App.Viewer;

/// <summary>What a left click on a page does. Tools switch this while they need the mouse.</summary>
public enum InteractionMode
{
    /// <summary>Drag pans the document (hand tool). Default.</summary>
    Browse,

    /// <summary>A click raises <see cref="PdfViewer.PageClick"/>.</summary>
    PointClick,

    /// <summary>A drag draws a dashed box and raises <see cref="PdfViewer.PageRectangleSelected"/>.</summary>
    RectangleDrag,

    /// <summary>A drag draws a line and raises <see cref="PdfViewer.PageFreehandCompleted"/>.</summary>
    Freehand
}

public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage
}

/// <summary>
/// A mouse event on a page. <see cref="X"/> and <see cref="Y"/> are PDF user-space points of the
/// unrotated page (origin bottom-left), the same space Core uses for rectangles.
/// <see cref="OverlayPoint"/> is the position on that page's overlay canvas.
/// </summary>
public sealed class PageMouseEventArgs : EventArgs
{
    public required int PageNumber { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required Point OverlayPoint { get; init; }
    public MouseButton Button { get; init; } = MouseButton.Left;
    public int ClickCount { get; init; } = 1;
    public ModifierKeys Modifiers { get; init; }

    /// <summary>Set true in a PageMouseDown handler to stop the viewer's own handling (pan, drag).</summary>
    public bool Handled { get; set; }
}

/// <summary>A box dragged on a page, in unrotated PDF points (origin bottom-left).</summary>
public sealed class PageRectEventArgs : EventArgs
{
    public required int PageNumber { get; init; }
    public required double Left { get; init; }
    public required double Bottom { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required Rect OverlayRect { get; init; }
}

/// <summary>A freehand stroke on a page. Points are unrotated PDF points (origin bottom-left).</summary>
public sealed class PageInkEventArgs : EventArgs
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<Point> PdfPoints { get; init; }
    public required IReadOnlyList<Point> OverlayPoints { get; init; }
}

/// <summary>A search hit (or any area) to highlight, in unrotated PDF points.</summary>
public readonly record struct PageHighlight(int PageNumber, double Left, double Bottom, double Width, double Height);
