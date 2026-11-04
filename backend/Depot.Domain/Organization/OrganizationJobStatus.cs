namespace Depot.Domain.Organization;

public enum OrganizationJobStatus
{
    // Found in a finished download and waiting to be imported.
    Discovered,
    // Found, but where it goes is uncertain (an extra, another season...): it isn't imported
    // until a person confirms or corrects its placement.
    NeedsReview,
    Processing,
    Completed,
    Failed,
    Skipped,
}
