namespace Runic.Application.Tool.Packaging;

/// <summary>
/// The <c>RAPPPKG</c> fault codes of <c>dotnet runic package</c>, catalogued in docs/diagnostics.md. A command line of
/// the wrong shape exits with 2, a failure on what the command found with 1.
/// </summary>
internal static class PackageCodes
{
    /// <summary>The application identity is invalid: ApplicationId, URL schemes, file types or web link hosts. Exit 1.</summary>
    internal const string IdentityInvalid = "RAPPPKG1001";

    /// <summary>An external packaging tool is missing; the message names it and what provides it. Exit 1.</summary>
    internal const string ToolMissing = "RAPPPKG1002";

    /// <summary><c>dotnet publish</c> failed. Exit 1.</summary>
    internal const string PublishFailed = "RAPPPKG1003";

    /// <summary>
    /// The target, or the selected runtime identifier, needs another operating system; several runtime identifiers; or a
    /// runtime identifier Runic does not support, such as osx-x64. Exit 2.
    /// </summary>
    internal const string WrongOperatingSystem = "RAPPPKG1004";

    /// <summary>A packaging tool exited with an error; the message names it and its exit code. Exit 1.</summary>
    internal const string ToolFailed = "RAPPPKG1005";

    /// <summary>A manifest or package failed validation. Exit 1.</summary>
    internal const string ValidationFailed = "RAPPPKG1006";

    /// <summary>
    /// The Flathub manifest or its sources cannot be generated. Exit 1, or 2 when the command line gives neither or both
    /// of --git-commit and --git-tag.
    /// </summary>
    internal const string FlathubSourcesUnavailable = "RAPPPKG1007";

    // Warnings: the payload's warnings[].code. The command still succeeds.

    /// <summary>Warning: the package, repository or bundle is unsigned.</summary>
    internal const string Unsigned = "RAPPPKG2001";

    /// <summary>Warning: the macOS disk image is not notarized (and the bundle may be only ad-hoc signed).</summary>
    internal const string NotNotarized = "RAPPPKG2002";

    /// <summary>Warning: a validation tool is not installed, so the result was not validated.</summary>
    internal const string NotValidated = "RAPPPKG2003";

    /// <summary>Warning: the AppImage loads GTK and WebKitGTK from the target system.</summary>
    internal const string SystemLibraries = "RAPPPKG2004";

    /// <summary>Warning: the Flathub manifest assumes the project directory is the repository root (no git checkout).</summary>
    internal const string NoGitCheckout = "RAPPPKG2005";

    /// <summary>Warning: the Flathub manifest is written, not built; Flathub builds and signs it after submission.</summary>
    internal const string FlathubSubmission = "RAPPPKG2006";
}
