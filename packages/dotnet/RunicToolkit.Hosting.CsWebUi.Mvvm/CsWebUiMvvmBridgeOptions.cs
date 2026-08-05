using System;
using System.Linq;
using RunicToolkit.Hosting.WebUi;

namespace RunicToolkit.Hosting.CsWebUi.Mvvm;

/// <summary>Configures the single framed binding used by a CsWebUi MVVM bridge.</summary>
public sealed record CsWebUiMvvmBridgeOptions
{
    /// <summary>Gets the JavaScript binding invoked with client-to-host binary frames.</summary>
    public string BindingName { get; init; } = "__runicToolkit_mvvm_send";

    /// <summary>Gets the JavaScript function invoked with host-to-client binary frames.</summary>
    public string ReceiveFunctionName { get; init; } = "__runicToolkit_mvvm_receive";

    /// <summary>Gets the bounded retained-session transport configuration.</summary>
    public MvvmWebUiTransportOptions TransportOptions { get; init; } = new();

    internal void Validate()
    {
        ValidateJavaScriptIdentifier(BindingName, nameof(BindingName));
        ValidateJavaScriptIdentifier(ReceiveFunctionName, nameof(ReceiveFunctionName));
        if (string.Equals(BindingName, ReceiveFunctionName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The send binding and receive function names must differ.");
        }

        ArgumentNullException.ThrowIfNull(TransportOptions);
        TransportOptions.CodecLimits.Validate();
    }

    private static void ValidateJavaScriptIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!(value[0] is '_' or '$' || char.IsAsciiLetter(value[0])) ||
            value.Skip(1).Any(static character =>
                !(character is '_' or '$' || char.IsAsciiLetterOrDigit(character))))
        {
            throw new ArgumentException(
                "The value must be one simple ASCII JavaScript identifier.",
                parameterName);
        }
    }
}

/// <summary>Identifies the CsWebUi client and its current physical connection.</summary>
public readonly record struct CsWebUiMvvmConnectionIdentity(ulong ClientId, ulong ConnectionId);
