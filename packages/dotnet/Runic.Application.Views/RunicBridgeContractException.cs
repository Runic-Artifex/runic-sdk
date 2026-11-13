namespace Runic.Application.Views;

/// <summary>
/// Reports generated Bridges that were built against another Runic release than the running
/// <c>Runic.Application.Views</c> (<c>RUNICBRIDGE038</c>). Rebuild <see cref="AssemblyName"/> against this release.
/// </summary>
/// <remarks>
/// The generated <c>AddRunicBridges()</c> throws it before it registers a Bridge, when the library's generated-code
/// contract differs from this runtime's. A library generated before 0.19 has no such check, so the host throws it when
/// its first Window or View attaches the Bridge, with the <see cref="TypeLoadException"/> or
/// <see cref="MissingMemberException"/> of the stale Bridge as the inner exception.
/// </remarks>
public sealed class RunicBridgeContractException : InvalidOperationException
{
    /// <summary>The diagnostic ID in the message, <c>RUNICBRIDGE038</c>.</summary>
    public const string DiagnosticId = "RUNICBRIDGE038";

    private RunicBridgeContractException(string message, string assemblyName, Exception? innerException)
        : base(message, innerException)
    {
        AssemblyName = assemblyName;
    }

    /// <summary>Gets the simple name of the assembly whose generated Bridges must be rebuilt.</summary>
    public string AssemblyName { get; }

    // The Runic release of the running Runic.Application.Views, such as 0.19.0.
    internal static string RuntimeVersion => RunicReleaseLinks.Tag.TrimStart('v');

    internal static RunicBridgeContractException ForContract(string assemblyName, int contractVersion)
    {
        var newer = contractVersion > BridgeGeneratedCode.ContractVersion;
        var message = $"{DiagnosticId}: the generated Runic Bridges in {assemblyName} were built against "
            + (newer ? "a newer" : "an older") + $" Runic release (generated-code contract {contractVersion}; "
            + $"Runic.Application.Views {RuntimeVersion} runs contract {BridgeGeneratedCode.ContractVersion}). "
            + $"Rebuild this library against Runic {RuntimeVersion}"
            + (newer ? ", or update the application's Runic packages to the release the library uses." : ".");
        return new(WithHelpLink(message), assemblyName, null);
    }

    internal static RunicBridgeContractException ForStaleBridge(string assemblyName, string viewModel, Exception error)
    {
        var message = $"{DiagnosticId}: the generated Runic Bridge for {viewModel} in {assemblyName} was built against "
            + $"another Runic release and cannot load on Runic.Application.Views {RuntimeVersion} ({error.GetType().Name}: "
            + $"{error.Message}). Rebuild this library against Runic {RuntimeVersion}.";
        return new(WithHelpLink(message), assemblyName, error);
    }

    private static string WithHelpLink(string message) => $"{message} See {RunicReleaseLinks.Diagnostic(DiagnosticId)}";
}
