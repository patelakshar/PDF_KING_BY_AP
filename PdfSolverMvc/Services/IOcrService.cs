namespace PdfSolverMvc.Services;

public record OcrResult(
    bool Success,
    string Text,
    string? Message = null,
    string? RawFallbackText = null,
    float? Confidence = null,
    string? SelectedPsm = null,
    double? AppliedRotationDegrees = null);

public interface IOcrService
{
    bool IsAvailable(out string? statusMessage);
    IReadOnlyList<string> GetConfiguredAvailableLanguages();
    Task<OcrResult> ExtractTextFromPdfPageAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        CancellationToken cancellationToken = default);
}
