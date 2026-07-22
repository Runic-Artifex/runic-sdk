using System;
using System.Collections.Generic;
using System.IO;

namespace WebUIToolkit.DependencyNotices.Packaging.Tests;

internal sealed record CliOptions(
    string Feed,
    string Version,
    string RepositoryRoot,
    string? AotRid,
    string? AotSupportFeed)
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            }

            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Argument '{args[index]}' was supplied more than once.");
            }
        }

        string feed = RequiredDirectory(values, "--feed");
        string repositoryRoot = RequiredDirectory(values, "--repository-root");
        string version = Required(values, "--version");
        string? aotRid = Optional(values, "--aot-rid");
        string? aotSupportFeed = Optional(values, "--aot-support-feed");
        if (aotSupportFeed is not null)
        {
            aotSupportFeed = Path.GetFullPath(aotSupportFeed);
            if (!Directory.Exists(aotSupportFeed))
            {
                throw new DirectoryNotFoundException($"AOT support feed '{aotSupportFeed}' does not exist.");
            }
        }

        if (aotRid is not null && aotSupportFeed is null)
        {
            throw new ArgumentException("--aot-rid requires --aot-support-feed so Native-AOT restore remains offline.");
        }

        HashSet<string> known = new(StringComparer.Ordinal)
        {
            "--feed", "--version", "--repository-root", "--aot-rid", "--aot-support-feed",
        };
        foreach (string key in values.Keys)
        {
            if (!known.Contains(key))
            {
                throw new ArgumentException($"Unknown argument '{key}'.");
            }
        }

        return new CliOptions(feed, version, repositoryRoot, aotRid, aotSupportFeed);
    }

    private static string RequiredDirectory(Dictionary<string, string> values, string name)
    {
        string path = Path.GetFullPath(Required(values, name));
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException($"Directory '{path}' does not exist.");
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument '{name}' was not supplied.");

    private static string? Optional(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
