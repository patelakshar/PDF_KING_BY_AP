using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

namespace PdfSolverMvc.Services;

public sealed class ITextPdfService
{
    private readonly ILogger<ITextPdfService> _logger;

    public ITextPdfService(ILogger<ITextPdfService> logger)
    {
        _logger = logger;
    }

    public (bool Valid, int PageCount, string Text, string? Error) Inspect(string pdfFilePath)
    {
        try
        {
            using var reader = new PdfReader(pdfFilePath);
            using var document = new PdfDocument(reader);

            var pageCount = document.GetNumberOfPages();
            var text = new System.Text.StringBuilder();

            for (int pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                text.AppendLine($"--- Page {pageNumber} ---");
                text.AppendLine(PdfTextExtractor.GetTextFromPage(document.GetPage(pageNumber))?.Trim() ?? string.Empty);
            }

            return (true, pageCount, text.ToString().Trim(), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iText PDF inspection failed for {File}", pdfFilePath);
            return (false, 0, string.Empty, ex.Message);
        }
    }

    public IReadOnlyList<string> GetPageTexts(string pdfFilePath)
    {
        var pageTexts = new List<string>();

        try
        {
            using var reader = new PdfReader(pdfFilePath);
            using var document = new PdfDocument(reader);

            for (int pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
            {
                pageTexts.Add(PdfTextExtractor.GetTextFromPage(document.GetPage(pageNumber))?.Trim() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iText per-page text extraction failed for {File}", pdfFilePath);
        }

        return pageTexts;
    }
}
