using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using PdfSolverMvc.Models;

namespace PdfSolverMvc.Models.ViewModels;

public class PdfUploadViewModel
{
    [Required(ErrorMessage = "Please select at least one PDF file.")]
    [Display(Name = "PDF Files")]
    public List<IFormFile> Files { get; set; } = new();
}

public class PdfListViewModel
{
    public List<PdfDocumentRecord> Documents { get; set; } = new();
    public string? SearchQuery { get; set; }
    public string? FilterStatus { get; set; }
    public int TotalFiles { get; set; }
    public int TotalPages { get; set; }
    public int OcrProcessedCount { get; set; }
}

public class PdfDetailsViewModel
{
    public PdfDocumentRecord Document { get; set; } = new();
    public List<PdfPageContent> Pages { get; set; } = new();
}
