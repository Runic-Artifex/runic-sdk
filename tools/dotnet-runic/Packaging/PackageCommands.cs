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
        Examples = ["dotnet runic package macos",
            "dotnet runic package macos --sign \"Developer ID Application: Example Ltd (TEAMID)\" --notary-profile notary"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> MacOS(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription + " Runic supports osx-arm64 (Apple silicon) only.")] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "",
        [Option("--sign", ValueName = "IDENTITY", Description = "Sign with this Developer ID Application identity from the keychain instead of ad hoc.")] string sign = "",
        [Option("--notary-profile", ValueName = "NAME", Requires = ["sign"], Description = "Notarize and staple the disk image with this notarytool keychain profile.")] string notaryProfile = "") =>
        RunAsync(context, Common(PackageTarget.MacOS, project, configuration, runtime, outputDirectory) with
        {
            SigningIdentity = Value(sign),
            NotaryProfile = Value(notaryProfile),
        }, cancellationToken);

    [Command("package flatpak", Description = "Package the application as a Flatpak bundle on the GNOME runtime. Runs on Linux with flatpak-builder.",
        Examples = ["dotnet runic package flatpak", "dotnet runic package flatpak --gpg-sign 0123ABCD"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> Flatpak(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription)] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "",
        [Option("--gpg-sign", ValueName = "KEY", Description = "Sign the repository and bundle with this GPG key ID.")] string gpgSign = "") =>
        RunAsync(context, Common(PackageTarget.Flatpak, project, configuration, runtime, outputDirectory) with { GpgKey = Value(gpgSign) }, cancellationToken);

    [Command("package appimage", Description = "Package the application as an AppImage that uses the host's GTK and WebKitGTK. Runs on Linux with appimagetool.",
        Examples = ["dotnet runic package appimage", "RUNIC_APPIMAGE_RUNTIME=runtime-x86_64 dotnet runic package appimage"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> AppImage(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription)] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "") =>
        RunAsync(context, Common(PackageTarget.AppImage, project, configuration, runtime, outputDirectory), cancellationToken);

    [Command("package deb", Description = "Package the application as a Debian package. Runs on Linux with dpkg-deb.",
        Examples = ["dotnet runic package deb", "dotnet runic package deb --runtime linux-arm64"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> Deb(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription)] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "") =>
        RunAsync(context, Common(PackageTarget.Deb, project, configuration, runtime, outputDirectory), cancellationToken);

    [Command("package rpm", Description = "Package the application as an rpm. Runs on Linux with rpmbuild, also on Debian and Ubuntu.",
        Examples = ["dotnet runic package rpm", "dotnet runic package rpm --runtime linux-arm64"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> Rpm(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = ConfigurationDescription)] string configuration = "Release",
        [Option("--runtime", "--rid", "-r", Description = RuntimeDescription)] string runtime = "",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "") =>
        RunAsync(context, Common(PackageTarget.Rpm, project, configuration, runtime, outputDirectory), cancellationToken);

    [Command("package flathub", Description = "Write a Flathub source-build manifest with offline NuGet and npm sources from the committed lock files. --build builds it with flatpak-builder on Linux.",
        Examples = ["dotnet runic package flathub --git-url https://github.com/example/notes.git --git-tag v1.2.0",
            "dotnet runic package flathub --git-url https://github.com/example/notes.git --git-commit 0123abcd --build"])]
    [CommandResult(PackageCommandResult.PayloadType, typeof(PackageCommandJsonContext))]
    internal static Task<CommandOutcome<PackageCommandResult>> Flathub(
        CommandExecutionContext context,
        [Option("--build", Description = "Build the manifest as Flathub does: install the runtime and SDK extensions, download the sources, then build offline and write a bundle. Linux only.")] bool build,
        [Option("--git-url", ValueName = "URL", Required = true, Description = "The application's git repository, which Flathub checks out.")] string gitUrl,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = "The build configuration to evaluate.")] string configuration = "Release",
        [Option("--output-directory", "-o", Description = OutputDescription)] string outputDirectory = "",
        [Option("--git-commit", ValueName = "SHA", ConflictsWith = ["git-tag"], Description = "Build this commit.")] string gitCommit = "",
        [Option("--git-tag", ValueName = "TAG", ConflictsWith = ["git-commit"], Description = "Build this tag.")] string gitTag = "") =>
        RunAsync(context, Common(PackageTarget.Flathub, project, configuration, "", outputDirectory) with
        {
            GitUrl = Value(gitUrl),
            GitCommit = Value(gitCommit),
            GitTag = Value(gitTag),
            Build = build,
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
