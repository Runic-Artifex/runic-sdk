using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Networks;

/// <summary>Windows network category.</summary>
public enum WindowsNetworkCategory
{
    /// <summary>Public network.</summary>
    Public,
    /// <summary>Private network.</summary>
    Private,
    /// <summary>Domain-authenticated network.</summary>
    DomainAuthenticated
}
/// <summary>Network List Manager identity and connectivity; native flags retain IPv4/IPv6 distinctions.</summary>
public sealed record WindowsNetworkSnapshot(Guid Id, string Name, string Description, WindowsNetworkCategory Category,
    int NativeDomainType, int NativeConnectivity, bool Connected, bool InternetConnected);

/// <summary>Read-only local Network List Manager access.</summary>
public interface IWindowsNetworkClient
{
    /// <summary>Enumerates connected and disconnected network identities.</summary>
    Task<ImmutableArray<WindowsNetworkSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Static native Network List Manager bindings, without the legacy interop assembly.</summary>
public sealed class WindowsNetworkClient : IWindowsNetworkClient
{
    /// <summary>Creates a local Windows x64 network client.</summary>
    public WindowsNetworkClient() => Automation.RequireX64();

    /// <inheritdoc/>
    public Task<ImmutableArray<WindowsNetworkSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        ComApartment.RunAsync(Enumerate, cancellationToken);

    private static unsafe ImmutableArray<WindowsNetworkSnapshot> Enumerate()
    {
        using var manager = ComObject.Create(new("dcb00c01-570f-4a9b-8d69-199fdba5723b"), new("dcb00000-570f-4a9b-8d69-199fdba5723b"));
        nint pointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)manager.Slot(7))(manager.Pointer, 3, &pointer), "Enumerate Windows networks");
        using var iterator = ComObject.Own(pointer);
        var result = ImmutableArray.CreateBuilder<WindowsNetworkSnapshot>();
        while (true)
        {
            nint networkPointer = 0;
            uint fetched = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)iterator.Slot(8))(iterator.Pointer, 1, &networkPointer, &fetched);
            NativeError.Check(status, "Read Windows network");
            if (fetched == 0) break;
            using var network = ComObject.Own(networkPointer);
            Guid id;
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)network.Slot(11))(network.Pointer, &id), "Read network ID");
            result.Add(new(id, Automation.GetString(network, 7, "Read network name"),
                Automation.GetString(network, 9, "Read network description"),
                (WindowsNetworkCategory)Automation.GetInt32(network, 18, "Read network category"),
                Automation.GetInt32(network, 12, "Read network domain type"),
                Automation.GetInt32(network, 17, "Read network connectivity"),
                Automation.GetBoolean(network, 16, "Read network connection state"),
                Automation.GetBoolean(network, 15, "Read network Internet state")));
        }
        return result.ToImmutable();
    }
}
