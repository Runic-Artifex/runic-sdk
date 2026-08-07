using System.Text.RegularExpressions;
using RunicToolkit.ApplicationBridge;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

/// <summary>Configures the fixed native Application Bridge channel.</summary>
public sealed partial record CsWebUiApplicationBridgeOptions
{
    /// <summary>Client-to-host binary binding name.</summary>
    public string BindingName { get; init; } = "__runicToolkit_applicationBridge_send";

    /// <summary>Host-to-client receiver name.</summary>
    public string ReceiverName { get; init; } = "__runicToolkit_applicationBridge_receiveHostEvent";

    /// <summary>Untrusted input and session limits.</summary>
    public BridgeLimits Limits { get; init; } = BridgeLimits.Default;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        if (!JavaScriptIdentifier().IsMatch(BindingName) ||
            !JavaScriptIdentifier().IsMatch(ReceiverName) ||
            string.Equals(BindingName, ReceiverName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Application Bridge channel names must be distinct JavaScript identifiers.");
        }
    }

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptIdentifier();
}
