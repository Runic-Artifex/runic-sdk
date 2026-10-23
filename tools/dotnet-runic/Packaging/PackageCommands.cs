using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>dotnet runic package &lt;target&gt;</c>. Each target shares the project, configuration, runtime, output directory
/// and format options; the signing options pass the consumer's own credentials through (D032.1).
/// </summary>
[CommandGroup("package", Description = "Package the application for distribution: MSIX, .app and disk image, Flatpak, AppImage, deb, rpm or a Flathub manifest.")]
internal static class PackageCommands
{
    private const string ProjectDescription =
        "The project file, or a directory with one .csproj. Defaults to the current directory.";
    private const string ConfigurationDescription = "The build configuration to publish.";
    private const string RuntimeDescription =
        "The runtime identifier to publish for. Defaults to the host's architecture on the target's operating system.";
    private const string OutputDescription =
        "The directory to write the package to. Defaults to artifacts/package/<target> beside the project.";

    [Command("package windows", Description = "Package the application as an MSIX package. Runs on Windows with the Windows SDK.",
        Examples = ["dotnet runic package windows", "dotnet runic package windows --certificate app.pfx --timestamp-url http://timestamp.digicert.com"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> Windows(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription)] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "",
        [Option("--publisher", ValueName = "CN=...", Description = "The package's Publisher, which must equal the signing certificate's subject. Defaults to RunicApplicationPublisher.")] string publisher = "",
        [Option("--certificate", ValueName = "PFX", ConflictsWith = ["thumbprint"], Description = "Sign the package with this PFX file; its password comes from RUNIC_SIGNING_PASSWORD.")] string certificate = "",
        [Option("--thumbprint", ValueName = "SHA1", ConflictsWith = ["certificate"], Description = "Sign the package with the certificate of this SHA-1 thumbprint from the certificate store.")] string thumbprint = "",
        [Option("--timestamp-url", ValueName = "URL", Description = "Timestamp the signature with this RFC 3161 server.")] string timestampUrl = "") =>
        RunAsync(context, Common(PackageTarget.Windows, project, configuration, runtime, outputDirectory) with
        {
            Publisher = Value(publisher),
            Certificate = Value(certificate),
            Thumbprint = Value(thumbprint),
            TimestampUrl = Value(timestampUrl),
        }, cancellationToken);

    [Command("package macos", Description = "Package the application as an .app bundle in a disk image. Runs on macOS.",
        Examples = ["dotnet runic package macos", "dotnet runic package macos --runtime osx-arm64,osx-x64",
            "dotnet runic package macos --sign \"Developer ID Application: Example Ltd (TEAMID)\" --notary-profile notary"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> MacOS(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription + " Separate two with a comma, osx-arm64,osx-x64, for a universal bundle.")] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "",
        [Option("--sign", ValueName = "IDENTITY", Description = "Sign with this Developer ID Application identity from the keychain instead of ad hoc.")] string sign = "",
        [Option("--notary-profile", ValueName = "NAME", Requires = ["sign"], Description = "Notarize and staple the disk image with this notarytool keychain profile.")] string notaryProfile = "") =>
        RunAsync(context, Common(PackageTarget.MacOS, project, configuration, runtime, outputDirectory) with
        {
            SigningIdentity = Value(sign),
            NotaryProfile = Value(notaryProfile),
        }, cancellationToken);

    private static PackageOptions Common(PackageTarget target, string project, string configuration, string runtime, string outputDirectory) =>
        new(target)
        {
            Project = Value(project),
            Configuration = string.IsNullOrWhiteSpace(configuration) ? "Release" : configuration,
            Runtimes = PackageOptions.ParseRuntimes(runtime),
            OutputDirectory = Value(outputDirectory),
        };

    private static string? Value(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Task<CommandOutcome<PackageCommandResult>> RunAsync(
        CommandExecutionContext context, PackageOptions options, CancellationToken cancellationToken) =>
        Program.ExecuteAsync<PackageCommandResult>(context, stream: true, async _ =>
        {
            PackageCommandResult result = await PackageApplication.RunAsync(options, cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(result);
        });
}
