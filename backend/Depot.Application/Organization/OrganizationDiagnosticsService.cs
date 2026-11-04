using Depot.Domain.Organization;

namespace Depot.Application.Organization;

// Checks that the library folders can be written and that every mapped download folder can
// be read, so imports never start against folders that would fail them.
public sealed class OrganizationDiagnosticsService(IMediaOrganizer organizer, IPathEnvironment environment)
{
    public async Task<OrganizationDiagnosticsView> CheckAsync(OrganizationSettings settings, CancellationToken cancellationToken)
    {
        var home = environment.HomeDirectory;
        var paths = settings.Resolve(home);
        var folders = new List<FolderCheckView>
        {
            await CheckAsync("movies", "Movies", settings.MoviesPath, paths.MoviesRoot, FolderAccess.Write, cancellationToken),
            await CheckAsync("series", "Series", settings.SeriesPath, paths.SeriesRoot, FolderAccess.Write, cancellationToken),
        };

        foreach (var mapping in settings.PathMappings)
        {
            var resolved = LibraryPaths.Resolve(mapping.DepotPath, home, "depotPath", "folder as Depot sees it");
            folders.Add(await CheckAsync(
                "downloads", $"Downloads ({mapping.QbittorrentPath})", mapping.DepotPath, resolved, FolderAccess.Read, cancellationToken));
        }

        return new OrganizationDiagnosticsView(folders.All(folder => folder.Problem is null), folders, Notes(settings, folders));
    }

    private async Task<FolderCheckView> CheckAsync(
        string role,
        string label,
        string configured,
        string resolved,
        FolderAccess access,
        CancellationToken cancellationToken)
    {
        var report = await organizer.InspectFolderAsync(resolved, access, cancellationToken);
        string? problem = null;
        if (!report.Exists)
        {
            // Library folders are never created automatically: a typo would otherwise create a
            // folder that Jellyfin doesn't watch.
            problem = role == "downloads"
                ? "This folder doesn't exist here. Check the path as Depot sees it."
                : "This folder doesn't exist. Create it (the same folder your Jellyfin library points to), then test again.";
        }
        else if (!report.CanRead)
        {
            problem = "Depot can't read this folder. Check its permissions.";
        }
        else if (access == FolderAccess.Write && !report.CanWrite)
        {
            problem = "Depot can't write to this folder. Check its permissions.";
        }

        if (problem is not null && report.Error is not null)
        {
            problem = $"{problem} ({report.Error})";
        }

        return new FolderCheckView(
            role, label, configured, resolved, report.Exists, report.CanRead, report.CanWrite,
            report.MountPoint, report.AvailableBytes, problem);
    }

    // Explains what Auto and Hardlink will do with these folders: hardlinks only work when the
    // download and the library are on the same filesystem.
    private static List<string> Notes(OrganizationSettings settings, List<FolderCheckView> folders)
    {
        var notes = new List<string>();
        var libraries = folders.Where(folder => folder.Role != "downloads" && folder.MountPoint is not null).ToList();
        var downloads = folders.Where(folder => folder.Role == "downloads" && folder.MountPoint is not null).ToList();

        if (settings.PathMappings.Count == 0)
        {
            notes.Add("No path mappings, so nothing will be imported: Depot only reads downloads from mapped folders. "
                + "Map qBittorrent's download folder to the same folder as Depot sees it (the same path on both sides when qBittorrent runs on this machine).");
        }

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
