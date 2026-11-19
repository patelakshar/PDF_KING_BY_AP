using Microsoft.EntityFrameworkCore;
using PdfSolverMvc.Models;

namespace PdfSolverMvc.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<PdfDocumentRecord> PdfDocuments => Set<PdfDocumentRecord>();
}
