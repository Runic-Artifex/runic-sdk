using System.Runtime.Versioning;
using System.Text.Json;
using TUnit.Core;

namespace Runic.Platform.Administration.Windows.Tests;

// The verifier's runic.administration.verify/1 payload names its properties in PascalCase.
// Runic.CommandLine 0.6.0-preview.3 and later serialize it through VerifierJsonContext.Default,
// and earlier versions through their own options; the names must not depend on either.
// The summary is plain data, so this case runs on every operating system; the attribute only
// satisfies the platform analyzer for the verifier's Windows-only assembly.
[SupportedOSPlatform("windows")]
internal sealed class VerifierPayloadTests
{
    [Test]
    public void SummaryKeepsItsPascalCasePayload()
    {
        var summary = new VerificationSummary("run", "local", false, false, "folder", 1, 2, 3, 4);
        foreach (JsonSerializerOptions? options in new[] { null, new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            VerifierJsonContext context = options is null ? VerifierJsonContext.Default : new VerifierJsonContext(options);
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(summary, context.VerificationSummary));
            string names = string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name));
            if (names != "RunId,Suite,ChangesEnabled,ExpectDenied,ReportFolder,Passed,Failed,Skipped,Canceled")
            {
                throw new InvalidOperationException($"Unexpected runic.administration.verify/1 properties: {names}");
            }
        }
    }
}
