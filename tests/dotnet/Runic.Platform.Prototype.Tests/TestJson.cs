using System.Text.Json;

internal static class TestJson
{
    internal static JsonElement Zero => Parse("0");
    internal static JsonElement Empty => Parse("{}");
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
