// Small helpers shared by business tests: parse JSON fixtures and round-trip
// anonymous response objects back into JsonElements for assertions.

namespace PersonalDashboard.Tests;

using System.Text.Json;

public static class TestJson
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // Serializes an anonymous response object (the repository's wire contract)
    // and re-parses it so tests can assert on keys without knowing its type.
    public static JsonElement RoundTrip(object value)
    {
        return Parse(JsonSerializer.Serialize(value));
    }

    public static int ArrayCount(JsonElement root, string name)
    {
        return root.GetProperty(name).EnumerateArray().Count();
    }
}