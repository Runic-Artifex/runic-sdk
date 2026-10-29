using System.ComponentModel;

namespace Runic.Application.Testing;

// Tombstone (0.14): a removed type that test suites upgrading from older releases still name. It is an error that
// names the replacement, instead of CS0246, and is removed in the release after 0.14.

/// <summary>Removed in 0.13 with <c>InMemoryBridgeTransport.Call</c>.</summary>
[Obsolete("BridgeTestArguments was removed in 0.13 with InMemoryBridgeTransport.Call: drive the route by member with " +
    "host.Root.SetAsync(vm => vm.Step, 5), ExecuteAsync(vm => vm.SaveCommand, input) and the other RunicViewDriver " +
    "methods, or send a protocol 4 request with Transport.RequestAsync(json). See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeTestArguments
{
    private BridgeTestArguments() { }
}
