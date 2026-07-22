using System;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Acquisition;

internal static class OriginPolicy
{
    public static void EnsureAuthorized(AcquisitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation != AcquisitionOperation.Acquire || !request.AllowNetwork)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.NetworkAccessForbidden,
                $"Network access is forbidden for the '{request.Operation}' operation.");
        }
    }

    public static void EnsureAllowed(Uri origin, AcquisitionPolicy policy, bool isRedirect)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(policy);
        string code = isRedirect
            ? NoticeDiagnosticCodes.AcquisitionRedirectBlocked
            : NoticeDiagnosticCodes.AcquisitionOriginBlocked;

        if (!origin.IsAbsoluteUri)
        {
            throw Blocked(code, "The acquisition origin must be an absolute URI.", origin);
        }

        if (!string.IsNullOrEmpty(origin.UserInfo))
        {
            throw Blocked(code, "Credentials and user information are forbidden in acquisition origins.", origin);
        }

        bool allowedScheme = origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (policy.AllowHttp && origin.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        if (!allowedScheme)
        {
            throw Blocked(code, "The acquisition origin scheme is blocked by policy.", origin);
        }

        if (!policy.IsHostAllowed(origin))
        {
            throw Blocked(code, "The acquisition origin host is not in the exact host allowlist.", origin);
        }
    }

    public static string Sanitize(Uri? origin)
    {
        if (origin is null || !origin.IsAbsoluteUri)
        {
            return "<invalid-origin>";
        }

        return SanitizeUri(origin).GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }

    public static Uri SanitizeUri(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        UriBuilder builder = new(origin)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static AcquisitionException Blocked(string code, string reason, Uri origin) =>
        new(code, $"{reason} Origin: '{Sanitize(origin)}'.");
}
