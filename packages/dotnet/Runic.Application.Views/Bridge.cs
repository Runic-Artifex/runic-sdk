namespace Runic.Application.Views;

// The application refers to this stable entry point. Generated adapters
// register themselves when the final assembly loads, so ordinary C# source
// does not depend on a generated type during a clean IDE design-time build.
public static class Bridge
{
    private static readonly Dictionary<Type, Func<IBridgeTransport, object, IDisposable>> Factories = new();
    private static readonly object Gate = new();

    public static void Register<T>(Func<IBridgeTransport, T, IDisposable> factory) where T : class
    {
        lock (Gate)
        {
            if (!Factories.TryAdd(typeof(T), (transport, vm) => factory(transport, (T)vm)))
                throw new InvalidOperationException($"A Bridge for {typeof(T).FullName} is already registered.");
        }
    }

    public static IDisposable Attach<T>(IBridgeTransport transport, T viewModel) where T : class
    {
        Func<IBridgeTransport, object, IDisposable> factory;
        lock (Gate)
        {
            if (!Factories.TryGetValue(typeof(T), out factory!))
                throw new InvalidOperationException($"No generated Bridge is registered for {typeof(T).FullName}.");
        }
        return factory(transport, viewModel);
    }
}
