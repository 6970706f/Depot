namespace Depot.Domain.Library;

// Where a download goes, chosen by a person when adding it, recorded by torrent hash before the
// torrent is sent to qBittorrent. Downloads added as "Other" have no choice at all.
public sealed class LibraryChoice
{
    public LibraryChoice(string torrentHash, LibraryTarget target, DateTimeOffset chosenAt)
    {
        TorrentHash = torrentHash.ToLowerInvariant();
        MediaType = target.Type;
        Title = target.Title;
        Season = target.Season;
        ChosenAt = chosenAt;
    }

    // For EF Core.
    private LibraryChoice()
    {
        TorrentHash = Title = string.Empty;
    }

    public string TorrentHash { get; private set; }

    public MediaType MediaType { get; private set; }

    public string Title { get; private set; }

    public int? Season { get; private set; }

    public DateTimeOffset ChosenAt { get; private set; }

    // When the finished download was turned into import jobs. Null until then.
    public DateTimeOffset? ExaminedAt { get; private set; }

    public LibraryTarget Target => new(MediaType, Title, Season);

    public void MarkExamined(DateTimeOffset now) => ExaminedAt = now;

    public void Replace(LibraryTarget target, DateTimeOffset chosenAt)
    {
        ExaminedAt = null;
        MediaType = target.Type;
        Title = target.Title;
        Season = target.Season;
        ChosenAt = chosenAt;
    }
}
