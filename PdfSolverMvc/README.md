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

The Docker deployment copies the English traineddata package into `/app/tessdata` automatically.

## Docker

From the `LANDT` repository root:

```bash
docker build -t pdf-solver-mvc .
docker run --rm -p 8080:8080 pdf-solver-mvc
```

The container listens on `http://0.0.0.0:8080` and exposes `/health` for hosting health checks.
