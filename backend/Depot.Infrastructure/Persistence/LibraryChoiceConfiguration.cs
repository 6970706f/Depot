using Depot.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Depot.Infrastructure.Persistence;

internal sealed class LibraryChoiceConfiguration : IEntityTypeConfiguration<LibraryChoice>
{
    public void Configure(EntityTypeBuilder<LibraryChoice> builder)
    {
        builder.ToTable("LibraryChoices");
        builder.HasKey(choice => choice.TorrentHash);
        builder.Property(choice => choice.TorrentHash).HasMaxLength(64);
        builder.Property(choice => choice.MediaType).HasConversion<string>().HasMaxLength(16);
        builder.Property(choice => choice.Title).HasMaxLength(LibraryFolderName.MaxLength);
        builder.Ignore(choice => choice.Target);
    }
}
