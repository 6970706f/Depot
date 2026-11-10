using Depot.Domain.Media;

namespace Depot.Tests.Domain;

public sealed class MediaCategoriesTests
{
    private static readonly MediaLibrary Shows = new("lib", "Shows", MediaLibraryKind.Shows, []);

    [Fact]
    public void ALibraryNamedAnimeMakesItsItemsAnime()
    {
        var library = new MediaLibrary("lib", "Anime Series", MediaLibraryKind.Shows, []);

        Assert.True(MediaCategories.IsAnime(Series(), library));
    }

    [Theory]
    [InlineData(new[] { "Anime" }, new string[0], new string[0], null)]
    [InlineData(new[] { "Action" }, new[] { "anime", "mecha" }, new string[0], null)]
    [InlineData(new[] { "Animation" }, new string[0], new[] { "Japan" }, null)]
    [InlineData(new[] { "Animation", "Drama" }, new string[0], new string[0], "君の名は。")]
    public void AnimeIsRecognizedFromItsMetadata(string[] genres, string[] tags, string[] countries, string? originalTitle)
    {
        var item = Series() with { Genres = genres, Tags = tags, ProductionLocations = countries, OriginalTitle = originalTitle };

        Assert.True(MediaCategories.IsAnime(item, Shows));
    }

    [Theory]
    [InlineData(new[] { "Animation", "Family" }, new[] { "United States of America" }, "Toy Story")]
    [InlineData(new[] { "Drama" }, new[] { "Japan" }, "東京物語")]
    // Kanji alone could be Chinese.
    [InlineData(new[] { "Animation" }, new[] { "China" }, "哪吒之魔童降世")]
    public void AnimationFromElsewhereOrJapaneseLiveActionIsNot(string[] genres, string[] countries, string originalTitle)
    {
        var item = Series() with { Genres = genres, ProductionLocations = countries, OriginalTitle = originalTitle };

        Assert.False(MediaCategories.IsAnime(item, Shows));
    }

    [Fact]
    public void ACollectionIsNeverAnime()
    {
        var collection = new MediaItem { ExternalId = "c", Kind = MediaItemKind.Collection, Name = "Ghibli", Genres = ["Anime"] };

        Assert.False(MediaCategories.IsAnime(collection, new MediaLibrary("lib", "Anime", MediaLibraryKind.Collections, [])));
    }

    private static MediaItem Series() => new() { ExternalId = "s", Kind = MediaItemKind.Series, Name = "Show" };
}
