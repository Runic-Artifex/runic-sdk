/// <summary>
/// Generated helpers that an assembly's Bridges share, one file each beside the Bridges (runic-sdk#1109): the
/// descriptor table reader and the command runners. A file is written when a Bridge uses it and removed
/// otherwise, so an application without, say, CommunityToolkit commands never compiles code that names its types.
/// </summary>
internal static class BridgeSupportFiles
{
    /// <summary>The descriptor table reader (<see cref="BridgeTableSupport"/>), which every Bridge uses.</summary>
    internal const string Tables = BridgeTableSupport.FileName;

    /// <summary>CommunityToolkit.Mvvm async commands (<c>RunicToolkitCommands</c>).</summary>
    internal const string Toolkit = "RunicShared.Toolkit.Bridge.g.cs";

    /// <summary>Commands whose runtime helper returns a task to await and encode (<c>RunicBridgeCommands</c>).</summary>
    internal const string Commands = "RunicShared.Commands.Bridge.g.cs";

    /// <summary>Every support file, which the stale-output cleanup keeps for <see cref="WriteAll"/> to decide on.</summary>
    internal static readonly string[] All = [Commands, Tables, Toolkit];

    private static readonly SortedDictionary<string, string> Used = new(StringComparer.Ordinal);

    /// <summary>Writes the file with this content when the generated Bridges are written.</summary>
    internal static void Use(string fileName, string content)
    {
        if (!All.Contains(fileName, StringComparer.Ordinal)) throw new ArgumentException($"{fileName} is not a Bridge support file.", nameof(fileName));
        Used[fileName] = content;
    }

    internal static void WriteAll(string directory)
    {
        foreach (var fileName in All)
        {
            var path = Path.Combine(directory, fileName);
            if (Used.TryGetValue(fileName, out var content)) GeneratedFile.WriteIfChanged(path, content);
            else if (File.Exists(path) && GeneratedOutput.IsGenerated(path)) File.Delete(path);
        }
        Used.Clear();
    }
}
