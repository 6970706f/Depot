using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Depot.Domain.Common;

namespace Depot.Domain.Downloads;

// A .torrent file that has been read and checked. Its hashes are computed from the exact
// bytes of the info dictionary: SHA-1 for v1 torrents, SHA-256 for v2, both for hybrids.
public sealed class TorrentFile
{
    public const int MaxSizeBytes = 10 * 1024 * 1024;

    private const string Field = "file";
    private const int MaxNesting = 64;
    private const string InvalidMessage = "This file isn't a valid .torrent file.";

    private TorrentFile(string fileName, byte[] content, TorrentHashes hashes, string name)
    {
        FileName = fileName;
        Content = content;
        Hashes = hashes;
        Name = name;
    }

    public string FileName { get; }

    public byte[] Content { get; }

    public TorrentHashes Hashes { get; }

    public string Name { get; }

    public static TorrentFile Parse(string? fileName, byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            throw new DomainValidationException(Field, "Choose a .torrent file.");
        }

        if (content.Length > MaxSizeBytes)
        {
            throw new DomainValidationException(
                Field, $"This file is larger than {MaxSizeBytes / (1024 * 1024)} MB, which is too large for a .torrent file.");
        }

        if (!new BencodeReader(content).TryReadRoot(out var info, out var infoStart, out var infoEnd))
        {
            throw new DomainValidationException(Field, InvalidMessage);
        }

        var infoBytes = content.AsSpan(infoStart, infoEnd - infoStart);
        var isV1 = info.HasPieces;
        var isV2 = info.MetaVersion == 2;
        if (!isV1 && !isV2)
        {
            throw new DomainValidationException(Field, InvalidMessage);
        }

        var hashes = new TorrentHashes(
            isV1 ? Convert.ToHexStringLower(SHA1.HashData(infoBytes)) : null,
            isV2 ? Convert.ToHexStringLower(SHA256.HashData(infoBytes))[..40] : null);

        var safeFileName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (safeFileName.Length == 0)
        {
            safeFileName = "upload.torrent";
        }

        var name = info.NameUtf8 ?? info.Name ?? Path.GetFileNameWithoutExtension(safeFileName);
        return new TorrentFile(safeFileName, content, hashes, name);
    }

    // The few fields of the info dictionary that Depot uses.
    private sealed class InfoFields
    {
        public bool HasPieces { get; set; }

        public long? MetaVersion { get; set; }

        public string? Name { get; set; }

        public string? NameUtf8 { get; set; }
    }

    // Walks the bencode to find the info dictionary and its exact bytes, which the info hash is
    // computed from. Only the fields above are kept; every other value is skipped without being
    // built in memory, so a file packed with millions of tiny values costs one pass and nothing
    // more. The file must be exactly one dictionary, with no repeated keys at the top level or in
    // info: libtorrent could read those differently, and the hash computed here would then not
    // be the one qBittorrent uses.
    private sealed class BencodeReader(byte[] data)
    {
        // Enough digits for any 64-bit number, with its sign.
        private const int MaxNumberLength = 20;

        private int position;

        public bool TryReadRoot(out InfoFields info, out int infoStart, out int infoEnd)
        {
            info = new InfoFields();
            infoStart = infoEnd = -1;
            try
            {
                Expect('d');
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (Peek() != 'e')
                {
                    if (ReadKey(keys) == "info")
                    {
                        infoStart = position;
                        info = ReadInfo();
                        infoEnd = position;
                    }
                    else
                    {
                        Skip(depth: 1);
                    }
                }

                position++;
                return infoStart >= 0 && position == data.Length;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private InfoFields ReadInfo()
        {
            Expect('d');
            var info = new InfoFields();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (Peek() != 'e')
            {
                switch (ReadKey(keys))
                {
                    case "pieces":
                        info.HasPieces = true;
                        Skip(depth: 2);
                        break;
                    case "meta version" when Peek() == 'i':
                        position++;
                        info.MetaVersion = ReadNumberUntil('e');
                        break;
                    case "name" when IsByteString():
                        info.Name = ReadText();
                        break;
                    case "name.utf-8" when IsByteString():
                        info.NameUtf8 = ReadText();
                        break;
                    default:
                        Skip(depth: 2);
                        break;
                }
            }

            position++;
            return info;
        }

        private int Peek() => position < data.Length ? data[position] : -1;

        private bool IsByteString() => Peek() is >= '0' and <= '9';

        private void Expect(char token)
        {
            if (Peek() != token)
            {
                throw new FormatException();
            }

            position++;
        }

        private string ReadKey(HashSet<string> seen)
        {
            var key = Encoding.UTF8.GetString(ReadBytes());
            return seen.Add(key) ? key : throw new FormatException();
        }

        private string? ReadText()
        {
            var bytes = ReadBytes();
            return bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : null;
        }

        // Moves past one value of any kind.
        private void Skip(int depth)
        {
            if (depth > MaxNesting)
            {
                throw new FormatException();
            }

            switch (Peek())
            {
                case 'i':
                    position++;
                    ReadNumberUntil('e');
                    break;
                case 'l':
                    position++;
                    while (Peek() != 'e')
                    {
                        Skip(depth + 1);
                    }

                    position++;
                    break;
                case 'd':
                    position++;
                    while (Peek() != 'e')
                    {
                        ReadBytes();
                        Skip(depth + 1);
                    }

                    position++;
                    break;
                case >= '0' and <= '9':
                    ReadBytes();
                    break;
                default:
                    throw new FormatException();
            }
        }

        // A byte string: "<length>:<bytes>". The span points into the file; nothing is copied.
        private ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadNumberUntil(':');
            if (length < 0 || length > data.Length - position)
            {
                throw new FormatException();
            }

            var bytes = data.AsSpan(position, (int)length);
            position += (int)length;
            return bytes;
        }

        private long ReadNumberUntil(char terminator)
        {
            var end = Array.IndexOf(data, (byte)terminator, position, Math.Min(MaxNumberLength + 1, data.Length - position));
            if (end < 0 || end == position)
            {
                throw new FormatException();
            }

            if (!long.TryParse(data.AsSpan(position, end - position), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                throw new FormatException();
            }

            position = end + 1;
            return number;
        }
    }
}
