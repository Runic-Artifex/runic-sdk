using System.Collections.Immutable;
namespace Runic.Platform.Administration.Windows.Processes;

/// <summary>Read-only process inspection, replaceable by application-owned fakes.</summary>
public interface IWindowsProcessClient
{
    /// <summary>Reads all processes in one local snapshot.</summary>
    ImmutableArray<ProcessSnapshot> Enumerate();
    /// <summary>Finds a process in a new snapshot.</summary>
    ProcessSnapshot? Find(uint processId);
    /// <summary>Reads immediate children from a new snapshot.</summary>
    ImmutableArray<ProcessSnapshot> GetChildren(uint parentProcessId);
}
