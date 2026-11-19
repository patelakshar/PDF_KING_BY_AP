using System.ComponentModel.DataAnnotations;

namespace PdfSolverMvc.Models;

public class PdfDocumentRecord
{
    public int Id { get; set; }

    [Required]
    [MaxLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(260)]
    public string StoredFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public int PageCount { get; set; }

    /// <summary>
    /// DirectText, OcrFallback, HybridOcr, NeedsOcr, or Failed
    /// </summary>
    [MaxLength(50)]
    public string ExtractionMethod { get; set; } = "DirectText";

    /// <summary>
    /// Processed, PartialOcrNeeded, or Error
    /// </summary>
    [MaxLength(50)]
    public string Status { get; set; } = "Processed";

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;

    public string RawExtractedText { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of per-page extracted content objects.
    /// </summary>
    public string PageTextsJson { get; set; } = "[]";

    public string? CustomPrompt { get; set; }

    public string? AiSolution { get; set; }

    public string? Notes { get; set; }
}

public class PdfPageContent
{
    public int PageNumber { get; set; }
    public string ExtractionType { get; set; } = "Text";
    public int WordCount { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Column-aligned 2D grid of extracted rows and cells (preserving blank cells).
    /// </summary>
    public List<List<string>> TableRows { get; set; } = new();

    /// <summary>
    /// Unmodified raw OCR or raw reading-order text before layout reconstruction.
    /// </summary>
    public string? RawOcrText { get; set; }

    /// <summary>
    /// Mean OCR confidence (0.0 to 1.0) when OCR was used.
    /// </summary>
    public float? OcrConfidence { get; set; }

    /// <summary>
    /// Tesseract PageSegMode that produced the winning OCR result.
    /// </summary>
    public string? SelectedPsm { get; set; }

    /// <summary>
    /// Page rotation in degrees (e.g., 0, 90, 180, 270) or deskew angle.
    /// </summary>
    public double? RotationDegrees { get; set; }

    /// <summary>
    /// Per-page extraction diagnostics (e.g., low-confidence warning, fallback DPI used).
    /// </summary>
    public string? ExtractionNotes { get; set; }
}
