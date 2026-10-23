namespace Runic.Application.Tool.Packaging;

/// <summary>
/// The <c>RAPPPKG</c> fault codes of <c>dotnet runic package</c>, catalogued in docs/diagnostics.md. Usage and
/// validation codes exit with 2, the others with 1.
/// </summary>
internal static class PackageCodes
{
    /// <summary>The application identity is invalid: ApplicationId, URL schemes, file types or web link hosts. Exit 2.</summary>
    internal const string IdentityInvalid = "RAPPPKG1001";

    /// <summary>An external packaging tool is missing; the message names it and what provides it. Exit 1.</summary>
    internal const string ToolMissing = "RAPPPKG1002";

    /// <summary><c>dotnet publish</c> failed. Exit 1.</summary>
    internal const string PublishFailed = "RAPPPKG1003";

    /// <summary>The target, or the selected runtime identifier, needs another operating system. Exit 2.</summary>
    internal const string WrongOperatingSystem = "RAPPPKG1004";

    /// <summary>A packaging tool exited with an error; the message names it and its exit code. Exit 1.</summary>
    internal const string ToolFailed = "RAPPPKG1005";

    /// <summary>A manifest or package failed validation. Exit 1.</summary>
    internal const string ValidationFailed = "RAPPPKG1006";

    /// <summary>The Flathub manifest or its sources cannot be generated. Exit 2.</summary>
    internal const string FlathubSourcesUnavailable = "RAPPPKG1007";
}
