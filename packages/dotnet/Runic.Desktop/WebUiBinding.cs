namespace Runic.Desktop;

/// <summary>Represents one JavaScript binding registered with a managed window.</summary>
public sealed class WebUiBinding : IDisposable
{
    private WebUiWindow? _window;
    private readonly long _registrationId;

    internal WebUiBinding(WebUiWindow window, long registrationId, string element)
    {
        _window = window;
        _registrationId = registrationId;
        Element = element;
    }

    /// <summary>Gets the bound JavaScript function name.</summary>
    public string Element { get; }

    /// <summary>Stops dispatching this binding to managed code.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _window, null)?.RemoveBinding(Element, _registrationId);
        GC.SuppressFinalize(this);
    }
}
