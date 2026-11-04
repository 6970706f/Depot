using Depot.Application.Downloads;
using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Organization;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

public sealed class AddDownloadTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string Magnet = $"magnet:?xt=urn:btih:{Hash}&dn=Some.Show.S02";

    [Fact]
    public async Task ASeriesDownloadRemembersWhereItGoes()
    {
        await using var harness = await OrganizationHarness.CreateAsync();

        await AddAsync(harness, new AddMagnetInput(Magnet, "series", " Some Show ", 2));

        var target = Assert.Single(await TargetsAsync(harness));
        Assert.Equal(Hash, target.Key);
        Assert.Equal(new LibraryTarget(MediaType.Series, "Some Show", 2), target.Value);
        // The qBittorrent category still decides its save folder.
        Assert.Equal("series", Assert.Single(harness.QBittorrent.Added).Options.Category);
    }

    [Fact]
    public async Task AMovieDownloadUsesTheMoviesCategory()
    {
        await using var harness = await OrganizationHarness.CreateAsync();

        await AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune (2021)"));

        Assert.Equal(new LibraryTarget(MediaType.Movie, "Dune (2021)", null), Assert.Single(await TargetsAsync(harness)).Value);
        Assert.Equal("movies", Assert.Single(harness.QBittorrent.Added).Options.Category);
    }

    [Fact]
    public async Task AnOtherDownloadIsOnlySentToQbittorrent()
    {
        await using var harness = await OrganizationHarness.CreateAsync();

        await AddAsync(harness, new AddMagnetInput(Magnet, "other"));

        Assert.Empty(await TargetsAsync(harness));
        Assert.Null(Assert.Single(harness.QBittorrent.Added).Options.Category);
    }

    [Fact]
    public async Task AnInvalidChoiceSendsNothing()
    {
        await using var harness = await OrganizationHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            AddAsync(harness, new AddMagnetInput(Magnet, "series", "Some Show", null)));

        Assert.Equal("season", exception.Field);
        Assert.Empty(harness.QBittorrent.Added);
        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task ARefusedTorrentLeavesNoChoiceBehind()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        harness.QBittorrent.RejectAdds = true;

        await Assert.ThrowsAsync<QBittorrentException>(() => AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune")));

        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task AddingAgainAsOtherDropsTheEarlierChoice()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        // Left from an earlier download of the torrent, removed outside Depot.
        await harness.ChooseAsync(Hash, new LibraryTarget(MediaType.Movie, "Dune", null));

        await AddAsync(harness, new AddMagnetInput(Magnet, "other"));

        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task RemovingADownloadDropsItsChoice()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        harness.QBittorrent.AllowRemove = true;
        await AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune"));

        await harness.RunAsync<DownloadService, RemovedDownloadView>(service => service.RemoveAsync(Hash, false, CancellationToken.None));

        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task ATimeoutAfterQbittorrentTookTheTorrentKeepsTheChoice()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        harness.QBittorrent.TimeOutAfterAccepting = true;

        var exception = await Assert.ThrowsAsync<QBittorrentException>(() => AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune")));

        Assert.Equal(QBittorrentFailure.Timeout, exception.Failure);
        Assert.Equal(new LibraryTarget(MediaType.Movie, "Dune", null), Assert.Single(await TargetsAsync(harness)).Value);
    }

    [Fact]
    public async Task TwoAddsAtOnceKeepTheChoiceOfTheOneQbittorrentTook()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        harness.QBittorrent.AddDelay = TimeSpan.FromMilliseconds(200);

        var first = AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune"));
        var second = AddAsync(harness, new AddMagnetInput(Magnet, "series", "Dune", 1));
        var results = await Task.WhenAll(Capture(first), Capture(second));

        Assert.Single(results, result => result is null);
        Assert.IsType<DuplicateDownloadException>(Assert.Single(results, result => result is not null));
        Assert.Single(harness.QBittorrent.Added);
        var expected = results[0] is null
            ? new LibraryTarget(MediaType.Movie, "Dune", null)
            : new LibraryTarget(MediaType.Series, "Dune", 1);
        Assert.Equal(expected, Assert.Single(await TargetsAsync(harness)).Value);

        static async Task<Exception?> Capture(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    [Fact]
    public async Task TheDownloadListShowsTheChoice()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.ChooseAsync(Hash, new LibraryTarget(MediaType.Series, "Some Show", 2));
        harness.AddDownloadedTorrent(Hash, "Some.Show.S02", "series", ("ep.mkv", 16));
        harness.AddDownloadedTorrent("1111111111111111111111111111111111111111", "other", null, ("file.iso", 16));

        var downloads = await harness.RunAsync<DownloadService, IReadOnlyList<DownloadView>>(
            service => service.ListAsync(CancellationToken.None));

        Assert.Equal(new LibraryTargetView(MediaType.Series, "Some Show", 2), downloads.Single(download => download.Hash == Hash).Library);
        Assert.Null(downloads.Single(download => download.Hash != Hash).Library);
    }

    private static Task<DownloadAddedView> AddAsync(OrganizationHarness harness, AddMagnetInput input) =>
        harness.RunAsync<DownloadService, DownloadAddedView>(service => service.AddMagnetAsync(input, CancellationToken.None));

    private static Task<IReadOnlyDictionary<string, LibraryTarget>> TargetsAsync(OrganizationHarness harness) =>
        harness.RunAsync<ILibraryTargetRepository, IReadOnlyDictionary<string, LibraryTarget>>(
            repository => repository.GetAllAsync(CancellationToken.None));
}
