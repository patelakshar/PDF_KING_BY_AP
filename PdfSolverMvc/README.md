# PdfSolverMvc (.NET 11 ASP.NET Core MVC)

A local PDF extraction application for uploading, indexing, and viewing complex PDFs.

## Features

1. **Upload**: Upload single or multiple PDFs up to 100 MB.
2. **Extract**: Use PdfPig for selectable text and PDFtoImage plus Tesseract for scanned pages.
3. **Align**: Preserve column and table structure with coordinate-aware row reconstruction.
4. **Store**: Save uploaded PDF metadata and extraction results in SQLite.
5. **Export**: Download extracted CSV data or open/download the original PDF.

This application does not use Groq or any external AI service.

## Run Locally

```bash
cd PdfSolverMvc
dotnet run --urls http://localhost:5050
```

## OCR

For OCR, place `eng.traineddata` at:

```text
PdfSolverMvc/tessdata/eng.traineddata
```

OCR uses the local Tesseract traineddata configured by `Ocr:TessDataPath`.
