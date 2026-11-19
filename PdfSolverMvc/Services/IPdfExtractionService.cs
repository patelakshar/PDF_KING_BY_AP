using PdfSolverMvc.Models;

namespace PdfSolverMvc.Services;

public record PdfExtractionResult(
    int PageCount,
    string RawText,
    List<PdfPageContent> Pages,
    string ExtractionMethod,
    string Status,
    string? Notes);

public interface IPdfExtractionService
{
    Task<PdfExtractionResult> ExtractAsync(
        string pdfFilePath,
        string? imageOutputDirectory = null,
        CancellationToken cancellationToken = default);
}
