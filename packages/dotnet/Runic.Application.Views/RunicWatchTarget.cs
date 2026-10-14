using System.IO.Enumeration;
using System.Text;

namespace Runic.Application.Views;

/// <summary>A file, symbolic link, directory or Git checkout that a <see cref="RunicPathWatcher"/> observes.</summary>
/// <remarks>
/// A target is compared by reference: the <see cref="RunicPathChange.Targets"/> of a change are the instances that
/// were passed to <see cref="RunicPathWatcher.Start"/>.
/// </remarks>
public sealed class RunicWatchTarget
{
    // Symbolic link chains longer than this are treated as a loop, as the kernel does with ELOOP.
    private const int MaxLinkHops = 40;

    private RunicWatchTarget(WatchTargetKind kind, string path, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Kind = kind;
        Path = path;
        FullPath = System.IO.Path.GetFullPath(path);
        Pattern = pattern;
    }

    /// <summary>
    /// Watches one file: it is created, deleted, replaced, renamed or written. A symbolic link at the path counts as
    /// changed when it is re-pointed, not when the file it points to changes; use <see cref="Link"/> to follow it.
    /// </summary>
    /// <param name="path">The file's path. It need not exist.</param>
    public static RunicWatchTarget File(string path) => new(WatchTargetKind.File, path, "*");

    /// <summary>
    /// Watches where a symbolic link leads: the target changes when the link, or a link it leads through, is created,
    /// removed or re-pointed so that the final path differs, as when <c>/run/current-system</c> or a Nix profile
    /// link is replaced by a rename.
    /// </summary>
    /// <remarks>
    /// Changes inside the final file or directory are not reported; Nix store paths never change. Directories in the
    /// path that are themselves symbolic links are resolved once, when the watcher evaluates the target.
    /// </remarks>
    /// <param name="path">The link's path. It need not exist.</param>
    public static RunicWatchTarget Link(string path) => new(WatchTargetKind.Link, path, "*");

    /// <summary>
    /// Watches the entries of one directory whose names match <paramref name="pattern"/>: one is added, removed or
    /// renamed. Writes to an entry do not count, and subdirectories are not searched.
    /// </summary>
    /// <param name="path">The directory's path. It need not exist.</param>
    /// <param name="pattern">
    /// A simple pattern with <c>*</c> and <c>?</c>, such as <c>system-*-link</c>, matched with the platform's
    /// file-name casing.
    /// </param>
    public static RunicWatchTarget Directory(string path, string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        return new(WatchTargetKind.Directory, path, pattern);
    }

    /// <summary>
    /// Watches the commit a Git checkout's <c>HEAD</c> resolves to: a commit, reset, checkout or switch to another
    /// branch, including in a linked worktree, whose <c>.git</c> is a file.
    /// </summary>
    /// <remarks>
    /// The watcher reads <c>HEAD</c>, the loose ref and <c>packed-refs</c> itself and starts no <c>git</c> process.
    /// Packing refs, fetching and changes to other branches do not count. With the reftable ref format, any update
    /// of the reftable stack counts. Changes to the work tree and the index are not watched.
    /// </remarks>
    /// <param name="repository">The checkout's root, which contains <c>.git</c>, or a bare repository.</param>
    public static RunicWatchTarget GitHead(string repository) => new(WatchTargetKind.GitHead, repository, "*");

    /// <summary>Gets the path this target was created with.</summary>
    public string Path { get; }

    internal WatchTargetKind Kind { get; }
    internal string FullPath { get; }
    internal string Pattern { get; }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        WatchTargetKind.Directory => $"Directory {System.IO.Path.Combine(Path, Pattern)}",
        _ => $"{Kind} {Path}",
    };

    // Reads the target's current state. Runs outside the model context and never throws for file-system state.
    internal WatchProbe Probe() => Kind switch
    {
        WatchTargetKind.File => ProbeFile(),
        WatchTargetKind.Link => ProbeLink(),
        WatchTargetKind.Directory => ProbeDirectory(),
        _ => ProbeGit(),
    };

    private WatchProbe ProbeFile()
    {
        var (directory, name) = Split(FullPath);
        return new(DescribeEntry(FullPath), [new(directory, name, false)], DirectoryOrMissing(directory), null);
    }

    private WatchProbe ProbeLink()
    {
        var specs = new List<WatchSpec>();
        var current = FullPath;
        var signature = new StringBuilder();
        for (var hop = 0; ; hop++)
        {
            var (directory, name) = Split(current);
            specs.Add(new(directory, name, false));
            string? next;
            try { next = new FileInfo(current).LinkTarget; }
            catch (Exception exception) when (IsFileSystemFailure(exception)) { next = null; }
            if (next is null || hop == MaxLinkHops)
            {
                signature.Append(current).Append('\n').Append(Exists(current) ? "present" : "missing");
                break;
            }
            current = System.IO.Path.GetFullPath(next, directory);
        }
        return new(signature.ToString(), specs, DirectoryOrMissing(specs[0].Directory), null);
    }

    private WatchProbe ProbeDirectory()
    {
        var directory = WatchPaths.Canonical(FullPath);
        var signature = new StringBuilder();
        if (System.IO.Directory.Exists(directory))
        {
            try
            {
                var names = new List<string>();
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory, Pattern, EntryOptions))
                    names.Add(System.IO.Path.GetFileName(entry));
                names.Sort(StringComparer.Ordinal);
                foreach (var name in names) signature.Append(name).Append('\n');
            }
            catch (Exception exception) when (IsFileSystemFailure(exception)) { signature.Append("unreadable"); }
        }
        else signature.Append("missing");
        return new(signature.ToString(), [new(directory, Pattern, false)], DirectoryOrMissing(directory), null);
    }

    private WatchProbe ProbeGit()
    {
        var root = WatchPaths.Canonical(FullPath);
        var dotGit = System.IO.Path.Combine(root, ".git");
        var specs = new List<WatchSpec>();
        string gitDirectory;
        if (System.IO.Directory.Exists(dotGit))
        {
            gitDirectory = dotGit;
            specs.Add(new(root, ".git", false));
        }
        else if (System.IO.File.Exists(dotGit))
        {
            // A linked worktree or submodule: ".git" is a file holding "gitdir: <path>".
            specs.Add(new(root, ".git", false));
            var line = ReadText(dotGit)?.Trim();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal))
                return new("unreadable .git file", specs, null, root);
            gitDirectory = WatchPaths.Canonical(System.IO.Path.GetFullPath(line["gitdir:".Length..].Trim(), root));
        }
        else if (System.IO.File.Exists(System.IO.Path.Combine(root, "HEAD")))
            gitDirectory = root; // A bare repository.
        else if (System.IO.Directory.Exists(root))
            // Not a repository yet: watch for .git (git init, clone) or HEAD (a bare repository) to appear.
            return new("not a repository", [new(root, ".git", false), new(root, "HEAD", false)], null, root);
        else
            return new("missing", [new(System.IO.Path.GetDirectoryName(root) ?? root, System.IO.Path.GetFileName(root), false)], root, null);

        var common = gitDirectory;
        var commonFile = ReadText(System.IO.Path.Combine(gitDirectory, "commondir"))?.Trim();
        if (!string.IsNullOrEmpty(commonFile))
            common = WatchPaths.Canonical(System.IO.Path.GetFullPath(commonFile, gitDirectory));

        specs.Add(new(gitDirectory, "HEAD", false));
        var reftable = System.IO.Path.Combine(common, "reftable");
        var head = ReadText(System.IO.Path.Combine(gitDirectory, "HEAD"))?.Trim() ?? "";
        string resolved;
        if (System.IO.Directory.Exists(reftable))
        {
            // HEAD only names refs/heads/.invalid; the reftable stack holds it. Git replaces tables.list by a rename.
            specs.Add(new(reftable, "tables.list", false));
            resolved = ReadText(System.IO.Path.Combine(reftable, "tables.list")) ?? "";
            // A linked worktree keeps its HEAD and other per-worktree refs in a stack of its own.
            var own = System.IO.Path.Combine(gitDirectory, "reftable");
            if (!WatchPaths.Comparer.Equals(gitDirectory, common) && System.IO.Directory.Exists(own))
            {
                specs.Add(new(own, "tables.list", false));
                resolved += "\n" + (ReadText(System.IO.Path.Combine(own, "tables.list")) ?? "");
            }
        }
        else
        {
            specs.Add(new(common, "packed-refs", false));
            var heads = System.IO.Path.Combine(common, "refs", "heads");
            if (System.IO.Directory.Exists(heads)) specs.Add(new(heads, "*", true));
            resolved = head.StartsWith("ref:", StringComparison.Ordinal) ? ResolveRef(common, head[4..].Trim(), 0) : "";
        }
        return new($"{head}\n{resolved}", specs, null, null);
    }

    // Git follows at most five levels of symbolic refs.
    private static string ResolveRef(string common, string name, int depth)
    {
        var loose = ReadText(System.IO.Path.Combine(common, name.Replace('/', System.IO.Path.DirectorySeparatorChar)))?.Trim();
        if (loose is not null)
        {
            if (!loose.StartsWith("ref:", StringComparison.Ordinal)) return loose;
            return depth < 5 ? $"{loose}\n{ResolveRef(common, loose[4..].Trim(), depth + 1)}" : "symbolic ref loop";
        }
        var packed = ReadText(System.IO.Path.Combine(common, "packed-refs"));
        if (packed is null) return "unborn";
        foreach (var line in packed.Split('\n'))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && line.AsSpan(space + 1).TrimEnd('\r').SequenceEqual(name)) return line[..space];
        }
        return "unborn";
    }

    internal static bool Matches(WatchSpec spec, string fullPath)
    {
        var comparison = WatchPaths.Comparison;
        if (spec.Recursive)
        {
            var prefix = System.IO.Path.EndsInDirectorySeparator(spec.Directory)
                ? spec.Directory
                : spec.Directory + System.IO.Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, comparison);
        }
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        return directory is not null
            && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(directory), System.IO.Path.TrimEndingDirectorySeparator(spec.Directory), comparison)
            && FileSystemName.MatchesSimpleExpression(spec.Pattern, System.IO.Path.GetFileName(fullPath), WatchPaths.IgnoreCase);
    }

    private static readonly EnumerationOptions EntryOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.PlatformDefault,
        MatchType = MatchType.Simple,
        RecurseSubdirectories = false,
    };

    private static (string Directory, string Name) Split(string fullPath)
    {
        var trimmed = System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        var directory = System.IO.Path.GetDirectoryName(trimmed) ?? trimmed;
        return (WatchPaths.Canonical(directory), System.IO.Path.GetFileName(trimmed));
    }

    private static string? DirectoryOrMissing(string directory) => System.IO.Directory.Exists(directory) ? null : directory;

    private static bool Exists(string path) => System.IO.File.Exists(path) || System.IO.Directory.Exists(path);

    // The file's identity as far as a stat shows it: existence, kind, link target, size and write time.
    private static string DescribeEntry(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target) return $"link\n{target}";
            if (System.IO.Directory.Exists(path)) return "directory";
            if (!info.Exists) return "missing";
            return $"file\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (IsFileSystemFailure(exception)) { return "unreadable"; }
    }

    private static string? ReadText(string path)
    {
        try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
        catch (Exception exception) when (IsFileSystemFailure(exception)) { return null; }
    }

    internal static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;
}

internal enum WatchTargetKind
{
    File,
    Link,
    Directory,
    GitHead,
}

// Events under Directory whose name matches Pattern; with Recursive, every event below Directory.
internal readonly record struct WatchSpec(string Directory, string Pattern, bool Recursive);

// A target's state: what identifies it, which directories show its changes, a directory it needs that is missing,
// and a GitHead directory that is not a repository.
internal sealed record WatchProbe(string Signature, IReadOnlyList<WatchSpec> Specs, string? MissingDirectory, string? NotRepository);

internal static class WatchPaths
{
    // Windows and macOS file systems are case-insensitive by default.
    internal static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    internal static readonly StringComparison Comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static readonly StringComparer Comparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Resolves symbolic links among the directories of an absolute path, so that a watched directory and the event
    // paths reported for it agree (macOS reports /var/folders below /private/var).
    internal static string Canonical(string fullPath) => Canonical(fullPath, 0);

    private static string Canonical(string fullPath, int depth)
    {
        var root = System.IO.Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;
        var current = root;
        var parts = fullPath[root.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = System.IO.Path.Combine(current, parts[i]);
            string? target;
            try { target = new DirectoryInfo(next).LinkTarget; }
            catch (Exception exception) when (RunicWatchTarget.IsFileSystemFailure(exception)) { target = null; }
            // The target, absolute or relative, can lead through other links: resolve all of it, as realpath does.
            if (target is not null)
                next = depth < 40 ? Canonical(System.IO.Path.GetFullPath(target, current), depth + 1) : next;
            current = next;
        }
        return current;
    }
}
