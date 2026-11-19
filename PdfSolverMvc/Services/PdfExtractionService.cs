using System.Text;
using System.Text.RegularExpressions;
using PdfSolverMvc.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfSolverMvc.Services;

public class PdfExtractionService : IPdfExtractionService
{
    private readonly IOcrService _ocrService;
    private readonly int _minSelectableCharsPerPage;
    private readonly ILogger<PdfExtractionService> _logger;

    public PdfExtractionService(
        IOcrService ocrService,
        IConfiguration configuration,
        ILogger<PdfExtractionService> logger)
    {
        _ocrService = ocrService;
        _minSelectableCharsPerPage = Math.Max(1, configuration.GetValue<int?>("Ocr:MinSelectableCharsPerPage") ?? 15);
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
        int vectorPages = 0;
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
                int selectableChars = words.Sum(w => w.Text?.Trim().Length ?? 0);
                double pageRotation = page.Rotation.Value;

                PdfPageContent pageContent;

                // Path 1: Searchable / Vector PDF page (including rotated vector pages)
                if (selectableChars >= _minSelectableCharsPerPage)
                {
                    pageContent = ExtractVectorPage(page, words, pageRotation);
                    vectorPages++;
                }
                else
                {
                    // Path 2: Scanned / Image-only or low-text page -> OCR pipeline
                    pageContent = await ExtractScannedPageAsync(pdfFilePath, page, words, pageRotation, cancellationToken);
                    if (pageContent.ExtractionType.StartsWith("OCR", StringComparison.OrdinalIgnoreCase))
                    {
                        ocrPages++;
                    }
                    else
                    {
                        emptyScannedPages++;
                    }

                    if (!string.IsNullOrWhiteSpace(pageContent.ExtractionNotes) &&
                        !notes.Contains(pageContent.ExtractionNotes))
                    {
                        notes.Add(pageContent.ExtractionNotes);
                    }
                }

                pages.Add(pageContent);

                fullTextBuilder.AppendLine($"--- Page {pageContent.PageNumber} ({pageContent.ExtractionType}) ---");
                fullTextBuilder.AppendLine(pageContent.Content);
                fullTextBuilder.AppendLine();
            }

            string method = (vectorPages, ocrPages, emptyScannedPages) switch
            {
                (> 0, 0, 0) => "DirectText",
                (0, > 0, _) => "OcrFallback",
                (> 0, > 0, _) => "HybridOcr",
                (_, 0, > 0) => "NeedsOcr",
                _ => "DirectText"
            };

            string status = emptyScannedPages > 0 && vectorPages == 0 && ocrPages == 0
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

    private static PdfPageContent ExtractVectorPage(Page page, List<Word> words, double pageRotation)
    {
        // 1) Column-aligned grid reconstruction (preserves tables & sparse matrices)
        var (alignedText, tableRows) = ExtractColumnAlignedGrid(words);

        // 2) Reading-order layout blocks (preserves multi-column prose, headers, and footers)
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var orderedBlocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var readingOrderText = string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedBlocks.Select(b => b.Text.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)));

        var finalText = !string.IsNullOrWhiteSpace(alignedText) ? alignedText : readingOrderText;
        var wordCount = CountWords(finalText);
        var pageType = pageRotation != 0
            ? $"Vector (Rotated {pageRotation:0}°)"
            : "Vector (Column-Grid)";

        return new PdfPageContent
        {
            PageNumber = page.Number,
            ExtractionType = pageType,
            WordCount = wordCount,
            Content = finalText,
            TableRows = tableRows,
            RawOcrText = readingOrderText != finalText ? readingOrderText : null,
            RotationDegrees = pageRotation,
            ExtractionNotes = pageRotation != 0 ? $"Vector page rotated {pageRotation:0}°; coordinates normalized." : null
        };
    }

    private async Task<PdfPageContent> ExtractScannedPageAsync(
        string pdfFilePath,
        Page page,
        List<Word> fallbackWords,
        double pageRotation,
        CancellationToken cancellationToken)
    {
        var ocrResult = await _ocrService.ExtractTextFromPdfPageAsync(pdfFilePath, page.Number - 1, cancellationToken);

        if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            var rawOcr = ocrResult.Text;
            var (reconstructed, tableRows) = ReconstructOcrLayout(rawOcr);

            return new PdfPageContent
            {
                PageNumber = page.Number,
                ExtractionType = "OCR",
                WordCount = CountWords(reconstructed),
                Content = reconstructed,
                TableRows = tableRows,
                RawOcrText = rawOcr,
                OcrConfidence = ocrResult.Confidence,
                SelectedPsm = ocrResult.SelectedPsm,
                RotationDegrees = ocrResult.AppliedRotationDegrees ?? pageRotation,
                ExtractionNotes = ocrResult.Message
            };
        }

        // Preserve any sparse vector words or raw OCR fallback if OCR failed or confidence was empty
        var sparseVectorText = string.Join(" ", fallbackWords.Select(w => w.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var bestFallback = !string.IsNullOrWhiteSpace(ocrResult.RawFallbackText)
            ? ocrResult.RawFallbackText
            : sparseVectorText;

        var tableFallback = string.IsNullOrWhiteSpace(bestFallback)
            ? new List<List<string>>()
            : new List<List<string>> { new() { bestFallback } };

        return new PdfPageContent
        {
            PageNumber = page.Number,
            ExtractionType = "Scanned (No OCR)",
            WordCount = CountWords(bestFallback),
            Content = bestFallback,
            TableRows = tableFallback,
            RawOcrText = ocrResult.RawFallbackText,
            OcrConfidence = ocrResult.Confidence,
            SelectedPsm = ocrResult.SelectedPsm,
            RotationDegrees = ocrResult.AppliedRotationDegrees ?? pageRotation,
            ExtractionNotes = ocrResult.Message
        };
    }

    private static (string ReconstructedText, List<List<string>> TableRows) ReconstructOcrLayout(string rawOcrText)
    {
        var lines = rawOcrText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var tableRows = new List<List<string>>();
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            // Split on 2+ spaces or tabs to preserve tabular OCR columns
            var cells = Regex.Split(line.Trim(), @"\s{2,}|\t+")
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            if (cells.Count == 0) continue;
            tableRows.Add(cells);
            sb.AppendLine(string.Join(" | ", cells));
        }

        return (sb.ToString().Trim(), tableRows);
    }

    private static int CountWords(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
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

        var heights = words.Select(w => w.BoundingBox.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double yTolerance = Math.Clamp(medianHeight * 0.45, 1.0, 4.5);

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

            if (gridRow.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                tableRows.Add(gridRow);
            }
        }

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

        var textBuilder = new StringBuilder();
        foreach (var row in compactRows)
        {
            int lastNonEmpty = row.FindLastIndex(c => !string.IsNullOrWhiteSpace(c));
            if (lastNonEmpty < 0) continue;

            var slice = row.Take(lastNonEmpty + 1);
            textBuilder.AppendLine(string.Join(" | ", slice));
        }

        return (textBuilder.ToString().Trim(), compactRows);
    }
}
