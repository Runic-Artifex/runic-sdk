namespace Runic.Platform.Runtime;

/// <summary>A statically selected file-dialog provider.</summary>
public interface IPickerBackend : IFileDialogs
{
    /// <summary>Whether the provider can currently show dialogs.</summary>
    bool IsAvailable { get; }
}

/// <summary>A verified presentation owner. Native handles stay in C# and are usable only during dispatch.</summary>
public interface INativePickerOwner
{
    /// <summary>The verified presentation generation, shared with its lifetime.</summary>
    Guid Generation { get; }
    /// <summary>Whether the provider can currently show dialogs.</summary>
    bool IsAvailable { get; }
    /// <summary>Revalidates owner identity and dispatches on its native thread. The handle cannot escape the callback.</summary>
    ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default);
}
