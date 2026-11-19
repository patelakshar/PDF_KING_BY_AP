using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfSolverMvc.Data;
using PdfSolverMvc.Models;
using PdfSolverMvc.Models.ViewModels;
using PdfSolverMvc.Services;

namespace PdfSolverMvc.Controllers;

public class PdfController : Controller
{
    private readonly AppDbContext _db;
    private readonly IPdfExtractionService _extractionService;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfController> _logger;

    public PdfController(
        AppDbContext db,
        IPdfExtractionService extractionService,
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<PdfController> logger)
    {
        _db = db;
        _extractionService = extractionService;
        _env = env;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetUploadFolder()
    {
        var configured = _configuration["Storage:UploadFolder"] ?? "UploadedPdfs";
        var folder = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_env.ContentRootPath, configured);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // PAGE 1: PDF Upload
    [HttpGet]
    public IActionResult Upload()
    {
        return View(new PdfUploadViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(PdfUploadViewModel model, CancellationToken cancellationToken)
    {
        if (model.Files == null || model.Files.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Files), "Please select at least one PDF file.");
            return View(model);
        }

        var uploadFolder = GetUploadFolder();
        var createdIds = new List<int>();
        var warnings = new List<string>();

        foreach (var file in model.Files)
        {
            if (file.Length == 0) continue;

            if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped '{file.FileName}' because it is not a .pdf file.");
                continue;
            }

            var storedFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}";
            var fullPath = Path.Combine(uploadFolder, storedFileName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var extraction = await _extractionService.ExtractAsync(fullPath, cancellationToken);

            var record = new PdfDocumentRecord
            {
                OriginalFileName = Path.GetFileName(file.FileName),
                StoredFileName = storedFileName,
                FileSizeBytes = file.Length,
                PageCount = extraction.PageCount,
                ExtractionMethod = extraction.ExtractionMethod,
                Status = extraction.Status,
                UploadedAtUtc = DateTime.UtcNow,
                RawExtractedText = extraction.RawText,
                PageTextsJson = JsonSerializer.Serialize(extraction.Pages),
                Notes = extraction.Notes
            };

            _db.PdfDocuments.Add(record);
            await _db.SaveChangesAsync(cancellationToken);
            createdIds.Add(record.Id);
        }

        if (createdIds.Count == 0)
        {
            ModelState.AddModelError(string.Empty, warnings.Count > 0 ? string.Join(" ", warnings) : "No valid PDF files were uploaded.");
            return View(model);
        }

        if (warnings.Count > 0)
        {
            TempData["Warning"] = string.Join(" ", warnings);
        }

        if (createdIds.Count == 1)
        {
            return RedirectToAction(nameof(Details), new { id = createdIds[0] });
        }

        TempData["Success"] = $"Successfully processed {createdIds.Count} PDF files.";
        return RedirectToAction(nameof(Index));
    }

    // PAGE 2: List Information
    [HttpGet]
    public async Task<IActionResult> Index(string? searchQuery, string? filterStatus, CancellationToken cancellationToken)
    {
        var allDocs = await _db.PdfDocuments
            .OrderByDescending(d => d.UploadedAtUtc)
            .ToListAsync(cancellationToken);

        var filtered = allDocs.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            filtered = filtered.Where(d =>
                d.OriginalFileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.RawExtractedText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filterStatus) && !filterStatus.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(d =>
                d.Status.Equals(filterStatus, StringComparison.OrdinalIgnoreCase) ||
                d.ExtractionMethod.Equals(filterStatus, StringComparison.OrdinalIgnoreCase));
        }

        var vm = new PdfListViewModel
        {
            Documents = filtered.ToList(),
            SearchQuery = searchQuery,
            FilterStatus = filterStatus ?? "All",
            TotalFiles = allDocs.Count,
            TotalPages = allDocs.Sum(d => d.PageCount),
            OcrProcessedCount = allDocs.Count(d => d.ExtractionMethod.Contains("Ocr", StringComparison.OrdinalIgnoreCase))
        };

        return View(vm);
    }

    // PAGE 3: Web Page / PDF Content
    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var doc = await _db.PdfDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc == null)
        {
            return NotFound();
        }

        List<PdfPageContent> pages;
        try
        {
            pages = JsonSerializer.Deserialize<List<PdfPageContent>>(doc.PageTextsJson) ?? new List<PdfPageContent>();
        }
        catch
        {
            pages = new List<PdfPageContent>();
        }

        var vm = new PdfDetailsViewModel
        {
            Document = doc,
            Pages = pages
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> ExportText(int id, CancellationToken cancellationToken)
    {
        var doc = await _db.PdfDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc == null)
        {
            return NotFound();
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(doc.RawExtractedText ?? string.Empty)).ToArray();
        var txtName = $"{Path.GetFileNameWithoutExtension(doc.OriginalFileName)}_extracted.txt";
        return File(bytes, "text/plain; charset=utf-8", txtName);
    }

    [HttpGet]
    public async Task<IActionResult> ExportCsv(int id, CancellationToken cancellationToken)
    {
        var doc = await _db.PdfDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc == null)
        {
            return NotFound();
        }

        List<PdfPageContent> pages;
        try
        {
            pages = JsonSerializer.Deserialize<List<PdfPageContent>>(doc.PageTextsJson) ?? new List<PdfPageContent>();
        }
        catch
        {
            pages = new List<PdfPageContent>();
        }

        var csv = new StringBuilder();

        foreach (var page in pages)
        {
            csv.AppendLine($"\"--- Page {page.PageNumber} ---\"");

            if (page.TableRows != null && page.TableRows.Count > 0)
            {
                foreach (var row in page.TableRows)
                {
                    var escaped = row.Select(cell => $"\"{(cell ?? string.Empty).Replace("\"", "\"\"")}\"");
                    csv.AppendLine(string.Join(",", escaped));
                }
            }
            else
            {
                var lines = (page.Content ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var cols = line.Split(" | ", StringSplitOptions.None);
                    var escaped = cols.Select(cell => $"\"{cell.Trim().Replace("\"", "\"\"")}\"");
                    csv.AppendLine(string.Join(",", escaped));
                }
            }

            csv.AppendLine();
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var csvName = $"{Path.GetFileNameWithoutExtension(doc.OriginalFileName)}_extracted.csv";
        return File(bytes, "text/csv", csvName);
    }

    [HttpGet]
    public async Task<IActionResult> ViewFile(int id, bool download = false, CancellationToken cancellationToken = default)
    {
        var doc = await _db.PdfDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc == null)
        {
            return NotFound();
        }

        var fullPath = Path.Combine(GetUploadFolder(), doc.StoredFileName);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound("Stored PDF file was not found on disk.");
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(fullPath, cancellationToken);
        return download
            ? File(bytes, "application/pdf", doc.OriginalFileName)
            : File(bytes, "application/pdf");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var doc = await _db.PdfDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc != null)
        {
            var fullPath = Path.Combine(GetUploadFolder(), doc.StoredFileName);
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }

            _db.PdfDocuments.Remove(doc);
            await _db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = $"Deleted '{doc.OriginalFileName}'.";
        }

        return RedirectToAction(nameof(Index));
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return Content("An unexpected error occurred while processing your request.");
    }
}
