using Depot.Domain.Media;

namespace Depot.Tests.Domain;

public sealed class LibrarySyncRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARunWhereEveryLibraryWasReadSucceeds()
    {
        var run = LibrarySyncRun.Start(SyncTrigger.Manual, Now);
        run.BeginLibraries(2);
        run.BeginLibrary("Movies");
        run.RecordItems(seen: 3, added: 2, updated: 1, skipped: 0);
        run.CompleteLibrary();
        run.BeginLibrary("Shows");
        run.RecordRemoved(1);
        run.CompleteLibrary();

        run.Finish(Now.AddMinutes(1));

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal((3, 2, 1, 1), (run.ItemsSeen, run.ItemsAdded, run.ItemsUpdated, run.ItemsRemoved));
        Assert.Equal(Now.AddMinutes(1), run.FinishedAt);
        Assert.Null(run.CurrentLibrary);
    }

    [Fact]
    public void SomeLibrariesFailingIsAPartialResultAndAllFailingIsAFailure()
    {
        var partial = LibrarySyncRun.Start(SyncTrigger.Manual, Now);
        partial.BeginLibraries(2);
        partial.CompleteLibrary();
        partial.FailLibrary("Shows", "Timed out.");
        partial.Finish(Now);

        var failed = LibrarySyncRun.Start(SyncTrigger.Manual, Now);
        failed.BeginLibraries(1);
        failed.FailLibrary("Movies", "Timed out.");
        failed.Finish(Now);

        Assert.Equal(SyncRunStatus.CompletedWithErrors, partial.Status);
        Assert.Equal(new LibrarySyncError("Shows", "Timed out."), Assert.Single(partial.Errors));
        Assert.Equal(SyncRunStatus.Failed, failed.Status);
    }

    [Fact]
    public void AFinishedRunCantChange()
    {
        var run = LibrarySyncRun.Start(SyncTrigger.Manual, Now);
        run.Interrupt("Stopped.", Now);

        Assert.Throws<InvalidOperationException>(() => run.RecordItems(1, 1, 0, 0));
        Assert.Throws<InvalidOperationException>(() => run.Finish(Now));
        Assert.Equal(SyncRunStatus.Interrupted, run.Status);
    }

    [Fact]
    public void ErrorsAreCapped()
    {
        var run = LibrarySyncRun.Start(SyncTrigger.Manual, Now);
        run.BeginLibraries(LibrarySyncRun.MaxErrors + 10);
        for (var library = 0; library < LibrarySyncRun.MaxErrors + 10; library++)
        {
            run.FailLibrary($"Library {library}", "Failed.");
        }

        Assert.Equal(LibrarySyncRun.MaxErrors, run.Errors.Count);
        Assert.Equal(LibrarySyncRun.MaxErrors + 10, run.LibrariesFailed);
    }

    [Fact]
    public void BackdropsHaveIndexesAndOtherImagesOnlyOne()
    {
        var tags = new MediaImageTags("poster", null, "logo", ["b0", "b1"]);

        Assert.Equal("poster", tags.TagFor(ImageKind.Primary, 0));
        Assert.Null(tags.TagFor(ImageKind.Primary, 1));
        Assert.Equal("b1", tags.TagFor(ImageKind.Backdrop, 1));
        Assert.Null(tags.TagFor(ImageKind.Backdrop, 2));
        Assert.Null(tags.TagFor(ImageKind.Backdrop, -1));
        Assert.Null(tags.TagFor(ImageKind.Thumb, 0));
    }
}
