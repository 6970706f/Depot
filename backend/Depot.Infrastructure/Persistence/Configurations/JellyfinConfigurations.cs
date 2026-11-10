using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Depot.Infrastructure.Persistence.Configurations;

public sealed class JellyfinSettingsConfiguration : IEntityTypeConfiguration<JellyfinSettingsRecord>
{
    public void Configure(EntityTypeBuilder<JellyfinSettingsRecord> builder)
    {
        builder.ToTable("JellyfinSettings", table =>
            table.HasCheckConstraint("CK_JellyfinSettings_SingleRow", $"Id = {JellyfinSettingsRecord.SingletonId}"));

        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.Url).IsRequired();
        builder.Property(settings => settings.ProtectedApiKey).IsRequired();
    }
}

public sealed class MediaLibraryConfiguration : IEntityTypeConfiguration<MediaLibraryRecord>
{
    public void Configure(EntityTypeBuilder<MediaLibraryRecord> builder)
    {
        builder.ToTable("MediaLibraries");
        builder.HasKey(library => library.Id);
        builder.Property(library => library.ExternalId).IsRequired();
        builder.Property(library => library.Name).IsRequired();
        builder.Property(library => library.Kind).HasConversion<string>().IsRequired();
        builder.Property(library => library.Locations).IsRequired();
        builder.HasIndex(library => library.ExternalId).IsUnique();

        // A library that disappears from the server takes its items, and their files, along.
        builder.HasMany(library => library.Items)
            .WithOne(item => item.Library)
            .HasForeignKey(item => item.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MediaItemConfiguration : IEntityTypeConfiguration<MediaItemRecord>
{
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicks =
        new(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    public void Configure(EntityTypeBuilder<MediaItemRecord> builder)
    {
        builder.ToTable("MediaItems");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ExternalId).IsRequired();
        builder.Property(item => item.Kind).HasConversion<string>().IsRequired();
        builder.Property(item => item.Name).IsRequired();
        builder.Property(item => item.SortName).IsRequired().UseCollation("NOCASE");
        builder.Property(item => item.Genres).IsRequired();
        builder.Property(item => item.Studios).IsRequired();
        builder.Property(item => item.ProviderIds).IsRequired();
        builder.Property(item => item.BackdropImageTags).IsRequired();
        // SQLite can't sort DateTimeOffset text, so this one is stored as its UTC ticks, which sort in
        // time order and, unlike EF's binary converter, keep every digit Jellyfin sends: a value that
        // reads back different would count as a change on every sync.
        builder.Property(item => item.AddedAt).HasConversion(UtcTicks);

        // The same source item is never kept twice.
        builder.HasIndex(item => item.ExternalId).IsUnique();
        builder.HasIndex(item => new { item.LibraryId, item.Kind });
        builder.HasIndex(item => item.SeriesExternalId);
        builder.HasIndex(item => item.SeasonExternalId);

        builder.HasMany(item => item.Files)
            .WithOne()
            .HasForeignKey(file => file.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFileRecord>
{
    public void Configure(EntityTypeBuilder<MediaFileRecord> builder)
    {
        builder.ToTable("MediaFiles");
        builder.HasKey(file => file.Id);
        builder.Property(file => file.ExternalId).IsRequired();
        builder.Property(file => file.Streams).IsRequired();
        builder.HasIndex(file => new { file.MediaItemId, file.ExternalId }).IsUnique();
    }
}

public sealed class LibrarySyncRunConfiguration : IEntityTypeConfiguration<LibrarySyncRunRecord>
{
    public void Configure(EntityTypeBuilder<LibrarySyncRunRecord> builder)
    {
        builder.ToTable("LibrarySyncRuns");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Trigger).HasConversion<string>().IsRequired();
        builder.Property(run => run.Status).HasConversion<string>().IsRequired();
        builder.Property(run => run.Errors).IsRequired();
        builder.HasIndex(run => run.Status);
    }
}
