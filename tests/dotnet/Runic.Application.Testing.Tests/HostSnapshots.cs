using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The raw snapshot reply ({"ok", "revision", "state", "fields"?}) that the wire-shape tests inspect; application tests
// read a driver's Snapshot() instead.
internal static class HostSnapshots
{
    internal static JsonDocument RawSnapshot<TViewModel>(this RunicWindowTestHost<TViewModel> host) where TViewModel : class =>
        RawSnapshot(host, host.RootRoute);

    internal static JsonDocument RawSnapshot<TViewModel>(this RunicWindowTestHost<TViewModel> host, PageReference reference)
        where TViewModel : class =>
        RawSnapshot(host, $"content{reference.Id}");

    // Sends a request on the host's test connection, after its hello, and returns the reply.
    internal static string Request<TViewModel>(this RunicWindowTestHost<TViewModel> host, string json) where TViewModel : class =>
        host.Transport.Send(json);

    private static JsonDocument RawSnapshot<TViewModel>(RunicWindowTestHost<TViewModel> host, string route) where TViewModel : class
    {
        return JsonDocument.Parse(host.Transport.Snapshot(route));
    }
}
