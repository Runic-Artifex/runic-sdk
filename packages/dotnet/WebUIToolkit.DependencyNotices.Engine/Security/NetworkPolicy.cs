using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Engine;

public enum NoticeOperation
{
    Scan,
    Evaluate,
    Generate,
    Verify,
    Acquire,
}

public static class NetworkPolicy
{
    public static void EnsurePermitted(NoticeOperation operation, bool allowNetwork)
    {
        if (operation != NoticeOperation.Acquire || !allowNetwork)
        {
            throw new NoticeSecurityException(
                NoticeDiagnosticCodes.NetworkAccessForbidden,
                $"Network access is forbidden for the '{operation}' operation.");
        }
    }
}
