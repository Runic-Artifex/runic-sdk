using System;
using System.Globalization;
using System.Text.Json;

internal static class FixtureOutput
{
    internal static void ApplyCulture()
    {
        string? name = Environment.GetEnvironmentVariable("WUT_FIXTURE_CULTURE");
        if (!string.IsNullOrEmpty(name))
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    internal static int Invoke(string commandPath, object? data = null)
    {
        string path = JsonEncodedText.Encode(commandPath).ToString();
        Console.Out.WriteLine($"{{\"disposition\":\"invoke\",\"commandPath\":\"{path}\",\"protocolIdentity\":\"webuitoolkit.cli/1\"}}");
        return 0;
    }
}
