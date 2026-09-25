using ReactiveUI;
using Runic.Application.Views;

namespace NotesWindowViews;

// An optional ReactiveUI adapter: the Runic core does not depend on ReactiveUI.
// These .NET objects describe view identity, context, and presentation lifetime.
// Their HTML and component lifetimes remain in the frontend.
public abstract class ReactiveRunicView<TViewModel> : RunicView<TViewModel>,
    IViewFor<TViewModel>, IRunicViewLifetime, IRunicWebMountLifetime where TViewModel : class
{
    public TViewModel? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }

    public virtual void OnAttached() => ViewTrace.Attached(GetType());
    public virtual void OnDetached() => ViewTrace.Detached(GetType());
    public virtual void OnWebMounted() => ViewTrace.WebMounted(GetType());
    public virtual void OnWebUnmounted() => ViewTrace.WebUnmounted(GetType());
}

public sealed partial class SidebarView : ReactiveRunicView<SidebarViewModel>;
public sealed partial class HomeView : ReactiveRunicView<HomeViewModel>;
public sealed partial class DocumentView : ReactiveRunicView<DocumentViewModel>;
public sealed partial class EditorView : ReactiveRunicView<EditorViewModel>;
public sealed partial class PreviewView : ReactiveRunicView<PreviewViewModel>;
public sealed partial class ConfirmNavigationView : ReactiveRunicView<ConfirmNavigationViewModel>;

public static class ViewTrace
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> Active = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> WebMounts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> WebUnmounts = new();

    public static void Attached(Type type) => Active.AddOrUpdate(type, 1, static (_, count) => count + 1);
    public static void Detached(Type type) => Active.AddOrUpdate(type, 0, static (_, count) => count - 1);
    public static int ActiveCount(Type type) => Active.GetValueOrDefault(type);
    public static void WebMounted(Type type) => WebMounts.AddOrUpdate(type, 1, static (_, count) => count + 1);
    public static void WebUnmounted(Type type) => WebUnmounts.AddOrUpdate(type, 1, static (_, count) => count + 1);
    public static void VerifyWebMounts()
    {
        if (WebMounts.GetValueOrDefault(typeof(EditorView)) == 0)
            throw new InvalidOperationException("The browser never acknowledged an Editor mount.");
        foreach (var (type, count) in WebMounts)
            if (WebUnmounts.GetValueOrDefault(type) != count)
                throw new InvalidOperationException($"{type.Name} has {count} web mounts but {WebUnmounts.GetValueOrDefault(type)} unmounts.");
    }
}
