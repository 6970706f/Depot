using Depot.Domain.Downloads;

namespace Depot.Domain.Library;

// One file of a torrent, relative to the torrent's save path ("/"-separated), and its size.
public sealed record PlannedFile(string RelativePath, long SizeBytes);

// One video to import with the subtitles that come along. Folder is the destination relative
// to the library root: "<Title>" for a movie, "<Title>/<season folder>" for a series.
public sealed record PlannedImport(PlannedFile Video, string Folder, IReadOnlyList<PlannedFile> Subtitles);

// Decides what of a finished download goes to the library and where, from the person's choice
// alone: Depot never derives a title or a season from file names. Only downloaded videos that
// aren't samples are imported, each with the subtitles named after it; .nfo files, images,
// samples and everything else stay in the download.
public static class ImportPlanner
{
    // existingFolders lists the folders already inside the series folder, so an existing
    // "Season 1" or "Specials" is reused instead of creating "Season 01" next to it.
    public static IReadOnlyList<PlannedImport> Plan(
        LibraryTarget target,
        IReadOnlyList<TorrentContentFile> files,
        IReadOnlyList<string> existingFolders)
    {
        var downloaded = files.Where(file => file.IsDownloaded).ToList();
        var paths = downloaded.Select(file => file.RelativePath).ToList();
        var sizes = downloaded.ToDictionary(file => file.RelativePath, file => file.SizeBytes, StringComparer.Ordinal);
        var folder = FolderFor(target, existingFolders);

        return downloaded
            .Where(file => MediaFiles.IsVideo(file.RelativePath) && !MediaFiles.IsSample(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(video => new PlannedImport(
                new PlannedFile(video.RelativePath, video.SizeBytes),
                folder,
                MediaFiles.SubtitlesFor(video.RelativePath, paths).Select(path => new PlannedFile(path, sizes[path])).ToList()))
            .ToList();
    }

    public static string FolderFor(LibraryTarget target, IReadOnlyList<string> existingFolders)
    {
        if (target.Type == MediaType.Movie)
        {
            return target.Title;
        }

        var season = target.Season!.Value;
        var seasonFolder = existingFolders.FirstOrDefault(name => SeasonFolders.Parse(name) == season) ?? SeasonFolders.NameFor(season);
        return $"{target.Title}/{seasonFolder}";
    }
}
