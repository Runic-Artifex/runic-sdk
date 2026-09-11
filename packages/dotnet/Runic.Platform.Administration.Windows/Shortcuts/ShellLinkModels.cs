namespace Runic.Platform.Administration.Windows.Shortcuts;

/// <summary>Shell link window presentation values supported by IShellLink.</summary>
public enum ShellLinkShowState
{
    /// <summary>Normal window.</summary>
    Normal = 1,
    /// <summary>Maximized window.</summary>
    Maximized = 3,
    /// <summary>Minimized window without activation.</summary>
    Minimized = 7
}

/// <summary>Icon location and zero-based index (or negative resource identifier).</summary>
public sealed record ShellLinkIcon(string Path, int Index = 0);

/// <summary>The filesystem fields read from a shell link without resolving its target.</summary>
public sealed record ShellLinkSnapshot(string TargetPath, string Arguments, string WorkingDirectory,
    string Description, ShellLinkIcon Icon, int ShowState, ushort Hotkey);

/// <summary>A new shell link. TargetPath is required; no parent directory is created implicitly.</summary>
public sealed record ShellLinkSpecification(string TargetPath)
{
    /// <summary>Command-line arguments passed verbatim.</summary>
    public string Arguments { get; init; } = "";
    /// <summary>Working directory, or empty for the shell default.</summary>
    public string WorkingDirectory { get; init; } = "";
    /// <summary>Descriptive text.</summary>
    public string Description { get; init; } = "";
    /// <summary>Icon location; an empty path requests the target's default icon.</summary>
    public ShellLinkIcon Icon { get; init; } = new("");
    /// <summary>Window presentation.</summary>
    public ShellLinkShowState ShowState { get; init; } = ShellLinkShowState.Normal;
    /// <summary>Native virtual-key/modifier word, or zero for no hotkey.</summary>
    public ushort Hotkey { get; init; }
}

/// <summary>Selected shell-link edits. Null leaves a field unchanged; empty clears a string field.</summary>
public sealed record ShellLinkUpdate
{
    /// <summary>Replacement target, or null to preserve it.</summary>
    public string? TargetPath { get; init; }
    /// <summary>Replacement arguments, or null to preserve them.</summary>
    public string? Arguments { get; init; }
    /// <summary>Replacement working directory, or null to preserve it.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Replacement description, or null to preserve it.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement icon, or null to preserve it.</summary>
    public ShellLinkIcon? Icon { get; init; }
    /// <summary>Replacement show state, or null to preserve it.</summary>
    public ShellLinkShowState? ShowState { get; init; }
    /// <summary>Replacement hotkey, or null to preserve it.</summary>
    public ushort? Hotkey { get; init; }
}
