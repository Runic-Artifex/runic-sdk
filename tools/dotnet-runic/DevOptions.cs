using System;
using System.Collections.Generic;
using System.Linq;
using Runic.CommandLine;

namespace Runic.Application.Tool;

internal sealed record DevOptions(
    string? Project,
    string Configuration,
    bool Restore,
    bool WatchFrontend,
    bool WatchHost,
    bool DryRun,
    IReadOnlyList<string> ApplicationArguments)
{
    /// <summary>The MSBuild verbosity for the builds dev runs, from <c>--verbosity</c>; null leaves each tool's default.</summary>
    internal string? Verbosity { get; init; }

    /// <summary>The <c>launchSettings.json</c> profile from <c>--launch-profile</c>; null runs without a launch profile.</summary>
    internal string? LaunchProfile { get; init; }

    /// <summary>False with <c>--no-build</c>: dev runs no .NET restore or build of its own before starting the Window.</summary>
    internal bool Build { get; init; } = true;

    /// <summary>The <c>-e</c> variables for the Window process, in the order given; a later value of a name wins.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; } = [];
}

/// <summary>Reads and checks the option values that dev and size pass on to dotnet and MSBuild.</summary>
internal static class ToolOptionValues
{
    internal const string EnvironmentFormatCode = "RAPPDEV1011";
    internal const string ReservedEnvironmentCode = "RAPPDEV1012";
    internal const string PropertyFormatCode = "RAPPSIZE1005";
    internal const string ReservedPropertyCode = "RAPPSIZE1006";

    /// <summary>
    /// Variables dev sets or clears for the Window process to describe the development server and the live frontend
    /// output. They are an internal contract with the host, so <c>-e</c> cannot replace them.
    /// </summary>
    internal static bool IsReservedEnvironmentVariable(string name) =>
        name.Equals(HostProcessController.DevelopmentAssetsVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(ViteDevelopmentServer.ProjectEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RUNIC_APPLICATION_VITE_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("RUNIC_APPLICATION_FRONTEND_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <c>-e NAME=VALUE</c> values, as <c>dotnet run -e</c> takes them. The value may be empty.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseEnvironment(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var variables = new List<KeyValuePair<string, string>>(values.Count);
        foreach (string value in values)
        {
            int separator = value.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? value : value[..separator];
            if (separator <= 0 || name.Any(char.IsWhiteSpace) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new DevUsageException(EnvironmentFormatCode,
                    "-e takes NAME=VALUE: a variable name without spaces, then '=' and the value, which may be empty.");
            }
            if (IsReservedEnvironmentVariable(name))
            {
                throw new DevUsageException(ReservedEnvironmentCode,
                    $"-e cannot set {name.ToUpperInvariant()}: dev sets the RUNIC_APPLICATION_VITE_*, RUNIC_APPLICATION_FRONTEND_*, " +
                    "RUNIC_APPLICATION_DEV_PROJECT and RUNIC_APPLICATION_DEVELOPMENT_ASSETS variables itself.");
            }
            variables.Add(new(name, value[(separator + 1)..]));
        }
        return variables;
    }

    /// <summary>
    /// MSBuild global properties that size sets from its own options. <c>--property</c> cannot set them, so a report
    /// always says which option chose them.
    /// </summary>
    internal static readonly string[] SizeOptionProperties = ["Configuration", "RuntimeIdentifier", "PublishAot", "SelfContained"];

    /// <summary>
    /// Reads <c>--property Name=Value</c> values into MSBuild <c>-property:</c> arguments. A semicolon or comma in a
    /// value is escaped, so each value sets exactly one property; other MSBuild escapes such as %3B pass through.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseProperties(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var properties = new List<KeyValuePair<string, string>>(values.Count);
        foreach (string value in values)
        {
            int separator = value.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? value : value[..separator];
            if (separator <= 0 || !IsPropertyName(name))
            {
                throw new DevUsageException(PropertyFormatCode,
                    "--property takes Name=Value: an MSBuild property name of letters, digits, '_' and '-', starting with a letter or '_', then '=' and the value.");
            }
            if (Array.FindIndex(SizeOptionProperties, reserved => reserved.Equals(name, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                throw new DevUsageException(ReservedPropertyCode,
                    $"--property cannot set {name}: use --configuration, --runtime, --aot or --no-aot, and --self-contained or --no-self-contained.");
            }
            properties.Add(new(name, value[(separator + 1)..]));
        }
        return properties;
    }

    /// <summary>The <c>-property:</c> argument for one property; MSBuild's escapes keep separators in the value.</summary>
    internal static string PropertyArgument(KeyValuePair<string, string> property) =>
        $"-property:{property.Key}={property.Value.Replace(";", "%3B", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal)}";

    private static bool IsPropertyName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return false;
        }
        return true;
    }
}

/// <summary>
/// Input of the wrong shape that the parser and binder do not check, such as <c>-e</c> without <c>=</c> or two options
/// that need each other: <see cref="CommandExitCategory.Validation"/>, exit code 2. The same command line fails the same
/// way whatever the project, files or tools it finds.
/// </summary>
/// <remarks>
/// The message reaches the JSON fault and must not contain absolute paths.
/// <see cref="LocalDetail"/> carries paths and hints for human output only.
/// </remarks>
internal sealed class DevUsageException(string code, string message, string? localDetail = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? LocalDetail { get; } = localDetail;
}

/// <summary>
/// A failure on a well-formed command line, exit code 1: <see cref="CommandExitCategory.CommandFailure"/> for a project
/// or step that failed, <see cref="CommandExitCategory.Unavailable"/> for a project, file or program that is missing. See
/// <see cref="DevUsageException"/> for the message rules.
/// </summary>
internal sealed class DevDevelopmentException(
    string code,
    string message,
    string? localDetail = null,
    CommandExitCategory category = CommandExitCategory.CommandFailure) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? LocalDetail { get; } = localDetail;
    internal CommandExitCategory Category { get; } = category;
}
