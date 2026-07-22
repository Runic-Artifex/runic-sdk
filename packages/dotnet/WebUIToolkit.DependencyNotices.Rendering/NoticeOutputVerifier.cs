using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class NoticeOutputVerifier
{
    public static NoticeVerificationResult Verify(
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> expected,
        IReadOnlyList<RenderedNoticeOutput> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        List<NoticeDiagnostic> diagnostics = [];
        Dictionary<string, RenderedNoticeOutput> actualByName = new(StringComparer.Ordinal);
        foreach (RenderedNoticeOutput output in actual)
        {
            if (!actualByName.TryAdd(output.FileName, output))
            {
                diagnostics.Add(Drift(output.FileName, "Generated output name is duplicated."));
            }
        }

        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> pair in expected)
        {
            if (!actualByName.Remove(pair.Key, out RenderedNoticeOutput? output))
            {
                diagnostics.Add(Drift(pair.Key, "Generated output is missing."));
                continue;
            }

            if (!pair.Value.Span.SequenceEqual(output.Content))
            {
                string expectedDigest = RenderingUtilities.Sha256(pair.Value.ToArray());
                diagnostics.Add(Drift(
                    pair.Key,
                    $"Generated output differs: expected sha256:{expectedDigest}, actual sha256:{output.Sha256}."));
            }
        }

        foreach (string unexpected in actualByName.Keys)
        {
            diagnostics.Add(Drift(unexpected, "Generated output is unexpected."));
        }

        diagnostics.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Source, right.Source));
        return new NoticeVerificationResult(diagnostics.AsReadOnly());
    }

    private static NoticeDiagnostic Drift(string outputName, string message) => new(
        NoticeDiagnosticCodes.OutputDrift,
        NoticeDiagnosticSeverity.Error,
        message,
        Source: outputName,
        Remediation: "Regenerate and review the deterministic notice outputs.");
}
