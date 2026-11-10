using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Depot.Application.Jellyfin;
using Depot.Application.Settings;
using Depot.Domain.Media;
using Depot.Domain.Settings;
using Depot.Infrastructure;
using Depot.Infrastructure.Jellyfin;
using Depot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Depot.Tests.Infrastructure;

// The real client against a scripted Jellyfin REST API. The answers are cut from what
// Jellyfin 10.11 sends.
public sealed class JellyfinClientTests
{
    private const string Key = "a683810a6b6f459a849a7126a2673247";
    private const string Library = "f137a2dd21bbc1b99aa5c0f6bf02a805";
    private const string Dune = "66da3bb5c0522ccf8d8051ca159a5440";

    private readonly ScriptedJellyfin server = new();
    private readonly SavedSettings settings = new();
    private readonly JellyfinClient client;

    public JellyfinClientTests()
    {
        client = new JellyfinClient(new JellyfinHttp(CreateHttpClient(server)), settings);
    }

    [Fact]
    public async Task TheKeyTravelsOnlyInTheAuthorizationHeader()
    {
        server.Answer("/System/Info", Json("""{"ServerName":"media","Version":"10.11.11","Id":"748d0cbbde53417ca38d9ae059c39712"}"""));

        var info = await client.GetServerInfoAsync(CancellationToken.None);

        Assert.Equal(new JellyfinServerInfo("media", "10.11.11"), info);
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain(Key, request.Url);
        Assert.StartsWith("MediaBrowser Client=\"Depot\"", request.Authorization);
        Assert.EndsWith($"Token=\"{Key}\"", request.Authorization);
    }

    [Fact]
    public async Task ARefusedKeyIsReportedAsSuch()
    {
        server.Answer("/System/Info", new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.InvalidApiKey, exception.Failure);
        Assert.DoesNotContain(Key, exception.Message);
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://attacker.example/System/Info");
        server.Answer("/System/Info", redirect);

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.UnexpectedResponse, exception.Failure);
        Assert.Contains("redirects", exception.Message);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "text/plain", "Jellyfin Server is loading. Please try again shortly.", JellyfinFailure.Unreachable)]
    [InlineData(HttpStatusCode.OK, "text/html", "<html>router login</html>", JellyfinFailure.UnexpectedResponse)]
    [InlineData(HttpStatusCode.OK, "application/json", "{not json", JellyfinFailure.UnexpectedResponse)]
    [InlineData(HttpStatusCode.OK, "application/json", "[]", JellyfinFailure.UnexpectedResponse)]
    [InlineData(HttpStatusCode.OK, "application/json", """{"ServerName":"x","Version":"1"}""", JellyfinFailure.UnexpectedResponse)]
    [InlineData(HttpStatusCode.InternalServerError, "application/json", "{}", JellyfinFailure.UnexpectedResponse)]
    public async Task AnswersThatArentJellyfinsAreRefused(HttpStatusCode status, string contentType, string body, JellyfinFailure failure)
    {
        server.Answer("/System/Info", new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(failure, exception.Failure);
    }

    [Fact]
    public async Task AServerThatDoesntAnswerInTimeIsATimeout()
    {
        server.Delay = TimeSpan.FromSeconds(5);
        var slowClient = new JellyfinClient(new JellyfinHttp(CreateHttpClient(server, TimeSpan.FromMilliseconds(100))), settings);

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => slowClient.GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.Timeout, exception.Failure);
    }

    [Fact]
    public async Task NothingIsSentBeforeJellyfinIsSetUp()
    {
        settings.Current = null;

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetLibrariesAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.NotConfigured, exception.Failure);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task LibrariesAreReadWithTheirKind()
    {
        server.Answer("/Library/VirtualFolders", Json("""
            [
              {"Name":"Shows","CollectionType":"tvshows","ItemId":"a656b907eb3a73532e40e44b968d0225","Locations":["/media/series"]},
              {"Name":"Movies","CollectionType":"movies","ItemId":"f137a2dd21bbc1b99aa5c0f6bf02a805","Locations":["/media/movies"]},
              {"Name":"Music","CollectionType":"music","ItemId":"0c0d4e9a7f0f4a51a2f6b1e7c3d2a100","Locations":[]},
              {"Name":"No id","CollectionType":"movies","ItemId":"../../System","Locations":[]}
            ]
            """));

        var libraries = await client.GetLibrariesAsync(CancellationToken.None);

        Assert.Equal(["Shows", "Movies", "Music"], libraries.Select(library => library.Name));
        Assert.Equal([MediaLibraryKind.Shows, MediaLibraryKind.Movies, MediaLibraryKind.Other], libraries.Select(library => library.Kind));
        Assert.Equal(["/media/movies"], libraries[1].Locations);
        Assert.False(libraries[2].IsSynced);
    }

    [Fact]
    public async Task APageOfItemsAsksForTheLibraryKindsAndPosition()
    {
        server.Answer("/Items", Json("""{"Items":[],"TotalRecordCount":0,"StartIndex":200}"""));

        await client.GetItemsAsync(Library, [MediaItemKind.Series, MediaItemKind.Season, MediaItemKind.Episode], 200, 100, CancellationToken.None);

        var query = Query(Assert.Single(server.Requests).Url);
        Assert.Equal(Library, query["ParentId"]);
        Assert.Equal("true", query["Recursive"]);
        Assert.Equal("Series,Season,Episode", query["IncludeItemTypes"]);
        Assert.Equal("200", query["StartIndex"]);
        Assert.Equal("100", query["Limit"]);
        Assert.Contains("MediaSources", query["Fields"]!.Split(','));
        Assert.Equal("false", query["CollapseBoxSetItems"]);
    }

    [Fact]
    public async Task AnItemIsReadWithEveryFileItsStreamsAndImages()
    {
        server.Answer("/Items", Json($$"""{"Items":[{{DuneJson}}],"TotalRecordCount":2,"StartIndex":0}"""));

        var page = await client.GetItemsAsync(Library, [MediaItemKind.Movie], 0, 100, CancellationToken.None);

        Assert.Equal((1, 0, 2), (page.Received, page.Skipped, page.TotalCount));
        var dune = Assert.Single(page.Items);
        Assert.Equal((Dune, MediaItemKind.Movie, "Dune", 2021), (dune.ExternalId, dune.Kind, dune.Name, dune.ProductionYear));
        Assert.Equal(new DateOnly(2021, 9, 15), dune.PremiereDate);
        Assert.Equal(7.79, dune.CommunityRating);
        Assert.Equal(["Science Fiction", "Adventure"], dune.Genres);
        Assert.Equal(["Legendary Pictures"], dune.Studios);
        Assert.Equal("tt1160419", dune.ProviderIds["Imdb"]);
        Assert.Equal("019c71391dff264d638a8a783c33a40d", dune.Images.Primary);
        Assert.Equal(["0f277a35d6c76ff96563ad63cdc44226"], dune.Images.Backdrops);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T15:07:41.2136713Z"), dune.AddedAt);

        Assert.Equal(["1080p", "720p"], dune.Files.Select(file => file.Name));
        var file = dune.Files[1];
        Assert.Equal(("91f1ef10866343eaf3dc39abe5c50cca", "mkv", 123440L), (file.ExternalId, file.Container, file.SizeBytes));
        Assert.Equal(TimeSpan.FromTicks(30230000), file.Runtime);
        Assert.Equal([MediaStreamKind.Video, MediaStreamKind.Audio], file.Streams.Select(stream => stream.Kind));
        Assert.Equal((1280, 720, "SDR"), (file.Streams[0].Width, file.Streams[0].Height, file.Streams[0].VideoRange));
        Assert.Equal(("aac", "eng", 1), (file.Streams[1].Codec, file.Streams[1].Language, file.Streams[1].Channels));
    }

    [Fact]
    public async Task AnItemThatCantBeReadIsSkippedRatherThanTheWholePage()
    {
        server.Answer("/Items", Json($$"""
            {"Items":[
              {"Name":"No id","Type":"Movie"},
              {"Id":"07aebe72925f0b877d593f046abf77c3","Name":"Odd","Type":"Episode","IndexNumber":"one"},
              {"Id":"14f3f6c2f0a4d358ca4d2255250e1293","Name":"Folder","Type":"Folder"},
              {"Id":"65b8f4184b2df303d9cba51735d40a0f","Name":"  ","Type":"Episode","SeriesId":"d80f95ea7f22df4562f43f625e06d64a","SeasonId":"not-a-guid","IndexNumber":-4,"ParentIndexNumber":2,"ProductionYear":99999,"CommunityRating":42},
              {{DuneJson}}
            ],"TotalRecordCount":5}
            """));

        var page = await client.GetItemsAsync(Library, [MediaItemKind.Movie, MediaItemKind.Episode], 0, 100, CancellationToken.None);

        Assert.Equal((5, 3), (page.Received, page.Skipped));
        Assert.Equal(2, page.Items.Count);
        var episode = page.Items[0];
        Assert.Equal("Untitled", episode.Name);
        Assert.Equal("d80f95ea7f22df4562f43f625e06d64a", episode.SeriesExternalId);
        Assert.Null(episode.SeasonExternalId);
        Assert.Equal((null, 2, null, null), (episode.IndexNumber, episode.ParentIndexNumber, episode.ProductionYear, episode.CommunityRating));
    }

    [Fact]
    public async Task ALookupKeepsOnlyTheItemsAskedFor()
    {
        server.Answer("/Items", Json($$"""{"Items":[{{DuneJson}},{"Id":"07aebe72925f0b877d593f046abf77c3","Name":"Extra","Type":"Movie"}],"TotalRecordCount":2}"""));

        var found = await client.GetItemsByIdAsync([Dune, "not-an-id"], CancellationToken.None);

        Assert.Equal([Dune], found.Select(item => item.ExternalId));
        Assert.Equal(Dune, Query(Assert.Single(server.Requests).Url)["Ids"]);
    }

    [Fact]
    public async Task ImagesAreCheckedByTheirContent()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        server.Answer($"/Items/{Dune}/Images/Primary/0", Image(png, "image/png"));
        server.Answer($"/Items/{Dune}/Images/Logo/0", Image(Encoding.UTF8.GetBytes("<svg onload=\"alert(1)\"/>"), "image/svg+xml"));
        server.Answer($"/Items/{Dune}/Images/Thumb/0", Image(Encoding.UTF8.GetBytes("<html></html>"), "image/jpeg"));

        var poster = await client.GetImageAsync(Dune, ImageKind.Primary, 0, "019c71391dff264d638a8a783c33a40d", 320, CancellationToken.None);
        var logo = await Assert.ThrowsAsync<JellyfinException>(() => client.GetImageAsync(Dune, ImageKind.Logo, 0, "a", null, CancellationToken.None));
        var thumb = await Assert.ThrowsAsync<JellyfinException>(() => client.GetImageAsync(Dune, ImageKind.Thumb, 0, "a", null, CancellationToken.None));
        var missing = await client.GetImageAsync(Dune, ImageKind.Backdrop, 0, "a", null, CancellationToken.None);

        Assert.Equal("image/png", poster!.ContentType);
        Assert.Equal(png, poster.Content);
        Assert.Equal(JellyfinFailure.UnexpectedResponse, logo.Failure);
        Assert.Equal(JellyfinFailure.UnexpectedResponse, thumb.Failure);
        Assert.Null(missing);
        var query = Query(server.Requests.First().Url);
        Assert.Equal(("019c71391dff264d638a8a783c33a40d", "320"), (query["tag"], query["maxWidth"]));
    }

    [Fact]
    public async Task AnOversizedImageIsRefused()
    {
        var huge = new byte[JellyfinHttp.MaxImageBytes + 1];
        huge[0] = 0xFF;
        huge[1] = 0xD8;
        huge[2] = 0xFF;
        server.Answer($"/Items/{Dune}/Images/Backdrop/0", new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(huge)) });

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetImageAsync(Dune, ImageKind.Backdrop, 0, "a", null, CancellationToken.None));

        Assert.Contains("larger than", exception.Message);
    }

    [Fact]
    public async Task TheConnectionTestChecksTheServerIsJellyfinBeforeSendingTheKey()
    {
        var tester = new JellyfinConnectionTester(new JellyfinHttp(CreateHttpClient(server)));
        server.Answer("/System/Info/Public", Json("""{"ServerName":"nas","Version":"4.8","ProductName":"Some Other Server","Id":"748d0cbbde53417ca38d9ae059c39712"}"""));

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => tester.TestAsync(settings.Current!, CancellationToken.None));

        Assert.Equal(JellyfinFailure.UnexpectedResponse, exception.Failure);
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("Token", request.Authorization);
    }

    [Fact]
    public async Task TheConnectionTestNeedsAFinishedSetupAndAnAcceptedKey()
    {
        var tester = new JellyfinConnectionTester(new JellyfinHttp(CreateHttpClient(server)));
        const string Public = """{"ServerName":"nas","Version":"10.11.11","ProductName":"Jellyfin Server","Id":"748d0cbbde53417ca38d9ae059c39712","StartupWizardCompleted":%DONE%}""";
        server.Answer("/System/Info/Public", Json(Public.Replace("%DONE%", "false")));
        var wizard = await Assert.ThrowsAsync<JellyfinException>(() => tester.TestAsync(settings.Current!, CancellationToken.None));

        server.Answer("/System/Info/Public", Json(Public.Replace("%DONE%", "true")));
        server.Answer("/System/Info", Json("""{"ServerName":"nas","Version":"10.11.11","Id":"748d0cbbde53417ca38d9ae059c39712"}"""));
        var info = await tester.TestAsync(settings.Current!, CancellationToken.None);

        Assert.Contains("setup wizard", wizard.Message);
        Assert.Equal(new JellyfinServerInfo("nas", "10.11.11"), info);
    }

    [Fact]
    public void TheRealHttpClientFollowsNoRedirectsUsesNoProxyAndChecksAddresses()
    {
        using var data = new TempDirectory();
        using var provider = BuildInfrastructure(data);

        var sockets = PrimaryHandler(provider);

        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.UseProxy);
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Fact]
    public async Task TheRealClientConnectsToAnAllowedAddress()
    {
        using var jellyfin = new TinyHttpServer(_ => (200, "Content-Type: application/json\r\n",
            """{"ServerName":"loopback","Version":"10.11.11","Id":"748d0cbbde53417ca38d9ae059c39712"}"""));
        using var data = new TempDirectory();
        using var provider = BuildInfrastructure(data);
        settings.Current = JellyfinSettings.Create(jellyfin.Url, Key);
        var realClient = new JellyfinClient(provider.GetRequiredService<JellyfinHttp>(), settings);

        var info = await realClient.GetServerInfoAsync(CancellationToken.None);

        Assert.Equal("loopback", info.ServerName);
        Assert.StartsWith("GET /System/Info HTTP/1.1", Assert.Single(jellyfin.Requests));
    }

    [Fact]
    public async Task HostsThatOnlyLeadToRefusedAddressesAreNotConnectedTo()
    {
        var refused = await Assert.ThrowsAsync<AddressNotAllowedException>(() => JellyfinNetwork.ResolveAllowedAsync("169.254.169.254", CancellationToken.None));
        var mapped = await Assert.ThrowsAsync<AddressNotAllowedException>(() => JellyfinNetwork.ResolveAllowedAsync("::ffff:169.254.169.254", CancellationToken.None));
        var allowed = await JellyfinNetwork.ResolveAllowedAsync("localhost", CancellationToken.None);

        Assert.Equal("169.254.169.254", refused.Host);
        Assert.NotNull(mapped);
        Assert.All(allowed, address => Assert.True(IPAddress.IsLoopback(address)));
    }

    [Fact]
    public async Task PeopleKeywordsAndCountriesAreReadWithTheItem()
    {
        server.Answer("/Items", Json("""
            {"Items":[{"Id":"66da3bb5c0522ccf8d8051ca159a5440","Name":"Dune","Type":"Movie",
              "Tags":["space opera","anime"],"ProductionLocations":["United States of America"],
              "People":[
                {"Name":"Timothée Chalamet","Id":"da5aa3243db377e39fa6dcbab1bb1763","Role":"Paul Atreides","Type":"Actor","PrimaryImageTag":"456e18a254409337d94c75bf76eb1968"},
                {"Name":"Denis Villeneuve","Id":"0f4d0f0aa4f3a8c0b5c1d2e3f4a5b6c7","Type":"Director"},
                {"Name":"No id","Type":"Actor"}
              ]},
             {"Id":"876f770f32972d0b5536d0addf7cdcdd","Name":"Villeneuve Collection","Type":"BoxSet"}],"TotalRecordCount":2}
            """));

        var page = await client.GetItemsAsync(Library, [MediaItemKind.Movie, MediaItemKind.Collection], 0, 100, CancellationToken.None);

        var dune = page.Items[0];
        Assert.Equal(["space opera", "anime"], dune.Tags);
        Assert.Equal(["United States of America"], dune.ProductionLocations);
        Assert.Equal(
            [new MediaPerson("da5aa3243db377e39fa6dcbab1bb1763", "Timothée Chalamet", PersonKind.Actor, "Paul Atreides", "456e18a254409337d94c75bf76eb1968"),
             new MediaPerson("0f4d0f0aa4f3a8c0b5c1d2e3f4a5b6c7", "Denis Villeneuve", PersonKind.Director, null, null)],
            dune.People);
        Assert.Equal(MediaItemKind.Collection, page.Items[1].Kind);
        Assert.Equal("Movie,BoxSet", Query(Assert.Single(server.Requests).Url)["IncludeItemTypes"]);
        Assert.Contains("People", Query(server.Requests.Single().Url)["Fields"]!.Split(','));
    }

    [Fact]
    public async Task ACollectionsMembersAreReadRecursively()
    {
        server.Answer("/Items", Json("""
            {"Items":[{"Id":"7b1df19bc7f3ed45a0d5ed435de78a62","Name":"Arrival","Type":"Movie"},{"Id":"66da3bb5c0522ccf8d8051ca159a5440","Name":"Dune","Type":"Movie"}],"TotalRecordCount":2}
            """));

        var members = await client.GetCollectionItemIdsAsync("876f770f32972d0b5536d0addf7cdcdd", CancellationToken.None);

        Assert.Equal(["7b1df19bc7f3ed45a0d5ed435de78a62", Dune], members);
        var query = Query(Assert.Single(server.Requests).Url);
        Assert.Equal(("876f770f32972d0b5536d0addf7cdcdd", "true", "Movie,Series"), (query["ParentId"], query["Recursive"], query["IncludeItemTypes"]));
    }

    [Fact]
    public async Task TheCollectionsFolderIsALibraryOfCollections()
    {
        server.Answer("/Library/VirtualFolders", Json("""[{"Name":"Collections","CollectionType":"boxsets","ItemId":"9d7ad6afe9afa2dab1a2f6e00ad28fa6","Locations":[]}]"""));

        var library = Assert.Single(await client.GetLibrariesAsync(CancellationToken.None));

        Assert.Equal(MediaLibraryKind.Collections, library.Kind);
        Assert.Equal([MediaItemKind.Collection], library.ItemKinds);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(handler) { Timeout = timeout ?? JellyfinHttp.Timeout, MaxResponseContentBufferSize = JellyfinHttp.MaxResponseBytes };

    private static ServiceProvider BuildInfrastructure(TempDirectory data)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = data.Path })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new HostEnvironment(data.Path));
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler PrimaryHandler(ServiceProvider provider)
    {
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(JellyfinHttp));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    private static Dictionary<string, string?> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : null);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] content, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    // A movie with two versions, as Jellyfin 10.11 lists it (some fields left out).
    private const string DuneJson = """
        {
          "Name":"Dune","OriginalTitle":"Dune","Id":"66da3bb5c0522ccf8d8051ca159a5440","DateCreated":"2026-11-10T15:07:41.2136713Z",
          "SortName":"dune","PremiereDate":"2021-09-15T03:00:00.0000000Z","OfficialRating":"PG-13","Overview":"Paul Atreides...",
          "Genres":["Science Fiction","Adventure"],"CommunityRating":7.786,"RunTimeTicks":30230000,"ProductionYear":2021,
          "ProviderIds":{"Imdb":"tt1160419","Tmdb":"438631"},"IsFolder":false,"Type":"Movie",
          "Studios":[{"Name":"Legendary Pictures","Id":"5f83e77e3f465e528750d05c6d9cdb0c"}],
          "ImageTags":{"Primary":"019c71391dff264d638a8a783c33a40d","Logo":"a10ab2c700bb140a9020a2325f22d37e"},
          "BackdropImageTags":["0f277a35d6c76ff96563ad63cdc44226"],"LocationType":"FileSystem",
          "MediaSources":[
            {"Protocol":"File","Id":"66da3bb5c0522ccf8d8051ca159a5440","Path":"/media/movies/Dune (2021)/Dune (2021) - 1080p.mkv","Container":"mkv",
             "Size":142218,"Name":"1080p","RunTimeTicks":30230000,"Bitrate":568362,
             "MediaStreams":[{"Codec":"h264","VideoRange":"SDR","VideoRangeType":"SDR","DisplayTitle":"1080p H264 SDR","BitRate":376362,"IsDefault":false,
                              "IsForced":false,"Height":1080,"Width":1920,"Type":"Video","Index":0,"IsExternal":false}]},
            {"Protocol":"File","Id":"91f1ef10866343eaf3dc39abe5c50cca","Path":"/media/movies/Dune (2021)/Dune (2021) - 720p.mkv","Container":"mkv",
             "Size":123440,"Name":"720p","RunTimeTicks":30230000,"Bitrate":518668,
             "MediaStreams":[{"Codec":"h264","VideoRange":"SDR","VideoRangeType":"SDR","DisplayTitle":"720p H264 SDR","Height":720,"Width":1280,"Type":"Video","Index":0},
                             {"Codec":"aac","Language":"eng","VideoRange":"Unknown","DisplayTitle":"English - AAC - Mono","Channels":1,"Type":"Audio","Index":1},
                             {"Codec":"mjpeg","Type":"EmbeddedImage","Index":2}]}
          ]
        }
        """;

    private sealed class SavedSettings : IJellyfinSettingsRepository
    {
        public JellyfinSettings? Current { get; set; } = JellyfinSettings.Create("http://jellyfin:8096", Key);

        public Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(JellyfinSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record SeenRequest(string Url, string Authorization);

    // Answers each path with what a test set; anything else is a 404.
    private sealed class ScriptedJellyfin : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, HttpResponseMessage> answers = new();

        public ConcurrentQueue<SeenRequest> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        public void Answer(string path, HttpResponseMessage response) => answers[path] = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : "";
            Requests.Enqueue(new SeenRequest(request.RequestUri!.ToString(), authorization));
            await Task.Delay(Delay, cancellationToken);
            return answers.TryGetValue(request.RequestUri.AbsolutePath, out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
