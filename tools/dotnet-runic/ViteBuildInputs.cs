using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Application.Tool;

/// <summary>
/// Reads the HTML documents that <c>vite build</c> emits, so the development
/// loop prepares the same pages that build and publish copy to the web root.
/// </summary>
internal static class ViteBuildInputs
{
    internal const string EntryNotFoundCode = "RAPPDEV1009";
    private const string InputsUnavailableCode = "RAPPDEV1010";
    private const string Marker = "RUNIC_VITE_INPUTS=";

    /// <summary>
    /// Returns the configuration with its development documents taken from the
    /// Vite build inputs, or with the explicit
    /// <c>RunicApplicationFrontendDevServerDocument</c> list after checking
    /// that each document exists below the Vite root.
    /// </summary>
    internal static async Task<DevProjectConfiguration> ApplyAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using PhaseTimer phase = PhaseTimer.Start("Reading Vite build inputs");
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        // Bun projects may have no Node.js; the probe runs on the runtime that runs Vite.
        string runtime = packageManager.Name == "bun" ? "bun" : "node";
        IReadOnlyList<string> arguments = ["-e", CreateProbe(configuration.ViteConfigurationPath)];
        CommandResult result = await CommandRunner
            .RunAsync(runtime, configuration.FrontendPackageDirectory, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevDevelopmentException(
                InputsUnavailableCode,
                "Could not read the build inputs from the Vite configuration. Check that 'vite build' works in the frontend directory.",
                CommandRunner.LocalDetail(configuration.FrontendPackageDirectory, result.CombinedOutput.Trim()));
        }
        (string root, IReadOnlyList<string> inputs, bool routed) = Parse(result.StandardOutput);
        IReadOnlyList<string> documents = SelectDocuments(
            root,
            inputs,
            configuration.HasExplicitDevelopmentServerDocuments ? configuration.DevelopmentServerDocuments : null,
            routed);
        Console.WriteLine($"[dev] Pages: {string.Join(", ", documents)}");
        phase.Complete();
        return configuration with
        {
            DevelopmentServerDocument = string.Join(';', documents),
            UsesRunicVitePlugin = ParseRunicPlugin(result.StandardOutput),
        };
    }

    /// <summary>
    /// Chooses the development documents: the explicit list when one is set,
    /// otherwise every HTML build input with <c>index.html</c> first, because
    /// the first document is the application's entry document. Every document
    /// must exist below the Vite root, which a missing window entry does not.
    /// A routed configuration, such as SvelteKit's, builds its pages from routes: its development server serves
    /// <c>index.html</c> without a file of that name, so only the explicit list is checked there.
    /// </summary>
    internal static IReadOnlyList<string> SelectDocuments(
        string root,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string>? explicitDocuments,
        bool routed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);
        bool derived = explicitDocuments is null;
        List<string> documents = derived
            ? [.. inputs
                .Where(input => input.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .Select(input => Relative(root, input))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(document => document == "index.html" ? 0 : 1)]
            : [.. explicitDocuments!];
        if (documents.Count == 0)
        {
            // A library-style configuration builds no page; keep the conventional entry.
            documents.Add("index.html");
        }
        if (routed && derived)
        {
            return documents;
        }
        foreach (string document in documents)
        {
            if (!File.Exists(Path.Combine(root, document.Replace('/', Path.DirectorySeparatorChar))))
            {
                throw new DevDevelopmentException(
                    EntryNotFoundCode,
                    derived
                        ? $"Window entry not found: '{document}' is a Vite build input, but the file does not exist. " +
                          "Create the page or remove it from the build input in the Vite configuration."
                        : $"Window entry not found: '{document}' is listed in RunicApplicationFrontendDevServerDocument, " +
                          "but the file does not exist below the Vite root.",
                    $"Vite root: {root}\n", category: CommandExitCategory.Unavailable);
            }
        }
        return documents;
    }

    internal static (string Root, IReadOnlyList<string> Inputs, bool Routed) Parse(string output)
    {
        using JsonDocument document = ReadMarker(output);
        string root = document.RootElement.GetProperty("root").GetString() ?? string.Empty;
        List<string> inputs = [.. document.RootElement.GetProperty("inputs").EnumerateArray()
            .Select(input => input.GetString() ?? string.Empty)];
        bool routed = document.RootElement.TryGetProperty("routed", out JsonElement value) && value.ValueKind == JsonValueKind.True;
        return (root, inputs, routed);
    }

    /// <summary>
    /// Whether the resolved configuration has <c>runic()</c> (SvelteKit: <c>runic({ html: false })</c>) or <c>runicDevSession()</c> from
    /// <c>@runic-artifex/vite-plugin-runic</c>, whose <c>/__runic/state</c> confirms the dev session; null when the
    /// probe did not say.
    /// </summary>
    internal static bool? ParseRunicPlugin(string output)
    {
        using JsonDocument document = ReadMarker(output);
        return document.RootElement.TryGetProperty("runic", out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static JsonDocument ReadMarker(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        // Plugins may write to stdout while the configuration loads; the marker line is last.
        string? line = output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(candidate => candidate.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null)
        {
            throw new DevDevelopmentException(
                InputsUnavailableCode,
                "Could not read the build inputs from the Vite configuration.",
                output.Trim());
        }
        return JsonDocument.Parse(line[Marker.Length..]);
    }

    /// <summary>
    /// A script for <c>node -e</c> and <c>bun -e</c> that resolves the Vite
    /// configuration as <c>vite build</c> does and prints the absolute input paths.
    /// Vite resolves relative inputs against its root and defaults to index.html.
    /// <c>routed</c> is true when SvelteKit's plugin builds the pages from routes instead of HTML inputs.
    /// </summary>
    internal static string CreateProbe(string configurationPath)
    {
        string configFile = configurationPath.Length == 0
            ? "undefined"
            : "\"" + JsonEncodedText.Encode(configurationPath).ToString() + "\"";
        return $$"""
            import("vite").then(async ({ resolveConfig }) => {
              const path = await import("node:path");
              const config = await resolveConfig({ configFile: {{configFile}}, logLevel: "silent" }, "build", "production");
              const options = config.build.rolldownOptions ?? config.build.rollupOptions ?? {};
              const input = options.input ?? path.resolve(config.root, "index.html");
              const inputs = typeof input === "string" ? [input] : Array.isArray(input) ? input : Object.values(input);
              process.stdout.write("\n{{Marker}}" + JSON.stringify({
                root: config.root,
                inputs: inputs.map((entry) => path.resolve(config.root, String(entry))),
                routed: (config.plugins ?? []).some((plugin) => plugin?.name === "vite-plugin-sveltekit-setup"),
                runic: (config.plugins ?? []).some((plugin) => plugin?.name === "runic" || plugin?.name === "runic:dev-session"),
              }) + "\n");
            }).catch((error) => {
              console.error(error instanceof Error ? error.message : String(error));
              process.exit(1);
            });
            """;
    }

    private static string Relative(string root, string input)
    {
        string relative = Path.GetRelativePath(root, input);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new DevDevelopmentException(
                "RAPPDEV1005",
                $"The Vite build input '{Path.GetFileName(input)}' is outside the Vite root.",
                $"Input: {input}\nVite root: {root}\n");
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
