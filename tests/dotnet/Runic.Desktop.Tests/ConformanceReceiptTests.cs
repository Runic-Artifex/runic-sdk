using System.Text.Json;

namespace Runic.Desktop.Tests;

public sealed class ConformanceReceiptTests
{
    [Fact]
    public void DotNetReceiptPassesEveryPortableFixtureExactlyOnce()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "conformance");
        var fixtureIds = Directory.EnumerateFiles(Path.Combine(root, "scenarios"), "*.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "vectors"), "*.json"))
            .Select(ReadId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "dotnet-m6.json")));
        var results = receipt.RootElement.GetProperty("results").EnumerateArray().ToArray();
        var resultIds = results
            .Select(static result => result.GetProperty("id").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(fixtureIds, resultIds);
        Assert.All(results, static result => Assert.Equal("pass", result.GetProperty("outcome").GetString()));
        var testNames = typeof(ConformanceReceiptTests).Assembly.GetTypes()
            .SelectMany(static type => type.GetMethods()
                .Where(static method => method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length != 0)
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(results, result => Assert.Contains(result.GetProperty("evidence").GetString()!, testNames));
    }

    private static string ReadId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
