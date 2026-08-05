using System;

namespace RunicToolkit.DotNet.RunicToolkit;

internal sealed record DoctorOptions(string? Project, string Configuration)
{
    internal static DoctorOptions Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0
            || !StringComparer.Ordinal.Equals(arguments[0], "doctor"))
        {
            throw new DevUsageException(
                "RTKDEV1001",
                "The doctor command must start with 'doctor'.");
        }

        string? project = null;
        string configuration = "Debug";
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--project":
                    project = RequireValue(arguments, ref index, argument);
                    break;
                case "--configuration":
                    configuration = RequireValue(arguments, ref index, argument);
                    break;
                case "--help":
                case "-h":
                    throw new DevUsageException("RTKDEV1000", "help");
                default:
                    if (argument.StartsWith('-'))
                    {
                        throw new DevUsageException(
                            "RTKDEV1001",
                            $"Unknown option '{argument}'.");
                    }

                    if (project is not null)
                    {
                        throw new DevUsageException(
                            "RTKDEV1001",
                            "Specify at most one project.");
                    }

                    project = argument;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new DevUsageException("RTKDEV1001", "Configuration cannot be empty.");
        }

        return new(project, configuration);
    }

    internal static bool RequestsHelp(string[] arguments) =>
        arguments.Length > 1
        && (StringComparer.Ordinal.Equals(arguments[1], "--help")
            || StringComparer.Ordinal.Equals(arguments[1], "-h")
            || StringComparer.Ordinal.Equals(arguments[1], "help"));

    private static string RequireValue(
        string[] arguments,
        ref int index,
        string option)
    {
        if (++index == arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new DevUsageException("RTKDEV1001", $"{option} requires a value.");
        }

        return arguments[index];
    }
}
