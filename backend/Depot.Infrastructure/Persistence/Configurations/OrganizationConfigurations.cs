using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Depot.Infrastructure.Persistence.Configurations;

public sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettingsRecord>
{
    public void Configure(EntityTypeBuilder<OrganizationSettingsRecord> builder)
    {
        builder.ToTable("OrganizationSettings", table =>
            table.HasCheckConstraint("CK_OrganizationSettings_SingleRow", $"Id = {OrganizationSettingsRecord.SingletonId}"));

        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.MoviesPath).IsRequired();
        builder.Property(settings => settings.SeriesPath).IsRequired();
        builder.Property(settings => settings.ImportMode).HasConversion<string>().IsRequired();
    }
}

public sealed class OrganizationPathMappingConfiguration : IEntityTypeConfiguration<OrganizationPathMappingRecord>
{
    public void Configure(EntityTypeBuilder<OrganizationPathMappingRecord> builder)
    {
        builder.ToTable("OrganizationPathMappings");
        builder.HasKey(mapping => mapping.Id);
        builder.Property(mapping => mapping.QbittorrentPath).IsRequired();
        builder.Property(mapping => mapping.DepotPath).IsRequired();
        builder.HasIndex(mapping => mapping.QbittorrentPath).IsUnique();
    }
}

public sealed class LibraryTargetConfiguration : IEntityTypeConfiguration<LibraryTargetRecord>
{
    public void Configure(EntityTypeBuilder<LibraryTargetRecord> builder)
    {
        builder.ToTable("LibraryTargets");
        builder.HasKey(target => target.Hash);
        // qBittorrent reports hashes in lowercase, but a stored one is matched in any case.
        builder.Property(target => target.Hash).UseCollation("NOCASE");
        builder.Property(target => target.MediaType).HasConversion<string>().IsRequired();
        builder.Property(target => target.Title).IsRequired();
        builder.HasIndex(target => target.ScannedAt);
    }
}

public sealed class OrganizationJobConfiguration : IEntityTypeConfiguration<OrganizationJobRecord>
{
    public void Configure(EntityTypeBuilder<OrganizationJobRecord> builder)
    {
        builder.ToTable("OrganizationJobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.TorrentHash).IsRequired();
        builder.Property(job => job.TorrentName).IsRequired();
        builder.Property(job => job.RelativePath).IsRequired();
        builder.Property(job => job.MediaType).HasConversion<string>().IsRequired();
        builder.Property(job => job.Title).IsRequired();
        builder.Property(job => job.Status).HasConversion<string>().IsRequired();
        builder.Property(job => job.ImportedWith).HasConversion<string>();
        builder.Property(job => job.Version).IsConcurrencyToken();

        // One job per file of a torrent: a season pack has many, but the same file never twice.
        builder.HasIndex(job => new { job.TorrentHash, job.RelativePath }).IsUnique();
        builder.HasIndex(job => job.Status);
    }
}
