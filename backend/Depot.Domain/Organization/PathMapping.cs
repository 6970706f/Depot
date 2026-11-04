using System.Text.RegularExpressions;
using Depot.Domain.Common;

namespace Depot.Domain.Organization;

// One folder as qBittorrent reports it (inside its container, for example) and the same
// folder as Depot sees it. DepotPath is kept as configured and may start with "~/".
public sealed partial record PathMapping(string QbittorrentPath, string DepotPath)
{
    public static PathMapping Create(string? qbittorrentPath, string? depotPath, int index)
    {
        var remote = NormalizeRemote(qbittorrentPath)
            ?? throw new DomainValidationException(
                $"pathMappings[{index}].qbittorrentPath",
                "Enter the folder exactly as qBittorrent shows it, such as /downloads.");

        var local = depotPath?.Trim();
        if (string.IsNullOrEmpty(local))
        {
            throw new DomainValidationException($"pathMappings[{index}].depotPath", "Enter the same folder as Depot sees it.");
        }

        return new PathMapping(remote, local);
    }

    // qBittorrent may run on another system, so its paths are compared as text, with "/" as
    // the separator and no trailing slash. Null for relative paths or paths with "..".
    public static string? NormalizeRemote(string? path)
    {
        var text = path?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(text) || !RemoteRootPattern().IsMatch(text))
        {
            return null;
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        var drive = text.StartsWith('/') ? string.Empty : segments[0];
        var rest = text.StartsWith('/') ? segments : segments[1..];
        return rest.Length == 0 ? $"{drive}/" : $"{drive}/{string.Join('/', rest)}";
    }

    // "/downloads" or a Windows drive such as "D:/Torrents".
    [GeneratedRegex(@"^(?:/|[A-Za-z]:/)")]
    private static partial Regex RemoteRootPattern();
}

// Translates qBittorrent paths into Depot paths. The longest matching mapping wins, and a
// match must end at a folder boundary: "/downloads" covers "/downloads/x", not "/downloads2".
// The mappings are also the list of folders Depot may read downloads from: a path no mapping
// covers is never used, whatever qBittorrent reports. When qBittorrent runs on the same
// machine, a folder is mapped to itself.
public sealed class PathMapper
{
    private readonly IReadOnlyList<(string Remote, string Local)> mappings;

    // Local paths must already be resolved and absolute.
    public PathMapper(IEnumerable<(string Remote, string Local)> mappings)
    {
        this.mappings = mappings.OrderByDescending(mapping => mapping.Remote.Length).ToList();
    }

    public bool HasMappings => mappings.Count > 0;

    // The Depot path for a qBittorrent path, or null when no mapping covers it.
    public string? ToDepotPath(string qbittorrentPath) => Map(qbittorrentPath)?.Path;

    // The mapped folder, as Depot sees it, that holds a qBittorrent path: everything read for
    // that path must stay inside it. Null when no mapping covers the path.
    public string? MappedRootOf(string qbittorrentPath) => Map(qbittorrentPath)?.Root;

    private (string? Path, string Root)? Map(string qbittorrentPath)
    {
        var remote = PathMapping.NormalizeRemote(qbittorrentPath);
        if (remote is null)
        {
            return null;
        }

        foreach (var (mappedRemote, local) in mappings)
        {
            var prefix = mappedRemote.EndsWith('/') ? mappedRemote : mappedRemote + "/";
            if (remote != mappedRemote && !remote.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = remote.Length > mappedRemote.Length ? remote[prefix.Length..] : string.Empty;
            return (rest.Length == 0 ? local : LibraryPaths.CombineInside(local, rest), local);
        }

        return null;
    }
}
