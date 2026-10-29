using System.ComponentModel;

#pragma warning disable CA1040 // Tombstones have no members: using one is the error.

namespace Runic.Application.Views;

// Tombstones (0.14): removed types that applications upgrading from older releases still name. Each is an error that
// names the replacement, instead of CS0246. They are removed in the release after 0.14.

/// <summary>Removed in 0.9: <c>[JsonIgnore]</c> members are excluded by default.</summary>
[Obsolete("RunicBridgeJsonIgnore was removed in 0.9: [JsonIgnore] members are excluded from the Bridge by default. Delete the " +
    "attribute; add [assembly: RunicIncludeJsonIgnored] to keep [JsonIgnore] members. See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RunicBridgeJsonIgnoreAttribute : Attribute;

/// <summary>Removed in 0.13 with Views protocol 3.</summary>
[Obsolete("IAsyncBridgeTransport was removed in 0.13 (Views protocol 4): implement IBridgeTransport.Serve(IBridgeRequestHandler) " +
    "and BroadcastAsync, and IBridgeConnectionTransport.SendAsync for pushes to one connection. See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IAsyncBridgeTransport;

/// <summary>Removed in 0.13 with Views protocol 3.</summary>
[Obsolete("IBridgeUtf8Transport was removed in 0.13 (Views protocol 4): IBridgeTransport.BroadcastAsync receives UTF-8 " +
    "messages, and IBridgeConnectionTransport.SendAsync pushes to one connection. See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBridgeUtf8Transport;

/// <summary>Removed in 0.13 with Views protocol 3.</summary>
[Obsolete("IBridgeArguments was removed in 0.13 (Views protocol 4): generated descriptors read the request's JSON value. " +
    "Rebuild to regenerate the Bridges. See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBridgeArguments;
