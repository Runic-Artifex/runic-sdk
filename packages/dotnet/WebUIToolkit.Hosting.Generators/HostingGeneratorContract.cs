namespace WebUIToolkit.Hosting.Generators;

/// <summary>
/// Identifies the stable contract understood by hosting registration producers.
/// </summary>
/// <remarks>
/// The version covers the semantic meaning and deterministic ordering of
/// <see cref="HostingRegistrationDescriptor"/> instances. It does not identify a
/// Roslyn generator implementation or a generated source-text format.
/// </remarks>
public static class HostingGeneratorContract
{
    /// <summary>
    /// Gets the stable name of the hosting registration contract.
    /// </summary>
    public const string Name = "WebUIToolkit.Hosting.GeneratedRegistrations";

    /// <summary>
    /// Gets the current hosting registration contract version.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Determines whether this contract implementation supports a version.
    /// </summary>
    /// <param name="version">The version to test.</param>
    /// <returns><see langword="true"/> when <paramref name="version"/> is supported.</returns>
    public static bool SupportsVersion(int version) => version == Version;
}
