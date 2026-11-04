using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Infrastructure.Organization;
using Depot.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Depot.Tests.Infrastructure;

public sealed class MediaOrganizerTests : IDisposable
{
    private readonly TempDirectory root = new();
    private readonly string downloads;
    private readonly string movies;
    private readonly string series;

    public MediaOrganizerTests()
    {
        downloads = root.CreateFolder("downloads");
        movies = root.CreateFolder("library", "movies");
        series = root.CreateFolder("library", "series");
    }

    public void Dispose() => root.Dispose();

    [Fact]
    public async Task CopiesAMovieAndKeepsTheOriginal()
    {
        var source = root.WriteFile("downloads/Dune.2021/Dune.2021.mkv", RandomBytes(4096));

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal(ImportMode.Copy, outcome.UsedMode);
        var destination = Path.Join(movies, "Dune (2021)", "Dune.2021.mkv");
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(destination));
        Assert.True(File.Exists(source));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task HardlinksOnTheSameFilesystem()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = root.WriteFile("downloads/Dune.2021/Dune.2021.mkv", RandomBytes(4096));

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Hardlink), CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal(ImportMode.Hardlink, outcome.UsedMode);
        // A hardlink is the same file under a second name: growing one grows the other.
        var destination = Path.Join(movies, "Dune (2021)", "Dune.2021.mkv");
        await using (var stream = new FileStream(destination, FileMode.Append))
        {
            stream.WriteByte(1);
        }

        Assert.Equal(4097, new FileInfo(source).Length);
    }

    [Fact]
    public async Task HardlinksAcrossRealFilesystemsFailOrFallBackToCopy()
    {
        // /dev/shm is a separate tmpfs on most Linux systems; without it there's no second
        // filesystem to try, and the simulated cases below cover the logic.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        {
            return;
        }

        using var otherFilesystem = new TempDirectory("/dev/shm");
        var disk = new DriveDiskSpace();
        if (disk.MountPointOf(otherFilesystem.Path) == disk.MountPointOf(movies))
        {
            return;
        }

        var source = otherFilesystem.WriteFile("Dune.2021.mkv", RandomBytes(2048));
        var hardlinkOnly = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Hardlink, otherFilesystem.Path), CancellationToken.None);
        var automatic = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Auto, otherFilesystem.Path), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, hardlinkOnly.Result);
        Assert.Contains("different filesystems", hardlinkOnly.Message);
        Assert.Equal(ImportResult.Completed, automatic.Result);
        Assert.Equal(ImportMode.Copy, automatic.UsedMode);
    }

    [Fact]
    public async Task AutoCopiesWhenHardlinksAreNotPossible()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var organizer = Organizer(new ScriptedTransfer { HardLink = HardLinkResult.CrossDevice });

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Auto), CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal(ImportMode.Copy, outcome.UsedMode);
    }

    [Fact]
    public async Task HardlinkModeNeverFallsBackToCopy()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var organizer = Organizer(new ScriptedTransfer { HardLink = HardLinkResult.CrossDevice });

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Hardlink), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains("different filesystems", outcome.Message);
        Assert.False(Directory.Exists(Path.Join(movies, "Dune (2021)")));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task AddsToAnExistingMovieFolderWithoutOverwriting()
    {
        var extra = root.WriteFile("downloads/Dune.2021/Behind.the.Scenes.mkv", RandomBytes(512));
        var existing = root.WriteFile("library/movies/Dune (2021)/Dune.2021.mkv", "the movie already in the library");
        var request = MovieRequest(extra, ImportMode.Copy) with
        {
            Video = new ImportFile(extra, Path.Join(movies, "Dune (2021)", "Behind.the.Scenes.mkv"), 512),
        };

        var added = await Organizer().ImportAsync(request, CancellationToken.None);
        var taken = await Organizer().ImportAsync(
            request with { Video = request.Video with { DestinationPath = existing } }, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, added.Result);
        Assert.Equal(ImportResult.Conflict, taken.Result);
        Assert.Contains("already exists", taken.Message);
        Assert.Equal("the movie already in the library", await File.ReadAllTextAsync(existing));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task NeverOverwritesAnExistingEpisode()
    {
        var source = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(1024));
        var existing = root.WriteFile("library/series/Show/Season 01/Show.S01E01.mkv", "the episode already in the library");

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Conflict, outcome.Result);
        Assert.Equal("the episode already in the library", await File.ReadAllTextAsync(existing));
    }


    [Fact]
    public async Task RepeatingAnImportDoesNotDuplicateFiles()
    {
        var source = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(1024));

        var first = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), CancellationToken.None);
        var second = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Completed, first.Result);
        Assert.Equal(ImportResult.Conflict, second.Result);
        Assert.Single(Directory.GetFiles(Path.Join(series, "Show", "Season 01")));
    }

    [Fact]
    public async Task FailsClearlyWithoutEnoughSpace()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var organizer = Organizer(diskSpace: new FixedDiskSpace(10));

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains("Not enough free space", outcome.Message);
        Assert.False(Directory.Exists(Path.Join(movies, "Dune (2021)")));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task FailsClearlyWithoutPermission()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var source = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(1024));
        File.SetUnixFileMode(series, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains("Permission denied", outcome.Message);
    }

    [Fact]
    public async Task RefusesPathsOutsideTheirFolders()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var outside = root.WriteFile("elsewhere/secret.mkv", RandomBytes(16));
        var request = MovieRequest(source, ImportMode.Copy);

        var traversal = await Organizer().ImportAsync(
            request with { Video = request.Video with { DestinationPath = Path.Join(movies, "..", "escape", "Dune.mkv") } },
            CancellationToken.None);
        var foreignSource = await Organizer().ImportAsync(
            request with { Video = new ImportFile(outside, request.Video.DestinationPath, 16) },
            CancellationToken.None);

        Assert.Equal(ImportResult.Failed, traversal.Result);
        Assert.Contains("refused", traversal.Message);
        Assert.Equal(ImportResult.Failed, foreignSource.Result);
        Assert.Contains("outside the torrent's folder", foreignSource.Message);
        Assert.False(Directory.Exists(root.Combine("library", "escape")));
    }

    [Fact]
    public async Task RefusesSymbolicLinksAsSources()
    {
        var target = root.WriteFile("elsewhere/secret.mkv", RandomBytes(16));
        var link = Path.Join(downloads, "Dune.2021.mkv");
        File.CreateSymbolicLink(link, target);

        var outcome = await Organizer().ImportAsync(MovieRequest(link, ImportMode.Copy) with
        {
            Video = new ImportFile(link, Path.Join(movies, "Dune (2021)", "Dune.2021.mkv"), 16),
        }, CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains("symbolic link", outcome.Message);
    }

    [Fact]
    public async Task RefusesSourcesBehindALinkedFolder()
    {
        var secret = root.WriteFile("elsewhere/secret.mkv", RandomBytes(16));
        var linkedFolder = Path.Join(downloads, "Dune.2021");
        Directory.CreateSymbolicLink(linkedFolder, Path.GetDirectoryName(secret)!);
        var source = Path.Join(linkedFolder, "secret.mkv");

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains($"{linkedFolder} is a symbolic link", outcome.Message);
        Assert.False(Directory.Exists(Path.Join(movies, "Dune (2021)")));
    }

    [Fact]
    public async Task RefusesLibraryFoldersThatAreLinks()
    {
        var source = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(16));
        var elsewhere = root.CreateFolder("elsewhere");
        Directory.CreateSymbolicLink(Path.Join(series, "Show"), elsewhere);

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Contains("in the library is a symbolic link", outcome.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task ALinkedLibraryRootIsAllowed()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(16));
        var linkedMovies = Path.Join(root.Path, "movies-link");
        Directory.CreateSymbolicLink(linkedMovies, movies);
        var request = MovieRequest(source, ImportMode.Copy);

        var outcome = await Organizer().ImportAsync(request with
        {
            LibraryRoot = linkedMovies,
            Video = request.Video with { DestinationPath = Path.Join(linkedMovies, "Dune (2021)", "Dune.2021.mkv") },
        }, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.True(File.Exists(Path.Join(movies, "Dune (2021)", "Dune.2021.mkv")));
    }

    [Fact]
    public async Task ACopyThatFailsHalfwayLeavesNothingBehind()
    {
        var movie = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(4096));
        var episode = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(4096));
        var organizer = Organizer(new ScriptedTransfer { HardLink = HardLinkResult.CrossDevice, FailCopyHalfway = true });

        var movieOutcome = await organizer.ImportAsync(MovieRequest(movie, ImportMode.Auto), CancellationToken.None);
        var episodeOutcome = await organizer.ImportAsync(EpisodeRequest(episode, ImportMode.Auto), CancellationToken.None);

        Assert.Equal(ImportResult.Failed, movieOutcome.Result);
        Assert.Equal(ImportResult.Failed, episodeOutcome.Result);
        Assert.Empty(Directory.GetFileSystemEntries(movies));
        // The folders this import created are removed again because they're empty.
        Assert.Empty(Directory.GetFileSystemEntries(series));
        Assert.True(File.Exists(movie));
        Assert.True(File.Exists(episode));
    }

    [Fact]
    public async Task RecoveryCleansUpAndChecksWhatIsInPlace()
    {
        var destination = Path.Join(series, "Show", "Season 01", "Show.S01E01.mkv");
        var leftover = root.WriteFile("library/series/Show/Season 01/.depot-job-7-v.tmp", RandomBytes(10));
        var otherJob = root.WriteFile("library/series/Show/Season 01/.depot-job-8-v.tmp", RandomBytes(10));

        var notStarted = await Organizer().RecoverAsync(new RecoveryRequest(7, destination, 100), CancellationToken.None);
        root.WriteFile("library/series/Show/Season 01/Show.S01E01.mkv", RandomBytes(100));
        var completed = await Organizer().RecoverAsync(new RecoveryRequest(7, destination, 100), CancellationToken.None);
        var mismatch = await Organizer().RecoverAsync(new RecoveryRequest(7, destination, 99), CancellationToken.None);

        Assert.Equal(RecoveryResult.NotStarted, notStarted.Result);
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(otherJob));
        Assert.Equal(RecoveryResult.Completed, completed.Result);
        Assert.Equal(RecoveryResult.Failed, mismatch.Result);
    }

    [Fact]
    public async Task ListsVisibleFolders()
    {
        root.CreateFolder("library", "series", "The Last of Us", "Season 1");
        root.CreateFolder("library", "series", "Arcane");
        root.CreateFolder("library", "series", ".depot-job-3.tmp");
        root.WriteFile("library/series/notes.txt", "not a folder");

        var folders = await Organizer().ListFoldersAsync(series, CancellationToken.None);
        var missing = await Organizer().ListFoldersAsync(root.Combine("nope"), CancellationToken.None);

        Assert.Equal(["Arcane", "The Last of Us"], folders);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task InspectsFolders()
    {
        var writable = await Organizer().InspectFolderAsync(movies, FolderAccess.Write, CancellationToken.None);
        var missing = await Organizer().InspectFolderAsync(root.Combine("nope"), FolderAccess.Write, CancellationToken.None);

        Assert.True(writable is { Exists: true, CanRead: true, CanWrite: true });
        Assert.NotNull(writable.MountPoint);
        Assert.False(missing.Exists);
        Assert.Empty(Directory.GetFileSystemEntries(movies));
    }

    private MediaOrganizer Organizer(IFileTransfer? transfer = null, IDiskSpace? diskSpace = null) =>
        new(transfer ?? new FileTransfer(), diskSpace ?? new DriveDiskSpace(), NullLogger<MediaOrganizer>.Instance);

    private ImportRequest MovieRequest(string source, ImportMode mode, string? sourceRoot = null) => new(
        1, MediaType.Movie, mode, movies, sourceRoot ?? downloads, sourceRoot ?? downloads,
        new ImportFile(source, Path.Join(movies, "Dune (2021)", "Dune.2021.mkv"), new FileInfo(source).Length));

    private ImportRequest EpisodeRequest(string source, ImportMode mode) => new(
        2, MediaType.Series, mode, series, downloads, downloads,
        new ImportFile(source, Path.Join(series, "Show", "Season 01", "Show.S01E01.mkv"), new FileInfo(source).Length));

    private void AssertNoTemporaries()
    {
        var leftovers = Directory.EnumerateFileSystemEntries(root.Combine("library"), ".depot-job-*", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    // Lets a test decide what hardlinking returns and make a copy fail halfway.
    private sealed class ScriptedTransfer : IFileTransfer
    {
        private readonly FileTransfer real = new();

        public HardLinkResult HardLink { get; init; } = HardLinkResult.Created;

        public bool FailCopyHalfway { get; init; }

        public HardLinkResult TryCreateHardLink(string sourcePath, string destinationPath) =>
            HardLink == HardLinkResult.Created ? real.TryCreateHardLink(sourcePath, destinationPath) : HardLink;

        public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            if (!FailCopyHalfway)
            {
                await real.CopyAsync(sourcePath, destinationPath, cancellationToken);
                return;
            }

            var content = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            await File.WriteAllBytesAsync(destinationPath, content[..(content.Length / 2)], cancellationToken);
            throw new IOException("Simulated failure in the middle of a copy.");
        }
    }

    private sealed class FixedDiskSpace(long availableBytes) : IDiskSpace
    {
        public long? AvailableBytes(string path) => availableBytes;

        public string? MountPointOf(string path) => "/";
    }
}
