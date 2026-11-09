using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// [RunicSharedClient] (#839): read-only result pages that publish the same members, which one web component renders.
// The Bridge build generates resultPage.ts; SharedClientTypesCheck.ts registers one component for every kind of
// ResultPagePageReference without a cast, and SharedClientTests drives the pages through the shared interface.

/// <summary>A link or button of a result page.</summary>
public sealed record SharedResultElement(string Id, string Label);

/// <summary>A computed, read-only result page.</summary>
public sealed record SharedResultPage(string Title, IReadOnlyList<SharedResultElement> Elements);

/// <summary>A read-only result page with one action per element.</summary>
[RunicSharedClient(Name = "ResultPage")]
public interface ISharedResultPage : INotifyPropertyChanged
{
    /// <summary>The page the component renders.</summary>
    SharedResultPage Page { get; }

    /// <summary>The text that filters the page's elements.</summary>
    string? Filter { get; set; }

    /// <summary>Runs the action of the element with the id.</summary>
    IAsyncRelayCommand<string> RunActionCommand { get; }
}

public sealed partial class SharedOrdersView : RunicView<SharedOrdersViewModel>;

// A second View of the same ViewModel: its kind, sharedOrdersCompact, is a kind of the shared reference too.
[RunicViewContract("Compact")]
public sealed partial class SharedOrdersCompactView : RunicView<SharedOrdersViewModel>;

public sealed partial class SharedInvoicesView : RunicView<SharedInvoicesViewModel>;

public sealed partial class SharedResultsWindow : RunicWindow<SharedResultsViewModel>;

/// <summary>Orders, with CommunityToolkit's generated members.</summary>
public sealed partial class SharedOrdersViewModel : ObservableObject, ISharedResultPage
{
    /// <summary>The page the component renders.</summary>
    public SharedResultPage Page => new("Orders", [.. Elements.Where(element => Filter is null || element.Label.Contains(Filter, StringComparison.Ordinal))]);

    /// <summary>The text that filters the page's elements.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Page))]
    public partial string? Filter { get; set; }

    /// <summary>The number of open orders, which only this ViewModel publishes.</summary>
    public int OpenOrders { get; } = Elements.Length;

    internal List<string> Runs { get; } = [];

    private static readonly SharedResultElement[] Elements = [new("o-1", "Order 1"), new("o-2", "Order 2")];

    /// <summary>Runs the action of the element with the id.</summary>
    [RelayCommand]
    private Task RunActionAsync(string elementId)
    {
        Runs.Add(elementId);
        return Task.CompletedTask;
    }
}

/// <summary>Invoices, with hand-written members.</summary>
public sealed class SharedInvoicesViewModel : ISharedResultPage
{
    private string? _filter;

    public SharedInvoicesViewModel() =>
        RunActionCommand = new AsyncRelayCommand<string>(id =>
        {
            Runs.Add(id!);
            return Task.CompletedTask;
        });

    /// <summary>The page the component renders.</summary>
    public SharedResultPage Page { get; private set; } = new("Invoices", [new("i-1", "Invoice 1")]);

    /// <summary>The text that filters the page's elements.</summary>
    public string? Filter
    {
        get => _filter;
        set { _filter = value; PropertyChanged?.Invoke(this, new(nameof(Filter))); }
    }

    /// <summary>Runs the action of the element with the id.</summary>
    public IAsyncRelayCommand<string> RunActionCommand { get; }

    internal List<string> Runs { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A window that presents one result page.</summary>
public sealed class SharedResultsViewModel : INotifyPropertyChanged
{
    private ISharedResultPage? _current;

    /// <summary>The result page that the window shows.</summary>
    public ISharedResultPage? Current
    {
        get => _current;
        private set { _current = value; PropertyChanged?.Invoke(this, new(nameof(Current))); }
    }

    /// <summary>Shows a result page.</summary>
    public void Show(ISharedResultPage page) => Current = page;

    public event PropertyChangedEventHandler? PropertyChanged;
}
