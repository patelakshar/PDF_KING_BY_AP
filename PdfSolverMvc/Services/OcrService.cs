using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace PdfSolverMvc.Services;

public class OcrService : IOcrService
{
    private readonly string _tessDataPath;
    private readonly string _primaryLanguage;
    private readonly List<string> _additionalLanguages;
    private readonly int _dpi;
    private readonly int _fallbackDpi;
    private readonly bool _enablePreprocessing;
    private readonly bool _convertToGrayscale;
    private readonly bool _normalizeContrast;
    private readonly bool _removeNoise;
    private readonly bool _deskew;
    private readonly bool _autoRotate;
    private readonly bool _upscaleSmallText;
    private readonly int _minUpscaleWidth;
    private readonly List<PageSegMode> _psmModes;
    private readonly float _minAcceptableConfidence;
    private readonly ILogger<OcrService> _logger;

    public OcrService(IConfiguration configuration, IWebHostEnvironment env, ILogger<OcrService> logger)
    {
        var configuredPath = configuration["Ocr:TessDataPath"] ?? "tessdata";
        _tessDataPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(env.ContentRootPath, configuredPath);

        _primaryLanguage = configuration["Ocr:Language"] ?? "eng";
        _additionalLanguages = configuration.GetSection("Ocr:AdditionalLanguages").Get<List<string>>() ?? new List<string>();
        _dpi = Math.Clamp(configuration.GetValue<int?>("Ocr:Dpi") ?? 300, 150, 600);
        _fallbackDpi = Math.Clamp(configuration.GetValue<int?>("Ocr:FallbackDpi") ?? 400, _dpi, 600);
        _enablePreprocessing = configuration.GetValue<bool?>("Ocr:EnablePreprocessing") ?? true;
        _convertToGrayscale = configuration.GetValue<bool?>("Ocr:ConvertToGrayscale") ?? true;
        _normalizeContrast = configuration.GetValue<bool?>("Ocr:NormalizeContrast") ?? true;
        _removeNoise = configuration.GetValue<bool?>("Ocr:RemoveNoise") ?? true;
        _deskew = configuration.GetValue<bool?>("Ocr:Deskew") ?? true;
        _autoRotate = configuration.GetValue<bool?>("Ocr:AutoRotate") ?? true;
        _upscaleSmallText = configuration.GetValue<bool?>("Ocr:UpscaleSmallText") ?? true;
        _minUpscaleWidth = Math.Max(800, configuration.GetValue<int?>("Ocr:MinUpscaleWidth") ?? 1600);
        _minAcceptableConfidence = Math.Clamp(configuration.GetValue<float?>("Ocr:MinAcceptableConfidence") ?? 0.45f, 0.1f, 0.95f);

        var psmStrings = configuration.GetSection("Ocr:PageSegmentationModes").Get<List<string>>()
            ?? new List<string> { "Auto", "SingleBlock", "SparseText" };

        _psmModes = psmStrings
            .Select(s => Enum.TryParse<PageSegMode>(s, true, out var mode) ? (PageSegMode?)mode : null)
            .Where(m => m.HasValue)
            .Select(m => m!.Value)
            .Distinct()
            .ToList();

        if (_psmModes.Count == 0)
        {
            _psmModes.Add(PageSegMode.Auto);
        }

        _logger = logger;
    }

    public IReadOnlyList<string> GetConfiguredAvailableLanguages()
    {
        var requested = _primaryLanguage
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(_additionalLanguages.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Directory.Exists(_tessDataPath))
        {
            return Array.Empty<string>();
        }

        return requested
            .Where(lang => File.Exists(Path.Combine(_tessDataPath, $"{lang}.traineddata")))
            .ToList();
    }

    public bool IsAvailable(out string? statusMessage)
    {
        var available = GetConfiguredAvailableLanguages();
        if (available.Count == 0)
        {
            var expected = Path.Combine(_tessDataPath, $"{_primaryLanguage}.traineddata");
            statusMessage = $"OCR traineddata not found at '{expected}'. Add '{_primaryLanguage}.traineddata' to enable scanned PDF OCR.";
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

            var effectiveLang = string.Join("+", GetConfiguredAvailableLanguages());

            try
            {
                // First pass at configured DPI
                var firstPass = RunOcrAtDpi(pdfFilePath, zeroBasedPageIndex, effectiveLang, _dpi, cancellationToken);

                // If confidence is low or text is sparse, retry at higher fallback DPI (e.g. 400 DPI)
                if (_fallbackDpi > _dpi &&
                    (!firstPass.Success || (firstPass.Confidence ?? 0f) < _minAcceptableConfidence || firstPass.Text.Length < 25))
                {
                    var secondPass = RunOcrAtDpi(pdfFilePath, zeroBasedPageIndex, effectiveLang, _fallbackDpi, cancellationToken);
                    if (IsBetterResult(secondPass, firstPass))
                    {
                        var note = $"Used fallback {_fallbackDpi} DPI (confidence {(secondPass.Confidence ?? 0f):P0}).";
                        return secondPass with
                        {
                            Message = string.IsNullOrWhiteSpace(secondPass.Message) ? note : $"{secondPass.Message} | {note}",
                            RawFallbackText = string.IsNullOrWhiteSpace(firstPass.Text) ? secondPass.RawFallbackText : firstPass.Text
                        };
                    }
                }

                return firstPass;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR failed for {File} page {Page}", pdfFilePath, zeroBasedPageIndex + 1);
                return new OcrResult(false, string.Empty, ex.Message);
            }
        }, cancellationToken);
    }

    private OcrResult RunOcrAtDpi(
        string pdfFilePath,
        int zeroBasedPageIndex,
        string language,
        int dpi,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var pdfStream = File.OpenRead(pdfFilePath);
#pragma warning disable CA1416 // PDFtoImage supports the installed Linux/macOS/Windows native runtimes.
        using var rawBitmap = Conversion.ToImage(pdfStream, page: new Index(zeroBasedPageIndex), options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416

        using var engine = new TesseractEngine(_tessDataPath, language, EngineMode.Default);

        // Evaluate 0° first (preprocessed)
        var bestCandidate = EvaluateBitmapWithModes(rawBitmap, engine, 0, cancellationToken);
        string? rawFallback = bestCandidate.Text;

        // If auto-rotate is enabled and initial confidence is below threshold, test 90°, 180°, 270°
        if (_autoRotate && ((bestCandidate.Confidence ?? 0f) < _minAcceptableConfidence || bestCandidate.Text.Length < 20))
        {
            foreach (var angle in new[] { 90, 180, 270 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var rotated = RotateBitmap(rawBitmap, angle);
                var candidate = EvaluateBitmapWithModes(rotated, engine, angle, cancellationToken);
                if (IsBetterResult(candidate, bestCandidate))
                {
                    bestCandidate = candidate;
                }
            }
        }

        return bestCandidate with
        {
            RawFallbackText = string.IsNullOrWhiteSpace(rawFallback) ? bestCandidate.Text : rawFallback
        };
    }

    private OcrResult EvaluateBitmapWithModes(
        SKBitmap sourceBitmap,
        TesseractEngine engine,
        double baseRotationDegrees,
        CancellationToken cancellationToken)
    {
        double deskewAngle = 0;
        var processed = _enablePreprocessing
            ? PreprocessBitmap(sourceBitmap, out deskewAngle)
            : sourceBitmap.Copy();
        using var processedImage = processed;

        using var data = processedImage.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = data.ToArray();
        using var pix = Pix.LoadFromMemory(pngBytes);

        OcrResult? best = null;
        string? fallbackText = null;

        foreach (var psm in _psmModes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var page = engine.Process(pix, psm);
                var text = page.GetText()?.Trim() ?? string.Empty;
                var confidence = page.GetMeanConfidence();

                if (fallbackText == null && !string.IsNullOrWhiteSpace(text))
                {
                    fallbackText = text;
                }

                var candidate = new OcrResult(
                    Success: !string.IsNullOrWhiteSpace(text),
                    Text: text,
                    Message: confidence < _minAcceptableConfidence && !string.IsNullOrWhiteSpace(text)
                        ? $"Low OCR confidence ({confidence:P0}); best-effort result."
                        : null,
                    RawFallbackText: fallbackText,
                    Confidence: confidence,
                    SelectedPsm: psm.ToString(),
                    AppliedRotationDegrees: baseRotationDegrees + deskewAngle);

                if (best == null || IsBetterResult(candidate, best))
                {
                    best = candidate;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tesseract failed for PSM {Psm}", psm);
            }
        }

        return best ?? new OcrResult(false, string.Empty, "No text recognized by OCR.");
    }

    private static bool IsBetterResult(OcrResult candidate, OcrResult currentBest)
    {
        if (!candidate.Success && currentBest.Success) return false;
        if (candidate.Success && !currentBest.Success) return true;

        int candChars = candidate.Text.Count(c => !char.IsWhiteSpace(c));
        int bestChars = currentBest.Text.Count(c => !char.IsWhiteSpace(c));
        float candConf = candidate.Confidence ?? 0f;
        float bestConf = currentBest.Confidence ?? 0f;

        // Score balances confidence and meaningful character yield
        double candScore = candConf * Math.Log2(Math.Max(2, candChars));
        double bestScore = bestConf * Math.Log2(Math.Max(2, bestChars));
        return candScore > bestScore;
    }

    private SKBitmap PreprocessBitmap(SKBitmap source, out double deskewAngle)
    {
        deskewAngle = 0;

        // 1. Upscale small text if image width is below threshold
        SKBitmap working;
        if (_upscaleSmallText && source.Width < _minUpscaleWidth)
        {
            double scale = (double)_minUpscaleWidth / source.Width;
            int targetW = _minUpscaleWidth;
            int targetH = Math.Max(1, (int)Math.Round(source.Height * scale));
            working = new SKBitmap(targetW, targetH, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(working);
            canvas.DrawBitmap(source, new SKRect(0, 0, targetW, targetH));
        }
        else
        {
            working = source.Copy();
        }

        int width = working.Width;
        int height = working.Height;
        var pixels = working.Pixels;
        var gray = new byte[pixels.Length];

        // 2. Grayscale conversion
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            gray[i] = _convertToGrayscale
                ? (byte)Math.Clamp((int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue), 0, 255)
                : c.Red;
        }

        // 3. Normalize contrast (1% - 99% percentile stretch)
        if (_normalizeContrast && gray.Length > 0)
        {
            var hist = new int[256];
            foreach (var g in gray) hist[g]++;

            int lowCut = (int)(gray.Length * 0.01);
            int highCut = (int)(gray.Length * 0.99);
            int cum = 0;
            byte minVal = 0, maxVal = 255;
            bool foundMin = false;

            for (int v = 0; v < 256; v++)
            {
                cum += hist[v];
                if (!foundMin && cum >= lowCut) { minVal = (byte)v; foundMin = true; }
                if (cum >= highCut) { maxVal = (byte)v; break; }
            }

            int range = Math.Max(1, maxVal - minVal);
            if (range < 245)
            {
                for (int i = 0; i < gray.Length; i++)
                {
                    int stretched = (gray[i] - minVal) * 255 / range;
                    gray[i] = (byte)Math.Clamp(stretched, 0, 255);
                }
            }
        }

        // 4. Lightweight 3x3 isolated-noise filter
        if (_removeNoise && width > 2 && height > 2)
        {
            var cleaned = (byte[])gray.Clone();
            var window = new byte[9];
            for (int y = 1; y < height - 1; y++)
            {
                int rowOffset = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = rowOffset + x;
                    // Only filter isolated pixels that differ sharply from local neighbors
                    window[0] = gray[idx - width - 1];
                    window[1] = gray[idx - width];
                    window[2] = gray[idx - width + 1];
                    window[3] = gray[idx - 1];
                    window[4] = gray[idx];
                    window[5] = gray[idx + 1];
                    window[6] = gray[idx + width - 1];
                    window[7] = gray[idx + width];
                    window[8] = gray[idx + width + 1];

                    int neighborAvg = (window[0] + window[1] + window[2] + window[3] +
                                       window[5] + window[6] + window[7] + window[8]) / 8;
                    if (Math.Abs(window[4] - neighborAvg) > 70)
                    {
                        Array.Sort(window);
                        cleaned[idx] = window[4];
                    }
                }
            }
            gray = cleaned;
        }

        // Write processed grayscale back to working bitmap
        for (int i = 0; i < pixels.Length; i++)
        {
            byte g = gray[i];
            pixels[i] = new SKColor(g, g, g, 255);
        }
        working.Pixels = pixels;

        // 5. Deskew via horizontal projection variance (-8° to +8°)
        if (_deskew)
        {
            deskewAngle = EstimateSkewAngle(gray, width, height);
            if (Math.Abs(deskewAngle) >= 0.5)
            {
                var deskewed = RotateArbitrary(working, (float)deskewAngle);
                working.Dispose();
                return deskewed;
            }
        }

        return working;
    }

    private static double EstimateSkewAngle(byte[] gray, int width, int height)
    {
        // Downsample step for fast projection scoring
        int stepX = Math.Max(1, width / 400);
        int stepY = Math.Max(1, height / 400);
        double bestAngle = 0;
        double bestVariance = -1;

        for (double angle = -8.0; angle <= 8.0; angle += 1.0)
        {
            double rad = angle * Math.PI / 180.0;
            double sin = Math.Sin(rad);
            double cos = Math.Cos(rad);
            double cx = width / 2.0;
            double cy = height / 2.0;

            var rowInk = new long[height];
            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    if (gray[y * width + x] < 160) // dark ink pixel
                    {
                        int ny = (int)Math.Round((x - cx) * sin + (y - cy) * cos + cy);
                        if (ny >= 0 && ny < height)
                        {
                            rowInk[ny]++;
                        }
                    }
                }
            }

            double sumSq = 0;
            for (int y = 0; y < height; y++)
            {
                sumSq += (double)rowInk[y] * rowInk[y];
            }

            if (sumSq > bestVariance)
            {
                bestVariance = sumSq;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    private static SKBitmap RotateBitmap(SKBitmap source, int degreesClockwise)
    {
        bool swap = degreesClockwise == 90 || degreesClockwise == 270;
        int newW = swap ? source.Height : source.Width;
        int newH = swap ? source.Width : source.Height;

        var rotated = new SKBitmap(newW, newH, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(newW / 2f, newH / 2f);
        canvas.RotateDegrees(degreesClockwise);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap RotateArbitrary(SKBitmap source, float degrees)
    {
        var rotated = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(source.Width / 2f, source.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }
}
