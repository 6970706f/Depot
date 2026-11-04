namespace Depot.Domain.Organization;

// How files reach the library. Auto hardlinks when the download and the library share a
// filesystem and copies otherwise; Hardlink fails instead of silently copying.
public enum ImportMode
{
    Auto,
    Copy,
    Hardlink,
}
