using Depot.Domain.Common;
using Depot.Domain.Settings;

namespace Depot.Tests.Domain;

public sealed class QBittorrentSettingsTests
{
    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData(" https://seedbox.example/qbittorrent ", "https://seedbox.example/qbittorrent/")]
    public void Urls_are_normalized_with_a_trailing_slash(string url, string expected)
    {
        Assert.Equal(expected, QBittorrentSettings.Create(url, "admin", "secret").Url.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:8080")]
    [InlineData("ftp://localhost")]
    [InlineData("http://user:pass@localhost:8080")]
    [InlineData("http://localhost:8080/?x=1")]
    public void Invalid_urls_are_refused(string url)
    {
        var error = Assert.Throws<DomainValidationException>(() => QBittorrentSettings.Create(url, "admin", "secret"));
        Assert.Equal("url", error.Field);
    }

    [Fact]
    public void A_blank_password_keeps_the_saved_one_for_the_same_url()
    {
        var saved = QBittorrentSettings.Create("http://localhost:8080", "admin", "secret");

        var updated = saved.With("http://localhost:8080/", "other", "");

        Assert.Equal("secret", updated.Password);
        Assert.Equal("other", updated.Username);
    }

    [Fact]
    public void A_new_url_needs_the_password_again()
    {
        var saved = QBittorrentSettings.Create("http://localhost:8080", "admin", "secret");

        var error = Assert.Throws<DomainValidationException>(() => saved.With("http://attacker.example", "admin", null));

        Assert.Equal("password", error.Field);
    }

    [Fact]
    public void ToString_never_shows_the_password()
    {
        Assert.DoesNotContain("secret", QBittorrentSettings.Create("http://localhost:8080", "admin", "secret").ToString());
    }
}
