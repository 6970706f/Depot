namespace Depot.Domain.Organization;

public enum OrganizationJobStatus
{
    // Found in a finished download and waiting to be imported.
    Discovered,
    Processing,
    Completed,
    Failed,
    Skipped,
}
