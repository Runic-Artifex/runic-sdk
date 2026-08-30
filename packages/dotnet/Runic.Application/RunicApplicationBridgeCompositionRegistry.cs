using System;

namespace Runic.Application;

/// <summary>Provides the bridge session factory emitted with the generated application composition.</summary>
public static class RunicApplicationBridgeCompositionRegistry
{
    private static Func<object>? _createSession;

    /// <summary>Registers the application's generated bridge-session factory.</summary>
    public static void Register(Func<object> createSession)
    {
        ArgumentNullException.ThrowIfNull(createSession);
        if (System.Threading.Interlocked.CompareExchange(ref _createSession, createSession, null) is not null)
        {
            throw new InvalidOperationException("The application bridge composition has already been registered.");
        }
    }

    /// <summary>Creates the generated bridge session, if the application declared one.</summary>
    public static object? CreateSession() => _createSession?.Invoke();
}
