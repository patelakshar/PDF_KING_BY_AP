namespace PdfSolverMvc.Services;

public record OcrResult(bool Success, string Text, string? Message = null);

public interface IOcrService
{
    bool IsAvailable(out string? statusMessage);
    Task<OcrResult> ExtractTextFromPdfPageAsync(string pdfFilePath, int zeroBasedPageIndex, CancellationToken cancellationToken = default);
}
