using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.NetworkListManager;
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var manager = ComObject.Create(new("dcb00c01-570f-4a9b-8d69-199fdba5723b"), new("dcb00000-570f-4a9b-8d69-199fdba5723b"));
        nint pointer = 0;
        using var iterator = ComObject.FromResult(((INetworkListManager*)manager.Pointer)->GetNetworks(NLM_ENUM_NETWORK.NLM_ENUM_NETWORK_ALL, (IEnumNetworks**)&pointer).Value, pointer, "Enumerate Windows networks");
        var result = ImmutableArray.CreateBuilder<WindowsNetworkSnapshot>();
        while (true)
        {
            nint networkPointer = 0;
            uint fetched = 0;
            var status = ((IEnumNetworks*)iterator.Pointer)->Next(1, (INetwork**)&networkPointer, &fetched).Value;
            NativeError.Check(status, "Read Windows network");
            if (fetched == 0) break;
            using var network = ComObject.Own(networkPointer);
            var native = (INetwork*)network.Pointer;
            Guid id;
            NativeError.Check(native->GetNetworkId(&id).Value, "Read network ID");
            NLM_NETWORK_CATEGORY category;
            NativeError.Check(native->GetCategory(&category).Value, "Read network category");
            NLM_DOMAIN_TYPE domain;
            NativeError.Check(native->GetDomainType(&domain).Value, "Read network domain type");
            NLM_CONNECTIVITY connectivity;
            NativeError.Check(native->GetConnectivity(&connectivity).Value, "Read network connectivity");
            VARIANT_BOOL connected, internet;
            NativeError.Check(native->get_IsConnected(&connected).Value, "Read network connection state");
            NativeError.Check(native->get_IsConnectedToInternet(&internet).Value, "Read network Internet state");
            result.Add(new(id, ReadText(native, false), ReadText(native, true),
                (WindowsNetworkCategory)category, (int)domain, (int)connectivity, connected.Value != 0, internet.Value != 0));
        }
        return result.ToImmutable();
    }

    private static unsafe string ReadText(INetwork* network, bool description)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        BSTR text = default;
        try
        {
            NativeError.Check((description ? network->GetDescription(&text) : network->GetName(&text)).Value, "Read network text");
            return text.Value == null ? "" : Marshal.PtrToStringBSTR((nint)text.Value);
        }
        finally { if (text.Value != null) Marshal.FreeBSTR((nint)text.Value); }
    }
}
