namespace Depot.Domain.Organization;

// An action that the job's current state doesn't allow, such as skipping a job that's
// already being imported.
public sealed class InvalidJobStateException(string message) : Exception(message);
