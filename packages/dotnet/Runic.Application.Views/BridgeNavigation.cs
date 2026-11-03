using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Runic.Navigation;

namespace Runic.Application.Views;

// navigation-path/1 (specs/application/README.md, "Navigation paths"): the window's Back and its route path.
//
// - navigation {action:"back"} runs RunicNavigator.BackAsync: the innermost presented region that can go back.
// - navigation {action:"open", path} opens the route path in the window's path region (RunicPresentation.SyncNavigationPath)
//   with NavigationRouteOpen.BackToOrPush, so a browser's Back and Forward and its address bar go through the guards.
// - the hello reply carries navigationPath, and navigation-path {path, operation} is pushed to each connection that uses
//   the feature whenever the path region's current route changes, so the page keeps its URL in step.
//
// Replies carry the request's outcome and the path after it: a page whose URL differs from it reverts its URL.
internal static class BridgeNavigation
{
    // Reads the request at once: the dispatcher disposes the envelope when this returns.
    public static ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeDispatcher dispatcher, BridgeRequestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var root = envelope.Root;
        var open = root.GetProperty("action").GetString() == "open";
        var path = open ? root.GetProperty("path").GetString()! : null;
        if (dispatcher.Navigator is not { } navigator)
            return new(BridgeMessages.Rejected("unavailable", "The window presents no navigation regions."));
        var sync = NavigationPathSync.For(navigator);
        if (open && sync is null)
            return new(BridgeMessages.Rejected("unavailable", "The window has no navigation path region (RunicPresentation.SyncNavigationPath)."));
        return new(RunAsync(dispatcher, navigator, sync, path, cancellationToken));
    }

    private static async Task<ReadOnlyMemory<byte>> RunAsync(BridgeDispatcher dispatcher, RunicNavigator navigator, NavigationPathSync? sync,
        string? path, CancellationToken cancellationToken)
    {
        NavigationOutcome? outcome;
        try
        {
            var result = path is null
                ? await navigator.BackAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                : await sync!.Region.OpenRouteAsync(path, NavigationRouteOpen.BackToOrPush, sync.Routes, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            outcome = result?.Outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new NavigationOutcome(NavigationOutcomeKind.Rejected, NavigationRejection.Cancelled);
        }
        // The path push of a commit goes out before the reply, so the page sees its URL confirmed first: it is posted as
        // a turn after the commit's, so a turn of the model context runs after it.
        try { await navigator.ModelContext.InvokeAsync(static () => { }, CancellationToken.None).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        await BridgeTargetedPushes.For(dispatcher.Transport).WhenSent().ConfigureAwait(false);
        return BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            if (outcome is { } settled) BridgeRegionCollections.WriteOutcome(writer, settled);
            else writer.WriteNull("outcome");
            if (sync is not null) WritePath(writer, sync.CurrentPath());
            writer.WriteEndObject();
        });
    }

    // The window's route path for a hello reply: false without a path region.
    public static bool TryGetPath(BridgeDispatcher dispatcher, out string? path)
    {
        path = null;
        if (dispatcher.Navigator is not { } navigator || NavigationPathSync.For(navigator) is not { } sync) return false;
        path = sync.CurrentPath();
        return true;
    }

    public static byte[] Push(string? path, string operation) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "navigation-path");
        WritePath(writer, path);
        writer.WriteString("operation", operation);
        writer.WriteEndObject();
    });

    private static void WritePath(Utf8JsonWriter writer, string? path)
    {
        if (path is null) writer.WriteNull("path");
        else writer.WriteString("path", path);
    }
}

// The window's path region: its route is the page's route path. One per navigator.
internal sealed class NavigationPathSync : IDisposable
{
    private static readonly ConditionalWeakTable<RunicNavigator, NavigationPathSync> Syncs = new();

    private readonly object _gate = new();
    private NavigationEntryId[] _stack = [];
    private string? _path;
    private bool _disposed;

    private NavigationPathSync(NavigationRegion region, INavigationRouteMap routes)
    {
        Region = region;
        Routes = routes;
        (_stack, _path) = Read();
        region.PropertyChanged += OnChanged;
    }

    public NavigationRegion Region { get; }
    public INavigationRouteMap Routes { get; }

    public static NavigationPathSync? For(RunicNavigator navigator) => Syncs.TryGetValue(navigator, out var sync) ? sync : null;

    public static NavigationPathSync Create(NavigationRegion region, INavigationRouteMap routes)
    {
        var navigator = region.Navigator;
        lock (Syncs)
        {
            if (Syncs.TryGetValue(navigator, out var existing))
            {
                if (ReferenceEquals(existing.Region, region)) return existing;
                throw new InvalidOperationException("The navigator already syncs another region with the page's route path. A window has one path region.");
            }
            var sync = new NavigationPathSync(region, routes);
            Syncs.Add(navigator, sync);
            return sync;
        }
    }

    public string? CurrentPath()
    {
        lock (_gate) return _path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Region.PropertyChanged -= OnChanged;
        lock (Syncs)
            if (Syncs.TryGetValue(Region.Navigator, out var current) && ReferenceEquals(current, this)) Syncs.Remove(Region.Navigator);
    }

    private (NavigationEntryId[] Stack, string? Path) Read()
    {
        var history = Region.History;
        var current = Region.CurrentEntry;
        var stack = new NavigationEntryId[history.Count + (current is null ? 0 : 1)];
        for (var index = 0; index < history.Count; index++) stack[index] = history[index].Id;
        if (current is not null) stack[^1] = current.Id;
        return (stack, current is null ? null : Routes.GetRoute(current));
    }

    // Raised in the commit turn. A push extends the stack by one entry, a Back leaves a prefix of it; anything else
    // replaces the page's current history entry.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NavigationRegion.CurrentEntry) or null or "")) return;
        string operation;
        string? path;
        lock (_gate)
        {
            if (_disposed) return;
            var (stack, read) = Read();
            var old = _stack;
            if (stack.AsSpan().SequenceEqual(old) && read == _path) return;
            operation = stack.Length == old.Length + 1 && stack.AsSpan(0, old.Length).SequenceEqual(old) ? "push"
                : stack.Length < old.Length && old.AsSpan(0, stack.Length).SequenceEqual(stack) ? "back"
                : "replace";
            _stack = stack;
            _path = path = read;
        }
        // Sent in the next turn, once the commit's frames were handed to their deliveries, so the push follows them.
        if (!Region.Navigator.ModelContext.TryPost(() => Send(path, operation))) Send(path, operation);
    }

    private void Send(string? path, string operation)
    {
        if (NavigationPresentationBinding.BoundSession(Region.Navigator) is not { IsDisposed: false } session) return;
        var dispatcher = BridgeDispatcher.For(session.Transport);
        var pushes = BridgeTargetedPushes.For(session.Transport);
        foreach (var key in dispatcher.NavigationPathConnections()) pushes.Send(key, BridgeNavigation.Push(path, operation));
    }
}
