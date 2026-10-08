using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// Binds the window session that presents a navigator's regions, so retiring owned
// content forgets its routes. Bridges bind when they first observe a region slot.
// One session per navigator: binding is idempotent for the same session and ends
// when either side is disposed; a disposed session's binding is replaced.
#pragma warning disable RUNICNAV001 // Binds sessions to the experimental navigator.
internal static class NavigationPresentationBinding
{
    private static readonly ConditionalWeakTable<RunicNavigator, Slot> Slots = new();

    // A Bridge slot presents Current. It binds the window session and republishes
    // when Current changes. Disposing the subscription leaves the binding in place.
    public static IDisposable Observe(INavigationRegion region, WindowContentSession? session, Action changed)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(changed);
        if (session is not null) Bind(region.Navigator, session);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(INavigationRegion.Current)) changed();
        };
        region.PropertyChanged += handler;
        return new Subscription(region, handler);
    }

    /// <exception cref="InvalidOperationException">
    /// The session uses a different model context, or another live session is already bound.
    /// </exception>
    public static void Bind(RunicNavigator navigator, WindowContentSession session)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.ModelContext, navigator.ModelContext))
            throw new InvalidOperationException(
                "A navigator can be presented only by a window session that shares its model context. Create the session with the navigator's IRunicModelContext.");
        var slot = Slots.GetValue(navigator, static _ => new Slot());
        lock (slot)
        {
            if (session.IsDisposed) return;
            if (slot.Binding is { } bound)
            {
                if (ReferenceEquals(bound.Session, session)) return;
                if (!bound.Session.IsDisposed)
                    throw new InvalidOperationException(
                        "This navigator is already presented by another window session. Use one navigator per window.");
                bound.Attachment?.Dispose();
            }
            var binding = new Binding(session);
            binding.Attachment = navigator.AttachPresentation(binding);
            slot.Binding = binding;
        }
    }

    private sealed class Slot
    {
        public Binding? Binding { get; set; }
    }

    // Forget is a no-op once the session is disposed.
    private sealed class Binding(WindowContentSession session) : INavigationPresentation
    {
        public WindowContentSession Session { get; } = session;

        public IDisposable? Attachment { get; set; }

        public void Forget(object content) => Session.Forget(content);
    }

    private sealed class Subscription(INavigationRegion region, PropertyChangedEventHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) region.PropertyChanged -= handler;
        }
    }
}
