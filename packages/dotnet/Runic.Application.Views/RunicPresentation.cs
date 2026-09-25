namespace Runic.Application.Views;

/// <summary>A .NET presentation object associated with a web component.</summary>
public interface IRunicView
{
    object? DataContext { get; set; }
}

/// <summary>
/// A typed logical view. It owns no DOM and does not inherit Avalonia's control
/// tree; the frontend chooses and renders the corresponding component.
/// </summary>
public abstract class RunicView<TViewModel> : IRunicView where TViewModel : class
{
    private TViewModel? _dataContext;

    protected RunicView() { }

    protected RunicView(TViewModel dataContext) => _dataContext = dataContext;

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
    protected RunicWindow(TViewModel dataContext) : base(dataContext) { }
}

/// <summary>Selects a named .NET and web View for a content property.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class RunicViewContractAttribute(string contract) : Attribute
{
    public string Contract { get; } = contract;
}

/// <summary>Leaves a public ViewModel member on .NET without exposing it to the web Bridge.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicIgnoreAttribute : Attribute;

/// <summary>Resolves a fresh .NET view for a presented ViewModel.</summary>
public interface IRunicViewLocator
{
    TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class;

    TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class => contract is null
            ? Locate<TView, TViewModel>()
            : throw new InvalidOperationException($"The view locator does not support contract '{contract}'.");
}

/// <summary>Optional hooks for the period during which a view is presented.</summary>
public interface IRunicViewLifetime
{
    void OnAttached();
    void OnDetached();
}

/// <summary>Optional visual mount acknowledgement from a web component.</summary>
public interface IRunicWebMountLifetime
{
    void OnWebMounted();
    void OnWebUnmounted();
}

/// <summary>
/// Optional ownership hooks for a logical View instance created for each web
/// presentation. The presentation identifier is private transport bookkeeping;
/// application code should not manufacture or retain it.
/// </summary>
public interface IRunicWebPresentationLifetime
{
    void OnWebMounted(string presentationId);
    void OnWebUnmounted(string presentationId);
}
