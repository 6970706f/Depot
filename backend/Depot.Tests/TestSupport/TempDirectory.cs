namespace Depot.Tests.TestSupport;

// A folder under the system temp directory, removed (with any read-only parts) on dispose.
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? parent = null)
    {
        Path = System.IO.Path.Join(parent ?? System.IO.Path.GetTempPath(), $"depot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Join([Path, .. parts]);

    // Writes a file, creating its folders, and returns its full path.
    public string WriteFile(string relativePath, string content) => WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    public string WriteFile(string relativePath, byte[] content)
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string CreateFolder(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var folder in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Prepend(Path))
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}
