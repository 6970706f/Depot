using Depot.Domain.Common;
using Depot.Domain.Downloads;

namespace Depot.Tests.Domain;

public sealed class MagnetLinkTests
{
    private const string V1 = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string V2Full = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    [Fact]
    public void ReadsAHexInfoHashAndTheDisplayName()
    {
        var magnet = MagnetLink.Parse($"  magnet:?xt=urn:btih:{V1.ToUpperInvariant()}&dn=Some+Show%20S01  ");

        Assert.Equal(V1, magnet.Hashes.V1);
        Assert.Null(magnet.Hashes.V2);
        Assert.Equal("Some Show S01", magnet.DisplayName);
        Assert.Equal(V1, magnet.Hashes.Id);
    }

    [Fact]
    public void ReadsABase32InfoHash()
    {
        // The same 20 bytes as V1, written in base32.
        var base32 = "VPG67K6N56V4335LZXX2XTPPVPG67K6N";

        Assert.Equal(V1, MagnetLink.Parse($"magnet:?xt=urn:btih:{base32}").Hashes.V1);
    }

    [Fact]
    public void AHybridLinkIsKnownByItsV2Hash()
    {
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{V1}&xt=urn:btmh:1220{V2Full}");

        Assert.Equal(V1, magnet.Hashes.V1);
        Assert.Equal(V2Full[..40], magnet.Hashes.V2);
        Assert.Equal(V2Full[..40], magnet.Hashes.Id);
        Assert.Null(magnet.DisplayName);
    }

    [Theory]
    // qBittorrent reads one link per line, so these would add a second torrent unchecked.
    [InlineData("\nhttp://example.com/other.torrent")]
    [InlineData("\r\nmagnet:?xt=urn:btih:1111111111111111111111111111111111111111")]
    [InlineData("&dn=two words")]
    [InlineData("&dn=tab\there")]
    [InlineData("&dn=nul\0")]
    [InlineData("&dn=line\u2028separator")]
    public void RefusesSpacesAndControlCharacters(string suffix)
    {
        var exception = Assert.Throws<DomainValidationException>(() => MagnetLink.Parse($"magnet:?xt=urn:btih:{V1}{suffix}"));

        Assert.Equal("magnet", exception.Field);
        Assert.Contains("spaces or line breaks", exception.Message);
    }

    [Theory]
    [InlineData(null, "Enter a magnet link.")]
    [InlineData("   ", "Enter a magnet link.")]
    [InlineData("http://example.com/a.torrent", "Enter a link starting with magnet:?")]
    [InlineData("magnet:?dn=no-hash", "missing a valid info hash")]
    [InlineData("magnet:?xt=urn:btih:abc", "missing a valid info hash")]
    [InlineData("magnet:?xt=urn:btih:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "missing a valid info hash")]
    public void RefusesLinksWithoutAUsableHash(string? text, string message)
    {
        var exception = Assert.Throws<DomainValidationException>(() => MagnetLink.Parse(text));

        Assert.Equal("magnet", exception.Field);
        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public void RefusesVeryLongLinks()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            MagnetLink.Parse($"magnet:?xt=urn:btih:{V1}&dn={new string('a', 9000)}"));

        Assert.Contains("too long", exception.Message);
    }
}
