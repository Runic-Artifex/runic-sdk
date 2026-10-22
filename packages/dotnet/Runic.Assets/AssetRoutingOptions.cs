using System;
namespace Runic.Assets;

/// <summary>
/// Selects how a host adapter maps request paths to manifest assets. The Runic Assets host
/// adapters share these rules and defaults, so one archive routes identically on every host.
/// Disable both options for exact routing, where only declared manifest paths resolve.
/// </summary>
public sealed record AssetRoutingOptions
{
    internal static AssetRoutingOptions Default { get; } = new();

    /// <summary>
    /// Gets whether an empty path or <c>/</c> resolves to the manifest entry point.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ServeEntryPointAtRoot { get; init; } = true;

    /// <summary>
    /// Gets whether a missing path whose last segment has no file extension resolves to the
    /// manifest entry point, so client-side routes survive a reload. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableSinglePageApplicationFallback { get; init; } = true;

    /// <summary>
    /// Gets the <c>Content-Security-Policy</c> header value that the host adapters send with every
    /// <c>text/html</c> asset, or <see langword="null"/>, the default, for none. Other assets are sent without it.
    /// </summary>
    /// <remarks>
    /// The value is the policy as it appears in the header, such as
    /// <c>default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'</c>. It must be printable
    /// ASCII: a carriage return, line feed, NUL or other control character throws <see cref="ArgumentException"/>.
    /// Runic Desktop sends its own <c>frame-ancestors</c> policy as a second header, and a browser enforces both.
    /// The archive does not carry a policy: script and connect origins are the application's to choose.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or contains a character that a header cannot carry.</exception>
    public string? ContentSecurityPolicy
    {
        get;
        init => field = ValidateHeaderValue(value, nameof(ContentSecurityPolicy));
    }

    // Header values the adapters send verbatim: no line breaks or other control characters, so a policy cannot add
    // a header or end the response head (SEC-06).
    internal static string? ValidateHeaderValue(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Content-Security-Policy must not be empty; use null for none.", parameterName);
        }

        foreach (char character in value)
        {
            if (character is < ' ' or > '~')
            {
                throw new ArgumentException(
                    "A Content-Security-Policy must be printable ASCII without line breaks or control characters.",
                    parameterName);
            }
        }

        return value;
    }
}
