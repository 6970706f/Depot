using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

// Depot killed at each step of an import, then started again: recovery must settle every job
// from what is really on disk, and never call an import done because a file of the right name
// and size is at the destination.
public sealed class ImportRecoveryTests
{
    private const string MovieHash = "1111111111111111111111111111111111111111";
    private const string FirstHash = "2222222222222222222222222222222222222222";
    private const string SecondHash = "3333333333333333333333333333333333333333";

    private static readonly LibraryTarget Dune = new(MediaType.Movie, "Dune (2021)", null);
    private static readonly LibraryTarget LastOfUs = new(MediaType.Series, "The Last of Us", 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ImportMode.Hardlink)]
    [InlineData(ImportMode.Copy)]
    public async Task StoppedBeforeTheFileIsRecordedTheImportRunsAgain(ImportMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(CrashStep.BeforeTheFileIsRecorded);
        await using var harness = await MovieHarnessAsync(crash, mode);
        await CrashDuringPassAsync(harness, crash);
        Assert.False(File.Exists(DuneDestination(harness)));

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();

        var recovered = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Discovered, recovered.Status);
        AssertNoTemporaries(restarted);

        await restarted.RunPassAsync();
        var imported = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, imported.Status);
        Assert.Equal(mode, imported.ImportedWith);
        Assert.Equal(await File.ReadAllBytesAsync(DuneSource(restarted)), await File.ReadAllBytesAsync(DuneDestination(restarted)));
    }

    [Fact]
    public async Task AFileOfTheSameSizeFoundAfterACrashIsNotTakenForTheImport()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(CrashStep.BeforeTheFileIsRecorded);
        await using var harness = await MovieHarnessAsync(crash, ImportMode.Hardlink);
        await CrashDuringPassAsync(harness, crash);
        // While Depot was down, another file with the same name and size showed up.
        var other = RandomBytes(4096);
        harness.Root.WriteFile("library/movies/Dune (2021)/Dune.2021.mkv", other);

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();
        await restarted.RecoverAsync();
        await restarted.RunPassAsync();

        var job = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Failed, job.Status);
        Assert.Contains("can't confirm this import wrote it", job.Message);
        Assert.Equal(other, await File.ReadAllBytesAsync(DuneDestination(restarted)));
        Assert.True(File.Exists(DuneSource(restarted)));
    }

    [Theory]
    [InlineData(CrashStep.BeforeTheFileIsRecorded)]
    [InlineData(CrashStep.CopyHalfway)]
    [InlineData(CrashStep.CopyWritten)]
    public async Task ACopyStoppedBeforeItIsPublishedIsCleanedUpAndRunAgain(CrashStep step)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(step);
        await using var harness = await MovieHarnessAsync(crash, ImportMode.Copy);
        await CrashDuringPassAsync(harness, crash);
        Assert.Single(Temporaries(harness));
        Assert.False(File.Exists(DuneDestination(harness)));

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();

        var recovered = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Discovered, recovered.Status);
        Assert.Contains("cleaned up", recovered.Message);
        AssertNoTemporaries(restarted);
        Assert.False(File.Exists(DuneDestination(restarted)));

        await restarted.RunPassAsync();
        var imported = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, imported.Status);
        Assert.Equal(await File.ReadAllBytesAsync(DuneSource(restarted)), await File.ReadAllBytesAsync(DuneDestination(restarted)));
        AssertNoTemporaries(restarted);
    }

    [Theory]
    [InlineData(ImportMode.Hardlink)]
    [InlineData(ImportMode.Copy)]
    public async Task AnImportStoppedBeforeItsCompletionIsSavedIsConfirmedFromTheFile(ImportMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(CrashStep.BeforeCompletionIsSaved);
        await using var harness = await MovieHarnessAsync(crash, mode);
        await CrashDuringPassAsync(harness, crash);
        Assert.Equal(OrganizationJobStatus.Processing, Assert.Single(await harness.JobsAsync()).Status);
        Assert.True(File.Exists(DuneDestination(harness)));

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();
        // Running recovery and passes again changes nothing.
        await restarted.RecoverAsync();
        await restarted.RunPassAsync();

        var job = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Contains("confirmed", job.Message);
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(DuneDestination(restarted))!));
        Assert.Equal(await File.ReadAllBytesAsync(DuneSource(restarted)), await File.ReadAllBytesAsync(DuneDestination(restarted)));
    }

    // Two downloads with an episode of the same name and size, both going to the same season.
    // Only one can be placed; the other must not be called done because of it.
    [Theory]
    [InlineData(CrashStep.BeforeTheFileIsRecorded)]
    [InlineData(CrashStep.CopyWritten)]
    public async Task OfTwoJobsForOneDestinationOnlyTheOneThatWroteItIsCompleted(CrashStep step)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(step, onlyFor: "second/");
        await using var harness = await OrganizationHarness.CreateAsync(crash);
        await harness.SaveSettingsAsync(ImportMode.Copy);
        await harness.ChooseAsync(FirstHash, LastOfUs);
        await harness.ChooseAsync(SecondHash, LastOfUs);
        harness.AddDownloadedTorrent(FirstHash, "first", "series", ("first/ep1.mkv", 4096));
        harness.AddDownloadedTorrent(SecondHash, "second", "series", ("second/ep1.mkv", 4096));

        await CrashDuringPassAsync(harness, crash);
        await WaitForAsync(harness, jobs => jobs.Any(job => job.RelativePath == "first/ep1.mkv" && job.Status == OrganizationJobStatus.Completed));

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();

        var jobs = await restarted.JobsAsync();
        Assert.Equal(OrganizationJobStatus.Completed, jobs.Single(job => job.RelativePath == "first/ep1.mkv").Status);
        var second = jobs.Single(job => job.RelativePath == "second/ep1.mkv");
        Assert.Equal(OrganizationJobStatus.Failed, second.Status);
        Assert.Contains("left alone", second.Message);

        var destination = Path.Join(restarted.Series, "The Last of Us", "Season 01", "ep1.mkv");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Join(restarted.Downloads, "first", "ep1.mkv")), await File.ReadAllBytesAsync(destination));
        Assert.True(File.Exists(Path.Join(restarted.Downloads, "second", "ep1.mkv")));
        AssertNoTemporaries(restarted);
    }

    private static async Task<OrganizationHarness> MovieHarnessAsync(Crash crash, ImportMode mode)
    {
        var harness = await OrganizationHarness.CreateAsync(crash);
        await harness.SaveSettingsAsync(mode);
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021", "movies", ("Dune.2021/Dune.2021.mkv", 4096));
        return harness;
    }

    // Runs a pass until the crash stops an import. The pass itself never finishes.
    private static async Task CrashDuringPassAsync(OrganizationHarness harness, Crash crash)
    {
        var pass = harness.RunPassAsync();
        var first = await Task.WhenAny(crash.Reached, pass).WaitAsync(Timeout);
        if (first == pass)
        {
            await pass;
            Assert.Fail("The pass finished without reaching the crash.");
        }
    }

    private static async Task WaitForAsync(OrganizationHarness harness, Func<IReadOnlyList<OrganizationJobView>, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition(await harness.JobsAsync()))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the jobs.");
            await Task.Delay(20);
        }
    }

    private static string DuneSource(OrganizationHarness harness) => Path.Join(harness.Downloads, "Dune.2021", "Dune.2021.mkv");

    private static string DuneDestination(OrganizationHarness harness) => Path.Join(harness.Movies, "Dune (2021)", "Dune.2021.mkv");

    private static IEnumerable<string> Temporaries(OrganizationHarness harness) =>
        Directory.EnumerateFiles(harness.Root.Combine("library"), ".depot-job-*", SearchOption.AllDirectories);

    private static void AssertNoTemporaries(OrganizationHarness harness) => Assert.Empty(Temporaries(harness));

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
