using System.Net;
using Depot.Domain.Common;
using Depot.Domain.Settings;

namespace Depot.Tests.Domain;

public sealed class JellyfinSettingsTests
{
    private const string Key = "a683810a6b6f459a849a7126a2673247";

    [Theory]
    [InlineData("http://localhost:8096", "http://localhost:8096/")]
    [InlineData(" http://192.168.1.10:8096/ ", "http://192.168.1.10:8096/")]
    [InlineData("https://media.example.com/jellyfin", "https://media.example.com/jellyfin/")]
    [InlineData("https://media.example.com/jellyfin//", "https://media.example.com/jellyfin/")]
    [InlineData("http://[::1]:8096", "http://[::1]:8096/")]
    [InlineData("http://jellyfin:8096", "http://jellyfin:8096/")]
    public void AServerOnThisMachineOrTheNetworkIsAccepted(string url, string expected)
    {
        Assert.Equal(expected, JellyfinSettings.Create(url, Key).Url.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("jellyfin:8096")]
    [InlineData("ftp://jellyfin/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://admin:secret@jellyfin:8096")]
    [InlineData("http://jellyfin:8096/?api_key=x")]
    [InlineData("http://jellyfin:8096/#fragment")]
    // Cloud metadata services and other machine-local or broadcast addresses.
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[fe80::1]:8096")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("http://0.0.0.0:8096")]
    [InlineData("http://224.0.0.1:8096")]
    [InlineData("http://255.255.255.255:8096")]
    public void OtherAddressesAreRefusedOnTheUrlField(string url)
    {
        var exception = Assert.Throws<DomainValidationException>(() => JellyfinSettings.Create(url, Key));

        Assert.Equal("url", exception.Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("a683810a6b6f459a849a7126a2673247\" , Token=\"other")]
    [InlineData("a683810a6b6f459a849a7126a2673247\r\nX-Injected: 1")]
    public void AKeyThatIsntOneIsRefused(string apiKey)
    {
        var exception = Assert.Throws<DomainValidationException>(() => JellyfinSettings.Create("http://localhost:8096", apiKey));

        Assert.Equal("apiKey", exception.Field);
    }

    [Fact]
    public void ABlankKeyKeepsTheStoredOneForTheSameServer()
    {
        var saved = JellyfinSettings.Create("http://localhost:8096", Key);

        var updated = saved.With("http://localhost:8096/", "");

        Assert.Equal(Key, updated.ApiKey);
    }

    [Fact]
    public void TheStoredKeyIsNeverSentToAnotherServer()
    {
        var saved = JellyfinSettings.Create("http://localhost:8096", Key);

        var exception = Assert.Throws<DomainValidationException>(() => saved.With("http://attacker.example:8096", null));

        Assert.Equal("apiKey", exception.Field);
        Assert.Equal("0123456789abcdef0123456789abcdef", saved.With("http://other:8096", "0123456789abcdef0123456789abcdef").ApiKey);
    }

    [Fact]
    public void TheKeyNeverShowsInToString()
    {
        Assert.DoesNotContain(Key, JellyfinSettings.Create("http://localhost:8096", Key).ToString());
        Assert.DoesNotContain(Key, new Depot.Application.Settings.JellyfinSettingsInput("http://localhost:8096", Key).ToString());
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.0.20", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("169.254.169.254", false)]
    [InlineData("169.254.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("240.0.0.1", false)]
    public void OnlyAddressesAServerCanHaveAreAllowed(string address, bool allowed)
    {
        Assert.Equal(allowed, ServerAddresses.IsAllowed(IPAddress.Parse(address)));
    }
}
