using System.Collections.Immutable;

namespace Runic.Platform;

// The argument checks of IDesktopApplications, so a call that is wrong on one platform is wrong on every platform.
// DesktopApplications.Unavailable uses them here; providers compile this file into their own assembly (see their csproj).
internal static class DesktopApplicationNames
{
    internal const int MaximumLength = 4096, MaximumItems = 1024, MaximumContentTypeLength = 255;

    internal static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.Contains('\0')) throw new ArgumentException("The value cannot contain NUL.", name);
        return value;
    }

    internal static string ApplicationId(string value, string name)
    {
        Required(value, name);
        if (value.Length > MaximumLength || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Supply an application identifier of at most {MaximumLength} characters.", name);
        return value;
    }

    internal static string ContentType(string value, string name)
    {
        Required(value, name);
        if (value.Length > MaximumContentTypeLength || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Supply a content type of at most {MaximumContentTypeLength} characters.", name);
        return value;
    }

    internal static string ContentPath(string value, DesktopContentDetection detection, string name)
    {
        Required(value, name);
        if (value.Length > MaximumLength) throw new ArgumentException($"Supply a path of at most {MaximumLength} characters.", name);
        if (!Enum.IsDefined(detection)) throw new ArgumentOutOfRangeException(nameof(detection));
        return value;
    }

    internal static ImmutableArray<string> PathsOrUris(ImmutableArray<string> items, string name)
    {
        if (items.IsDefault) throw new ArgumentException("Supply the paths and URIs, or an empty array.", name);
        if (items.Length > MaximumItems) throw new ArgumentException($"Supply at most {MaximumItems} paths and URIs.", name);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item) || item.Contains('\0') || item.Length > MaximumLength || !(Path.IsPathFullyQualified(item) || IsUri(item)))
                throw new ArgumentException("Supply fully qualified local paths and absolute URIs.", name);
        }
        return items;
    }

    // Whether a validated item is a local path rather than a URI.
    internal static bool IsPath(string item) => Path.IsPathFullyQualified(item) && !IsUri(item);

    // A URI has a scheme of at least two characters, so a Windows drive path is a path, not a URI.
    private static bool IsUri(string item) =>
        Uri.TryCreate(item, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && item.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase);
}
