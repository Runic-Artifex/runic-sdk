using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// The portable part of the Launch Services registration: which items the bundle's Info.plist does not declare. The
// native registration runs in eng/ci/macos-open-probe.sh on the macOS CI runner.
internal sealed class OpenRegistrationModelTests
{
    [Test]
    public void ReportsUndeclaredSchemesAndExtensions()
    {
        var declared = new MacOpenRegistrationModel.Declared(
            new HashSet<string>(["org.example.notes"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["org.example.notes.document", "public.plain-text"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["notes"], StringComparer.OrdinalIgnoreCase),
            extension => extension == "txt");
        var registration = new DesktopOpenRegistration("Notes")
        {
            UrlSchemes = ["ORG.example.notes", "other"],
            FileTypes = [new(".Notes"), new(".txt"), new(".md")],
        };
        var missing = MacOpenRegistrationModel.Undeclared(registration, declared);
        Check(missing.SequenceEqual(["other:", ".md"]), string.Join(",", missing));
        var diagnostic = MacOpenRegistrationModel.UndeclaredDiagnostic(missing);
        Check(diagnostic.Code == 2 && diagnostic.Message!.Contains("other:, .md", StringComparison.Ordinal), diagnostic.ToString());
        Check(MacOpenRegistrationModel.Undeclared(registration with { UrlSchemes = ["org.example.notes"], FileTypes = [new(".notes")] }, declared).Count == 0,
            "declared items pass");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
