namespace Depot.Infrastructure.Jellyfin;

public sealed class JellyfinOptions
{
    public const string SectionName = "Jellyfin";

    // The most disk the image cache uses; the images used longest ago go first.
    public int ImageCacheMegabytes { get; init; } = 512;
}
