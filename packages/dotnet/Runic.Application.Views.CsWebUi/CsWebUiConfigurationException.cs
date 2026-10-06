namespace Runic.Application.Views.CsWebUi;

/// <summary>Reports a CS-WebUI Window configuration that failed a check before the window opened.</summary>
/// <remarks>
/// <see cref="CsWebUiBridgeWindowExtensions.OpenWindow{TWindow, TViewModel}"/> and
/// <see cref="CsWebUiBridgeWindowExtensions.ValidateWindow{TViewModel}"/> throw it. The codes match the
/// Runic Desktop configuration diagnostics.
/// </remarks>
public sealed class CsWebUiConfigurationException : InvalidOperationException
{
    /// <summary>The code reported when no generated Bridge is registered for the Window's ViewModel.</summary>
    public const string BridgeNotRegisteredCode = "bridge-not-registered";

    internal CsWebUiConfigurationException(string code, string diagnosticMessage, string remediation)
        : base($"{code}: {diagnosticMessage} {remediation}")
    {
        Code = code;
        DiagnosticMessage = diagnosticMessage;
        Remediation = remediation;
    }

    /// <summary>Gets the stable code of the failed check, such as <see cref="BridgeNotRegisteredCode"/>.</summary>
    public string Code { get; }

    /// <summary>Gets what the check found, without the code or remediation.</summary>
    public string DiagnosticMessage { get; }

    /// <summary>Gets how to fix the configuration.</summary>
    public string Remediation { get; }
}
