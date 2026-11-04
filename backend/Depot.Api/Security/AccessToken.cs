using System.Security.Cryptography;
using System.Text;
using Depot.Infrastructure;

namespace Depot.Api.Security;

// The secret every API request must carry, as "Authorization: Bearer <token>". It comes from
// Auth:Token in the configuration or, when that isn't set, from data/access-token, which is
// created with a random token on the first start and readable only by Depot's user.
public sealed class AccessToken
{
    public const string SectionName = "Auth";
    public const string FileName = "access-token";
    public const int MinLength = 32;

    private readonly byte[] expected;

    private AccessToken(string token, string source)
    {
        expected = Encoding.UTF8.GetBytes(token);
        Source = source;
    }

    // Where the token came from, for the startup log. Never the token itself.
    public string Source { get; }

    public bool Matches(string? presented) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);

    public static AccessToken Load(IConfiguration configuration, StoragePaths storage)
    {
        var configured = configuration.GetSection(SectionName)["Token"]?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            return configured.Length >= MinLength
                ? new AccessToken(configured, "the Auth:Token setting")
                : throw new InvalidOperationException($"Auth:Token must have at least {MinLength} characters.");
        }

        var path = Path.Combine(storage.DataDirectory, FileName);
        if (!File.Exists(path))
        {
            Create(path);
        }

        var stored = File.ReadAllText(path).Trim();
        if (stored.Length < MinLength)
        {
            throw new InvalidOperationException($"{path} must hold a token of at least {MinLength} characters. Delete it to get a new one.");
        }

        StoragePaths.RestrictFile(path);
        return new AccessToken(stored, path);
    }

    private static void Create(string path)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = StoragePaths.PrivateFileMode;
        }

        using var writer = new StreamWriter(path, Encoding.ASCII, options);
        writer.WriteLine(token);
    }
}
