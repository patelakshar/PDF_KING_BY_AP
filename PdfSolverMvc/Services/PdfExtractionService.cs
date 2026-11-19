using System.Text;
using PdfSolverMvc.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfSolverMvc.Services;

public class PdfExtractionService : IPdfExtractionService
{
    private readonly IOcrService _ocrService;
    private readonly ILogger<PdfExtractionService> _logger;

    public PdfExtractionService(IOcrService ocrService, ILogger<PdfExtractionService> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
    }

    private sealed record CellToken(double Left, double Right, double CenterX, string Text);

    private sealed class ColumnCluster
    {
        public double MinLeft { get; set; }
        public double MaxRight { get; set; }
        public double CenterX => (MinLeft + MaxRight) / 2.0;
    }

    public async Task<PdfExtractionResult> ExtractAsync(string pdfFilePath, CancellationToken cancellationToken = default)
    {
        var pages = new List<PdfPageContent>();
        var fullTextBuilder = new StringBuilder();
        var notes = new List<string>();
        int directTextPages = 0;
        int ocrPages = 0;
        int emptyScannedPages = 0;

        try
        {
            using var document = PdfDocument.Open(pdfFilePath, new ParsingOptions { UseLenientParsing = true });
            int pageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
                var (alignedText, tableRows) = ExtractColumnAlignedGrid(words);

                string pageType = "Text (Column-Grid)";

                // If page has almost no selectable text, treat as scanned/image page and try OCR
                if (alignedText.Length < 15)
                {
                    var ocrResult = await _ocrService.ExtractTextFromPdfPageAsync(pdfFilePath, page.Number - 1, cancellationToken);
                    if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.Text))
                    {
                        alignedText = ocrResult.Text;
                        tableRows = ocrResult.Text
                            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => new List<string> { line.Trim() })
                            .ToList();
                        pageType = "OCR";
                        ocrPages++;
                    }
                    else
                    {
                        pageType = "Scanned (No OCR)";
                        emptyScannedPages++;
                        if (!string.IsNullOrWhiteSpace(ocrResult.Message) && !notes.Contains(ocrResult.Message))
                        {
                            notes.Add(ocrResult.Message);
                        }
                    }
                }
                else
                {
                    directTextPages++;
                }

                var wordCount = string.IsNullOrWhiteSpace(alignedText)
                    ? 0
                    : alignedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                pages.Add(new PdfPageContent
                {
                    PageNumber = page.Number,
                    ExtractionType = pageType,
                    WordCount = wordCount,
                    Content = alignedText,
                    TableRows = tableRows
                });

                fullTextBuilder.AppendLine($"--- Page {page.Number} ({pageType}) ---");
                fullTextBuilder.AppendLine(alignedText);
                fullTextBuilder.AppendLine();
            }

            string method = (directTextPages, ocrPages, emptyScannedPages) switch
            {
                (> 0, 0, 0) => "DirectText",
                (0, > 0, _) => "OcrFallback",
                (> 0, > 0, _) => "HybridOcr",
                (_, 0, > 0) => "NeedsOcr",
                _ => "DirectText"
            };

            string status = emptyScannedPages > 0 && directTextPages == 0 && ocrPages == 0
                ? "PartialOcrNeeded"
                : "Processed";

            return new PdfExtractionResult(
                pageCount,
                fullTextBuilder.ToString().Trim(),
                pages,
                method,
                status,
                notes.Count > 0 ? string.Join(" | ", notes) : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract PDF {File}", pdfFilePath);
            return new PdfExtractionResult(
                0,
                string.Empty,
                pages,
                "Failed",
                "Error",
                $"PDF extraction error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reconstructs rows using dynamic Y-tolerance (handles tiny 5pt tables as well as normal text),
    /// merges words into cell tokens, and snaps cells into global X-column bins so blank cells
    /// in sparse matrices (e.g., DN sizes, API 610 Min/Normal/Rated/Max) stay aligned.
    /// </summary>
    private static (string AlignedText, List<List<string>> TableRows) ExtractColumnAlignedGrid(List<Word> words)
    {
        if (words.Count == 0)
        {
            return (string.Empty, new List<List<string>>());
        }

        // 1. Dynamic Y-tolerance based on median word height
        var heights = words.Select(w => w.BoundingBox.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double yTolerance = Math.Clamp(medianHeight * 0.45, 1.0, 4.5);

        // 2. Group words into horizontal rows
        var sortedByY = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var rawRows = new List<List<Word>>();

        foreach (var word in sortedByY)
        {
            var matchingRow = rawRows.FirstOrDefault(r =>
                Math.Abs(r[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= yTolerance);

            if (matchingRow != null)
            {
                matchingRow.Add(word);
            }
            else
            {
                rawRows.Add(new List<Word> { word });
            }
        }

        // 3. Merge adjacent words on each row into cell tokens
        var rowTokens = new List<List<CellToken>>();
        foreach (var row in rawRows)
        {
            var ordered = row.OrderBy(w => w.BoundingBox.Left).ToList();
            var cells = new List<CellToken>();

            var currentText = new StringBuilder(ordered[0].Text);
            double cellLeft = ordered[0].BoundingBox.Left;
            double cellRight = ordered[0].BoundingBox.Right;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var curr = ordered[i];
                double gap = curr.BoundingBox.Left - prev.BoundingBox.Right;
                double avgCharWidth = Math.Max(2.2, prev.BoundingBox.Width / Math.Max(1, prev.Text.Length));

                // If gap is small, it's part of the same cell/phrase
                if (gap <= Math.Max(4.5, avgCharWidth * 1.9))
                {
                    currentText.Append(' ').Append(curr.Text);
                    cellRight = Math.Max(cellRight, curr.BoundingBox.Right);
                }
                else
                {
                    cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, currentText.ToString().Trim()));
                    currentText.Clear();
                    currentText.Append(curr.Text);
                    cellLeft = curr.BoundingBox.Left;
                    cellRight = curr.BoundingBox.Right;
                }
            }

            cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, currentText.ToString().Trim()));
            if (cells.Count > 0)
            {
                rowTokens.Add(cells);
            }
        }

        // 4. Cluster cell X-positions into global column bins
        // Sort all tokens by width ascending so narrow numeric columns establish crisp anchors before wide headers
        var allCells = rowTokens.SelectMany(r => r).OrderBy(c => c.Right - c.Left).ToList();
        var columns = new List<ColumnCluster>();

        foreach (var cell in allCells)
        {
            var match = columns
                .Select(col => new
                {
                    Col = col,
                    Overlap = Math.Min(col.MaxRight, cell.Right) - Math.Max(col.MinLeft, cell.Left),
                    CenterDist = Math.Abs(col.CenterX - cell.CenterX)
                })
                .Where(x => x.Overlap > 0 || x.CenterDist <= medianHeight * 1.2)
                .OrderBy(x => x.CenterDist)
                .FirstOrDefault();

            if (match != null)
            {
                // Expand cluster bounds gently only if it's a similar-width cell
                if ((cell.Right - cell.Left) <= (match.Col.MaxRight - match.Col.MinLeft) * 1.8)
                {
                    match.Col.MinLeft = Math.Min(match.Col.MinLeft, cell.Left);
                    match.Col.MaxRight = Math.Max(match.Col.MaxRight, cell.Right);
                }
            }
            else
            {
                columns.Add(new ColumnCluster { MinLeft = cell.Left, MaxRight = cell.Right });
            }
        }

        columns = columns.OrderBy(c => c.MinLeft).ToList();

        // 5. Snap every cell in every row into its column bin (preserving blank cells!)
        var tableRows = new List<List<string>>();
        foreach (var row in rowTokens)
        {
            var gridRow = Enumerable.Repeat(string.Empty, columns.Count).ToList();
            foreach (var cell in row)
            {
                int bestCol = 0;
                double bestScore = double.MaxValue;

                for (int c = 0; c < columns.Count; c++)
                {
                    var col = columns[c];
                    double overlap = Math.Min(col.MaxRight, cell.Right) - Math.Max(col.MinLeft, cell.Left);
                    double dist = Math.Abs(col.CenterX - cell.CenterX);
                    double score = overlap > 0 ? -overlap : dist + 1000;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestCol = c;
                    }
                }

                gridRow[bestCol] = string.IsNullOrEmpty(gridRow[bestCol])
                    ? cell.Text
                    : $"{gridRow[bestCol]} {cell.Text}";
            }

            // Trim trailing empty columns per row for cleaner display, or keep uniform?
            // Keeping uniform width makes CSV and HTML tables align perfectly.
            if (gridRow.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                tableRows.Add(gridRow);
            }
        }

        // Remove completely empty columns across the entire page
        var nonEmptyColIndices = new List<int>();
        for (int c = 0; c < columns.Count; c++)
        {
            if (tableRows.Any(r => !string.IsNullOrWhiteSpace(r[c])))
            {
                nonEmptyColIndices.Add(c);
            }
        }

        var compactRows = tableRows
            .Select(r => nonEmptyColIndices.Select(c => r[c]).ToList())
            .ToList();

        // 6. Format clean pipe-delimited text preserving blank cells ("|  |")
        var textBuilder = new StringBuilder();
        foreach (var row in compactRows)
        {
            // Trim trailing empty cells for readable text while keeping all leading/middle blank cells
            int lastNonEmpty = row.FindLastIndex(c => !string.IsNullOrWhiteSpace(c));
            if (lastNonEmpty < 0) continue;

            var slice = row.Take(lastNonEmpty + 1);
            textBuilder.AppendLine(string.Join(" | ", slice));
        }

        return (textBuilder.ToString().Trim(), compactRows);
    }
}
