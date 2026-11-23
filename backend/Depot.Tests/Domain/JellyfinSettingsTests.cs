using Depot.Domain.Common;
using Depot.Domain.Settings;

namespace Depot.Tests.Domain;

public sealed class JellyfinSettingsTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Keeps_a_path_prefix_with_a_trailing_slash()
    {
        Assert.Equal("https://media.example/jellyfin/", JellyfinSettings.Create("https://media.example/jellyfin", Key).Url.ToString());
    }

    [Theory]
    [InlineData("has\"quote0123456789")]
    [InlineData("short")]
    [InlineData("key with spaces 0123456789")]
    public void Refuses_keys_that_could_break_the_header(string key)
    {
        Assert.Equal("apiKey", Assert.Throws<DomainValidationException>(() => JellyfinSettings.Create("http://localhost:8096", key)).Field);
    }

    [Fact]
    public void A_blank_key_is_kept_only_for_the_same_server()
    {
        var saved = JellyfinSettings.Create("http://localhost:8096", Key);

        Assert.Equal(Key, saved.With("http://localhost:8096/", " ").ApiKey);
        Assert.Equal("apiKey", Assert.Throws<DomainValidationException>(() => saved.With("http://other:8096", null)).Field);
    }

    [Fact]
    public void ToString_never_shows_the_key()
    {
        Assert.DoesNotContain(Key, JellyfinSettings.Create("http://localhost:8096", Key).ToString());
    }
}
