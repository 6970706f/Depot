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

    public DbSet<JellyfinSettingsRecord> JellyfinSettings => Set<JellyfinSettingsRecord>();

    public DbSet<MediaLibraryRecord> MediaLibraries => Set<MediaLibraryRecord>();

    public DbSet<MediaItemRecord> MediaItems => Set<MediaItemRecord>();

    public DbSet<MediaFileRecord> MediaFiles => Set<MediaFileRecord>();

    public DbSet<LibrarySyncRunRecord> LibrarySyncRuns => Set<LibrarySyncRunRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DepotDbContext).Assembly);
    }
}
