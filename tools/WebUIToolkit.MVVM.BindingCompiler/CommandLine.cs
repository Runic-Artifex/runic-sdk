using System;
using System.Collections.Generic;
using System.IO;

namespace WebUIToolkit.MVVM.BindingCompiler;

internal enum CommandKind
{
    Help,
    Version,
    Compile,
    Validate,
}

internal sealed record CommandLine(
    CommandKind Command,
    string? OutputPath,
    IReadOnlyList<string> InputPaths)
{
    internal const int MaximumArgumentCount = 512;
    internal const int MaximumArgumentLength = 32_768;
    internal const int MaximumInputCount = 256;

    public static CommandLine Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length > MaximumArgumentCount)
        {
            throw new CommandLineException($"No more than {MaximumArgumentCount} arguments are accepted.");
        }

        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                throw new CommandLineException("Arguments cannot be null.");
            }

            if (argument.Length > MaximumArgumentLength)
            {
                throw new CommandLineException(
                    $"An argument exceeds the {MaximumArgumentLength} character limit.");
            }

            if (argument.Contains('\0', StringComparison.Ordinal))
            {
                throw new CommandLineException("Arguments cannot contain a NUL character.");
            }
        }

        if (arguments.Length == 0)
        {
            return new CommandLine(CommandKind.Help, null, Array.Empty<string>());
        }

        if (arguments.Length == 1 && arguments[0] is "help" or "--help" or "-h")
        {
            return new CommandLine(CommandKind.Help, null, Array.Empty<string>());
        }

        if (arguments.Length == 1 && arguments[0] is "version" or "--version")
        {
            return new CommandLine(CommandKind.Version, null, Array.Empty<string>());
        }

        CommandKind command = arguments[0] switch
        {
            "compile" => CommandKind.Compile,
            "validate" => CommandKind.Validate,
            _ => throw new CommandLineException($"Unknown command '{arguments[0]}'."),
        };

        string? outputPath = null;
        var inputPaths = new List<string>();
        bool optionsEnded = false;
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && argument == "--output")
            {
                if (command != CommandKind.Compile)
                {
                    throw new CommandLineException("--output is valid only for the compile command.");
                }

                if (outputPath is not null || index + 1 >= arguments.Length)
                {
                    throw new CommandLineException("--output must occur exactly once and be followed by a path or '-'.");
                }

                outputPath = arguments[++index];
                if (outputPath.Length == 0)
                {
                    throw new CommandLineException("The --output value cannot be empty.");
                }

                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                throw new CommandLineException($"Unknown option '{argument}'. Use -- before a path beginning with '-'.");
            }

            if (argument.Length == 0)
            {
                throw new CommandLineException("Input paths cannot be empty.");
            }

            inputPaths.Add(argument);
            if (inputPaths.Count > MaximumInputCount)
            {
                throw new CommandLineException($"No more than {MaximumInputCount} input files are accepted.");
            }
        }

        if (inputPaths.Count == 0)
        {
            throw new CommandLineException($"The {arguments[0]} command requires at least one input file.");
        }

        if (command == CommandKind.Compile && outputPath is null)
        {
            outputPath = "-";
        }

        return new CommandLine(command, outputPath, inputPaths);
    }
}

internal sealed class CommandLineException : Exception
{
    public CommandLineException(string message)
        : base(message)
    {
    }

    public CommandLineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
