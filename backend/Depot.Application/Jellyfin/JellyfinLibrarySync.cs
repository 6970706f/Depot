using Depot.Application.Media;
using Depot.Domain.Media;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Jellyfin;

// One sync: brings the local copy of the library up to date with Jellyfin. Every library is
// read page by page; each page is saved as it arrives (new items added, changed ones updated),
// so a failure halfway keeps what was read. Items are only removed from a library that was read
// to the end, and only once Jellyfin confirms they're gone, so a failure or a page that shifted
// while items were added never deletes anything that still exists.
public sealed class JellyfinLibrarySync(
    IJellyfinClient client,
    IMediaCatalogRepository catalog,
    ILibrarySyncRunRepository runs,
    TimeProvider clock,
    ILogger<JellyfinLibrarySync> logger)
{
    public const int PageSize = 100;

    // Ids per lookup when checking the items that weren't seen; keeps the URL short.
    public const int LookupBatchSize = 50;

    // More pages than this means the server keeps sending the same ones.
    private const int MaxPages = 20_000;

    public async Task RunAsync(int runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, cancellationToken) ?? throw new SyncRunNotFoundException(runId);
        if (run.IsFinished)
        {
            return;
        }

        logger.LogInformation("Library sync {RunId} started.", runId);
        try
        {
            await SyncAsync(run, cancellationToken);
            run.Finish(clock.GetUtcNow());
        }
        catch (JellyfinException exception)
        {
            logger.LogWarning("Library sync {RunId} failed: {Reason}", runId, exception.Message);
            run.Fail(exception.Message, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Interrupt("Depot stopped during this sync. Start it again.", clock.GetUtcNow());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Library sync {RunId} failed.", runId);
            run.Fail("The sync stopped on an unexpected error. Depot's log has the details.", clock.GetUtcNow());
        }

        // Saved even when Depot is stopping, so the run never stays "running".
        await runs.UpdateAsync(run, CancellationToken.None);
        logger.LogInformation(
            "Library sync {RunId} ended {Status}: {Seen} items seen, {Added} added, {Updated} updated, {Removed} removed, {Skipped} skipped, {Failed} of {Total} libraries failed.",
            run.Id, run.Status, run.ItemsSeen, run.ItemsAdded, run.ItemsUpdated, run.ItemsRemoved, run.ItemsSkipped, run.LibrariesFailed, run.LibrariesTotal);
    }

    private async Task SyncAsync(LibrarySyncRun run, CancellationToken cancellationToken)
    {
        var libraries = await client.GetLibrariesAsync(cancellationToken);
        var saved = await catalog.SaveLibrariesAsync(libraries, cancellationToken);
        run.RecordRemoved(saved.RemovedItems);

        var synced = libraries.Where(library => library.IsSynced).ToList();
        run.BeginLibraries(synced.Count);
        await runs.UpdateAsync(run, cancellationToken);

        // Every item seen in this run, to count each once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; position < synced.Count; position++)
        {
            var library = synced[position];
            run.BeginLibrary(library.Name);
            await runs.UpdateAsync(run, cancellationToken);
            try
            {
                await SyncLibraryAsync(run, library, saved.IdsByExternalId[library.ExternalId], seen, cancellationToken);
                run.CompleteLibrary();
            }
            catch (JellyfinException exception)
            {
                logger.LogWarning("Library sync {RunId}: {Library} couldn't be read: {Reason}", run.Id, library.Name, exception.Message);
                run.FailLibrary(library.Name, exception.Message);
                if (exception.AffectsEveryRequest)
                {
                    // The server is down or refuses the key: the other libraries would fail the
                    // same way, so they're reported rather than tried.
                    foreach (var skipped in synced.Skip(position + 1))
                    {
                        run.FailLibrary(skipped.Name, $"Not read, because the previous library failed: {exception.Message}");
                    }

                    break;
                }
            }

            await runs.UpdateAsync(run, cancellationToken);
        }
    }

    private async Task SyncLibraryAsync(
        LibrarySyncRun run,
        MediaLibrary library,
        int libraryId,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var seenHere = new HashSet<string>(StringComparer.Ordinal);
        var startIndex = 0;
        for (var pages = 0; ; pages++)
        {
            if (pages == MaxPages)
            {
                throw new JellyfinException(
                    JellyfinFailure.UnexpectedResponse, $"Jellyfin kept sending pages of {library.Name} past {MaxPages * PageSize} items.");
            }

            var page = await client.GetItemsAsync(library.ExternalId, library.ItemKinds, startIndex, PageSize, cancellationToken);
            // When items are added or removed while reading, a page can repeat one from the last.
            var items = page.Items.Where(item => seenHere.Add(item.ExternalId)).ToList();
            await SaveAsync(run, libraryId, items, seen, page.Skipped, cancellationToken);

            startIndex += page.Received;
            if (page.Received == 0 || startIndex >= page.TotalCount)
            {
                break;
            }
        }

        var unseen = (await catalog.ListExternalIdsAsync(libraryId, cancellationToken))
            .Where(id => !seenHere.Contains(id))
            .ToList();
        foreach (var batch in unseen.Chunk(LookupBatchSize))
        {
            var stillThere = await client.GetItemsByIdAsync(batch, cancellationToken);
            await SaveAsync(run, libraryId, stillThere, seen, skipped: 0, cancellationToken);

            var existing = stillThere.Select(item => item.ExternalId).ToHashSet(StringComparer.Ordinal);
            var gone = batch.Where(id => !existing.Contains(id)).ToList();
            if (gone.Count > 0)
            {
                run.RecordRemoved(await catalog.RemoveItemsAsync(gone, cancellationToken));
                logger.LogInformation("Library sync {RunId}: {Count} item(s) no longer in {Library} were removed.", run.Id, gone.Count, library.Name);
            }
        }

        await catalog.MarkLibrarySyncedAsync(libraryId, clock.GetUtcNow(), cancellationToken);
    }

    private async Task SaveAsync(
        LibrarySyncRun run,
        int libraryId,
        IReadOnlyList<MediaItem> items,
        HashSet<string> seen,
        int skipped,
        CancellationToken cancellationToken)
    {
        var saved = await catalog.SaveItemsAsync(libraryId, items, clock.GetUtcNow(), cancellationToken);
        run.RecordItems(items.Count(item => seen.Add(item.ExternalId)), saved.Added, saved.Updated, skipped);
        await runs.UpdateAsync(run, cancellationToken);
    }
}
