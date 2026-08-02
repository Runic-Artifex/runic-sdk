using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed record DevOptions(
    string? Project,
    string Configuration,
    bool Restore,
    bool GenerateContracts,
    bool WatchFrontend,
    bool WatchHost,
    bool DryRun,
    IReadOnlyList<string> ApplicationArguments)
{
    internal static DevOptions Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0 || IsHelp(arguments[0]))
        {
            return new(null, "Debug", true, true, true, true, false, Array.Empty<string>());
        }

        if (!StringComparer.Ordinal.Equals(arguments[0], "dev"))
        {
            throw new DevUsageException(
                "WUTDEV1001",
                $"Unknown command '{arguments[0]}'. Expected 'dev', 'doctor', or 'inspect'.");
        }

        string? project = null;
        string configuration = "Debug";
        bool restore = true;
        bool contracts = true;
        bool watchFrontend = true;
        bool watchHost = true;
        bool dryRun = false;
        var applicationArguments = new List<string>();
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (StringComparer.Ordinal.Equals(argument, "--"))
            {
                for (index++; index < arguments.Length; index++)
                {
                    applicationArguments.Add(arguments[index]);
                }

                break;
            }

            switch (argument)
            {
                case "--project":
                    project = RequireValue(arguments, ref index, argument);
                    break;
                case "--configuration":
                    configuration = RequireValue(arguments, ref index, argument);
                    break;
                case "--no-restore":
                    restore = false;
                    break;
                case "--no-contracts":
                    contracts = false;
                    break;
                case "--no-frontend-watch":
                    watchFrontend = false;
                    break;
                case "--no-dotnet-watch":
                    watchHost = false;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help":
                case "-h":
                    throw new DevUsageException("WUTDEV1000", "help");
                default:
                    if (argument.StartsWith('-'))
                    {
                        throw new DevUsageException(
                            "WUTDEV1001",
                            $"Unknown option '{argument}'.");
                    }

                    if (project is not null)
                    {
                        throw new DevUsageException(
                            "WUTDEV1001",
                            "Specify at most one project. Pass application arguments after '--'.");
                    }

                    project = argument;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new DevUsageException("WUTDEV1001", "Configuration cannot be empty.");
        }

        return new(
            project,
            configuration,
            restore,
            contracts,
            watchFrontend,
            watchHost,
            dryRun,
            new ReadOnlyCollection<string>(applicationArguments));
    }

    internal static bool RequestsHelp(string[] arguments)
    {
        if (arguments.Length == 0 || (arguments.Length == 1 && IsHelp(arguments[0])))
        {
            return true;
        }

        for (int index = 0; index < arguments.Length; index++)
        {
            if (StringComparer.Ordinal.Equals(arguments[index], "--"))
            {
                return false;
            }

            if (StringComparer.Ordinal.Equals(arguments[index], "--help")
                || StringComparer.Ordinal.Equals(arguments[index], "-h"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelp(string value) =>
        StringComparer.Ordinal.Equals(value, "--help")
        || StringComparer.Ordinal.Equals(value, "-h")
        || StringComparer.Ordinal.Equals(value, "help");

    private static string RequireValue(string[] arguments, ref int index, string option)
    {
        if (++index == arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new DevUsageException("WUTDEV1001", $"{option} requires a value.");
        }

        return arguments[index];
    }
}

internal sealed class DevUsageException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class DevDevelopmentException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
