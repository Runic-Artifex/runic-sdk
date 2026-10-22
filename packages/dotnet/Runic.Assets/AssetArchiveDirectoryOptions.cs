using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Runic.Assets;

/// <summary>
/// Selects the entry point and the files left out when <see cref="AssetArchive"/> packs a directory.
/// </summary>
public sealed class AssetArchiveDirectoryOptions
{
    internal static AssetArchiveDirectoryOptions Default { get; } = new();

    /// <summary>
    /// Gets the entry document relative to the directory, with <c>/</c> separators. Defaults to <c>index.html</c>.
    /// It must exist and must not be excluded.
    /// </summary>
    public string EntryPoint { get; init; } = "index.html";

    /// <summary>
    /// Gets exact asset paths, relative to the directory with <c>/</c> separators, to leave out. Empty by default.
    /// </summary>
    public ImmutableArray<string> ExcludedPaths { get; init; } = [];

    /// <summary>
    /// Gets whether <c>.git</c> directories and files, and files whose names start with <c>.env</c> (such as
    /// <c>.env.local</c> or <c>.envrc</c>), are left out at any depth. Names are compared without case.
    /// Defaults to <see langword="true"/>; set <see langword="false"/> to pack them.
    /// </summary>
    public bool ExcludeDefaultPatterns { get; init; } = true;
}

// The entry point and exclusions of one directory scan, shared by the pinned Linux and the trusted sources.
internal sealed class AssetDirectoryExclusions
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly bool _defaults;

    internal AssetDirectoryExclusions(AssetArchiveDirectoryOptions options)
        : this(options.EntryPoint, options.ExcludedPaths.IsDefault ? [] : options.ExcludedPaths, options.ExcludeDefaultPatterns)
    {
    }

    internal AssetDirectoryExclusions(string entryPoint, IEnumerable<string>? excludedPaths, bool excludeDefaultPatterns)
    {
        EntryPoint = AssetPath.Normalize(entryPoint);
        if (excludedPaths is not null)
        {
            foreach (string excludedPath in excludedPaths)
            {
                _paths.Add(AssetPath.Normalize(excludedPath));
            }
        }

        _defaults = excludeDefaultPatterns;
    }

    internal string EntryPoint { get; }

    internal bool ExcludesDirectory(string name) => _defaults && IsDefaultDirectoryName(name);

    internal bool ExcludesDefaultFileName(string name) => _defaults && IsDefaultFileName(name);

    internal bool ExcludesFile(string relativePath, string name) =>
        _paths.Contains(relativePath) || ExcludesDefaultFileName(name);

    /// <summary>Whether a directory of this name is left out by default: <c>.git</c>.</summary>
    internal static bool IsDefaultDirectoryName(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file of this name is left out by default: <c>.git</c> (a Git link file) or <c>.env*</c>.</summary>
    internal static bool IsDefaultFileName(string name) =>
        IsDefaultDirectoryName(name) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a relative asset path is left out by default: a <c>.git</c> segment or a <c>.env*</c> file name.</summary>
    internal static bool IsDefaultPath(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (IsDefaultDirectoryName(segments[index]))
            {
                return true;
            }
        }

        return IsDefaultFileName(segments[^1]);
    }
}
