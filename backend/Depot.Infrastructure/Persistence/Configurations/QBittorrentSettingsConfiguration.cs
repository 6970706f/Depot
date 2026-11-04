using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Depot.Infrastructure.Persistence.Configurations;

public sealed class QBittorrentSettingsConfiguration : IEntityTypeConfiguration<QBittorrentSettingsRecord>
{
    public void Configure(EntityTypeBuilder<QBittorrentSettingsRecord> builder)
    {
        builder.ToTable("QBittorrentSettings", table =>
            table.HasCheckConstraint("CK_QBittorrentSettings_SingleRow", $"Id = {QBittorrentSettingsRecord.SingletonId}"));

        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.Url).IsRequired();
        builder.Property(settings => settings.Username).IsRequired();
        builder.Property(settings => settings.ProtectedPassword).IsRequired();
    }
}
