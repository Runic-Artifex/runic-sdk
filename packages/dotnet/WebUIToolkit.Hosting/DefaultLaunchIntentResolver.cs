using System;
using System.Collections.Generic;

namespace WebUIToolkit.Hosting;

/// <summary>Classifies root launch arguments without resolving services or causing side effects.</summary>
public sealed class DefaultLaunchIntentResolver : ILaunchIntentResolver
{
    private const string AmbiguousDiagnostic =
        "A reserved launch option cannot be combined with other arguments.";
    private const string UnknownOptionDiagnostic =
        "The launch option is not recognized.";
    private const string InvalidCommandDiagnostic =
        "The command name is not valid.";

    /// <inheritdoc />
    public LaunchDecision Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return new LaunchDecision(LaunchKind.UserInterface, arguments);
        }

        string first = arguments[0]
            ?? throw new ArgumentException("Launch arguments cannot contain null entries.", nameof(arguments));

        LaunchKind? reservedKind = first switch
        {
            "--ui" => LaunchKind.UserInterface,
            "--help" or "-h" => LaunchKind.Help,
            "--version" => LaunchKind.Version,
            _ => null,
        };

        if (reservedKind is not null)
        {
            return arguments.Count == 1
                ? new LaunchDecision(reservedKind.Value, arguments)
                : Invalid(arguments, AmbiguousDiagnostic);
        }

        if (string.IsNullOrWhiteSpace(first) || ContainsControlCharacter(first))
        {
            return Invalid(arguments, InvalidCommandDiagnostic);
        }

        if (first[0] == '-')
        {
            return Invalid(arguments, UnknownOptionDiagnostic);
        }

        return new LaunchDecision(LaunchKind.Command, arguments, first);
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private static LaunchDecision Invalid(
        IReadOnlyList<string> arguments,
        string safeDiagnostic) =>
        new(LaunchKind.Invalid, arguments, diagnostic: safeDiagnostic);
}
