using System.Security.Cryptography;
using System.Text;
using Depot.Domain.Common;
using Depot.Domain.Downloads;

namespace Depot.Tests.Domain;

public sealed class TorrentFileTests
{
    private static readonly byte[] V1Info = Dict(
        ("length", Int(1024)), ("name", Str("Dune.2021.mkv")), ("piece length", Int(16384)), ("pieces", Str(new string('x', 20))));

    [Fact]
    public void HashesTheExactBytesOfAV1Info()
    {
        var torrent = TorrentFile.Parse("Dune.torrent", Dict(("announce", Str("http://tracker/")), ("info", V1Info)));

        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(V1Info)), torrent.Hashes.V1);
        Assert.Null(torrent.Hashes.V2);
        Assert.Equal("Dune.2021.mkv", torrent.Name);
    }

    [Fact]
    public void HashesAV2InfoWithSha256AndAHybridWithBoth()
    {
        var v2Info = Dict(("meta version", Int(2)), ("name", Str("v2")), ("piece length", Int(16384)));
        var hybridInfo = Dict(("meta version", Int(2)), ("name", Str("hybrid")), ("pieces", Str(new string('x', 20))));

        var v2 = TorrentFile.Parse("v2.torrent", Dict(("info", v2Info)));
        var hybrid = TorrentFile.Parse("hybrid.torrent", Dict(("info", hybridInfo)));

        Assert.Null(v2.Hashes.V1);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(v2Info))[..40], v2.Hashes.V2);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(hybridInfo)), hybrid.Hashes.V1);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(hybridInfo))[..40], hybrid.Hashes.V2);
    }

    [Fact]
    public void PrefersTheUtf8NameAndFallsBackToTheFileName()
    {
        var named = TorrentFile.Parse("a.torrent", Dict(("info", Dict(("name", Str("plain")), ("name.utf-8", Str("Ação")), ("pieces", Str("x"))))));
        var unnamed = TorrentFile.Parse("../../Some Upload.torrent", Dict(("info", Dict(("pieces", Str("x"))))));

        Assert.Equal("Ação", named.Name);
        Assert.Equal("Some Upload.torrent", unnamed.FileName);
        Assert.Equal("Some Upload", unnamed.Name);
    }

    [Fact]
    public void RefusesARepeatedInfoKey()
    {
        // libtorrent and Depot could read different info dictionaries, and so compute different hashes.
        var other = Dict(("name", Str("other")), ("pieces", Str("y")));
        var content = DictWithRepeats(("info", V1Info), ("info", other));

        AssertInvalid(content);
    }

    [Fact]
    public void RefusesRepeatedKeysInsideInfo()
    {
        AssertInvalid(Dict(("info", DictWithRepeats(("name", Str("a")), ("name", Str("b")), ("pieces", Str("x"))))));
    }

    [Theory]
    [InlineData("e")]
    [InlineData("garbage")]
    [InlineData("d1:ai1ee")]
    public void RefusesAnythingAfterTheTorrent(string trailing)
    {
        AssertInvalid([.. Dict(("info", V1Info)), .. Encoding.ASCII.GetBytes(trailing)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not bencode at all")]
    [InlineData("li1ei2ee")]
    [InlineData("d4:infod6:pieces1:x")]
    [InlineData("d4:infoi1ee")]
    [InlineData("d4:info99999999999999999999999:xe")]
    [InlineData("d4:infod4:name4:Dune12:piece lengthi1eee")]
    public void RefusesFilesThatArentTorrents(string text)
    {
        var exception = Assert.Throws<DomainValidationException>(() => TorrentFile.Parse("a.torrent", Encoding.ASCII.GetBytes(text)));

        Assert.Equal("file", exception.Field);
    }

    [Fact]
    public void RefusesDeepNesting()
    {
        var nested = new StringBuilder("d4:infod6:pieces1:x5:extra");
        nested.Append('l', 100).Append('e', 100).Append("ee");

        AssertInvalid(Encoding.ASCII.GetBytes(nested.ToString()));
    }

    [Fact]
    public void ManyTinyValuesAreSkippedWithoutBeingBuilt()
    {
        // About 9 MB of "i0e": built as objects, this would take several hundred megabytes.
        var filler = new StringBuilder("l");
        filler.Insert(1, "i0e", 3_000_000).Append('e');
        var content = Dict(("comment", Encoding.ASCII.GetBytes(filler.ToString())), ("info", V1Info));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var torrent = TorrentFile.Parse("big.torrent", content);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(V1Info)), torrent.Hashes.V1);
        Assert.True(allocated < 1024 * 1024, $"Parsing allocated {allocated} bytes.");
    }

    [Fact]
    public void RefusesFilesOverTheLimit()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            TorrentFile.Parse("big.torrent", new byte[TorrentFile.MaxSizeBytes + 1]));

        Assert.Contains("larger than", exception.Message);
    }

    private static void AssertInvalid(byte[] content)
    {
        var exception = Assert.Throws<DomainValidationException>(() => TorrentFile.Parse("a.torrent", content));

        Assert.Equal("This file isn't a valid .torrent file.", exception.Message);
    }

    private static byte[] Str(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. Encoding.ASCII.GetBytes($"{bytes.Length}:"), .. bytes];
    }

    private static byte[] Int(long value) => Encoding.ASCII.GetBytes($"i{value}e");

    // Keys are written in the given order, which is also how a test repeats one.
    private static byte[] Dict(params (string Key, byte[] Value)[] entries) => DictWithRepeats(entries);

    private static byte[] DictWithRepeats(params (string Key, byte[] Value)[] entries) =>
        [(byte)'d', .. entries.SelectMany(entry => Str(entry.Key).Concat(entry.Value)), (byte)'e'];
}
