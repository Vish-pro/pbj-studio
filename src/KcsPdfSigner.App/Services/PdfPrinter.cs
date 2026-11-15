using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KcsPdfSigner.App.Viewer;

namespace KcsPdfSigner.App.Services;

/// <summary>Prints a PDF through the Windows print dialog, each page rendered at 300 dpi.</summary>
public static class PdfPrinter
{
    public const int PrintDpi = 300;

    /// <summary>Shows the print dialog and prints. Returns false when the user cancelled.</summary>
    public static bool Print(byte[] pdf, string? password, int pageCount, string jobName, int currentPage)
    {
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)Math.Max(1, pageCount),
            PageRange = new PageRange(1, Math.Max(1, pageCount))
        };

        if (dialog.ShowDialog() != true) return false;

        var pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.UserPages => Enumerable
                .Range(Math.Max(1, dialog.PageRange.PageFrom),
                    Math.Max(0, Math.Min(pageCount, dialog.PageRange.PageTo) - Math.Max(1, dialog.PageRange.PageFrom) + 1))
                .ToList(),
            PageRangeSelection.CurrentPage => [Math.Clamp(currentPage, 1, pageCount)],
            _ => Enumerable.Range(1, pageCount).ToList()
        };

        if (pages.Count == 0) return false;

        var area = dialog.PrintQueue?.GetPrintCapabilities(dialog.PrintTicket)?.PageImageableArea;
        var paginator = new RenderedPdfPaginator(
            pdf, password, pages,
            new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight),
            area is null ? new Rect() : new Rect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight));

        dialog.PrintDocument(paginator, jobName);
        return true;
    }

    /// <summary>Renders each page only when the spooler asks for it, so memory stays flat.</summary>
    private sealed class RenderedPdfPaginator : DocumentPaginator
    {
        private readonly byte[] _pdf;
        private readonly string? _password;
        private readonly IReadOnlyList<int> _pages;
        private readonly Rect _imageable;

        public RenderedPdfPaginator(byte[] pdf, string? password, IReadOnlyList<int> pages, Size pageSize, Rect imageable)
        {
            _pdf = pdf;
            _password = password;
            _pages = pages;
            PageSize = pageSize;
            _imageable = imageable;
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => _pages.Count;

        public override Size PageSize { get; set; }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var bitmap = PageRenderer.Render(_pdf, _pages[pageNumber], _password, PrintDpi);

            // Fit the page inside the printable area, keeping its proportions, centred.
            var area = _imageable.Width > 0 && _imageable.Height > 0
                ? _imageable
                : new Rect(0, 0, PageSize.Width, PageSize.Height);

            var imageWidth = bitmap.PixelWidth * 96.0 / PrintDpi;
            var imageHeight = bitmap.PixelHeight * 96.0 / PrintDpi;

            // Turn landscape pages onto portrait paper (and vice versa) when that fits better.
            var rotate = (imageWidth > imageHeight) != (area.Width > area.Height);
            var (fitWidth, fitHeight) = rotate ? (imageHeight, imageWidth) : (imageWidth, imageHeight);

            var scale = Math.Min(area.Width / fitWidth, area.Height / fitHeight);
            var drawWidth = imageWidth * scale;
            var drawHeight = imageHeight * scale;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var centre = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                if (rotate) context.PushTransform(new RotateTransform(90, centre.X, centre.Y));
                context.DrawImage(bitmap, new Rect(
                    centre.X - drawWidth / 2, centre.Y - drawHeight / 2, drawWidth, drawHeight));
                if (rotate) context.Pop();
            }

            var full = new Rect(0, 0, PageSize.Width, PageSize.Height);
            return new DocumentPage(visual, PageSize, full, full);
        }
    }
}
