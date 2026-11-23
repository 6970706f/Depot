using Depot.Application.Imports;
using Depot.Application.Jellyfin;
using Depot.Application.Library;
using Depot.Domain.Library;
using Depot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Depot.Tests.Application;

public sealed class JellyfinRefreshTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    [Fact]
    public async Task Jellyfin_is_asked_to_scan_after_videos_are_imported_and_only_then()
    {
        var jellyfin = new CountingJellyfin();
        await using var harness = await DepotHarness.CreateAsync(services => services.AddScoped<IJellyfinClient>(_ => jellyfin));
        var file = Path.Combine(harness.DownloadsRoot, "Dune", "Dune.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[100]);
        harness.QBittorrent.Add(Hash, "Dune", "/downloads", [("Dune/Dune.mkv", 100)]);

        await harness.RunAsync<ImportCycle>(cycle => cycle.RunAsync(CancellationToken.None));
        Assert.Equal(0, jellyfin.Refreshes);

        await harness.RunAsync<ILibraryChoiceRepository>(repository =>
            repository.SaveAsync(Hash, new LibraryTarget(MediaType.Movie, "Dune", null), DateTimeOffset.UnixEpoch, CancellationToken.None));
        await harness.RunAsync<ImportCycle>(cycle => cycle.RunAsync(CancellationToken.None));

        Assert.Equal(1, jellyfin.Refreshes);
    }

    [Fact]
    public async Task A_failed_scan_request_does_not_fail_the_import()
    {
        var listener = new JellyfinRefreshOnImport(new CountingJellyfin { Fail = true }, NullLogger<JellyfinRefreshOnImport>.Instance);

        await listener.ImportedAsync(CancellationToken.None);
    }

    private sealed class CountingJellyfin : IJellyfinClient
    {
        public int Refreshes { get; private set; }

        public bool Fail { get; init; }

        public Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JellyfinServerInfo("Test", "10.11.11"));

        public Task<MediaPage> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaCounts> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaDetail?> GetItemAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaImage?> GetImageAsync(string id, MediaImageType type, int width, string? tag, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RefreshLibraryAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            return Fail ? throw new JellyfinException(JellyfinFailure.Unreachable, "down") : Task.CompletedTask;
        }
    }
}
