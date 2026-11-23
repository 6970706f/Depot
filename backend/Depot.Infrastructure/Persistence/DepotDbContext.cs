using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class DepotDbContext(DbContextOptions<DepotDbContext> options) : DbContext(options)
{
    public DbSet<SettingsEntry> Settings => Set<SettingsEntry>();

    public DbSet<Depot.Domain.Library.LibraryChoice> LibraryChoices => Set<Depot.Domain.Library.LibraryChoice>();

    public DbSet<Depot.Domain.Library.ImportJob> ImportJobs => Set<Depot.Domain.Library.ImportJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DepotDbContext).Assembly);
    }
}
