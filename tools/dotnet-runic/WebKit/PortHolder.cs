using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// What holds port 9222 when the CDP adapter cannot have it (#1065), so dev's line names it instead of only noting the
/// move: a process of the user who runs dev, from the listener's inode in Linux's TCP tables and the <c>/proc</c> entries
/// whose file descriptors point to that socket, and the project's VS Code attach configurations that name the port.
/// </summary>
internal static class PortHolder
{
    private const int CommandLineLimit = 80;

    /// <summary>
    /// What holds <paramref name="port"/> on 127.0.0.1 or <c>[::1]</c> (DEV-SEC-80): another user's uid, or else this
    /// user's process as <see cref="Describe"/> names it. A listener on either address takes the port from dev, which
    /// listens on both (<see cref="LoopbackListeners"/>).
    /// </summary>
    internal static (uint? Owner, string? Holder) OnLoopback(LoopbackSockets? sockets, int port)
    {
        if (sockets is null) return (null, null);
        IPAddress[] addresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        foreach (IPAddress address in addresses)
            if (sockets.ForeignListener(address, port) is { } owner) return (owner, null);
        foreach (IPAddress address in addresses)
            if (Describe(sockets, address, port) is { } holder) return (null, holder);
        return (null, null);
    }

    /// <summary>
    /// The process of this user that listens on <paramref name="address"/>:<paramref name="port"/>, as a phrase such as
    /// "another dotnet runic dev session (process 4242, in /home/me/OtherApp)", or null when it cannot be told: another
    /// user's process, which <c>/proc</c> does not show, or no listener in the tables.
    /// </summary>
    internal static string? Describe(LoopbackSockets? sockets, IPAddress address, int port, string procRoot = "/proc")
    {
        if (sockets is null) return null;
        IReadOnlyList<ulong> inodes = sockets.OwnListenerInodes(address, port);
        if (inodes.Count == 0) return null;
        HashSet<string> links = [.. inodes.Select(static inode => $"socket:[{inode.ToString(CultureInfo.InvariantCulture)}]")];
        IEnumerable<string> processes;
        try
        {
            processes = Directory.EnumerateDirectories(procRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (string directory in processes)
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
            if (!HoldsSocket(directory, links)) continue;
            return Name(pid, ReadArguments(directory), ReadLink(Path.Combine(directory, "cwd")));
        }
        return null;
    }

    private static bool HoldsSocket(string directory, HashSet<string> links)
    {
        try
        {
            foreach (string descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")))
            {
                if (ReadLink(descriptor) is { } target && links.Contains(target)) return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another user's process, or one that ended.
        }
        return false;
    }

    private static string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] ReadArguments(string directory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>How dev names the holder: another dev session with its directory, else the command line.</summary>
    internal static string Name(int pid, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        string process = pid.ToString(CultureInfo.InvariantCulture);
        if (IsDevCommandLine(arguments))
        {
            return workingDirectory is null
                ? $"another dotnet runic dev session (process {process})"
                : $"another dotnet runic dev session (process {process}, in {workingDirectory})";
        }
        if (arguments.Count == 0) return $"process {process}";
        string command = string.Join(' ', [Path.GetFileName(arguments[0]), .. arguments.Skip(1)]);
        if (command.Length > CommandLineLimit) command = command[..CommandLineLimit] + "…";
        return $"process {process} ({command})";
    }

    /// <summary>Whether a command line runs <c>dotnet runic dev</c> (the tool's assembly or apphost, then <c>dev</c>).</summary>
    internal static bool IsDevCommandLine(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string name = Path.GetFileName(arguments[index].Replace('\\', '/'));
            if (name.Equals("dotnet-runic", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dotnet-runic.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dotnet-runic.exe", StringComparison.OrdinalIgnoreCase))
            {
                return arguments.Skip(index + 1).Contains("dev", StringComparer.Ordinal);
            }
        }
        return false;
    }

    /// <summary>
    /// The names of the attach configurations in the VS Code <c>launch.json</c> nearest to the project (its directory,
    /// else a parent up to the repository's root) that attach a JavaScript debugger to the fixed port
    /// <paramref name="port"/>, and that file's path; empty when there is none or it does not parse.
    /// </summary>
    internal static (string? Path, IReadOnlyList<string> Names) FixedAttachConfigurations(string projectDirectory, int port)
    {
        string? launch = FindLaunchJson(projectDirectory);
        if (launch is null) return (null, []);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(launch),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("configurations", out JsonElement configurations) ||
                configurations.ValueKind != JsonValueKind.Array) return (launch, []);
            var names = new List<string>();
            foreach (JsonElement configuration in configurations.EnumerateArray())
            {
                if (configuration.ValueKind != JsonValueKind.Object) continue;
                if (String(configuration, "request") != "attach" || !IsJavaScriptDebugger(String(configuration, "type"))) continue;
                if (!configuration.TryGetProperty("port", out JsonElement value)) continue;
                bool fixedPort = value.ValueKind switch
                {
                    JsonValueKind.Number => value.TryGetInt32(out int number) && number == port,
                    JsonValueKind.String => value.GetString() == port.ToString(CultureInfo.InvariantCulture),
                    _ => false,
                };
                if (fixedPort) names.Add(String(configuration, "name") ?? "(unnamed)");
            }
            return (launch, names);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (launch, []);
        }

        static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // VS Code's JavaScript debugger (js-debug) types that attach over the DevTools Protocol.
    private static bool IsJavaScriptDebugger(string? type) =>
        type is "msedge" or "chrome" or "pwa-msedge" or "pwa-chrome";

    private static string? FindLaunchJson(string projectDirectory)
    {
        for (DirectoryInfo? directory = new(projectDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, ".vscode", "launch.json");
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return null;
        }
        return null;
    }
}
