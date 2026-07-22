using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class NoticeOutputNames
{
    public const string Json = "dependency-notices.json";
    public const string Text = "THIRD-PARTY-NOTICES.txt";
    public const string Html = "dependency-notices.html";
    public const string Manifest = "dependency-notices.manifest.json";
}

public sealed record NoticeManifestInput(string Name, string Sha256);

public sealed record NoticeRenderOptions(
    string ToolVersion,
    IReadOnlyList<NoticeManifestInput> Inputs,
    string? EvidenceLockSha256,
    IReadOnlyList<string> SelectedRoots,
    IReadOnlyList<string> Profiles);

public sealed record RenderedNoticeOutput(string FileName, byte[] Content, string Sha256)
{
    public ReadOnlyMemory<byte> Bytes => Content;
}

public sealed record NoticeRenderResult(
    IReadOnlyList<RenderedNoticeOutput> Outputs,
    IReadOnlyList<NoticeDiagnostic> Diagnostics)
{
    public bool Succeeded
    {
        get
        {
            foreach (NoticeDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

public sealed record NoticeVerificationResult(IReadOnlyList<NoticeDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.Count == 0;
}
