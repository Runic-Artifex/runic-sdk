using Runic.Platform.Linux.Portal;
using TUnit.Core;

// PortalDiagnostic has the Code, Message and optional Remediation of RunicWindowDiagnostic and DesktopDiagnostic
// (W319-003 D30), and every code the package reports is listed in its README as "for logs only".
internal sealed class DiagnosticTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [Test, DisplayName("diagnostics: the remediation is optional, as in the other diagnostic records")]
    public void RemediationIsOptional()
    {
        var diagnostic = new PortalDiagnostic("status-icon-bus-lost", "The session bus connection ended.");
        Check(diagnostic.Remediation is null, "Remediation defaults to null.");
        var parameter = typeof(PortalDiagnostic).GetConstructors().Single().GetParameters()[2];
        Check(parameter.Name == "Remediation" && parameter.HasDefaultValue, "Remediation is an optional positional parameter.");
    }

    [Test, DisplayName("diagnostics: the README lists every code the package reports")]
    public void EveryCodeIsListed()
    {
        // The published test binaries can run outside a checkout, such as in the real-portal container.
        if (FindRepository() is not { } root) return;
        string package = Path.Combine(root, "packages", "dotnet", "Runic.Platform.Linux.Portal");
        string readme = File.ReadAllText(Path.Combine(package, "README.md"));
        var reported = Directory.EnumerateFiles(package, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                @"(?:Diagnose|new PortalDiagnostic)\(\s*""([a-z0-9]+(?:-[a-z0-9]+)+)""").Select(match => match.Groups[1].Value))
            .Distinct().ToArray();
        Check(reported.Length > 20, "The scan found the codes.");
        var missing = reported.Where(code => !readme.Contains($"| `{code}` |", StringComparison.Ordinal)).ToArray();
        Check(missing.Length == 0, "README is missing: " + string.Join(", ", missing));
    }

    private static string? FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.slnx"))) return directory.FullName;
        return null;
    }
}
