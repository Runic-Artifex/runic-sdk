using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The WebKit Inspector Protocol of the running WebKit, read from the <c>InspectorBackendCommands.js</c> that its remote
/// inspector server sends a new client (<c>InspectorBackend.registerCommand("Debugger.enable", …)</c> lines). The adapter
/// sends WebKit only commands it has; the rest are emulated or answered as unsupported.
/// </summary>
internal sealed partial class WebKitProtocol
{
    private readonly HashSet<string> _commands;
    private readonly HashSet<string> _events;

    private WebKitProtocol(HashSet<string> commands, HashSet<string> events, bool known)
    {
        _commands = commands;
        _events = events;
        IsKnown = known;
    }

    /// <summary>
    /// A protocol that has every command: for a server that sent no description, so the adapter behaves as with the
    /// WebKit the spike measured (2.54) and lets WebKit answer an unknown command itself.
    /// </summary>
    internal static WebKitProtocol Unknown { get; } = new([], [], known: false);

    /// <summary>Whether the server described its protocol.</summary>
    internal bool IsKnown { get; }

    internal int CommandCount => _commands.Count;

    internal static WebKitProtocol Parse(string backendCommands)
    {
        ArgumentNullException.ThrowIfNull(backendCommands);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Registration().Matches(backendCommands))
        {
            (match.Groups["kind"].Value == "Command" ? commands : events).Add(match.Groups["name"].Value);
        }
        return commands.Count == 0 ? Unknown : new WebKitProtocol(commands, events, known: true);
    }

    /// <summary>Whether WebKit has the command, for example <c>Debugger.setPauseOnDebuggerStatements</c>.</summary>
    internal bool HasCommand(string method) => !IsKnown || _commands.Contains(method);

    internal bool HasEvent(string method) => !IsKnown || _events.Contains(method);

    [GeneratedRegex("InspectorBackend\\.register(?<kind>Command|Event)\\(\"(?<name>[A-Za-z]+\\.[A-Za-z]+)\"")]
    private static partial Regex Registration();
}
