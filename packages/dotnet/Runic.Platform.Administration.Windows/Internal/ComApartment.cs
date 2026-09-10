using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal static partial class ComApartment
{
    internal static Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        NativeError.Windows();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeError.Check(CoInitializeEx(0, 2), "Initialize COM apartment");
                initialized = true;
                cancellationToken.ThrowIfCancellationRequested();
                // Once execution begins, return its real outcome; cancellation cannot undo native effects.
                completion.SetResult(operation());
            }
            catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.SetException(error); }
            finally { if (initialized) CoUninitialize(); }
        }) { IsBackground = true, Name = "Runic Windows administration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
