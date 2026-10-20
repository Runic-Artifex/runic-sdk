using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The raw snapshot reply ({"ok", "state", "protocol", ...}) that the wire-shape tests inspect; application tests
// read a driver's Snapshot() instead.
internal static class HostSnapshots
{
    internal static JsonDocument RawSnapshot<TViewModel>(this RunicWindowTestHost<TViewModel> host) where TViewModel : class =>
        JsonDocument.Parse(host.Transport.Call($"{host.RootRoute}Snapshot"));

    internal static JsonDocument RawSnapshot<TViewModel>(this RunicWindowTestHost<TViewModel> host, PageReference reference)
        where TViewModel : class =>
        JsonDocument.Parse(host.Transport.Call($"content{reference.Id}Snapshot"));
}
