using Depot.Application.Imports;
using Depot.Application.Library;
using Depot.Domain.Downloads;
using Depot.Domain.Library;
using Depot.Infrastructure.Imports;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

// The whole import path on real files: choice → finished torrent → jobs → files in the library.
public sealed class ImportFlowTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    [Fact]
    public async Task A_finished_movie_is_imported_with_its_subtitles_and_the_download_is_untouched()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Download(harness, "Dune.2021", ("Dune.2021/Dune.2021.mkv", 3000), ("Dune.2021/Dune.2021.en.srt", 20),
            ("Dune.2021/Sample/sample.mkv", 10), ("Dune.2021/Dune.nfo", 5));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune (2021)", null));

        await RunPassAsync(harness);

        var job = Assert.Single(await JobsAsync(harness));
        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(ImportMode.Hardlink, job.UsedMode);
        Assert.Contains("Dune.2021.en.srt", job.Message);
        var folder = Path.Combine(harness.MoviesRoot, "Dune (2021)");
        Assert.Equal(["Dune.2021.en.srt", "Dune.2021.mkv"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal(File.ReadAllBytes(Source(harness, "Dune.2021/Dune.2021.mkv")), File.ReadAllBytes(Path.Combine(folder, "Dune.2021.mkv")));
        Assert.True(File.Exists(Source(harness, "Dune.2021/Dune.2021.mkv")));
        Assert.True(File.Exists(Source(harness, "Dune.2021/Dune.nfo")));
    }

    [Fact]
    public async Task A_series_goes_to_its_existing_season_folder()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.CreateDirectory(Path.Combine(harness.SeriesRoot, "Show", "Season 1"));
        Download(harness, "Show.S01", ("Show.S01/Show.S01E01.mkv", 100), ("Show.S01/Show.S01E02.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Series, "Show", 1));

        await RunPassAsync(harness);

        Assert.All(await JobsAsync(harness), job => Assert.Equal(ImportJobStatus.Completed, job.Status));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(harness.SeriesRoot, "Show", "Season 1")).Length);
        Assert.False(Directory.Exists(Path.Combine(harness.SeriesRoot, "Show", "Season 01")));
    }

    [Fact]
    public async Task Copy_mode_copies_and_leaves_no_temporary_files()
    {
        await using var harness = await DepotHarness.CreateAsync();
        await SaveSettingsAsync(harness, ImportMode.Copy);
        Download(harness, "Dune", ("Dune/Dune.mkv", 2000));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));

        await RunPassAsync(harness);

        var job = Assert.Single(await JobsAsync(harness));
        Assert.Equal(ImportMode.Copy, job.UsedMode);
        Assert.Equal(["Dune.mkv"], Directory.GetFiles(Path.Combine(harness.MoviesRoot, "Dune")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Nothing_is_overwritten_and_a_retry_works_once_the_file_is_moved()
    {
        await using var harness = await DepotHarness.CreateAsync();
        var existing = Path.Combine(harness.MoviesRoot, "Dune", "Dune.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "someone else's file");
        Download(harness, "Dune", ("Dune/Dune.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));

        await RunPassAsync(harness);
        var failed = Assert.Single(await JobsAsync(harness));

        Assert.Equal(ImportJobStatus.Failed, failed.Status);
        Assert.Contains("already exists", failed.Message);
        Assert.Equal("someone else's file", File.ReadAllText(existing));

        File.Delete(existing);
        await harness.RunAsync<ImportJobService>(service => service.RetryAsync(failed.Id, CancellationToken.None));
        await RunPassAsync(harness);

        Assert.Equal(ImportJobStatus.Completed, Assert.Single(await JobsAsync(harness)).Status);
    }

    [Fact]
    public async Task Unfinished_downloads_and_downloads_without_a_choice_are_left_alone()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Download(harness, "Dune", [("Dune/Dune.mkv", 100)], TorrentStatus.Downloading, 0.5);
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));
        harness.QBittorrent.Add("1111111111111111111111111111111111111111", "Other", "/downloads", [("Other/Other.mkv", 100)]);

        await RunPassAsync(harness);

        Assert.Empty(await JobsAsync(harness));
        Assert.Empty(Directory.GetFileSystemEntries(harness.MoviesRoot));
    }

    [Fact]
    public async Task A_download_saved_outside_every_mapping_fails_with_the_reason()
    {
        await using var harness = await DepotHarness.CreateAsync();
        harness.QBittorrent.Add(Hash, "Dune", "/elsewhere", [("Dune/Dune.mkv", 100)]);
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));

        await RunPassAsync(harness);

        var job = Assert.Single(await JobsAsync(harness));
        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Contains("no path mapping covers", job.Message);
    }

    [Fact]
    public async Task Imports_wait_while_a_library_folder_is_missing()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.Delete(harness.MoviesRoot);
        Download(harness, "Dune", ("Dune/Dune.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));

        await RunPassAsync(harness);

        Assert.Equal(ImportJobStatus.Waiting, Assert.Single(await JobsAsync(harness)).Status);
        var status = await harness.RunAsync<ImportStatusView, ImportJobService>(service => service.StatusAsync(CancellationToken.None));
        Assert.Equal(ImportRunState.Paused, status.Worker.State);
        Assert.Contains("doesn't exist", status.Worker.Message);
    }

    [Fact]
    public async Task A_download_is_examined_only_once()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Download(harness, "Dune", ("Dune/Dune.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));

        await RunPassAsync(harness);
        await RunPassAsync(harness);

        Assert.Single(await JobsAsync(harness));
    }

    [Fact]
    public async Task Skipping_only_marks_the_job()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.Delete(harness.MoviesRoot);
        Download(harness, "Dune", ("Dune/Dune.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));
        await RunPassAsync(harness);
        var job = Assert.Single(await JobsAsync(harness));

        var skipped = await harness.RunAsync<ImportJobView, ImportJobService>(service => service.SkipAsync(job.Id, CancellationToken.None));
        Directory.CreateDirectory(harness.MoviesRoot);
        await RunPassAsync(harness);

        Assert.Equal(ImportJobStatus.Skipped, skipped.Status);
        Assert.Equal(ImportJobStatus.Skipped, Assert.Single(await JobsAsync(harness)).Status);
        Assert.Empty(Directory.GetFileSystemEntries(harness.MoviesRoot));
        Assert.True(File.Exists(Source(harness, "Dune/Dune.mkv")));
        await Assert.ThrowsAsync<InvalidJobStateException>(() =>
            harness.RunAsync<ImportJobService>(service => service.SkipAsync(job.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task An_interrupted_import_runs_again_or_goes_to_a_person()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.Delete(harness.MoviesRoot);
        Download(harness, "Dune", ("Dune/Dune.mkv", 100), ("Dune/Extended.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));
        await RunPassAsync(harness);
        Directory.CreateDirectory(Path.Combine(harness.MoviesRoot, "Dune"));

        // Both jobs were importing when Depot stopped: one left only a temporary copy, the other
        // left the video at its destination.
        foreach (var job in await JobsAsync(harness))
        {
            await harness.RunAsync<IImportJobRepository>(async repository =>
            {
                var tracked = (await repository.GetAsync(job.Id, CancellationToken.None))!;
                tracked.Start(DateTimeOffset.UnixEpoch);
                await repository.SaveAsync(tracked, CancellationToken.None);
            });
        }

        var jobs = await JobsAsync(harness);
        var temporary = Path.Combine(harness.MoviesRoot, "Dune", $"{MediaImporter.TemporaryPrefix}{jobs.Single(job => job.FileName == "Dune.mkv").Id}-v.tmp");
        File.WriteAllText(temporary, "partial");
        File.WriteAllText(Path.Combine(harness.MoviesRoot, "Dune", "Extended.mkv"), "whole");

        await harness.RunAsync<ImportRecovery>(recovery => recovery.RecoverAsync(CancellationToken.None));
        var recovered = await JobsAsync(harness);

        Assert.False(File.Exists(temporary));
        Assert.Equal(ImportJobStatus.Waiting, recovered.Single(job => job.FileName == "Dune.mkv").Status);
        var extended = recovered.Single(job => job.FileName == "Extended.mkv");
        Assert.Equal(ImportJobStatus.Failed, extended.Status);
        Assert.Contains("Depot stopped during this import", extended.Message);
        Assert.Equal("whole", File.ReadAllText(Path.Combine(harness.MoviesRoot, "Dune", "Extended.mkv")));
    }

    [Fact]
    public async Task Adding_a_torrent_again_with_a_new_choice_drops_its_unfinished_jobs()
    {
        await using var harness = await DepotHarness.CreateAsync();
        Directory.Delete(harness.MoviesRoot);
        Download(harness, "Dune", ("Dune/Dune.mkv", 100));
        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune", null));
        await RunPassAsync(harness);

        await ChooseAsync(harness, new LibraryTarget(MediaType.Movie, "Dune Part One", null));

        Assert.Empty(await JobsAsync(harness));
        Assert.Single(await harness.RunAsync<IReadOnlyDictionary<string, LibraryTarget>, ILibraryChoiceRepository>(
            repository => repository.GetPendingAsync(CancellationToken.None)));
    }

    private static void Download(DepotHarness harness, string name, params (string Path, int Size)[] files) =>
        Download(harness, name, files, TorrentStatus.Completed, 1);

    private static void Download(DepotHarness harness, string name, (string Path, int Size)[] files, TorrentStatus status, double progress)
    {
        foreach (var (path, size) in files)
        {
            var full = Source(harness, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray());
        }

        harness.QBittorrent.Add(Hash, name, "/downloads", files.Select(file => (file.Path, (long)file.Size)), status, progress);
    }

    private static string Source(DepotHarness harness, string relativePath) => Path.Combine(harness.DownloadsRoot, relativePath);

    private static Task ChooseAsync(DepotHarness harness, LibraryTarget target) =>
        harness.RunAsync<ILibraryChoiceRepository>(repository => repository.SaveAsync(Hash, target, DateTimeOffset.UnixEpoch, CancellationToken.None));

    private static Task SaveSettingsAsync(DepotHarness harness, ImportMode mode) =>
        harness.RunAsync<LibrarySettingsService>(service => service.SaveAsync(
            new LibrarySettingsInput(harness.MoviesRoot, harness.SeriesRoot, mode, 60, [new PathMappingInput("/downloads", harness.DownloadsRoot)]),
            CancellationToken.None));

    private static Task RunPassAsync(DepotHarness harness) =>
        harness.RunAsync<ImportCycle>(cycle => cycle.RunAsync(CancellationToken.None));

    private static Task<IReadOnlyList<ImportJobView>> JobsAsync(DepotHarness harness) =>
        harness.RunAsync<IReadOnlyList<ImportJobView>, ImportJobService>(service => service.ListAsync(null, CancellationToken.None));
}
