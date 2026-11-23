using System.Text.Json;

namespace Depot.Infrastructure.Jellyfin;

// Jellyfin answers some endpoints in PascalCase and others in camelCase (System/Info/Public in
// 10.11), so property names are matched ignoring case.
internal static class JsonReading
{
    public static bool TryGet(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    // Trimmed text, cut to maxLength; null when missing, not a string or blank.
    public static string? Text(this JsonElement element, string name, int maxLength = 500)
    {
        if (!element.TryGet(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maxLength ? text[..maxLength] : text;
    }

    public static int? Int(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    public static long? Long(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    public static double? Double(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Round(number, 1)
            : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
