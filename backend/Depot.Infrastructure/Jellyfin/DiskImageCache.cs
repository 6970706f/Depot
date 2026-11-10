using System.Security.Cryptography;
using System.Text;
using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Depot.Infrastructure.Jellyfin;

// Images on disk in data/cache/images, never in the database. A file is named after a hash of
// its key, with the extension of its format, so nothing from the key or the server reaches a
// path. Writes go through a temporary file and a rename, so a reader never sees half an image.
// Over the size limit, the files used longest ago are deleted. It's only a cache: deleting the
// folder loses nothing, and it isn't part of a backup.
public sealed class DiskImageCache : IImageCache
{
    // Each hit refreshes a file's time at most this often, which is what "used" means here.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private readonly string folder;
    private readonly long maxBytes;
    private readonly TimeProvider clock;
    private readonly ILogger<DiskImageCache> logger;
    private readonly SemaphoreSlim pruning = new(1, 1);
    private long writesSincePrune;

    public DiskImageCache(StoragePaths storage, IOptions<JellyfinOptions> options, TimeProvider clock, ILogger<DiskImageCache> logger)
    {
        folder = storage.ImageCacheDirectory;
        maxBytes = Math.Max(1, options.Value.ImageCacheMegabytes) * 1024L * 1024L;
        this.clock = clock;
        this.logger = logger;
    }

    public async Task<MediaImageContent?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var name = FileName(key);
        foreach (var (contentType, extension) in Extensions)
        {
            var path = Path.Join(folder, name + extension);
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            Touch(path);
            return new MediaImageContent(contentType, content);
        }

        return null;
    }

    public async Task SetAsync(string key, MediaImageContent image, CancellationToken cancellationToken)
    {
        if (!Extensions.TryGetValue(image.ContentType, out var extension))
        {
            return;
        }

        try
        {
            // Private like the rest of the data folder: what's in a library is nobody else's business.
            StoragePaths.CreatePrivateFolder(Path.GetDirectoryName(folder)!);
            StoragePaths.CreatePrivateFolder(folder);
            var path = Path.Join(folder, FileName(key) + extension);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = StoragePaths.PrivateFileMode;
            }

            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(image.Content, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A full disk or a folder Depot can't write only means the next request asks Jellyfin again.
            logger.LogWarning("An image couldn't be cached: {Reason}", exception.Message);
            return;
        }

        if (Interlocked.Increment(ref writesSincePrune) % 20 == 1)
        {
            await PruneAsync();
        }
    }

    // Deletes the oldest images until the cache is back under 80% of its limit, and any
    // temporary file left by a write that never finished.
    public async Task PruneAsync()
    {
        if (!await pruning.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var files = new DirectoryInfo(folder).EnumerateFiles().ToList();
            foreach (var stale in files.Where(file => file.Extension == ".tmp" && file.LastWriteTimeUtc < clock.GetUtcNow().UtcDateTime.AddHours(-1)))
            {
                stale.Delete();
            }

            var images = files.Where(file => file.Extension != ".tmp").OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = images.Sum(file => file.Length);
            if (total <= maxBytes)
            {
                return;
            }

            var target = maxBytes * 8 / 10;
            var deleted = 0;
            foreach (var file in images)
            {
                if (total <= target)
                {
                    break;
                }

                total -= file.Length;
                file.Delete();
                deleted++;
            }

            logger.LogInformation("Image cache over {Limit} MB: {Deleted} image(s) used longest ago were deleted.", maxBytes / (1024 * 1024), deleted);
        }
        catch (IOException exception)
        {
            logger.LogWarning("The image cache couldn't be pruned: {Reason}", exception.Message);
        }
        finally
        {
            pruning.Release();
        }
    }

    private void Touch(string path)
    {
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (File.GetLastWriteTimeUtc(path) < now - TouchInterval)
            {
                File.SetLastWriteTimeUtc(path, now);
            }
        }
        catch (IOException)
        {
            // Deleted by a prune in between; nothing to refresh.
        }
    }

    private static string FileName(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
