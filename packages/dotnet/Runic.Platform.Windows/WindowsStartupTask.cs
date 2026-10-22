using System.Runtime.Versioning;
using static Runic.Platform.Windows.WindowsNotificationInterop;

namespace Runic.Platform.Windows;

// Login startup of a process installed from an MSIX package: the package's desktop:StartupTask with TaskId
// LoginStartup, through Windows.ApplicationModel.StartupTask in raw WinRT ABI calls. Windows starts the package's
// application with the manifest's arguments, so the request's arguments do not apply; Task Manager's Startup apps page
// shows the task, and a task the user turned off there stays off. A package without the task reports unavailable.
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class WindowsStartupTask(string taskId) : IDesktopLoginStartup
{
    internal const string DefaultTaskId = "LoginStartup";
    // StartupTaskState.
    internal const int Disabled = 0, DisabledByUser = 1, Enabled = 2, DisabledByPolicy = 3, EnabledByPolicy = 4;
    private const string StartupTaskStatics = "ee5b60bd-a148-41a7-b26e-e8b88a1e62f8", StartupTaskId = "f75c23c8-b5f2-4f6c-88dd-36cb1d599d17",
        AsyncInfo = "00000036-0000-0000-c000-000000000046";
    // AsyncStatus.
    private const int Started = 0, Completed = 1;

    public async ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await UseAsync<bool>(static (task, _) => ValueTask.FromResult(Abi.State(task) is Enabled or EnabledByPolicy), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.IsDefault || request.Arguments.Length > 64 || request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Supply at most 64 arguments without NUL characters.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var result = await UseAsync(static async (task, token) =>
        {
            // A task the user or a policy turned off stays off: RequestEnableAsync shows nothing and reports it.
            nint operation = Get(task, 6); // RequestEnableAsync
            try
            {
                await CompleteAsync(operation, token).ConfigureAwait(false);
                return Abi.EnumResult(operation);
            }
            finally { Release(operation); }
        }, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            PlatformResult<int>.Success { Value: Enabled or EnabledByPolicy } => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
            PlatformResult<int>.Success { Value: var state } => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied,
                new PlatformDiagnostic("StartupTaskState", state, state == DisabledByUser
                    ? "The user turned the startup task off in Settings or Task Manager; only the user turns it on."
                    : "A policy keeps the startup task off.")),
            PlatformResult<int>.Unavailable unavailable => new PlatformResult<PlatformUnit>.Unavailable(unavailable.Reason),
            PlatformResult<int>.Failed failed => new PlatformResult<PlatformUnit>.Failed(failed.Code, failed.Diagnostic),
            _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown),
        };
    }

    public async ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await UseAsync(static (task, _) =>
        {
            // A task the user or a policy controls cannot be changed; disabling it leaves it as it is.
            if (Abi.State(task) is Enabled) Abi.Disable(task);
            return ValueTask.FromResult(new PlatformUnit());
        }, cancellationToken).ConfigureAwait(false);
    }

    // The package's startup task, for one request on a thread of the multithreaded apartment.
    private async ValueTask<PlatformResult<T>> UseAsync<T>(Func<nint, CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        await Task.Yield();
        nint task;
        try { task = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidCastException or ArgumentException
            or NotImplementedException or EntryPointNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // The manifest declares no such task (ERROR_NOT_FOUND), or the process has no package.
            return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        try { return new PlatformResult<T>.Success(await action(task, cancellationToken).ConfigureAwait(false)); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            return new PlatformResult<T>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromHResult(error.HResult));
        }
        finally { Release(task); }
    }

    private static async ValueTask<nint> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        nint operation = Start(taskId);
        try
        {
            await CompleteAsync(operation, cancellationToken).ConfigureAwait(false);
            nint task = Get(operation, 8); // IAsyncOperation<StartupTask>.GetResults
            try { return Query(task, StartupTaskId); }
            finally { Release(task); }
        }
        finally { Release(operation); }
    }

    private static nint Start(string taskId)
    {
        InitializeRuntime();
        nint statics = Factory("Windows.ApplicationModel.StartupTask", StartupTaskStatics);
        try
        {
            using var id = new HString(taskId);
            return Get(statics, 7, id.Handle); // GetAsync(taskId)
        }
        finally { Release(statics); }
    }

    // Waits for a WinRT asynchronous operation by polling IAsyncInfo, which needs no parameterized completion delegate.
    private static async ValueTask CompleteAsync(nint operation, CancellationToken cancellationToken)
    {
        nint info = Query(operation, AsyncInfo);
        try
        {
            for (int delay = 1; ; delay = Math.Min(delay * 2, 50))
            {
                int status = Abi.Status(info);
                if (status == Completed) return;
                if (status != Started)
                {
                    int error = Abi.ErrorCode(info);
                    System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(error < 0 ? error : unchecked((int)0x80004004)); // E_ABORT when cancelled
                }
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Release(info); }
    }
}

// The ABI calls that read a value through a pointer, outside the asynchronous methods.
[SupportedOSPlatform("windows10.0.17134")]
file static unsafe class Abi
{
    internal static int Status(nint info)
    {
        int status = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status)); // get_Status
        return status;
    }

    internal static int ErrorCode(nint info)
    {
        int error = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error)); // get_ErrorCode
        return error;
    }

    internal static int EnumResult(nint operation)
    {
        int value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(operation, 8))(operation, &value)); // GetResults
        return value;
    }

    internal static int State(nint task)
    {
        int state = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(task, 8))(task, &state)); // get_State
        return state;
    }

    internal static void Disable(nint task) => Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(task, 7))(task)); // Disable
}
