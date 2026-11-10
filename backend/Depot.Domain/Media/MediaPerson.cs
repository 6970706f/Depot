namespace Depot.Domain.Media;

public enum PersonKind
{
    Director,
    Writer,
    Actor,
    // An actor in one episode only.
    GuestStar,
    Producer,
    Composer,
    Other,
}

// Someone who worked on an item. ExternalId and ImageTag point to their photo in the source.
// Role is the character an actor plays.
public sealed record MediaPerson(string ExternalId, string Name, PersonKind Kind, string? Role, string? ImageTag);
