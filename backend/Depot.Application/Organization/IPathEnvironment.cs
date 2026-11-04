namespace Depot.Application.Organization;

public interface IPathEnvironment
{
    // The home folder of the user running Depot, used to expand "~/" in configured paths.
    string HomeDirectory { get; }
}
