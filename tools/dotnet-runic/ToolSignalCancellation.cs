using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Runic.Application.Tool;

/// <summary>
/// Replaces Runic Command Line's termination handling (<c>CommandApp.HandleCancelKeyPress</c>)
/// for the tool. The first Ctrl+C (SIGINT), SIGTERM or SIGQUIT cancels the command; a
/// repeated one exits at once with 128 plus the signal number, as Command Line does,
/// unless an operation holds <see cref="ProtectShutdown"/>. <c>dotnet runic dev</c> holds
/// it so that a second signal cannot skip stopping the frontend and application process
/// trees.
/// </summary>
internal sealed class ToolSignalCancellation : IDisposable
{
    private static int s_protectedOperations;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<PosixSignalRegistration> _registrations = new(3);
    private readonly Action<int> _exit;
    private int _received;

    private ToolSignalCancellation(Action<int> exit) => _exit = exit;

    internal CancellationToken Token => _cancellation.Token;

    /// <summary>Registers process signal handlers; dispose to remove them.</summary>
    internal static ToolSignalCancellation Register()
    {
        var signals = new ToolSignalCancellation(Environment.Exit);
        foreach (PosixSignal signal in (PosixSignal[])[PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM])
        {
            try { signals._registrations.Add(PosixSignalRegistration.Create(signal, context => context.Cancel = signals.Handle(context.Signal))); }
            catch (PlatformNotSupportedException) { }
        }
        return signals;
    }

    /// <summary>Creates an instance without process registrations for tests.</summary>
    internal static ToolSignalCancellation CreateForTest(Action<int> exit) => new(exit);

    /// <summary>While the returned scope is alive, repeated signals do not force an exit.</summary>
    internal static IDisposable ProtectShutdown()
    {
        Interlocked.Increment(ref s_protectedOperations);
        return new ProtectionScope();
    }

    /// <summary>Handles one signal and returns whether the default termination is cancelled.</summary>
    internal bool Handle(PosixSignal signal)
    {
        if (Interlocked.Increment(ref _received) > 1 && Volatile.Read(ref s_protectedOperations) == 0)
        {
            _exit(ForcedExitCode(signal));
            return true;
        }
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    public void Dispose()
    {
        foreach (PosixSignalRegistration registration in _registrations) registration.Dispose();
        _cancellation.Dispose();
    }

    private static int ForcedExitCode(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGINT => 130,
        PosixSignal.SIGQUIT => 131,
        _ => 143,
    };

    private sealed class ProtectionScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref s_protectedOperations);
        }
    }
}
