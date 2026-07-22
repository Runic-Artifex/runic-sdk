using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Tool;

public enum ToolCommand
{
    Help,
    ManualScan,
    NuGetScan,
    NpmScan,
    ContractPackageUrl,
    ContractSpdx,
    ContractDiagnostics,
    Policy,
    Generate,
    Verify,
    Sbom,
    Acquire,
}

public enum ToolOutputFormat
{
    Human,
    Json,
}

public sealed record ToolInvocation(
    ToolCommand Command,
    ToolOutputFormat Format,
    string RootDirectory,
    string ConfigPath,
    string? Value,
    bool AllowNetwork,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options)
{
    public string? GetValue(string name) =>
        Options.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count != 0 ? values[^1] : null;

    public IReadOnlyList<string> GetValues(string name) =>
        Options.TryGetValue(name, out IReadOnlyList<string>? values) ? values : Array.Empty<string>();

    public bool HasOption(string name) => Options.ContainsKey(name);
}

public sealed record ToolParseResult(ToolInvocation? Invocation, string? Error)
{
    public bool Succeeded => Invocation is not null;
}

public static class CommandLineParser
{
    public static ToolParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || IsHelp(args[0]))
        {
            return Success(ToolCommand.Help);
        }

        int optionStart;
        ToolCommand command;
        switch (args[0])
        {
            case "manual" when args.Count > 1 && args[1] == "scan":
            case "scan" when args.Count > 1 && args[1] == "manual":
                command = ToolCommand.ManualScan;
                optionStart = 2;
                break;
            case "scan" when args.Count > 1 && args[1] == "nuget":
                command = ToolCommand.NuGetScan;
                optionStart = 2;
                break;
            case "scan" when args.Count > 1 && args[1] == "npm":
                command = ToolCommand.NpmScan;
                optionStart = 2;
                break;
            case "contract" when args.Count > 1 && args[1] == "purl":
                command = ToolCommand.ContractPackageUrl;
                optionStart = 2;
                break;
            case "contract" when args.Count > 1 && args[1] == "spdx":
                command = ToolCommand.ContractSpdx;
                optionStart = 2;
                break;
            case "contract" when args.Count > 1 && args[1] == "diagnostics":
                command = ToolCommand.ContractDiagnostics;
                optionStart = 2;
                break;
            case "scan": return Failure("The scan command requires an explicit 'manual', 'nuget', or 'npm' adapter.");
            case "policy": command = ToolCommand.Policy; optionStart = 1; break;
            case "generate": command = ToolCommand.Generate; optionStart = 1; break;
            case "verify": command = ToolCommand.Verify; optionStart = 1; break;
            case "sbom": command = ToolCommand.Sbom; optionStart = 1; break;
            case "acquire": command = ToolCommand.Acquire; optionStart = 1; break;
            default:
                return Failure($"Unknown command '{SafeArgument(args[0])}'.");
        }

        string root = ".";
        string config = "dependency-notices.json";
        string? value = null;
        ToolOutputFormat format = ToolOutputFormat.Human;
        bool allowNetwork = false;
        bool formatSeen = false;
        HashSet<string> seen = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> optionValues = new(StringComparer.Ordinal);

        for (int index = optionStart; index < args.Count; index++)
        {
            string option = args[index];
            if (IsHelp(option))
            {
                if (args.Count != optionStart + 1)
                {
                    return Failure("The help option cannot be combined with other arguments.");
                }

                return Success(ToolCommand.Help);
            }

            if (!IsRepeatable(option) && !seen.Add(option))
            {
                return Failure($"Option '{SafeArgument(option)}' was specified more than once.");
            }

            switch (option)
            {
                case "--root":
                    if (!TryReadValue(args, ref index, out root)) return Failure("Option '--root' requires a value.");
                    break;
                case "--config":
                    if (!TryReadValue(args, ref index, out config)) return Failure("Option '--config' requires a value.");
                    break;
                case "--value":
                    if (!TryReadValue(args, ref index, out value)) return Failure("Option '--value' requires a value.");
                    break;
                case "--format":
                case "--diagnostics-format":
                    if (formatSeen) return Failure("The diagnostics format was specified more than once.");
                    formatSeen = true;
                    if (!TryReadValue(args, ref index, out string formatText)) return Failure($"Option '{option}' requires a value.");
                    format = formatText switch
                    {
                        "human" => ToolOutputFormat.Human,
                        "json" => ToolOutputFormat.Json,
                        _ => (ToolOutputFormat)(-1),
                    };
                    if ((int)format < 0) return Failure($"Option '{option}' must be 'human' or 'json'.");
                    break;
                case "--allow-network":
                    allowNetwork = true;
                    break;
                case "--allow-http":
                    AddFlag(optionValues, option);
                    break;
                case "--lock":
                case "--assets":
                case "--framework":
                case "--runtime":
                case "--packages-root":
                case "--workspace":
                case "--profile":
                case "--policy":
                case "--purl":
                case "--license":
                case "--selected-license":
                case "--evaluation-date":
                case "--output":
                case "--artifact-name":
                case "--artifact-version":
                case "--sbom":
                case "--origin":
                case "--sha256":
                case "--cache":
                case "--max-bytes":
                case "--timeout-seconds":
                case "--evidence-digest":
                case "--obligation":
                case "--component":
                case "--allow-host":
                    if (!TryReadValue(args, ref index, out string optionValue)) return Failure($"Option '{option}' requires a value.");
                    AddValue(optionValues, option, optionValue);
                    break;
                default:
                    return Failure($"Unknown option '{SafeArgument(option)}'.");
            }
        }

        if (allowNetwork && command != ToolCommand.Acquire)
        {
            return Failure("WUTNOTICE7001: '--allow-network' is valid only for the acquire command.");
        }

        if (command == ToolCommand.Acquire && !allowNetwork)
        {
            return Failure("WUTNOTICE7001: acquire requires explicit '--allow-network'.");
        }

        if (command is ToolCommand.ContractPackageUrl or ToolCommand.ContractSpdx)
        {
            if (string.IsNullOrWhiteSpace(value)) return Failure("This contract command requires '--value'.");
        }
        else if (value is not null)
        {
            return Failure("Option '--value' is valid only for purl and spdx contract commands.");
        }

        if (command is not (ToolCommand.ManualScan or ToolCommand.NpmScan or ToolCommand.Generate or ToolCommand.Verify) && root != ".")
        {
            return Failure("Option '--root' is not valid for the selected command.");
        }
        if (command is not (ToolCommand.ManualScan or ToolCommand.Generate or ToolCommand.Verify) && config != "dependency-notices.json")
        {
            return Failure("Option '--config' is not valid for the selected command.");
        }

        foreach (string option in optionValues.Keys)
        {
            if (!IsAllowed(command, option)) return Failure($"Option '{option}' is not valid for the selected command.");
        }

        string? missing = MissingRequired(command, optionValues);
        if (missing is not null) return Failure($"The {command.ToString().ToLowerInvariant()} command requires '{missing}'.");

        Dictionary<string, IReadOnlyList<string>> frozen = new(StringComparer.Ordinal);
        foreach ((string option, List<string> values) in optionValues) frozen.Add(option, values.AsReadOnly());
        return new ToolParseResult(new ToolInvocation(command, format, root, config, value, allowNetwork, frozen), null);
    }

    private static ToolParseResult Success(ToolCommand command) =>
        new(new ToolInvocation(command, ToolOutputFormat.Human, ".", "dependency-notices.json", null, false,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)), null);

    private static ToolParseResult Failure(string error) => new(null, error);

    private static bool IsHelp(string value) => value is "--help" or "-h" or "help";

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return value.Length > 0;
    }

    private static string SafeArgument(string value)
    {
        const int maximum = 64;
        string sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maximum ? sanitized : sanitized[..maximum] + "...";
    }

    private static bool IsRepeatable(string option) => option is "--evidence-digest" or "--obligation" or "--component" or "--allow-host";

    private static void AddValue(Dictionary<string, List<string>> values, string option, string value)
    {
        if (!values.TryGetValue(option, out List<string>? entries))
        {
            entries = [];
            values.Add(option, entries);
        }
        entries.Add(value);
    }

    private static void AddFlag(Dictionary<string, List<string>> values, string option) => AddValue(values, option, "true");

    private static bool IsAllowed(ToolCommand command, string option) => command switch
    {
        ToolCommand.NuGetScan => option is "--lock" or "--assets" or "--framework" or "--runtime" or "--packages-root",
        ToolCommand.NpmScan => option is "--lock" or "--workspace" or "--profile",
        ToolCommand.Policy => option is "--policy" or "--purl" or "--license" or "--selected-license" or "--evaluation-date" or "--evidence-digest" or "--obligation",
        ToolCommand.Generate or ToolCommand.Verify => option is "--output" or "--artifact-name" or "--artifact-version",
        ToolCommand.Sbom => option is "--sbom" or "--component",
        ToolCommand.Acquire => option is "--origin" or "--sha256" or "--cache" or "--allow-host" or "--allow-http" or "--max-bytes" or "--timeout-seconds",
        _ => false,
    };

    private static string? MissingRequired(ToolCommand command, Dictionary<string, List<string>> values)
    {
        string[] required = command switch
        {
            ToolCommand.NuGetScan => ["--lock", "--assets", "--framework"],
            ToolCommand.NpmScan => ["--lock"],
            ToolCommand.Policy => ["--policy", "--purl", "--license", "--evaluation-date"],
            ToolCommand.Generate or ToolCommand.Verify => ["--output", "--artifact-name"],
            ToolCommand.Sbom => ["--sbom", "--component"],
            ToolCommand.Acquire => ["--origin", "--sha256", "--cache", "--allow-host"],
            _ => [],
        };
        foreach (string option in required)
        {
            if (!values.ContainsKey(option)) return option;
        }
        return null;
    }
}
