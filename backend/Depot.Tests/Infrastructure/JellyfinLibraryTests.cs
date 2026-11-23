using System.Net;
using System.Web;
using Depot.Application.Jellyfin;
using Depot.Domain.Settings;
using Depot.Infrastructure.Jellyfin;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Infrastructure;

// The live library reads against a scripted Jellyfin, shaped like 10.11's answers.
public sealed class JellyfinLibraryTests
{
    private const string MovieId = "4f2b13d61e08331ce834ff2de2b1ddf7";
    private const string SeriesId = "a1681125fc7c2ebb4d6c5eaf31418bf1";
    private const string SeasonOne = "027e2c06350a89555a3234fc8b88d67c";
    private const string OtherSeason = "06db62c8e32d6bc8b9e68a920f236934";

    private readonly ScriptedJellyfin server = new();
    private readonly JellyfinClient client;

    public JellyfinLibraryTests()
    {
        client = new JellyfinClient(
            new JellyfinHttp(new HttpClient(server)),
            new FixedJellyfinSettings(JellyfinSettings.Create("http://jellyfin.test:8096", ScriptedJellyfin.ApiKey)));
        server.Responses["/Items"] = () => ScriptedJellyfin.Json(ItemsFor(LastQuery()));
    }

    [Fact]
    public async Task Lists_a_page_of_movies_with_paging_sorting_and_search()
    {
        var page = await client.GetItemsAsync(new MediaQuery(MediaKind.Movie, "dune & co", MediaSort.Added, 3, 24), CancellationToken.None);

        var query = LastQuery();
        Assert.Equal("Movie", query["IncludeItemTypes"]);
        Assert.Equal("48", query["StartIndex"]);
        Assert.Equal("24", query["Limit"]);
        Assert.Equal("DateCreated,SortName", query["SortBy"]);
        Assert.Equal("dune & co", query["SearchTerm"]);
        Assert.Equal(57, page.Total);
        var movie = Assert.Single(page.Items);
        Assert.Equal(new MediaSummary(MovieId, MediaKind.Movie, "Dune", 2021, 7.8, "PG-13", "aa11"), movie);
    }

    [Fact]
    public async Task A_movie_comes_with_its_files_and_people()
    {
        var movie = (await client.GetItemAsync(MovieId, CancellationToken.None))!;

        Assert.Equal("Paul Atreides…", movie.Overview);
        Assert.Equal(["Science Fiction", "Adventure"], movie.Genres);
        Assert.Equal(155, movie.RuntimeMinutes);
        Assert.Equal("bb22", movie.BackdropImageTag);
        Assert.Equal(new MediaPerson("Timothée Chalamet", "Paul Atreides", "Actor"), Assert.Single(movie.People));
        var file = Assert.Single(movie.Files);
        Assert.Equal("1080p H264", file.Video);
        Assert.Equal(["English AAC"], file.Audio);
        Assert.Equal("tt1160419", movie.ProviderIds["Imdb"]);
        Assert.Empty(movie.Seasons);
    }

    [Fact]
    public async Task A_series_comes_with_its_own_seasons_and_their_episodes()
    {
        var series = (await client.GetItemAsync(SeriesId, CancellationToken.None))!;

        var season = Assert.Single(series.Seasons);
        Assert.Equal(SeasonOne, season.Id);
        Assert.Equal(["Pilot", "Second"], season.Episodes.Select(episode => episode.Name));
        Assert.Equal(1, season.Episodes[0].Number);
    }

    [Fact]
    public async Task An_unknown_item_is_null()
    {
        Assert.Null(await client.GetItemAsync("ffffffffffffffffffffffffffffffff", CancellationToken.None));
    }

    [Fact]
    public async Task Images_are_passed_on_only_in_picture_formats()
    {
        server.Responses[$"/Items/{MovieId}/Images/Primary"] = () => Bytes([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        server.Responses[$"/Items/{MovieId}/Images/Backdrop"] = () => Bytes("<svg onload=alert(1)>"u8.ToArray());

        var poster = await client.GetImageAsync(MovieId, MediaImageType.Primary, 320, "aa11", CancellationToken.None);
        var error = await Assert.ThrowsAsync<JellyfinException>(() => client.GetImageAsync(MovieId, MediaImageType.Backdrop, 320, null, CancellationToken.None));

        Assert.Equal("image/jpeg", poster!.ContentType);
        Assert.Contains("maxWidth=320", server.Requests.First(request => request.PathAndQuery.Contains("Primary")).PathAndQuery);
        Assert.Contains("format", error.Message);
    }

    [Fact]
    public async Task A_missing_image_is_null()
    {
        Assert.Null(await client.GetImageAsync(MovieId, MediaImageType.Thumb, 320, null, CancellationToken.None));
    }

    private System.Collections.Specialized.NameValueCollection LastQuery() =>
        HttpUtility.ParseQueryString(new Uri("http://x" + server.Requests.Last().PathAndQuery).Query);

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static string ItemsFor(System.Collections.Specialized.NameValueCollection query)
    {
        if (query["Ids"] == MovieId)
        {
            return $$$"""
                {"Items":[{"Id":"{{{MovieId}}}","Name":"Dune","Type":"Movie","ProductionYear":2021,"Overview":"Paul Atreides…",
                "Genres":["Science Fiction","Adventure"],"RunTimeTicks":93000000000,"BackdropImageTags":["bb22"],"ImageTags":{"Primary":"aa11"},
                "People":[{"Name":"Timothée Chalamet","Role":"Paul Atreides","Type":"Actor"}],"ProviderIds":{"Imdb":"tt1160419","Tmdb":""},
                "MediaSources":[{"Path":"/media/movies/Dune (2021)/Dune.mkv","Size":3000,"Container":"mkv","MediaStreams":[
                  {"Type":"Video","DisplayTitle":"1080p H264"},{"Type":"Audio","DisplayTitle":"English AAC"}]}]}]}
                """;
        }

        if (query["Ids"] == SeriesId)
        {
            return $$"""{"Items":[{"Id":"{{SeriesId}}","Name":"Test Series","Type":"Series"}]}""";
        }

        if (query["IncludeItemTypes"] == "Season")
        {
            return $$"""
                {"Items":[{"Id":"{{OtherSeason}}","Name":"Season 1","IndexNumber":1,"SeriesId":"ffffffffffffffffffffffffffffffff"},
                {"Id":"{{SeasonOne}}","Name":"Season 1","IndexNumber":1,"SeriesId":"{{SeriesId}}"}]}
                """;
        }

        if (query["ParentId"] == SeasonOne)
        {
            return """
                {"Items":[{"Id":"22222222222222222222222222222222","Name":"Second","IndexNumber":2,"ParentIndexNumber":1},
                {"Id":"11111111111111111111111111111111","Name":"Pilot","IndexNumber":1,"ParentIndexNumber":1}]}
                """;
        }

        if (query["IncludeItemTypes"] == "Movie")
        {
            return $$$"""
                {"Items":[{"Id":"{{{MovieId}}}","Name":"Dune","Type":"Movie","ProductionYear":2021,"CommunityRating":7.8,"OfficialRating":"PG-13",
                "ImageTags":{"Primary":"aa11"}},{"Name":"Broken"}],"TotalRecordCount":57}
                """;
        }

        return """{"Items":[],"TotalRecordCount":0}""";
    }
}
