using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>A first-party extension: <c>dotnet runic &lt;name&gt;</c> runs its tool, <c>runic-&lt;name&gt;</c>.</summary>
/// <param name="Name">The command name under <c>dotnet runic</c>, such as <c>translations</c>.</param>
/// <param name="Command">The extension's own command, <c>runic-&lt;name&gt;</c>.</param>
/// <param name="Package">The .NET tool package that installs <see cref="Command"/>.</param>
/// <param name="TrainRepository">The compatibility set's train repository whose version line the extension follows.</param>
internal sealed record KnownExtension(string Name, string Command, string Package, string TrainRepository);

/// <summary>Where <see cref="ToolExtensions.Resolve"/> found an extension, in the order it looks.</summary>
internal enum ExtensionRoute
{
    /// <summary><c>RAX_&lt;NAME&gt;_TOOL</c> names the program.</summary>
    Environment,
    /// <summary>The nearest .NET tool manifest lists the tool; it runs with <c>dotnet tool run</c>.</summary>
    Manifest,
    /// <summary><c>node_modules/.bin</c> in the directory or above has the command (the npm channel).</summary>
    NodeModules,
    /// <summary>The command is on <c>PATH</c>: a global tool, a tool path or a native install.</summary>
    Path,
}

/// <summary>An extension's command line: run <see cref="Executable"/> with <see cref="Prefix"/>, then the arguments.</summary>
internal sealed record ResolvedExtension(KnownExtension Extension, ExtensionRoute Route, string Executable, IReadOnlyList<string> Prefix)
{
    internal IReadOnlyList<string> CommandLine(IReadOnlyList<string> arguments) => [.. Prefix, .. arguments];

    internal string RouteDescription => Route switch
    {
        ExtensionRoute.Environment => ToolExtensions.EnvironmentVariable(Extension),
        ExtensionRoute.Manifest => "the tool manifest",
        ExtensionRoute.NodeModules => "node_modules/.bin",
        _ => "PATH",
    };
}

/// <summary>What an extension reports for <c>--extension-info</c>, or its <c>--version</c> when it predates that option.</summary>
internal sealed record ExtensionInfo(string Name, string Version, int? Protocol);

/// <summary>The process environment that <see cref="ToolExtensions.Resolve"/> reads, replaceable in tests and doctor.</summary>
internal sealed record ExtensionEnvironment(Func<string, string?> GetVariable, Func<string, string?> FindOnPath, string DotNetHost)
{
    internal static ExtensionEnvironment System() =>
        new(global::System.Environment.GetEnvironmentVariable, ExecutableSearch.System.Find, DevApplication.ResolveDotNetHost());
}

/// <summary>
/// Extension dispatch, the git and cargo pattern: <c>dotnet runic translations …</c> runs the installed Translations
/// tool, <c>runic-translations …</c>, with the arguments unchanged, and returns its exit code. The core finds an
/// extension in this order: <c>RAX_&lt;NAME&gt;_TOOL</c> (a path), the nearest .NET tool
/// manifest (<c>dotnet tool run</c>), <c>node_modules/.bin</c>, then <c>PATH</c>. Without one, the command prints the
/// install command (<see cref="MissingCode"/>); it never installs anything itself. <c>doctor</c> reads each found
/// extension's <c>--extension-info</c> and checks its version against the compatibility set.
/// </summary>
/// <remarks>
/// Only the first-party extensions in <see cref="Known"/> dispatch: any other word after <c>dotnet runic</c> is an
/// argument of the default <c>dev</c> command for the application, as before. The 0.25 rename changes the names
/// (<c>rax &lt;name&gt;</c> runs <c>rax-&lt;name&gt;</c>), not the mechanism.
/// </remarks>
internal static class ToolExtensions
{
    internal const string TranslationsName = "translations";
    internal const string CommandPrefix = "runic-";

    /// <summary>The option that makes an extension print its <see cref="ExtensionInfo"/> as one JSON object.</summary>
    internal const string InfoOption = "--extension-info";

    /// <summary>The <c>extensionProtocol</c> this core speaks: arguments pass through unchanged, the exit code returns.</summary>
    internal const int Protocol = 1;

    internal const string MissingCode = "RAPPCLI1013";
    internal const string StartFailureCode = "RAPPCLI1014";

    internal static KnownExtension Translations { get; } =
        new(TranslationsName, CommandPrefix + TranslationsName, "dotnet-runic-translations", "Translations");

    internal static IReadOnlyList<KnownExtension> Known { get; } = [Translations];

    internal static KnownExtension? Find(string name) =>
        Known.FirstOrDefault(extension => StringComparer.Ordinal.Equals(extension.Name, name));

    /// <summary>
    /// The variable that names an extension's program, <c>RAX_TRANSLATIONS_TOOL</c>. It is new, so it takes the
    /// final prefix now and the 0.25 rename does not move it.
    /// </summary>
    internal static string EnvironmentVariable(KnownExtension extension) =>
        $"RAX_{extension.Name.ToUpperInvariant().Replace('-', '_')}_TOOL";

    /// <summary>Finds <paramref name="extension"/> for a command run in <paramref name="directory"/>; null when it is not installed.</summary>
    internal static ResolvedExtension? Resolve(KnownExtension extension, string directory, ExtensionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(environment);
        // An explicit path wins and is not checked here: a wrong one fails visibly when it starts, instead of quietly
        // running another installation.
        if (environment.GetVariable(EnvironmentVariable(extension)) is { Length: > 0 } configured)
            return new(extension, ExtensionRoute.Environment, Path.GetFullPath(configured.Trim('"'), directory), []);
        if (FindManifestListing(extension, directory))
            return new(extension, ExtensionRoute.Manifest, environment.DotNetHost, ["tool", "run", extension.Command, "--"]);
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            string bin = Path.Combine(current.FullName, "node_modules", ".bin");
            if (Directory.Exists(bin) && (ExecutableSearch.System with { Path = bin }).Find(extension.Command) is { } local)
                return new(extension, ExtensionRoute.NodeModules, local, []);
        }
        return environment.FindOnPath(extension.Command) is { } global
            ? new(extension, ExtensionRoute.Path, global, [])
            : null;
    }

    /// <summary>The nearest .NET tool manifest in <paramref name="directory"/> or above, or null.</summary>
    internal static string? FindManifest(string directory)
    {
        foreach (string manifest in Manifests(directory)) return manifest;
        return null;
    }

    // The manifests dotnet tool run reads: .config/dotnet-tools.json, then dotnet-tools.json (new in .NET 10), in each
    // directory up to the first one marked isRoot.
    private static IEnumerable<string> Manifests(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            bool root = false;
            foreach (string manifest in new[] { Path.Combine(current.FullName, ".config", "dotnet-tools.json"), Path.Combine(current.FullName, "dotnet-tools.json") })
            {
                if (!File.Exists(manifest)) continue;
                yield return manifest;
                root |= ReadManifest(manifest) is { } document && document.RootElement.TryGetProperty("isRoot", out JsonElement isRoot) &&
                    isRoot.ValueKind == JsonValueKind.True;
            }
            if (root) yield break;
        }
    }

    private static bool FindManifestListing(KnownExtension extension, string directory) =>
        Manifests(directory).Any(manifest => Lists(manifest, extension));

    // A manifest lists the extension by its package ID (case-insensitive, as NuGet IDs are) or by its command.
    private static bool Lists(string manifest, KnownExtension extension)
    {
        using JsonDocument? document = ReadManifest(manifest);
        if (document is null || !document.RootElement.TryGetProperty("tools", out JsonElement tools) || tools.ValueKind != JsonValueKind.Object)
            return false;
        foreach (JsonProperty tool in tools.EnumerateObject())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(tool.Name, extension.Package)) return true;
            if (tool.Value.ValueKind == JsonValueKind.Object && tool.Value.TryGetProperty("commands", out JsonElement commands) &&
                commands.ValueKind == JsonValueKind.Array &&
                commands.EnumerateArray().Any(command => command.ValueKind == JsonValueKind.String && command.GetString() == extension.Command))
                return true;
        }
        return false;
    }

    private static JsonDocument? ReadManifest(string manifest)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(manifest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The command that installs <paramref name="extension"/> at <paramref name="version"/>: a local tool when the
    /// directory has a tool manifest, so <c>dotnet tool restore</c> brings it back.
    /// </summary>
    internal static string InstallCommand(KnownExtension extension, string version, string directory, bool update = false) =>
        $"dotnet tool {(update ? "update" : "install")} {extension.Package} --version {version}{(FindManifest(directory) is null ? string.Empty : " --local")}";

    /// <summary>The message of <see cref="MissingCode"/>: what is missing and the command that installs it.</summary>
    internal static string MissingMessage(KnownExtension extension, string? version, string directory)
    {
        string install = InstallCommand(extension, version ?? "<version>", directory);
        return $"dotnet runic {extension.Name} runs the {extension.Command} tool, which is not installed: no {EnvironmentVariable(extension)}, " +
            $"tool manifest entry, node_modules/.bin/{extension.Command} or {extension.Command} on PATH. Install it with '{install}', then run 'dotnet runic {extension.Name}' again.";
    }

    /// <summary>
    /// Recognizes a first-party extension as the command, after any leading <c>--format</c> selector (or its hidden
    /// alias <c>--output</c>, passed on as <c>--format</c>). <paramref name="leading"/> is that selector and
    /// <paramref name="rest"/> everything after the name, unchanged: the extension's arguments are both, in order.
    /// </summary>
    internal static bool TryParseDispatch(
        IReadOnlyList<string> arguments, out KnownExtension? extension, out IReadOnlyList<string> leading, out IReadOnlyList<string> rest)
    {
        extension = null;
        var selector = new List<string>();
        rest = [];
        int index = 0;
        while (index < arguments.Count)
        {
            string argument = arguments[index];
            if (argument is Program.FormatOptionName or Program.FormerFormatOptionName && index + 1 < arguments.Count)
            {
                selector.Add(Program.FormatOptionName);
                selector.Add(arguments[index + 1]);
                index += 2;
            }
            else if (argument.StartsWith(Program.FormatOptionName + "=", StringComparison.Ordinal)) selector.Add(arguments[index++]);
            else if (argument.StartsWith(Program.FormerFormatOptionName + "=", StringComparison.Ordinal))
                selector.Add(Program.FormatOptionName + arguments[index++][Program.FormerFormatOptionName.Length..]);
            else break;
        }
        leading = selector;
        if (index >= arguments.Count || Find(arguments[index]) is not { } found) return false;
        extension = found;
        rest = [.. arguments.Skip(index + 1)];
        return true;
    }

    /// <summary>
    /// Runs an extension with this console's standard streams and returns its exit code; the extension writes the
    /// output. A program that cannot start reports <see cref="StartFailureCode"/> on standard error and exits 1.
    /// </summary>
    internal static async Task<int> RunAsync(ResolvedExtension extension, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(extension);
        try
        {
            ProcessStartInfo start = CommandRunner.CreateInteractiveStartInfo(extension.Executable, workingDirectory, extension.CommandLine(arguments));
            // A terminal Ctrl+C reaches the extension too; wait for its exit code instead of ending first.
            Action<PosixSignalContext> wait = static context => context.Cancel = true;
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, wait);
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, wait);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new Win32Exception("The process did not start.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or DevDevelopmentException or IOException)
        {
            await Console.Error.WriteLineAsync(
                $"dotnet-runic : error {StartFailureCode}: could not start the {extension.Extension.Name} extension '{extension.Executable}' from {extension.RouteDescription}: {exception.Message}\n" +
                $"Help for {StartFailureCode}: {Program.HelpUri(StartFailureCode)}").ConfigureAwait(false);
            return Program.DevelopmentFailure;
        }
    }

    /// <summary>
    /// Reads <c>--extension-info</c>: one JSON object with <c>name</c>, <c>version</c> and <c>extensionProtocol</c>.
    /// Extra members are allowed. Returns null when the output is not such an object.
    /// </summary>
    internal static ExtensionInfo? ParseInfo(string output)
    {
        string text = output.Trim();
        // dotnet tool run may print a first-run notice before the extension's line.
        int start = text.LastIndexOf("\n{", StringComparison.Ordinal);
        if (start >= 0) text = text[(start + 1)..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("extensionProtocol", out JsonElement protocol) || !protocol.TryGetInt32(out int number))
                return null;
            return new(name.GetString()!, version.GetString()!, number);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the first line of <c>--version</c>, the fallback for an extension released before
    /// <c>--extension-info</c>: its last word (<c>runic-translations 0.15.1</c>), without build metadata after
    /// <c>+</c>. Null when it is not a version.
    /// </summary>
    internal static ExtensionInfo? ParseVersion(KnownExtension extension, string output)
    {
        string line = output.Trim().Split('\n', 2)[0].Trim();
        int space = line.LastIndexOf(' ');
        if (space >= 0) line = line[(space + 1)..];
        int metadata = line.IndexOf('+');
        if (metadata >= 0) line = line[..metadata];
        int prerelease = line.IndexOf('-');
        return Version.TryParse(prerelease < 0 ? line : line[..prerelease], out _) ? new(extension.Name, line, null) : null;
    }
}
