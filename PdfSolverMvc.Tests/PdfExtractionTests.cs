using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSolverMvc.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PdfSolverMvc.Tests;

public class PdfExtractionTests : IDisposable
{
    private readonly string _tempDir;

    public PdfExtractionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PdfSolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class StubOcrService : IOcrService
    {
        private readonly Func<int, OcrResult> _handler;
        public List<int> InvokedPages { get; } = new();

        public StubOcrService(Func<int, OcrResult> handler) => _handler = handler;

        public bool IsAvailable(out string? statusMessage)
        {
            statusMessage = null;
            return true;
        }

        public IReadOnlyList<string> GetConfiguredAvailableLanguages() => new[] { "eng" };

        public Task<OcrResult> ExtractTextFromPdfPageAsync(
            string pdfFilePath,
            int zeroBasedPageIndex,
            CancellationToken cancellationToken = default)
        {
            InvokedPages.Add(zeroBasedPageIndex);
            return Task.FromResult(_handler(zeroBasedPageIndex));
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PdfSolverMvc.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public StubWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?>? overrides = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Ocr:TessDataPath"] = "tessdata",
            ["Ocr:Language"] = "eng",
            ["Ocr:Dpi"] = "300",
            ["Ocr:MinSelectableCharsPerPage"] = "15"
        };

        if (overrides != null)
        {
            foreach (var kv in overrides) defaults[kv.Key] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    [Fact]
    public async Task VectorPdf_WithSparseTable_PreservesBlankColumnsAndMetadata()
    {
        var pdfPath = Path.Combine(_tempDir, "vector_table.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        // Header row: Item | DN15 | DN20 | DN25
        page.AddText("Item", 10, new PdfPoint(50, 750), font);
        page.AddText("DN15", 10, new PdfPoint(200, 750), font);
        page.AddText("DN20", 10, new PdfPoint(280, 750), font);
        page.AddText("DN25", 10, new PdfPoint(360, 750), font);

        // Sparse row: Valve | (blank DN15) | (blank DN20) | 0651 under DN25
        page.AddText("Valve", 10, new PdfPoint(50, 720), font);
        page.AddText("0651", 10, new PdfPoint(360, 720), font);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(false, string.Empty));
        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);

        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("DirectText", result.ExtractionMethod);
        Assert.Equal(1, result.PageCount);
        Assert.Empty(ocr.InvokedPages);
        Assert.True(result.Pages[0].TableRows.Count >= 2);
        var sparseRow = result.Pages[0].TableRows[1];
        Assert.Equal("Valve", sparseRow[0]);
        Assert.Equal("0651", sparseRow[^1]);
    }

    [Fact]
    public async Task MixedPdf_RoutesVectorAndScannedPagesSeparately()
    {
        var pdfPath = Path.Combine(_tempDir, "mixed.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var p1 = builder.AddPage(PageSize.A4);
        p1.AddText("Searchable Engineering Specification Header", 12, new PdfPoint(50, 750), font);

        // Page 2 has no selectable text (simulating scanned image page)
        builder.AddPage(PageSize.A4);

        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(idx => new OcrResult(
            Success: true,
            Text: "Pump Model  SBR200/8\nRated Flow  718.7 m3/h",
            Confidence: 0.87f,
            SelectedPsm: "Auto",
            AppliedRotationDegrees: 0));

        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);
        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("HybridOcr", result.ExtractionMethod);
        Assert.Equal(2, result.PageCount);
        Assert.Single(ocr.InvokedPages);
        Assert.Equal(1, ocr.InvokedPages[0]);
        Assert.Equal(0.87f, result.Pages[1].OcrConfidence);
        Assert.Equal("Auto", result.Pages[1].SelectedPsm);
        Assert.Contains("SBR200/8", result.Pages[1].RawOcrText);
    }

    [Fact]
    public async Task LowQualityOrHandwrittenPage_PreservesFallbackTextAndConfidenceNote()
    {
        var pdfPath = Path.Combine(_tempDir, "low_quality.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        File.WriteAllBytes(pdfPath, builder.Build());

        var ocr = new StubOcrService(_ => new OcrResult(
            Success: true,
            Text: "Rough handwritten note 104-J/JA",
            Message: "Low OCR confidence (38%); best-effort result.",
            RawFallbackText: "Rough handwritten note 104-J/JA",
            Confidence: 0.38f,
            SelectedPsm: "SparseText",
            AppliedRotationDegrees: 90));

        var service = new PdfExtractionService(ocr, BuildConfig(), NullLogger<PdfExtractionService>.Instance);
        var result = await service.ExtractAsync(pdfPath);

        Assert.Equal("OcrFallback", result.ExtractionMethod);
        Assert.Equal(0.38f, result.Pages[0].OcrConfidence);
        Assert.Equal("SparseText", result.Pages[0].SelectedPsm);
        Assert.Equal(90, result.Pages[0].RotationDegrees);
        Assert.Contains("Low OCR confidence", result.Pages[0].ExtractionNotes);
    }

    [Fact]
    public void OcrService_ResolvesConfiguredAvailableLanguagesOnly()
    {
        var tessDir = Path.Combine(_tempDir, "tessdata");
        Directory.CreateDirectory(tessDir);
        File.WriteAllBytes(Path.Combine(tessDir, "eng.traineddata"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(tessDir, "deu.traineddata"), new byte[] { 1, 2, 3 });

        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ocr:TessDataPath"] = tessDir,
            ["Ocr:Language"] = "eng",
            ["Ocr:AdditionalLanguages:0"] = "deu",
            ["Ocr:AdditionalLanguages:1"] = "fra" // missing traineddata should be skipped safely
        });

        var ocr = new OcrService(config, new StubWebHostEnvironment(_tempDir), NullLogger<OcrService>.Instance);
        var langs = ocr.GetConfiguredAvailableLanguages();

        Assert.Equal(new[] { "eng", "deu" }, langs);
        Assert.True(ocr.IsAvailable(out var msg));
        Assert.Null(msg);
    }
}
