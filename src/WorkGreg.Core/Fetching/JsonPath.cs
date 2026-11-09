using System.Text.Json;

namespace WorkGreg.Fetching;

/// <summary>Reads values from a <see cref="JsonElement"/> by dot-separated path ("data.jobs", "items.0.id").</summary>
public static class JsonPath
{
    /// <summary>
    /// Follows the path: object property names, or array indexes. Empty or null path = the element itself.
    /// Null when any step is missing.
    /// </summary>
    public static JsonElement? Walk(JsonElement el, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return el;

        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(part, out var next))
                el = next;
            else if (el.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i >= 0 && i < el.GetArrayLength())
                el = el[i];
            else
                return null;
        }

        return el;
    }

    /// <summary>The value at the path as text: strings as they are, numbers as written. Null for anything else (objects, arrays, bool, null, missing).</summary>
    public static string? ReadString(JsonElement el, string? path)
    {
        var found = Walk(el, path);
        return found?.ValueKind switch
        {
            JsonValueKind.String => found.Value.GetString(),
            JsonValueKind.Number => found.Value.GetRawText(),
            _ => null
        };
    }
}