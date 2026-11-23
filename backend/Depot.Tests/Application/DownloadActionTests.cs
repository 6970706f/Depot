using Depot.Application.Downloads;
using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Downloads;
using Depot.Domain.Library;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

public sealed class DownloadActionTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    [Fact]
    public async Task Pause_and_resume_return_the_new_state()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.Add(Hash, "Dune", "/downloads", [("Dune/Dune.mkv", 100)], TorrentStatus.Downloading, 0.5);

        var paused = await harness.RunAsync<DownloadView, DownloadService>(service => service.PauseAsync(Hash, CancellationToken.None));
        var resumed = await harness.RunAsync<DownloadView, DownloadService>(service => service.ResumeAsync(Hash, CancellationToken.None));

        Assert.Equal(TorrentStatus.Paused, paused.Status);
        Assert.Equal(TorrentStatus.Downloading, resumed.Status);
    }

    [Fact]
    public async Task Remove_needs_to_be_told_what_to_do_with_the_files()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.Add(Hash, "Dune", "/downloads", [("Dune/Dune.mkv", 100)]);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.RunAsync<RemovedDownloadView, DownloadService>(service => service.RemoveAsync(Hash, null, CancellationToken.None)));

        Assert.Equal("deleteFiles", error.Field);
        Assert.Empty(harness.QBittorrent.Removed);
    }

    [Fact]
    public async Task Remove_drops_the_library_choice()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.Add(Hash, "Dune", "/downloads", [("Dune/Dune.mkv", 100)]);
        await harness.RunAsync<ILibraryChoiceRepository>(repository =>
            repository.SaveAsync(Hash, new LibraryTarget(MediaType.Movie, "Dune", null), DateTimeOffset.UnixEpoch, CancellationToken.None));

        var removed = await harness.RunAsync<RemovedDownloadView, DownloadService>(service => service.RemoveAsync(Hash, false, CancellationToken.None));

        Assert.False(removed.FilesDeleted);
        Assert.Equal((Hash, false), Assert.Single(harness.QBittorrent.Removed));
        Assert.Null(await harness.RunAsync<LibraryTarget?, ILibraryChoiceRepository>(repository => repository.GetAsync(Hash, CancellationToken.None)));
    }

    [Fact]
    public async Task Actions_on_an_unknown_torrent_say_it_is_gone()
    {
        await using var harness = await DepotHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<QBittorrentException>(() =>
            harness.RunAsync<RemovedDownloadView, DownloadService>(service => service.RemoveAsync(Hash, true, CancellationToken.None)));

        Assert.Equal(QBittorrentFailure.TorrentNotFound, error.Failure);
    }
}
