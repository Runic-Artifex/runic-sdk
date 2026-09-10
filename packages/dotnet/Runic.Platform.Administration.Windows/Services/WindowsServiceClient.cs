using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Services;

/// <summary>Explicit local or remote Service Control Manager access using the current Windows identity.</summary>
/// <remarks>Each call owns its native handles. Configuration and status reads are not an atomic snapshot.</remarks>
public sealed partial class WindowsServiceClient
{
    private readonly string? _machineName;

    /// <summary>Creates a client; null targets the local computer. Remote access uses Windows SCM RPC.</summary>
    public WindowsServiceClient(string? machineName = null)
    {
        NativeError.Windows();
        if (machineName is not null) NativeError.Text(machineName, nameof(machineName));
        _machineName = machineName;
    }

    private ServiceHandle Manager(uint access)
    {
        var pointer = ServiceNative.OpenSCManager(_machineName, null, access);
        if (pointer == 0) throw NativeError.Win32("Open Service Control Manager", Marshal.GetLastPInvokeError());
        return new(pointer);
    }

    private ServiceHandle? Open(string name, uint access, bool allowMissing = false)
    {
        NativeError.Text(name, nameof(name));
        using var manager = Manager(1);
        var pointer = ServiceNative.OpenService(manager, name, access);
        if (pointer != 0) return new(pointer);
        var error = Marshal.GetLastPInvokeError();
        if (allowMissing && error == 1060) return null;
        throw NativeError.Win32("Open service", error);
    }

    /// <summary>Enumerates Win32 services, including inactive services, without reading privileged configuration.</summary>
    public unsafe ImmutableArray<ServiceSummary> Enumerate()
    {
        using var manager = Manager(4);
        var rows = ImmutableArray.CreateBuilder<ServiceSummary>();
        var buffer = new byte[256 * 1024];
        uint resume = 0;
        fixed (byte* data = buffer)
        {
            int success;
            do
            {
                success = ServiceNative.Enumerate(manager, 0, 0x30, 3, data, (uint)buffer.Length, out _, out var count, ref resume, null);
                var error = Marshal.GetLastPInvokeError();
                if (success == 0 && error != 234) throw NativeError.Win32("Enumerate services", error);
                var nativeRows = (ServiceNative.EnumRow*)data;
                for (var i = 0u; i < count; i++)
                    rows.Add(new(ServiceNative.Text(nativeRows[i].Name), ServiceNative.Text(nativeRows[i].DisplayName), nativeRows[i].Status.Snapshot()));
                if (success == 0 && count == 0) throw NativeError.Win32("Enumerate services", 13);
            } while (success == 0);
        }
        return rows.ToImmutable();
    }

    /// <summary>Reads status only; null means the service does not exist.</summary>
    public ServiceStatus? FindStatus(string name)
    {
        using var service = Open(name, 4, true);
        return service is null ? null : ReadStatus(service);
    }

    /// <summary>Reads service configuration and status; denied configuration access remains an error.</summary>
    public unsafe ServiceSnapshot? Find(string name)
    {
        using var service = Open(name, 1 | 4, true);
        if (service is null) return null;
        var configBuffer = ReadConfig(service, 0);
        string display, binary, account, group;
        uint type, error, start;
        ImmutableArray<string> dependencies;
        fixed (byte* data = configBuffer)
        {
            var config = (ServiceNative.Config*)data;
            display = ServiceNative.Text(config->DisplayName);
            binary = ServiceNative.Text(config->BinaryPath);
            account = ServiceNative.Text(config->Account);
            group = ServiceNative.Text(config->LoadOrderGroup);
            type = config->Type; error = config->Error; start = config->Start;
            dependencies = ReadMultiString(config->Dependencies);
        }
        var descriptionBuffer = ReadConfig(service, 1);
        string description;
        fixed (byte* data = descriptionBuffer) description = ServiceNative.Text(*(char**)data);
        var delayedBuffer = ReadConfig(service, 3);
        bool delayed;
        fixed (byte* data = delayedBuffer) delayed = *(int*)data != 0;
        var flagsBuffer = ReadConfig(service, 4);
        bool nonCrash;
        fixed (byte* data = flagsBuffer) nonCrash = *(int*)data != 0;
        var failureBuffer = ReadConfig(service, 2);
        ServiceFailurePolicy policy;
        fixed (byte* data = failureBuffer)
        {
            var failure = (ServiceNative.FailureActions*)data;
            var actions = ImmutableArray.CreateBuilder<ServiceFailureAction>();
            for (var i = 0u; i < failure->Count; i++)
                actions.Add(new((ServiceFailureActionKind)failure->Actions[i].Kind, TimeSpan.FromMilliseconds(failure->Actions[i].Delay)));
            policy = new(failure->Reset == uint.MaxValue ? null : TimeSpan.FromSeconds(failure->Reset),
                actions.ToImmutable(), ServiceNative.Text(failure->RebootMessage), ServiceNative.Text(failure->Command), nonCrash);
        }
        return new(name, display, binary, account, dependencies, (ServiceStartMode)start, type, error, group, description, delayed, policy, ReadStatus(service));
    }

    private static unsafe ImmutableArray<string> ReadMultiString(char* pointer)
    {
        var values = ImmutableArray.CreateBuilder<string>();
        if (pointer != null)
            while (*pointer != '\0')
            {
                var value = new string(pointer);
                values.Add(value);
                pointer += value.Length + 1;
            }
        return values.ToImmutable();
    }

    private static unsafe byte[] ReadConfig(ServiceHandle service, uint level)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Pinned arrays are required: native buffers contain pointers into themselves.
            var buffer = GC.AllocateArray<byte>(checked((int)size), pinned: true);
            fixed (byte* data = buffer)
            {
                uint needed;
                var success = level == 0
                    ? ServiceNative.QueryServiceConfig(service, data, size, out needed)
                    : ServiceNative.QueryServiceConfig2(service, level, data, size, out needed);
                if (success != 0) return buffer;
                var error = Marshal.GetLastPInvokeError();
                if (error != 122) throw NativeError.Win32("Read service configuration", error);
                if (needed == 0 || needed > 1024 * 1024) throw NativeError.Win32("Read service configuration", 13);
                size = needed;
            }
        }
        throw NativeError.Win32("Read changing service configuration", 183);
    }

    private static unsafe ServiceStatus ReadStatus(ServiceHandle service)
    {
        ServiceNative.Status status;
        NativeError.CheckWin32(ServiceNative.QueryServiceStatusEx(service, 0, &status, (uint)sizeof(ServiceNative.Status), out _), "Read service status");
        return status.Snapshot();
    }

    /// <summary>Starts a service. Already running is reported as a native conflict.</summary>
    public void Start(string name)
    {
        using var service = Open(name, 0x10)!;
        NativeError.CheckWin32(ServiceNative.StartService(service, 0, 0), "Start service");
    }

    /// <summary>Requests a stop; use WaitForStateAsync to observe completion.</summary>
    public void Stop(string name) => Control(name, 1, 0x20);
    /// <summary>Requests a pause.</summary>
    public void Pause(string name) => Control(name, 2, 0x40);
    /// <summary>Requests continuation of a paused service.</summary>
    public void Continue(string name) => Control(name, 3, 0x40);

    private unsafe void Control(string name, uint control, uint access)
    {
        using var service = Open(name, access)!;
        ServiceNative.Status status;
        NativeError.CheckWin32(ServiceNative.ControlService(service, control, &status), "Control service");
    }

    /// <summary>Marks an existing service for deletion. Returns false only if it was absent; deletion may be deferred by Windows.</summary>
    public bool Delete(string name)
    {
        using var service = Open(name, 0x10000, true);
        if (service is null) return false;
        NativeError.CheckWin32(ServiceNative.DeleteService(service), "Delete service");
        return true;
    }

    /// <summary>Waits for an observed state; timeout throws TimeoutException. Cancellation does not stop or undo service operations.</summary>
    public async Task<ServiceStatus> WaitForStateAsync(string name, ServiceState state, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        NativeError.Text(name, nameof(name));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = FindStatus(name) ?? throw NativeError.Win32("Wait for service state", 1060);
            if (current.State == state) return current;
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The service did not reach the requested state before the timeout.");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }
}
