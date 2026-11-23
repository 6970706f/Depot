using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Depot.Infrastructure.Persistence;

// One settings group (qBittorrent, Jellyfin, library) stored as a JSON document under its key.
// Secrets inside it are encrypted before they get here.
public sealed class SettingsEntry
{
    public required string Key { get; init; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SettingsEntryConfiguration : IEntityTypeConfiguration<SettingsEntry>
{
    public void Configure(EntityTypeBuilder<SettingsEntry> builder)
    {
        builder.ToTable("Settings");
        builder.HasKey(entry => entry.Key);
        builder.Property(entry => entry.Key).HasMaxLength(64);
    }
}
