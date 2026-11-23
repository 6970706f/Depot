using Depot.Application.Downloads;
using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Library;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

public sealed class AddDownloadTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string Magnet = $"magnet:?xt=urn:btih:{Hash}&dn=Some.Show.S02";

    [Fact]
    public async Task A_series_download_remembers_where_it_goes()
    {
        await using var harness = await DepotHarness.CreateAsync();

        var added = await AddAsync(harness, new AddMagnetInput(Magnet, "series", " Some Show ", 2));

        Assert.Equal(Hash, added.Hash);
        Assert.Equal("Some.Show.S02", added.Name);
        var target = Assert.Single(await TargetsAsync(harness));
        Assert.Equal(Hash, target.Key);
        Assert.Equal(new LibraryTarget(MediaType.Series, "Some Show", 2), target.Value);
        Assert.Equal("series", Assert.Single(harness.QBittorrent.Added).Options.Category);
    }

    [Fact]
    public async Task A_movie_download_uses_the_movies_category_and_shows_in_the_list()
    {
        await using var harness = await DepotHarness.CreateAsync();

        await AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune (2021)"));
        var list = await harness.RunAsync<IReadOnlyList<DownloadView>, DownloadService>(service => service.ListAsync(CancellationToken.None));

        Assert.Equal("movies", Assert.Single(harness.QBittorrent.Added).Options.Category);
        Assert.Equal(new LibraryTargetView(MediaType.Movie, "Dune (2021)", null), Assert.Single(list).Library);
    }

    [Fact]
    public async Task An_other_download_is_only_sent_to_qbittorrent()
    {
        await using var harness = await DepotHarness.CreateAsync();

        await AddAsync(harness, new AddMagnetInput(Magnet, "other"));

        Assert.Empty(await TargetsAsync(harness));
        Assert.Null(Assert.Single(harness.QBittorrent.Added).Options.Category);
    }

    [Theory]
    [InlineData("series", "Some Show", null, "season")]
    [InlineData("movie", "", null, "title")]
    [InlineData("movie", "../escape", null, "title")]
    [InlineData("music", "x", null, "mediaType")]
    public async Task An_invalid_choice_sends_nothing(string mediaType, string title, int? season, string field)
    {
        await using var harness = await DepotHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            AddAsync(harness, new AddMagnetInput(Magnet, mediaType, title, season)));

        Assert.Equal(field, exception.Field);
        Assert.Empty(harness.QBittorrent.Added);
    }

    [Fact]
    public async Task A_torrent_already_in_qbittorrent_is_refused_and_keeps_its_choice()
    {
        await using var harness = await DepotHarness.CreateAsync();
        await AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune"));

        var duplicate = await Assert.ThrowsAsync<DuplicateDownloadException>(() =>
            AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Something else")));

        Assert.Equal(Hash, duplicate.Hash);
        Assert.Equal("Dune", Assert.Single(await TargetsAsync(harness)).Value.Title);
    }

    [Fact]
    public async Task A_refused_add_drops_the_choice()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.RejectAdds = true;

        await Assert.ThrowsAsync<QBittorrentException>(() => AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune")));

        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task A_timeout_after_qbittorrent_took_the_torrent_keeps_the_choice()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.TimeOutAfterAccepting = true;

        await Assert.ThrowsAsync<QBittorrentException>(() => AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune")));

        Assert.Equal("Dune", Assert.Single(await TargetsAsync(harness)).Value.Title);
    }

    [Fact]
    public async Task Adding_again_as_other_forgets_an_old_choice()
    {
        await using var harness = await DepotHarness.CreateAsync();
        await AddAsync(harness, new AddMagnetInput(Magnet, "movie", "Dune"));
        harness.QBittorrent.Remove(Hash);

        await AddAsync(harness, new AddMagnetInput(Magnet, "other"));

        Assert.Empty(await TargetsAsync(harness));
    }

    [Fact]
    public async Task Series_lists_the_folders_and_their_seasons()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, "The Last of Us", "Season 01"));
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, "The Last of Us", "Season 2"));
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, "The Last of Us", "Extras"));
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, "Andor", "Specials"));
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, ".hidden"));

        var series = await harness.RunAsync<IReadOnlyList<LibrarySeriesView>, LibraryService>(service => service.ListSeriesAsync(CancellationToken.None));

        Assert.Equal(["Andor", "The Last of Us"], series.Select(entry => entry.Name));
        Assert.Equal([0], series[0].Seasons);
        Assert.Equal([1, 2], series[1].Seasons);
    }

    private static Task<DownloadAddedView> AddAsync(DepotHarness harness, AddMagnetInput input) =>
        harness.RunAsync<DownloadAddedView, DownloadService>(service => service.AddMagnetAsync(input, CancellationToken.None));

    private static Task<IReadOnlyDictionary<string, LibraryTarget>> TargetsAsync(DepotHarness harness) =>
        harness.RunAsync<IReadOnlyDictionary<string, LibraryTarget>, ILibraryChoiceRepository>(repository => repository.GetAllAsync(CancellationToken.None));
}
