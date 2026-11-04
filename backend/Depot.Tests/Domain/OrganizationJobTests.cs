using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class OrganizationJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly LibraryTarget Show = new(MediaType.Series, "The Last of Us", 1);

    [Fact]
    public void ANewJobWaitsToBeImportedWithItsLibraryChoice()
    {
        var job = New();

        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.True(job.IsReadyToImport);
        Assert.Equal(Show, job.Target);
    }

    [Fact]
    public void GoesThroughAnImport()
    {
        var job = New();

        job.StartProcessing("/media/series/The Last of Us/Season 01/ep1.mkv", Now);
        Assert.Equal(OrganizationJobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.IsReadyToImport);

        job.Complete(ImportMode.Hardlink, null, Now);
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal(ImportMode.Hardlink, job.ImportedWith);
        Assert.Equal(Now, job.CompletedAt);
        Assert.False(job.CanRetry);
        Assert.False(job.CanSkip);
    }

    [Fact]
    public void AFailedJobCanBeRetriedOrSkipped()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);
        job.Fail("Disk full.", Now);

        Assert.True(job.CanRetry);
        Assert.True(job.CanSkip);

        job.Retry(Now);
        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Null(job.Message);
        Assert.Null(job.DestinationPath);
    }

    [Fact]
    public void ASkippedJobIsOnlyImportedAfterARetry()
    {
        var job = New();
        job.Skip(Now);

        Assert.False(job.IsReadyToImport);
        Assert.Throws<InvalidJobStateException>(() => job.StartProcessing("/media/series/x.mkv", Now));

        job.Retry(Now);
        Assert.True(job.IsReadyToImport);
    }

    [Fact]
    public void AnInterruptedImportReturnsToTheQueue()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);

        job.ReturnToQueue("Depot stopped.", Now);

        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Null(job.DestinationPath);
        Assert.Equal("Depot stopped.", job.Message);
    }

    [Fact]
    public void ACompletedJobCantBeSkippedOrRetried()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);
        job.Complete(ImportMode.Copy, null, Now);

        var skip = Assert.Throws<InvalidJobStateException>(() => job.Skip(Now));
        Assert.Throws<InvalidJobStateException>(() => job.Retry(Now));

        Assert.Contains("already imported", skip.Message);
    }

    private static OrganizationJob New() =>
        OrganizationJob.Discover("hash", "The.Last.of.Us.S01", "The.Last.of.Us.S01/ep1.mkv", 1024, Show, Now);
}
