using System.Text.Json;
using Depot.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Depot.Infrastructure.Persistence;

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        builder.ToTable("ImportJobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.TorrentHash).HasMaxLength(64);
        builder.Property(job => job.MediaType).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.UsedMode).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.Subtitles)
            .HasConversion(
                subtitles => JsonSerializer.Serialize(subtitles, JsonSerializerOptions.Web),
                json => JsonSerializer.Deserialize<List<PlannedFile>>(json, JsonSerializerOptions.Web) ?? new List<PlannedFile>(),
                new ValueComparer<List<PlannedFile>>(
                    (left, right) => left!.SequenceEqual(right!),
                    list => list.Aggregate(0, (hash, file) => HashCode.Combine(hash, file.GetHashCode())),
                    list => list.ToList()));
        builder.Ignore(job => job.FileName);
        builder.Ignore(job => job.Destination);
        builder.HasIndex(job => job.Status);
        builder.HasIndex(job => job.TorrentHash);
    }
}
