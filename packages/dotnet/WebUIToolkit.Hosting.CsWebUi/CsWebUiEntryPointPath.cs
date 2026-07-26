using System;
using System.Collections.Generic;

namespace WebUIToolkit.Hosting.CsWebUi;

internal static class CsWebUiEntryPointPath
{
    internal static string Translate(Uri entryPoint, string applicationId)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (!entryPoint.IsAbsoluteUri ||
            !string.Equals(entryPoint.Scheme, "app", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entryPoint.Host, applicationId, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(entryPoint.UserInfo) ||
            !entryPoint.IsDefaultPort ||
            !string.IsNullOrEmpty(entryPoint.Query) ||
            !string.IsNullOrEmpty(entryPoint.Fragment))
        {
            throw new ArgumentException(
                "The entry point must be an app:// URI for this browser host without credentials, port, query, or fragment.",
                nameof(entryPoint));
        }

        string escapedPath = GetOriginalEscapedPath(entryPoint);
        if (escapedPath.Length == 0)
        {
            throw new ArgumentException("The entry point must identify a relative file path.", nameof(entryPoint));
        }

        string[] escapedSegments = escapedPath.Split('/');
        var segments = new List<string>(escapedSegments.Length);
        foreach (string escapedSegment in escapedSegments)
        {
            if (escapedSegment.Length == 0)
            {
                continue;
            }

            string segment;
            try
            {
                segment = Uri.UnescapeDataString(escapedSegment);
            }
            catch (UriFormatException exception)
            {
                throw new ArgumentException("The entry-point path contains invalid escaping.", nameof(entryPoint), exception);
            }

            if (segment is "." or ".." ||
                segment.IndexOfAny(['/', '\\', '\0']) >= 0 ||
                ContainsControlCharacter(segment))
            {
                throw new ArgumentException(
                    "The entry-point path must contain only safe relative path segments.",
                    nameof(entryPoint));
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException("The entry point must identify a relative file path.", nameof(entryPoint));
        }

        return string.Join('/', segments);
    }

    internal static string BuildNavigationUrl(string servedUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!Uri.TryCreate(servedUrl, UriKind.Absolute, out Uri? server) ||
            server.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "CsWebUi did not expose a valid local server URL.");
        }

        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        var destination = new UriBuilder(server)
        {
            Path = "/" + string.Join('/', segments),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return destination.Uri.AbsoluteUri;
    }

    private static string GetOriginalEscapedPath(Uri entryPoint)
    {
        string original = entryPoint.OriginalString;
        int schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
        int pathStart = schemeEnd < 0
            ? -1
            : original.IndexOf('/', schemeEnd + 3);
        return pathStart < 0 || pathStart == original.Length - 1
            ? string.Empty
            : original[(pathStart + 1)..];
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
