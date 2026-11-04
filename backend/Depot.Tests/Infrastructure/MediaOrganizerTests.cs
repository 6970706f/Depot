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

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

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

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Hardlink), IgnoreFileId, CancellationToken.None);

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
        var hardlinkOnly = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Hardlink, otherFilesystem.Path), IgnoreFileId, CancellationToken.None);
        var automatic = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Auto, otherFilesystem.Path), IgnoreFileId, CancellationToken.None);

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

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Auto), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal(ImportMode.Copy, outcome.UsedMode);
    }

    [Fact]
    public async Task HardlinkModeNeverFallsBackToCopy()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var organizer = Organizer(new ScriptedTransfer { HardLink = HardLinkResult.CrossDevice });

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Hardlink), IgnoreFileId, CancellationToken.None);

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

        var added = await Organizer().ImportAsync(request, IgnoreFileId, CancellationToken.None);
        var taken = await Organizer().ImportAsync(
            request with { Video = request.Video with { DestinationPath = existing } }, IgnoreFileId, CancellationToken.None);

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

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Conflict, outcome.Result);
        Assert.Equal("the episode already in the library", await File.ReadAllTextAsync(existing));
    }


    [Fact]
    public async Task RepeatingAnImportDoesNotDuplicateFiles()
    {
        var source = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(1024));

        var first = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);
        var second = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, first.Result);
        Assert.Equal(ImportResult.Conflict, second.Result);
        Assert.Single(Directory.GetFiles(Path.Join(series, "Show", "Season 01")));
    }

    [Fact]
    public async Task FailsClearlyWithoutEnoughSpace()
    {
        var source = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(1024));
        var organizer = Organizer(diskSpace: new FixedDiskSpace(10));

        var outcome = await organizer.ImportAsync(MovieRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

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

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

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
            IgnoreFileId, CancellationToken.None);
        var foreignSource = await Organizer().ImportAsync(
            request with { Video = new ImportFile(outside, request.Video.DestinationPath, 16) },
            IgnoreFileId, CancellationToken.None);

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
        }, IgnoreFileId, CancellationToken.None);

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

        var outcome = await Organizer().ImportAsync(MovieRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

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

        var outcome = await Organizer().ImportAsync(EpisodeRequest(source, ImportMode.Copy), IgnoreFileId, CancellationToken.None);

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
        }, IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.True(File.Exists(Path.Join(movies, "Dune (2021)", "Dune.2021.mkv")));
    }

    [Fact]
    public async Task ACopyThatFailsHalfwayLeavesNothingBehind()
    {
        var movie = root.WriteFile("downloads/Dune.2021.mkv", RandomBytes(4096));
        var episode = root.WriteFile("downloads/Show.S01E01.mkv", RandomBytes(4096));
        var organizer = Organizer(new ScriptedTransfer { HardLink = HardLinkResult.CrossDevice, FailCopyHalfway = true });

        var movieOutcome = await organizer.ImportAsync(MovieRequest(movie, ImportMode.Auto), IgnoreFileId, CancellationToken.None);
        var episodeOutcome = await organizer.ImportAsync(EpisodeRequest(episode, ImportMode.Auto), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Failed, movieOutcome.Result);
        Assert.Equal(ImportResult.Failed, episodeOutcome.Result);
        Assert.Empty(Directory.GetFileSystemEntries(movies));
        // The folders this import created are removed again because they're empty.
        Assert.Empty(Directory.GetFileSystemEntries(series));
        Assert.True(File.Exists(movie));
        Assert.True(File.Exists(episode));
    }

    [Fact]
    public async Task RecoveryCleansUpOnlyTheJobsOwnTemporaryFiles()
    {
        var destination = Path.Join(series, "Show", "Season 01", "Show.S01E01.mkv");
        var leftover = root.WriteFile("library/series/Show/Season 01/.depot-job-7-v.tmp", RandomBytes(10));
        var otherJob = root.WriteFile("library/series/Show/Season 01/.depot-job-8-v.tmp", RandomBytes(10));

        var outcome = await Organizer().RecoverAsync(new RecoveryRequest(7, destination, 100, 12345), CancellationToken.None);

        Assert.Equal(RecoveryResult.NotStarted, outcome.Result);
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(otherJob));
    }

    [Fact]
    public async Task RecoveryNeverTakesAFileForTheImportsBecauseOfItsSize()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var destination = Path.Join(series, "Show", "Season 01", "Show.S01E01.mkv");
        var content = RandomBytes(100);
        root.WriteFile("library/series/Show/Season 01/Show.S01E01.mkv", content);
        var otherFile = root.WriteFile("downloads/other.mkv", RandomBytes(100));

        var nothingRecorded = await Organizer().RecoverAsync(new RecoveryRequest(7, destination, 100, null), CancellationToken.None);
        var anotherFile = await Organizer().RecoverAsync(
            new RecoveryRequest(7, destination, 100, new FileTransfer().FileId(otherFile)), CancellationToken.None);

        Assert.Equal(RecoveryResult.Failed, nothingRecorded.Result);
        Assert.Contains("can't confirm this import wrote it", nothingRecorded.Message);
        Assert.Equal(RecoveryResult.Failed, anotherFile.Result);
        Assert.Contains("isn't the file this import wrote", anotherFile.Message);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task RecoveryRefusesItsOwnFileWhenItsSizeChanged()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var destination = root.WriteFile("library/series/Show/Season 01/Show.S01E01.mkv", RandomBytes(100));

        var outcome = await Organizer().RecoverAsync(
            new RecoveryRequest(7, destination, 99, new FileTransfer().FileId(destination)), CancellationToken.None);

        Assert.Equal(RecoveryResult.Failed, outcome.Result);
        Assert.Contains("instead of 99", outcome.Message);
    }

    [Theory]
    [InlineData(ImportMode.Hardlink)]
    [InlineData(ImportMode.Copy)]
    public async Task AnImportRecordsItsFileBeforePublishingIt(ImportMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = root.WriteFile("downloads/Dune.2021/Dune.2021.mkv", RandomBytes(4096));
        var destination = Path.Join(movies, "Dune (2021)", "Dune.2021.mkv");
        long? recorded = null;

        var outcome = await Organizer().ImportAsync(MovieRequest(source, mode), (fileId, _) =>
        {
            Assert.False(File.Exists(destination));
            recorded = fileId;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal(mode, outcome.UsedMode);
        Assert.NotNull(recorded);
        Assert.Equal(new FileTransfer().FileId(destination), recorded);

        // As if Depot had stopped before saving the finished import: the file is recognized,
        // as often as recovery runs, and left as it is.
        var request = new RecoveryRequest(1, destination, 4096, recorded);
        Assert.Equal(RecoveryResult.Completed, (await Organizer().RecoverAsync(request, CancellationToken.None)).Result);
        Assert.Equal(RecoveryResult.Completed, (await Organizer().RecoverAsync(request, CancellationToken.None)).Result);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(destination));
        AssertNoTemporaries();
    }

    [Theory]
    [InlineData(ImportMode.Hardlink)]
    [InlineData(ImportMode.Copy)]
    public async Task NothingIsPublishedWhenTheFileCantBeRecorded(ImportMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = root.WriteFile("downloads/Dune.2021/Dune.2021.mkv", RandomBytes(4096));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Organizer().ImportAsync(
            MovieRequest(source, mode),
            (_, _) => throw new InvalidOperationException("Simulated: the job couldn't be saved."),
            CancellationToken.None));

        // Not even the movie folder this import created is left.
        Assert.Empty(Directory.GetFileSystemEntries(movies));
        Assert.True(File.Exists(source));
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

    [Theory]
    [InlineData(ImportMode.Hardlink)]
    [InlineData(ImportMode.Copy)]
    public async Task SubtitlesGoNextToTheVideoWithTheSameStrategy(ImportMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var video = root.WriteFile("downloads/Dune/Dune.mkv", RandomBytes(4096));
        var plain = root.WriteFile("downloads/Dune/Dune.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var english = root.WriteFile("downloads/Dune/Dune.en.ass", "[Script Info]\n");
        var request = WithSubtitles(DuneRequest(video, mode), plain, english);

        var outcome = await Organizer().ImportAsync(request, IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal("Subtitles imported: Dune.srt, Dune.en.ass.", outcome.Message);
        var folder = Path.Join(movies, "Dune (2021)");
        Assert.Equal(["Dune.en.ass", "Dune.mkv", "Dune.srt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        foreach (var source in new[] { plain, english })
        {
            var destination = Path.Join(folder, Path.GetFileName(source));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(destination));
            // Hardlinked subtitles are the downloaded files themselves; copies are separate files.
            Assert.Equal(mode == ImportMode.Hardlink, new FileTransfer().FileId(source) == new FileTransfer().FileId(destination));
        }

        AssertNoTemporaries();
    }

    [Fact]
    public async Task ASubtitleNameTakenByAnotherFileIsLeftOutWithoutOverwriting()
    {
        var video = root.WriteFile("downloads/Dune/Dune.mkv", RandomBytes(4096));
        var taken = root.WriteFile("downloads/Dune/Dune.en.srt", "the downloaded subtitle");
        var free = root.WriteFile("downloads/Dune/Dune.pt.srt", "legenda");
        var existing = root.WriteFile("library/movies/Dune (2021)/Dune.en.srt", "someone else's subtitle");

        var outcome = await Organizer().ImportAsync(WithSubtitles(DuneRequest(video, ImportMode.Copy), taken, free), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Contains("Subtitles imported: Dune.pt.srt.", outcome.Message);
        Assert.Contains("Dune.en.srt (a different file with that name is already there; nothing was overwritten)", outcome.Message);
        Assert.Equal("someone else's subtitle", await File.ReadAllTextAsync(existing));
        Assert.Equal("the downloaded subtitle", await File.ReadAllTextAsync(taken));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task ASubtitleAlreadyInPlaceIsNotDuplicated()
    {
        var video = root.WriteFile("downloads/Dune/Dune.mkv", RandomBytes(4096));
        var subtitle = root.WriteFile("downloads/Dune/Dune.en.srt", "same text");
        var shippedTwice = root.WriteFile("downloads/Dune/Subs/Dune.en.srt", "same text");
        root.WriteFile("library/movies/Dune (2021)/Dune.en.srt", "same text");

        var outcome = await Organizer().ImportAsync(
            WithSubtitles(DuneRequest(video, ImportMode.Copy), subtitle, shippedTwice), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Equal("Subtitles imported: Dune.en.srt.", outcome.Message);
        Assert.Equal(["Dune.en.srt", "Dune.mkv"], Directory.GetFiles(Path.Join(movies, "Dune (2021)")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task AFailedVideoTakesItsNewSubtitlesBackOut()
    {
        var video = root.WriteFile("downloads/Dune/Dune.mkv", RandomBytes(4096));
        var subtitle = root.WriteFile("downloads/Dune/Dune.en.srt", "text");
        // The subtitle fits, the video doesn't.
        var organizer = Organizer(diskSpace: new FixedDiskSpace(MediaOrganizer.FreeSpaceMarginBytes + 1024));

        var outcome = await organizer.ImportAsync(WithSubtitles(DuneRequest(video, ImportMode.Copy), subtitle), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Failed, outcome.Result);
        Assert.Empty(Directory.GetFileSystemEntries(movies));
        Assert.True(File.Exists(subtitle));
    }

    [Fact]
    public async Task ASubtitleOutsideTheTorrentIsRefused()
    {
        var video = root.WriteFile("downloads/Dune/Dune.mkv", RandomBytes(4096));
        var outside = root.WriteFile("elsewhere/Dune.en.srt", "text");

        var outcome = await Organizer().ImportAsync(WithSubtitles(DuneRequest(video, ImportMode.Copy), outside), IgnoreFileId, CancellationToken.None);

        Assert.Equal(ImportResult.Completed, outcome.Result);
        Assert.Contains("Subtitles left out: Dune.en.srt", outcome.Message);
        Assert.False(File.Exists(Path.Join(movies, "Dune (2021)", "Dune.en.srt")));
    }

    private ImportRequest DuneRequest(string video, ImportMode mode) => MovieRequest(video, mode) with
    {
        Video = new ImportFile(video, Path.Join(movies, "Dune (2021)", Path.GetFileName(video)), new FileInfo(video).Length),
    };

    private ImportRequest WithSubtitles(ImportRequest request, params string[] sources) => request with
    {
        Subtitles = sources
            .Select(source => new ImportFile(
                source, Path.Join(Path.GetDirectoryName(request.Video.DestinationPath)!, Path.GetFileName(source)), new FileInfo(source).Length))
            .ToList(),
    };

    // For tests about the file system alone, where no job is saved.
    private static readonly ExpectFileAsync IgnoreFileId = (_, _) => Task.CompletedTask;

    private MediaOrganizer Organizer(IFileTransfer? transfer = null, IDiskSpace? diskSpace = null) =>
        new(transfer ?? new FileTransfer(), diskSpace ?? new DriveDiskSpace(), NullLogger<MediaOrganizer>.Instance);

    private ImportRequest MovieRequest(string source, ImportMode mode, string? sourceRoot = null) => new(
        1, MediaType.Movie, mode, movies, sourceRoot ?? downloads, sourceRoot ?? downloads,
        new ImportFile(source, Path.Join(movies, "Dune (2021)", "Dune.2021.mkv"), new FileInfo(source).Length), []);

    private ImportRequest EpisodeRequest(string source, ImportMode mode) => new(
        2, MediaType.Series, mode, series, downloads, downloads,
        new ImportFile(source, Path.Join(series, "Show", "Season 01", "Show.S01E01.mkv"), new FileInfo(source).Length), []);

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

        public async Task CopyAsync(string sourcePath, string destinationPath, Func<Task> created, CancellationToken cancellationToken)
        {
            if (!FailCopyHalfway)
            {
                await real.CopyAsync(sourcePath, destinationPath, created, cancellationToken);
                return;
            }

            var content = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            await File.WriteAllBytesAsync(destinationPath, content[..(content.Length / 2)], cancellationToken);
            throw new IOException("Simulated failure in the middle of a copy.");
        }

        public long? FileId(string path) => real.FileId(path);
    }

    private sealed class FixedDiskSpace(long availableBytes) : IDiskSpace
    {
        public long? AvailableBytes(string path) => availableBytes;

        public string? MountPointOf(string path) => "/";
    }
}
