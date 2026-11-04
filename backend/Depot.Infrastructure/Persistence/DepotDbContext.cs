using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class DepotDbContext(DbContextOptions<DepotDbContext> options) : DbContext(options)
{
    public DbSet<QBittorrentSettingsRecord> QBittorrentSettings => Set<QBittorrentSettingsRecord>();

    public DbSet<OrganizationSettingsRecord> OrganizationSettings => Set<OrganizationSettingsRecord>();

    public DbSet<OrganizationPathMappingRecord> OrganizationPathMappings => Set<OrganizationPathMappingRecord>();

    public DbSet<LibraryTargetRecord> LibraryTargets => Set<LibraryTargetRecord>();

    public DbSet<OrganizationJobRecord> OrganizationJobs => Set<OrganizationJobRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DepotDbContext).Assembly);
    }
}
