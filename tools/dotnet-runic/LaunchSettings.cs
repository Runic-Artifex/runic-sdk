using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Tool;

/// <summary>
/// A profile of <c>Properties/launchSettings.json</c>: the keys that dev and doctor read (design/ide-run-debug §3.2), and
/// what is wrong with its optional <c>rax</c> object, whose keys Runic DevKit reads (D084.4).
/// </summary>
internal sealed record LaunchProfile(
    string Name,
    string CommandName,
    string CommandLineArgs,
    IReadOnlyList<string> RaxProblems)
{
    /// <summary>The names of the profile's <c>environmentVariables</c>, which dotnet run sets over the process's own.</summary>
    internal IReadOnlyList<string> EnvironmentNames { get; init; } = [];

    /// <summary>A <c>Project</c> profile: the only kind that dotnet run, dev, vsdbg and Runic DevKit apply.</summary>
    internal bool IsProject => CommandName.Equals("Project", StringComparison.Ordinal);
}

/// <summary>The profiles of a project's <c>Properties/launchSettings.json</c>, or why it could not be read.</summary>
internal sealed record LaunchSettings(string Path, IReadOnlyList<LaunchProfile> Profiles, string? Error = null)
{
    internal const string RelativePath = "Properties/launchSettings.json";

    /// <summary>The keys of <c>rax</c>: <c>dev</c> and <c>page</c> for F5, <c>devArguments</c> for the dev that F5 starts.</summary>
    private static readonly string[] RaxKeys = ["dev", "page", "devArguments"];

    /// <summary>The first <c>Project</c> profile, the one dotnet run uses by default (and dev since 0.24); null for none.</summary>
    internal LaunchProfile? FirstProject => Profiles.FirstOrDefault(static profile => profile.IsProject);

    /// <summary>The profile named <paramref name="name"/>, as dotnet run matches it, else one that differs in case only.</summary>
    internal LaunchProfile? Find(string name) =>
        Profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.Ordinal)) ??
        Profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads <c>Properties/launchSettings.json</c> of <paramref name="projectDirectory"/>, with comments and trailing commas
    /// as dotnet run accepts them; null when the file does not exist.
    /// </summary>
    internal static LaunchSettings? Read(string projectDirectory)
    {
        string path = System.IO.Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return null;
        try
        {
            return Parse(path, File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new LaunchSettings(path, [], error.Message);
        }
    }

    internal static LaunchSettings Parse(string path, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException error)
        {
            return new LaunchSettings(path, [], error.Message);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("profiles", out JsonElement profiles))
            {
                return new LaunchSettings(path, []);
            }
            if (profiles.ValueKind != JsonValueKind.Object) return new LaunchSettings(path, [], "\"profiles\" is not an object.");
            var read = new List<LaunchProfile>();
            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                if (profile.Value.ValueKind != JsonValueKind.Object) continue;
                read.Add(new LaunchProfile(
                    profile.Name,
                    String(profile.Value, "commandName"),
                    String(profile.Value, "commandLineArgs"),
                    profile.Value.TryGetProperty("rax", out JsonElement rax) ? CheckRax(rax) : [])
                {
                    EnvironmentNames = profile.Value.TryGetProperty("environmentVariables", out JsonElement variables) &&
                        variables.ValueKind == JsonValueKind.Object
                            ? [.. variables.EnumerateObject().Select(static variable => variable.Name)]
                            : [],
                });
            }
            return new LaunchSettings(path, read);
        }
    }

    private static string String(JsonElement profile, string name) =>
        profile.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// What is wrong with a profile's <c>rax</c> object (§3.2): <c>dev</c> is <c>"no-window"</c> or <c>"off"</c>,
    /// <c>page</c> a boolean, <c>devArguments</c> an array of strings; other keys are unknown.
    /// </summary>
    internal static IReadOnlyList<string> CheckRax(JsonElement rax)
    {
        if (rax.ValueKind != JsonValueKind.Object) return ["\"rax\" is not an object"];
        var problems = new List<string>();
        foreach (JsonProperty key in rax.EnumerateObject())
        {
            switch (key.Name)
            {
                case "dev" when key.Value.ValueKind != JsonValueKind.String || key.Value.GetString() is not ("no-window" or "off"):
                    problems.Add("rax.dev must be \"no-window\" or \"off\"");
                    break;
                case "page" when key.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    problems.Add("rax.page must be true or false");
                    break;
                case "devArguments" when key.Value.ValueKind != JsonValueKind.Array ||
                    key.Value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String):
                    problems.Add("rax.devArguments must be an array of strings");
                    break;
                default:
                    if (!RaxKeys.Contains(key.Name, StringComparer.Ordinal)) problems.Add($"rax.{key.Name} is not a key Runic reads");
                    break;
            }
        }
        return problems;
    }

    /// <summary>
    /// Splits a command line into arguments by the rules of .NET's <c>ProcessStartInfo.Arguments</c>, with which dotnet
    /// run turns <c>RunArguments</c> and a profile's <c>commandLineArgs</c> into argv on every OS: spaces and tabs separate
    /// arguments outside double quotes; 2n backslashes before a quote are n backslashes and the quote toggles quoting,
    /// 2n+1 are n and a literal quote; <c>""</c> inside quotes is a quote; other backslashes are literal.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var arguments = new List<string>();
        int i = 0;
        while (true)
        {
            while (i < commandLine.Length && commandLine[i] is ' ' or '\t') i++;
            if (i == commandLine.Length) return arguments;
            var argument = new StringBuilder();
            bool quoted = false;
            while (i < commandLine.Length)
            {
                int backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (backslashes > 0)
                {
                    if (i == commandLine.Length || commandLine[i] != '"')
                    {
                        argument.Append('\\', backslashes);
                    }
                    else
                    {
                        argument.Append('\\', backslashes / 2);
                        if (backslashes % 2 != 0)
                        {
                            argument.Append('"');
                            i++;
                        }
                    }
                    continue;
                }
                char c = commandLine[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        argument.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    i++;
                    continue;
                }
                if (c is ' ' or '\t' && !quoted) break;
                argument.Append(c);
                i++;
            }
            arguments.Add(argument.ToString());
        }
    }
}

/// <summary>
/// Which launch profile dev runs the Window with (D084.3), and the arguments dev passes itself so that none is lost
/// (D084.2): dotnet run drops a profile's <c>commandLineArgs</c> when the project sets <c>StartArguments</c> or the
/// command line has arguments, while Visual Studio and Runic DevKit pass the project's, then the profile's.
/// </summary>
internal static class DevLaunch
{
    internal const string BothArgumentsCode = "RAPPDEV1020";

    /// <summary>
    /// The options with the profile resolved: <c>--launch-profile</c>'s, none with <c>--no-launch-profile</c>, otherwise
    /// the first <c>Project</c> profile, as dotnet run chooses it. When the profile has arguments and the project or the
    /// command line has some too, dev passes the profile's after <c>--</c>, before the command line's. Writes RAPPDEV1020
    /// when the project and the profile both set arguments. <c>--no-window</c> leaves the Window, and so the profile, to
    /// the IDE.
    /// </summary>
    internal static DevOptions Resolve(DevProjectConfiguration configuration, DevOptions options, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(warn);
        if (!options.StartWindow || !options.UseLaunchProfile) return options;
        LaunchSettings? settings = LaunchSettings.Read(configuration.ProjectDirectory);
        if (settings?.Error is { } error)
        {
            warn($"[dev] Launch profile: none; {LaunchSettings.RelativePath} could not be read: {error}");
            return options with { LaunchProfile = null };
        }
        LaunchProfile? profile = options.LaunchProfile is { } named ? settings?.Find(named) : settings?.FirstProject;
        options = options with
        {
            // An explicit name that the file lacks still goes to dotnet run, which reports it.
            LaunchProfile = profile?.Name ?? options.LaunchProfile,
            LaunchProfileIsDefault = options.LaunchProfile is null && profile is not null,
            // A profile's variables replace the process's, so a -e value of a name the profile sets goes to dotnet run's
            // own -e, which wins over the profile. Only those: a command line is visible to other local processes.
            RunEnvironment = profile is null
                ? []
                : [.. options.Environment.Where(variable => profile.EnvironmentNames.Contains(variable.Key, StringComparer.OrdinalIgnoreCase))],
        };
        if (profile is not { IsProject: true } || profile.CommandLineArgs.Trim().Length == 0) return options;
        bool projectArguments = configuration.StartArguments.Trim().Length != 0;
        if (projectArguments) warn(BothArgumentsWarning(configuration.ProjectPath, configuration.StartArguments, profile));
        return projectArguments || options.ApplicationArguments.Count != 0
            ? options with { ProfileArguments = LaunchSettings.SplitArguments(profile.CommandLineArgs) }
            : options;
    }

    internal static string BothArgumentsMessage(string startArguments, LaunchProfile profile) =>
        $"The project sets StartArguments ({startArguments.Trim()}) and launch profile '{profile.Name}' sets commandLineArgs " +
        $"({profile.CommandLineArgs.Trim()}). dotnet runic dev, Visual Studio and Runic DevKit pass both, the project's first, " +
        "but dotnet run drops the profile's. Keep each argument in one place.";

    internal static string BothArgumentsWarning(string project, string startArguments, LaunchProfile profile) =>
        $"{project} : warning {BothArgumentsCode}: {BothArgumentsMessage(startArguments, profile)}";
}
