using System;
using System.IO;
using System.Threading.Tasks;
using Runic.Application.Bridge;

namespace Runic.Application.Bridge.Tests;

internal static class PresentationLifetimeTests
{
    internal static async Task RunAsync()
    {
        var first = new Lifetime();
        var second = new Lifetime();
        var scope = new Scope();
        var session = new ApplicationBridgeSession(new DirectSnapshotDispatcher(), ownedScope: scope,
            presentationLifetimes: [first, second, first]);
        var stopping = session.DisposeAsync().AsTask();
        var concurrent = session.DisposeAsync().AsTask();
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            if (stopping.IsCompleted || concurrent.IsCompleted || scope.Disposals != 0)
                throw new InvalidOperationException("Scope disposed or a concurrent shutdown returned before both services drained.");
        }
        finally { first.Release.SetResult(); second.Release.SetResult(); }
        await Task.WhenAll(stopping, concurrent).WaitAsync(TimeSpan.FromSeconds(3));
        if (first.Stops != 1 || second.Stops != 1 || scope.Disposals != 1)
            throw new InvalidOperationException("Presentation stop or scope disposal was duplicated.");

        var failed = new Lifetime(); failed.Release.SetException(new IOException("Native release failure."));
        var healthy = new Lifetime(); healthy.Release.SetResult();
        var failedScope = new Scope();
        var failedSession = new ApplicationBridgeSession(new DirectSnapshotDispatcher(), ownedScope: failedScope,
            presentationLifetimes: [failed, healthy]);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { await failedSession.DisposeAsync(); throw new InvalidOperationException("Release failure was hidden."); }
            catch (IOException) { }
        }
        if (healthy.Stops != 1 || failedScope.Disposals != 1) throw new InvalidOperationException("One failed service skipped other cleanup.");
    }
    private sealed class Lifetime : IApplicationPresentationLifetime
    {
        internal int Stops;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StopAsync() { Stops++; Entered.TrySetResult(); return new(Release.Task); }
    }
    private sealed class Scope : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
