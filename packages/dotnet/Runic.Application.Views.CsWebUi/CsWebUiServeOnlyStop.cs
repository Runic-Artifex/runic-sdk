using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

// sdk#1190: the end of serving in serve-only mode (RUNIC_APPLICATION_SERVE_ONLY=1), which is the close of every serving
// Window: a line read from standard input. A browser tab that closes, the last one included, is not a close, since the
// server outlives its clients and a reload connects again. The Windows with a close drain (RunicCloseDrain) drain first,
// with their pages still connected; then WebUiApplication.Exit ends WebUI's wait, and with it each Window's
// WaitForCloseAsync. Lines read while they drain are ignored.
internal static class CsWebUiServeOnlyStop
{
    private static readonly object Gate = new();
    private static readonly List<Func<Task>> Drains = [];
    private static bool s_stopping;

    // A serving Window's drain, until the result is disposed.
    internal static IDisposable Register(Func<Task> drain)
    {
        lock (Gate) Drains.Add(drain);
        return new Registration(drain);
    }

    // On the input thread: drains, then WebUI exits.
    internal static void Request()
    {
        Func<Task>[] drains;
        lock (Gate)
        {
            if (s_stopping) return;
            s_stopping = true;
            drains = [.. Drains];
        }
        try
        {
            // A drain reports its failures to its Window's close; here only its end counts. A loop, not LINQ, keeps
            // System.Linq's generic instantiations out of a NativeAOT build.
            var running = new Task[drains.Length];
            for (var index = 0; index < drains.Length; index++) running[index] = Task.Run(drains[index]);
            foreach (var drain in running)
            {
                try { drain.Wait(); }
                catch (AggregateException) { }
            }
        }
        finally
        {
            lock (Gate) s_stopping = false;
            WebUiApplication.Exit();
        }
    }

    private sealed class Registration(Func<Task> drain) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (Gate) Drains.Remove(drain);
        }
    }
}
