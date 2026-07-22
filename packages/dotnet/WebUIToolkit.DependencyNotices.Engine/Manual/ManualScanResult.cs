using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Engine;

public sealed record ManualScanResult(
    IReadOnlyList<ManualDependencyComponent> Components,
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
