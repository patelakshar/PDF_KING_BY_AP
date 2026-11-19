using System.Text;
using System.Text.RegularExpressions;
using PdfSolverMvc.Models;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfSolverMvc.Services;

public class PdfExtractionService : IPdfExtractionService
{
    private readonly IOcrService _ocrService;
    private readonly ITextPdfService _iTextPdfService;
    private readonly int _minSelectableCharsPerPage;
    private readonly ILogger<PdfExtractionService> _logger;

    public PdfExtractionService(
        IOcrService ocrService,
        ITextPdfService iTextPdfService,
        IConfiguration configuration,
        ILogger<PdfExtractionService> logger)
    {
        _ocrService = ocrService;
        _iTextPdfService = iTextPdfService;
        _minSelectableCharsPerPage = Math.Max(1, configuration.GetValue<int?>("Ocr:MinSelectableCharsPerPage") ?? 15);
        _logger = logger;
    }

    private sealed record WordBox(double Left, double Right, double Bottom, double Top, double Height, double Width, string Text);
    private sealed record CellToken(double Left, double Right, double CenterX, string Text);

    private sealed class ColumnCluster
    {
        public double MinLeft { get; set; }
        public double MaxRight { get; set; }
        public double CenterX => (MinLeft + MaxRight) / 2.0;
    }

    public async Task<PdfExtractionResult> ExtractAsync(
        string pdfFilePath,
        string? imageOutputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<PdfPageContent>();
        var fullTextBuilder = new StringBuilder();
        var notes = new List<string>();
        int vectorPages = 0;
        int ocrPages = 0;
        int emptyScannedPages = 0;

        if (!string.IsNullOrWhiteSpace(imageOutputDirectory))
        {
            Directory.CreateDirectory(imageOutputDirectory);
        }

        try
        {
            var iTextInspection = _iTextPdfService.Inspect(pdfFilePath);
            if (!iTextInspection.Valid)
            {
                return new PdfExtractionResult(
                    0,
                    string.Empty,
                    pages,
                    "Failed",
                    "Error",
                    iTextInspection.Error == null
                        ? "The PDF could not be opened."
                        : $"PDF validation error: {iTextInspection.Error}");
            }

            var sourcePageTexts = _iTextPdfService.GetPageTexts(pdfFilePath);

            using var document = PdfDocument.Open(pdfFilePath, new ParsingOptions { UseLenientParsing = true });
            int pageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var streamWords = BuildStreamOrderedWords(page.Letters);
                int selectableChars = streamWords.Sum(w => w.Text.Length);
                double pageRotation = page.Rotation.Value;

                var extractedImages = ExtractPageImages(page, streamWords, imageOutputDirectory);

                PdfPageContent pageContent;

                if (selectableChars >= _minSelectableCharsPerPage)
                {
                    pageContent = ExtractVectorPage(page, streamWords, pageRotation);
                    vectorPages++;
                }
                else
                {
                    pageContent = await ExtractScannedPageAsync(pdfFilePath, page, streamWords, pageRotation, cancellationToken);
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

                var sourceText = page.Number <= sourcePageTexts.Count
                    ? sourcePageTexts[page.Number - 1]
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(sourceText) && selectableChars >= _minSelectableCharsPerPage)
                {
                    var accuracy = CalculateAccuracy(sourceText, pageContent.Content);
                    pageContent.CharacterAccuracy = accuracy.CharacterAccuracy;
                    pageContent.WordAccuracy = accuracy.WordAccuracy;
                    pageContent.MissingWordCount = accuracy.MissingWords;
                    pageContent.ExtraWordCount = accuracy.ExtraWords;
                    pageContent.GroundTruthAvailable = true;
                    pageContent.AccuracyStatus = accuracy.CharacterAccuracy >= 98
                        ? "Excellent"
                        : accuracy.CharacterAccuracy >= 90
                            ? "Good"
                            : "Review Needed";
                }
                else if (!pageContent.ExtractionType.StartsWith("OCR", StringComparison.OrdinalIgnoreCase))
                {
                    pageContent.GroundTruthAvailable = false;
                    pageContent.AccuracyStatus = "Visual Review Required";
                }

                pageContent.Images = extractedImages;
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

    /// <summary>
    /// Groups PDF letters in their original content-stream order instead of spatial nearest-neighbor,
    /// preventing overlapping columns from interleaving characters backwards (e.g. "46retaw").
    /// </summary>
    private static List<WordBox> BuildStreamOrderedWords(IReadOnlyList<Letter> letters)
    {
        var words = new List<WordBox>();
        if (letters.Count == 0) return words;

        var currentText = new StringBuilder();
        double left = 0, right = 0, bottom = 0, top = 0;
        Letter? prev = null;

        void Flush()
        {
            if (currentText.Length == 0) return;
            var cleaned = NormalizeSpaces(currentText.ToString());
            if (!string.IsNullOrEmpty(cleaned))
            {
                words.Add(new WordBox(
                    left,
                    right,
                    bottom,
                    top,
                    Math.Max(1.0, top - bottom),
                    Math.Max(1.0, right - left),
                    cleaned));
            }
            currentText.Clear();
        }

        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                Flush();
                prev = null;
                continue;
            }

            var box = letter.GlyphRectangle;
            if (prev == null)
            {
                currentText.Append(letter.Value);
                left = box.Left;
                right = box.Right;
                bottom = box.Bottom;
                top = box.Top;
                prev = letter;
                continue;
            }

            var prevBox = prev.GlyphRectangle;
            double charHeight = Math.Max(4.0, Math.Max(prevBox.Height, box.Height));
            double avgCharWidth = Math.Max(2.0, prevBox.Width);
            double dy = Math.Abs(box.Bottom - prevBox.Bottom);
            double dx = box.Left - prevBox.Right;

            // Keep in the same word only if on the same baseline and advancing forward within normal kerning
            bool sameWord = dy <= charHeight * 0.45 &&
                            dx >= -avgCharWidth * 0.4 &&
                            dx <= avgCharWidth * 0.65;

            if (sameWord)
            {
                currentText.Append(letter.Value);
                left = Math.Min(left, box.Left);
                right = Math.Max(right, box.Right);
                bottom = Math.Min(bottom, box.Bottom);
                top = Math.Max(top, box.Top);
            }
            else
            {
                Flush();
                currentText.Append(letter.Value);
                left = box.Left;
                right = box.Right;
                bottom = box.Bottom;
                top = box.Top;
            }

            prev = letter;
        }

        Flush();
        return words;
    }

    private static string NormalizeSpaces(string input)
    {
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    private List<PdfExtractedImage> ExtractPageImages(
        Page page,
        List<WordBox> pageWords,
        string? imageOutputDirectory)
    {
        var results = new List<PdfExtractedImage>();
        if (string.IsNullOrWhiteSpace(imageOutputDirectory))
        {
            return results;
        }

        int index = 0;
        foreach (var pdfImage in page.GetImages())
        {
            index++;
            try
            {
                byte[]? pngBytes = null;
                if (pdfImage.TryGetPng(out var directPng) && directPng is { Length: > 0 })
                {
                    pngBytes = directPng;
                }
                else if (pdfImage.TryGetBytesAsMemory(out var rawMemory) && !rawMemory.IsEmpty)
                {
                    using var decoded = SKBitmap.Decode(rawMemory.ToArray());
                    if (decoded != null)
                    {
                        using var encoded = decoded.Encode(SKEncodedImageFormat.Png, 100);
                        pngBytes = encoded.ToArray();
                    }
                }

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    continue;
                }

                var fileName = $"page{page.Number}_img{index}.png";
                var fullPath = Path.Combine(imageOutputDirectory, fileName);
                File.WriteAllBytes(fullPath, pngBytes);

                var caption = FindNearbyCaption(pdfImage.Bounds, pageWords);

                results.Add(new PdfExtractedImage
                {
                    ImageId = $"P{page.Number}-IMG{index}",
                    PageNumber = page.Number,
                    RelativeFileName = fileName,
                    WidthPx = pdfImage.WidthInSamples,
                    HeightPx = pdfImage.HeightInSamples,
                    BoundsLeft = Math.Round(pdfImage.Bounds.Left, 1),
                    BoundsBottom = Math.Round(pdfImage.Bounds.Bottom, 1),
                    BoundsWidth = Math.Round(pdfImage.Bounds.Width, 1),
                    BoundsHeight = Math.Round(pdfImage.Bounds.Height, 1),
                    NearbyCaption = caption
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not export embedded image {Index} on page {Page}", index, page.Number);
            }
        }

        return results;
    }

    private static string? FindNearbyCaption(PdfRectangle bounds, List<WordBox> words)
    {
        if (words.Count == 0) return null;

        double imgCenterX = (bounds.Left + bounds.Right) / 2.0;

        var candidateWords = words
            .Where(w =>
            {
                bool horizontallyAligned = w.Right >= bounds.Left - 40 &&
                                           w.Left <= bounds.Right + 40;
                double distBelow = bounds.Bottom - w.Top;
                double distAbove = w.Bottom - bounds.Top;
                bool nearBelow = distBelow >= -5 && distBelow <= 55;
                bool nearAbove = distAbove >= -5 && distAbove <= 40;
                return horizontallyAligned && (nearBelow || nearAbove);
            })
            .OrderByDescending(w => w.Bottom)
            .ThenBy(w => w.Left)
            .ToList();

        if (candidateWords.Count == 0) return null;

        var lines = new List<List<WordBox>>();
        foreach (var w in candidateWords)
        {
            var row = lines.FirstOrDefault(r => Math.Abs(r[0].Bottom - w.Bottom) <= 4.0);
            if (row != null) row.Add(w);
            else lines.Add(new List<WordBox> { w });
        }

        var bestLine = lines
            .OrderBy(line =>
            {
                double lineY = line.Average(w => w.Bottom);
                double lineX = line.Average(w => (w.Left + w.Right) / 2.0);
                double dy = lineY < bounds.Bottom
                    ? (bounds.Bottom - lineY)
                    : (lineY - bounds.Top) + 15;
                return dy + Math.Abs(lineX - imgCenterX) * 0.1;
            })
            .FirstOrDefault();

        if (bestLine == null) return null;
        var text = NormalizeSpaces(string.Join(" ", bestLine.OrderBy(w => w.Left).Select(w => w.Text)));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static PdfPageContent ExtractVectorPage(Page page, List<WordBox> streamWords, double pageRotation)
    {
        var (alignedText, tableRows) = ExtractColumnAlignedGrid(streamWords);

        var nnWords = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(nnWords);
        var orderedBlocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
        var readingOrderText = string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedBlocks.Select(b => NormalizeSpaces(b.Text)).Where(t => !string.IsNullOrWhiteSpace(t)));

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
        List<WordBox> fallbackWords,
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

        var sparseVectorText = NormalizeSpaces(string.Join(" ", fallbackWords.Select(w => w.Text)));
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
            var cells = Regex.Split(line.Trim(), @"\s{2,}|\t+")
                .Select(NormalizeSpaces)
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

    private static (string AlignedText, List<List<string>> TableRows) ExtractColumnAlignedGrid(List<WordBox> words)
    {
        if (words.Count == 0)
        {
            return (string.Empty, new List<List<string>>());
        }

        var heights = words.Select(w => w.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 8.0;
        double yTolerance = Math.Clamp(medianHeight * 0.45, 1.0, 4.5);

        var sortedByY = words.OrderByDescending(w => w.Bottom).ToList();
        var rawRows = new List<List<WordBox>>();

        foreach (var word in sortedByY)
        {
            var matchingRow = rawRows.FirstOrDefault(r =>
                Math.Abs(r[0].Bottom - word.Bottom) <= yTolerance);

            if (matchingRow != null)
            {
                matchingRow.Add(word);
            }
            else
            {
                rawRows.Add(new List<WordBox> { word });
            }
        }

        var rowTokens = new List<List<CellToken>>();
        foreach (var row in rawRows)
        {
            var ordered = row.OrderBy(w => w.Left).ToList();
            var cells = new List<CellToken>();

            var currentText = new StringBuilder(ordered[0].Text);
            double cellLeft = ordered[0].Left;
            double cellRight = ordered[0].Right;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var curr = ordered[i];
                double gap = curr.Left - prev.Right;
                double avgCharWidth = Math.Max(2.2, prev.Width / Math.Max(1, prev.Text.Length));

                // Do NOT merge overlapping text runs (gap < -1.5) from colliding columns
                if (gap >= -1.5 && gap <= Math.Max(4.5, avgCharWidth * 1.9))
                {
                    currentText.Append(' ').Append(curr.Text);
                    cellRight = Math.Max(cellRight, curr.Right);
                }
                else
                {
                    cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));
                    currentText.Clear();
                    currentText.Append(curr.Text);
                    cellLeft = curr.Left;
                    cellRight = curr.Right;
                }
            }

            cells.Add(new CellToken(cellLeft, cellRight, (cellLeft + cellRight) / 2.0, NormalizeSpaces(currentText.ToString())));
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
                    : NormalizeSpaces($"{gridRow[bestCol]} {cell.Text}");
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

    private static double LevenshteinSimilarity(string source, string extracted)
    {
        var a = NormalizeComparisonText(source);
        var b = NormalizeComparisonText(extracted);

        if (a.Length == 0 && b.Length == 0) return 100.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        int distance = previous[b.Length];
        return Math.Round((1.0 - (double)distance / Math.Max(a.Length, b.Length)) * 100.0, 2);
    }

    private static string NormalizeComparisonText(string text)
    {
        return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
    }

    private static (double CharacterAccuracy, double WordAccuracy, int MissingWords, int ExtraWords) CalculateAccuracy(string source, string extracted)
    {
        var characterAccuracy = LevenshteinSimilarity(source, extracted);

        var sourceWords = Regex.Split(NormalizeComparisonText(source).ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0).ToList();
        var extractedWords = Regex.Split(NormalizeComparisonText(extracted).ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0).ToList();

        var matched = new bool[sourceWords.Count];
        var extra = 0;

        foreach (var extractedWord in extractedWords)
        {
            int matchIndex = -1;
            for (int i = 0; i < sourceWords.Count; i++)
            {
                if (!matched[i] && sourceWords[i] == extractedWord)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0) matched[matchIndex] = true;
            else extra++;
        }

        int missing = matched.Count(m => !m);
        int matchedCount = matched.Count(m => m);
        double wordAccuracy = sourceWords.Count == 0
            ? (extractedWords.Count == 0 ? 100.0 : 0.0)
            : Math.Round((double)matchedCount / sourceWords.Count * 100.0, 2);

        return (characterAccuracy, wordAccuracy, missing, extra);
    }
}
