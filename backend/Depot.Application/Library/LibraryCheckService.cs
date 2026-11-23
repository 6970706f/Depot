using Depot.Domain.Library;

namespace Depot.Application.Library;

public sealed record FolderCheckView(
    string Role,
    string Label,
    string ConfiguredPath,
    string ResolvedPath,
    bool Exists,
    bool CanRead,
    bool CanWrite,
    long? AvailableBytes,
    string? MountPoint,
    string? Problem);

// Ready is true when imports can run: the libraries can be written, the mapped downloads read,
// and there is at least one mapping.
public sealed record LibraryCheckView(bool Ready, IReadOnlyList<FolderCheckView> Folders, IReadOnlyList<string> Notes);

// Checks that the library folders can be written and that every mapped download folder can be
// read, so imports never start against folders that would fail them.
public sealed class LibraryCheckService(ILibraryFileSystem fileSystem, IPathEnvironment environment)
{
    public LibraryCheckView Check(LibrarySettings settings)
    {
        var home = environment.HomeDirectory;
        var paths = settings.Resolve(home);
        var folders = new List<FolderCheckView>
        {
            Check("movies", "Movies", settings.MoviesPath, paths.MoviesRoot, write: true),
            Check("series", "Series", settings.SeriesPath, paths.SeriesRoot, write: true),
        };

        foreach (var mapping in settings.PathMappings)
        {
            var resolved = LibraryPaths.Resolve(mapping.DepotPath, home, "depotPath", "folder as Depot sees it");
            folders.Add(Check("downloads", $"Downloads ({mapping.QbittorrentPath})", mapping.DepotPath, resolved, write: false));
        }

        var ready = settings.PathMappings.Count > 0 && folders.All(folder => folder.Problem is null);
        return new LibraryCheckView(ready, folders, Notes(settings, folders));
    }

    private FolderCheckView Check(string role, string label, string configured, string resolved, bool write)
    {
        var report = fileSystem.InspectFolder(resolved, write);
        string? problem = null;
        if (!report.Exists)
        {
            // Library folders are never created automatically: a typo would otherwise create a
            // folder that Jellyfin doesn't watch.
            problem = role == "downloads"
                ? "This folder doesn't exist here. Check the path as Depot sees it."
                : "This folder doesn't exist. Create it (the same folder your Jellyfin library points to), then check again.";
        }
        else if (!report.CanRead)
        {
            problem = "Depot can't read this folder. Check its permissions.";
        }
        else if (write && !report.CanWrite)
        {
            problem = "Depot can't write to this folder. Check its permissions.";
        }

        if (problem is not null && report.Error is not null)
        {
            problem = $"{problem} ({report.Error})";
        }

        return new FolderCheckView(
            role, label, configured, resolved, report.Exists, report.CanRead, report.CanWrite, report.AvailableBytes, report.MountPoint, problem);
    }

    // Explains what Auto and Hardlink will do with these folders.
    private static List<string> Notes(LibrarySettings settings, List<FolderCheckView> folders)
    {
        var notes = new List<string>();
        if (settings.PathMappings.Count == 0)
        {
            notes.Add("No path mappings, so nothing will be imported: Depot only reads downloads from mapped folders. "
                + "Map qBittorrent's download folder to the same folder as Depot sees it (the same path on both sides when qBittorrent runs on this machine).");
        }

        var libraries = folders.Where(folder => folder.Role != "downloads" && folder.MountPoint is not null);
        var downloads = folders.Where(folder => folder.Role == "downloads" && folder.MountPoint is not null).ToList();
        foreach (var library in libraries)
        {
            foreach (var download in downloads)
            {
                notes.Add(library.MountPoint == download.MountPoint
                    ? $"{library.Label} and {download.Label} are on the same filesystem, so imports can use hardlinks (no extra space)."
                    : $"{library.Label} and {download.Label} are on different filesystems: Auto copies the files, and Hardlink mode fails.");
            }
        }

        return notes;
    }
}
