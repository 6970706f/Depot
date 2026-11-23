using Depot.Domain.Common;

namespace Depot.Domain.Downloads;

// A magnet link with at least one usable info hash.
public sealed class MagnetLink
{
    private const string Field = "magnet";
    private const int MaxLength = 8192;
    private const string V1Prefix = "urn:btih:";
    // "1220" is the multihash header for SHA-256 with a 32-byte digest.
    private const string V2Prefix = "urn:btmh:1220";

    private MagnetLink(string uri, TorrentHashes hashes, string? displayName)
    {
        Uri = uri;
        Hashes = hashes;
        DisplayName = displayName;
    }

    public string Uri { get; }

    public TorrentHashes Hashes { get; }

    public string? DisplayName { get; }

    public static MagnetLink Parse(string? text)
    {
        var uri = text?.Trim();
        if (string.IsNullOrEmpty(uri))
        {
            throw new DomainValidationException(Field, "Enter a magnet link.");
        }

        if (uri.Length > MaxLength)
        {
            throw new DomainValidationException(Field, "This magnet link is too long.");
        }

        // qBittorrent reads one link per line from the field the link is sent in, so a line
        // break would smuggle in a second torrent that skips every check here. Spaces have no
        // place in a link either: they are written as %20 or +.
        if (uri.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new DomainValidationException(Field, "A magnet link can't contain spaces or line breaks.");
        }

        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(Field, "Enter a link starting with magnet:?");
        }

        var parameters = uri["magnet:?".Length..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .Select(pair => (Key: pair[0].ToLowerInvariant(), Value: Decode(pair[1])))
            .ToList();

        var exactTopics = parameters.Where(parameter => parameter.Key == "xt").Select(parameter => parameter.Value).ToList();
        // A hybrid link carries both hashes.
        var v1Hash = exactTopics.Select(ReadV1Hash).FirstOrDefault(hash => hash is not null);
        var v2Hash = exactTopics.Select(ReadV2Hash).FirstOrDefault(hash => hash is not null);
        if (v1Hash is null && v2Hash is null)
        {
            throw new DomainValidationException(Field, "This magnet link is missing a valid info hash (xt=urn:btih:…).");
        }

        var displayName = parameters.FirstOrDefault(parameter => parameter.Key == "dn").Value?.Trim();
        return new MagnetLink(uri, new TorrentHashes(v1Hash, v2Hash), string.IsNullOrEmpty(displayName) ? null : displayName);
    }

    private static string? ReadV1Hash(string topic)
    {
        if (!topic.StartsWith(V1Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = topic[V1Prefix.Length..];
        if (hash.Length == 40 && IsHex(hash))
        {
            return hash.ToLowerInvariant();
        }

        // Some links write the same 20 bytes in base32 (32 characters).
        return hash.Length == 32 && TryDecodeBase32(hash, out var bytes)
            ? Convert.ToHexStringLower(bytes)
            : null;
    }

    private static string? ReadV2Hash(string topic)
    {
        if (!topic.StartsWith(V2Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = topic[V2Prefix.Length..];
        return hash.Length == 64 && IsHex(hash) ? hash[..40].ToLowerInvariant() : null;
    }

    private static bool IsHex(string text) => text.All(char.IsAsciiHexDigit);

    private static bool TryDecodeBase32(string text, out byte[] bytes)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        bytes = new byte[text.Length * 5 / 8];
        var buffer = 0;
        var bitCount = 0;
        var index = 0;
        foreach (var character in text.ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes[index++] = (byte)(buffer >> bitCount);
                buffer &= (1 << bitCount) - 1;
            }
        }

        return true;
    }

    // Magnet parameters are URL-encoded, with "+" for spaces in the display name.
    private static string Decode(string value)
    {
        try
        {
            return System.Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
