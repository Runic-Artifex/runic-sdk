using Windows.Win32;
using Windows.Win32.System.Com;
using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

internal static unsafe class ComApartment
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
                if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
                cancellationToken.ThrowIfCancellationRequested();
                NativeError.Check(PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value, "Initialize COM apartment");
                initialized = true;
                cancellationToken.ThrowIfCancellationRequested();
                // Once execution begins, return its real outcome; cancellation cannot undo native effects.
                completion.SetResult(operation());
            }
            catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.SetException(error); }
            finally { if (initialized && OperatingSystem.IsWindowsVersionAtLeast(6, 1)) PInvoke.CoUninitialize(); }
        }) { IsBackground = true, Name = "Runic Windows administration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

}
