using Depot.Domain.Common;

namespace Depot.Domain.Organization;

public static class LibraryPaths
{
    // Expands "~" and "~/..." with homeDirectory (.NET never does this on its own) and
    // normalizes the result. Anything still not absolute is rejected, because a relative path
    // would depend on the folder Depot happens to start in.
    public static string Resolve(string? configuredPath, string homeDirectory, string field, string description)
    {
        var text = configuredPath?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new DomainValidationException(field, $"Enter the {description}.");
        }

        if (text.StartsWith('~') && string.IsNullOrEmpty(homeDirectory))
        {
            throw new DomainValidationException(field, "Depot's user has no home folder set (HOME), so ~/ can't be used. Write the full path instead.");
        }

        string expanded;
        if (text == "~")
        {
            expanded = homeDirectory;
        }
        else if (text.StartsWith("~/", StringComparison.Ordinal))
        {
            expanded = Path.Join(homeDirectory, text[2..]);
        }
        else if (text.StartsWith('~'))
        {
            throw new DomainValidationException(field, "Only ~/ is supported for the home folder. Write the full path instead.");
        }
        else
        {
            expanded = text;
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new DomainValidationException(field, "Use an absolute path, such as /srv/media/movies, or one starting with ~/.");
        }

        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        if (string.Equals(Path.GetPathRoot(resolved), resolved, StringComparison.Ordinal))
        {
            throw new DomainValidationException(field, "The root of the filesystem can't be used.");
        }

        return resolved;
    }

    // Compares whole folder names: "/media/movies2" is not inside "/media/movies".
    public static bool IsSameOrInside(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, trimmedRoot, StringComparison.Ordinal)
            || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Joins a "/"-separated relative path to root. Null when the result would leave root,
    // for example through "..".
    public static string? CombineInside(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsSameOrInside(combined, root) && !string.Equals(combined, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal)
            ? combined
            : null;
    }
}
