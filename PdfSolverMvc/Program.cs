using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PdfSolverMvc.Data;
using PdfSolverMvc.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Allow large PDF uploads (up to 100 MB)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104_857_600;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104_857_600;
});

// Support reverse proxies (Render, Fly.io, Railway, Azure) terminating HTTPS
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=pdfsolver.db";

// Ensure SQLite parent directory exists (important for mounted volumes like /data/pdfsolver.db)
var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) && sqliteBuilder.DataSource != ":memory:")
{
    var dbFullPath = Path.IsPathRooted(sqliteBuilder.DataSource)
        ? sqliteBuilder.DataSource
        : Path.Combine(builder.Environment.ContentRootPath, sqliteBuilder.DataSource);
    var dbDir = Path.GetDirectoryName(dbFullPath);
    if (!string.IsNullOrWhiteSpace(dbDir))
    {
        Directory.CreateDirectory(dbDir);
    }
    sqliteBuilder.DataSource = dbFullPath;
    connectionString = sqliteBuilder.ToString();
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IOcrService, OcrService>();
builder.Services.AddScoped<IPdfExtractionService, PdfExtractionService>();

var app = builder.Build();

// Ensure SQLite database and upload directory exist
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

var configuredUploadFolder = app.Configuration["Storage:UploadFolder"] ?? "UploadedPdfs";
var uploadFolder = Path.IsPathRooted(configuredUploadFolder)
    ? configuredUploadFolder
    : Path.Combine(app.Environment.ContentRootPath, configuredUploadFolder);
Directory.CreateDirectory(uploadFolder);

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Pdf/Error");
    app.UseHsts();
}
else
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pdf}/{action=Upload}/{id?}");

app.Run();
