FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY ["PdfSolverMvc/PdfSolverMvc.csproj", "PdfSolverMvc/"]
RUN dotnet restore "PdfSolverMvc/PdfSolverMvc.csproj"

COPY . ./
RUN dotnet publish "PdfSolverMvc/PdfSolverMvc.csproj" \
    -c Release \
    --no-restore \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Ocr__TessDataPath=/app/tessdata
ENV Ocr__Language=eng

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libfontconfig1 \
        libfreetype6 \
        libharfbuzz0b \
        tesseract-ocr \
        tesseract-ocr-eng \
    && mkdir -p /app/tessdata /app/data/UploadedPdfs \
    && find /usr/share/tesseract-ocr -name eng.traineddata -exec cp {} /app/tessdata/eng.traineddata \; \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

EXPOSE 8080

ENTRYPOINT ["dotnet", "PdfSolverMvc.dll"]
