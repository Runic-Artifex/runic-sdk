namespace Runic.Application.Views;

/// <summary>A .NET presentation object associated with a web component.</summary>
public interface IRunicView
{
    /// <summary>The ViewModel presented by this view.</summary>
    object? DataContext { get; set; }
}

/// <summary>
/// A typed logical view. It owns no DOM and does not inherit Avalonia's control
/// tree; the frontend chooses and renders the corresponding component.
/// </summary>
public abstract class RunicView<TViewModel> : IRunicView where TViewModel : class
{
    private TViewModel? _dataContext;

    /// <summary>Creates a view whose ViewModel is assigned later.</summary>
    protected RunicView() { }

    /// <summary>Creates a view for <paramref name="dataContext"/>.</summary>
    protected RunicView(TViewModel dataContext) => _dataContext = dataContext;

    /// <summary>The typed ViewModel presented by this view.</summary>
    public virtual TViewModel? DataContext { get => _dataContext; set => _dataContext = value; }

    object? IRunicView.DataContext
    {
        get => DataContext;
        set => DataContext = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }
}

/// <summary>The native window owner and root typed context for one Bridge session.</summary>
public abstract class RunicWindow<TViewModel> : RunicView<TViewModel> where TViewModel : class
{
    /// <summary>Creates a window for its root ViewModel.</summary>
    protected RunicWindow(TViewModel dataContext) : base(dataContext) { }
}

/// <summary>Selects a named .NET and web View for a content property.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class RunicViewContractAttribute(string contract) : Attribute
{
    /// <summary>The contract name shared by the .NET and web View.</summary>
    public string Contract { get; } = contract;
}

/// <summary>Leaves a public ViewModel member on .NET without exposing it to the web Bridge.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicIgnoreAttribute : Attribute;

/// <summary>Resolves a fresh .NET view for a presented ViewModel.</summary>
public interface IRunicViewLocator
{
    /// <summary>Resolves the default view for <typeparamref name="TViewModel"/>.</summary>
    TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class;

    /// <summary>Resolves the view registered for <paramref name="contract"/>, or the default view when it is <see langword="null"/>.</summary>
    TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class => contract is null
            ? Locate<TView, TViewModel>()
            : throw new InvalidOperationException($"The view locator does not support contract '{contract}'.");
}

/// <summary>Optional hooks for the period during which a view is presented.</summary>
public interface IRunicViewLifetime
{
    /// <summary>Called when the view starts being presented.</summary>
    void OnAttached();
    /// <summary>Called when the view stops being presented.</summary>
    void OnDetached();
}

/// <summary>Optional visual mount acknowledgement from a web component.</summary>
public interface IRunicWebMountLifetime
{
    /// <summary>Called when the web component reports that it mounted.</summary>
    void OnWebMounted();
    /// <summary>Called when the web component unmounts or its presentation ends.</summary>
    void OnWebUnmounted();
}

/// <summary>
/// Optional ownership hooks for a logical View instance created for each web
/// presentation. The presentation identifier is private transport bookkeeping;
/// application code should not manufacture or retain it.
/// </summary>
public interface IRunicWebPresentationLifetime
{
    /// <summary>Called when the web presentation <paramref name="presentationId"/> mounted.</summary>
    void OnWebMounted(string presentationId);
    /// <summary>Called when the web presentation <paramref name="presentationId"/> ended.</summary>
    void OnWebUnmounted(string presentationId);
}
