using System.Globalization;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>Adapts a Desktop surface's removable capabilities to the Views transport.</summary>
public sealed class DesktopBridgeTransport(DesktopSurface surface) : IBridgeTransport
{
    private readonly DesktopSurface _surface = surface ?? throw new ArgumentNullException(nameof(surface));

    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _surface.RegisterCapability(name, (invocation, _) =>
            ValueTask.FromResult(PresentationResult.FromString(handler(new DesktopBridgeArguments(invocation)))));
    }

    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _surface.RegisterCapability(name, async (invocation, token) =>
            PresentationResult.FromString(await handler(new DesktopBridgeArguments(invocation), token)
                .ConfigureAwait(false)));
    }

    public void Publish(string name, string stateJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stateJson);
        var callback = JsonSerializer.Serialize($"__{name}Changed");
        _surface.RunJavaScriptAsync($"globalThis[{callback}]?.({stateJson});")
            .GetAwaiter().GetResult();
    }

    private sealed class DesktopBridgeArguments(PresentationInvocation invocation) : IBridgeArguments
    {
        private readonly string _connectionKey = invocation.Session.Id.ToString(CultureInfo.InvariantCulture);
        private int _nextArgument;

        public string ClientKey => _connectionKey;
        public string ConnectionKey => _connectionKey;
        public long GetInt64() => invocation.GetInt64(_nextArgument++);
        public bool GetBoolean() => invocation.GetBoolean(_nextArgument++);
        public string GetString() => invocation.GetString(_nextArgument++);
    }
}
