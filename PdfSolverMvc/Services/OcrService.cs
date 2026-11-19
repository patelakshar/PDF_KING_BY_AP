using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace PdfSolverMvc.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "PDFtoImage and native SkiaSharp dependencies are installed in the Linux Docker image.")]
public class OcrService : IOcrService
{
    private readonly string _tessDataPath;
    private readonly string _language;
    private readonly ILogger<OcrService> _logger;

    public OcrService(IConfiguration configuration, IWebHostEnvironment env, ILogger<OcrService> logger)
    {
        var configuredPath = configuration["Ocr:TessDataPath"] ?? "tessdata";
        _tessDataPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(env.ContentRootPath, configuredPath);
        _language = configuration["Ocr:Language"] ?? "eng";
        _logger = logger;
    }

    public bool IsAvailable(out string? statusMessage)
    {
        var trainedDataFile = Path.Combine(_tessDataPath, $"{_language}.traineddata");
        if (!Directory.Exists(_tessDataPath) || !File.Exists(trainedDataFile))
        {
            statusMessage = $"OCR traineddata not found at '{trainedDataFile}'. Add '{_language}.traineddata' to enable scanned PDF OCR.";
            return false;
        }

        statusMessage = null;
        return true;
    }

    public Task<OcrResult> ExtractTextFromPdfPageAsync(
        string pdfFilePath,
        int zeroBasedPageIndex,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!IsAvailable(out var statusMessage))
            {
                return new OcrResult(false, string.Empty, statusMessage);
            }

            try
            {
                using var pdfStream = File.OpenRead(pdfFilePath);
                using var bitmap = Conversion.ToImage(pdfStream, page: new Index(zeroBasedPageIndex), options: new RenderOptions(Dpi: 250));
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                var pngBytes = data.ToArray();

                using var engine = new TesseractEngine(_tessDataPath, _language, EngineMode.Default);
                using var pix = Pix.LoadFromMemory(pngBytes);
                using var page = engine.Process(pix);
                var text = page.GetText()?.Trim() ?? string.Empty;

                return new OcrResult(true, text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR failed for {File} page {Page}", pdfFilePath, zeroBasedPageIndex + 1);
                return new OcrResult(false, string.Empty, ex.Message);
            }
        }, cancellationToken);
    }
}
