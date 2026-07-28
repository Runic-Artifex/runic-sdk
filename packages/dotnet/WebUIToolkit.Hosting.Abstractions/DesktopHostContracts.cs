using System;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Desktop;

namespace WebUIToolkit.Hosting;

/// <summary>Contains one bounded event emitted by the browser desktop bridge.</summary>
public sealed class BrowserDesktopEventArgs : EventArgs
{
    /// <summary>Creates one immutable browser event.</summary>
    public BrowserDesktopEventArgs(string name, string id, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(payloadJson);
        Name = name;
        Id = id;
        PayloadJson = payloadJson;
    }

    /// <summary>Gets the stable event name.</summary>
    public string Name { get; }
    /// <summary>Gets the registration or event identity.</summary>
    public string Id { get; }
    /// <summary>Gets the bounded JSON payload.</summary>
    public string PayloadJson { get; }
}

/// <summary>
/// Optional host-adapter surface used to implement frontend-neutral desktop services.
/// Application and ViewModel code should consume <c>WebUIToolkit.Desktop</c> services.
/// </summary>
public interface IBrowserWindowDesktopAdapter
{
    /// <summary>Occurs when the installed browser bridge publishes a bounded event.</summary>
    event EventHandler<BrowserDesktopEventArgs>? DesktopEventReceived;

    /// <summary>Gets the complete capability report for this native window.</summary>
    DesktopCapabilityReport Capabilities { get; }

    /// <summary>Brings the native window to the foreground.</summary>
    ValueTask FocusWindowAsync(CancellationToken cancellationToken);

    /// <summary>Focuses one semantic browser-document element.</summary>
    ValueTask FocusElementAsync(string elementId, CancellationToken cancellationToken);

    /// <summary>Changes the outer native-window size.</summary>
    ValueTask SetSizeAsync(DesktopSize size, CancellationToken cancellationToken);

    /// <summary>Changes the native-window position.</summary>
    ValueTask SetPositionAsync(DesktopPosition position, CancellationToken cancellationToken);

    /// <summary>Centers the native window.</summary>
    ValueTask CenterAsync(CancellationToken cancellationToken);

    /// <summary>Changes the native-window state.</summary>
    ValueTask SetStateAsync(DesktopWindowState state, CancellationToken cancellationToken);

    /// <summary>Invokes one bounded browser capability operation and returns its JSON value.</summary>
    ValueTask<string> InvokeBrowserAsync(
        string operation,
        string payloadJson,
        CancellationToken cancellationToken);
}
