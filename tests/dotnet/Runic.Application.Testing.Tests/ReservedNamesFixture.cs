using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

// Types named like the bindings a generated client module declares for its own
// use (#258). Each name below is derived from a user type and was previously
// declared twice in one generated module, so tsc rejected the client. The
// shape tests type-check these modules.
namespace Runic.Application.Testing.Tests.ReservedNames;

// Named like Runic types that the generated C# bridges and their registration
// use. The bridges for this namespace's ViewModels are generated into it,
// where these declarations would hide the types of `using Runic.Application.Views;`.
#pragma warning disable CA1040, CA1711, CA1715 // The names are the shape under test.
public sealed class ViewModelBridge<T>;
public sealed class PropertyDescriptor<T>;
public sealed class CheckedPropertyDescriptor<T>;
public sealed class CommandDescriptor<T>;
public sealed class BridgeSnapshotWriter<T>;
public sealed class BridgeCollectionDescriptor<T>;
public sealed class IBridgeTransport;
public sealed class WindowContentSession;
public sealed class BridgeJson;
public sealed class BridgeWire;
public sealed class BridgeValidation;
public sealed class BridgeDataSubscriptionMember;
public enum CheckedFieldValueKind { None }
#pragma warning restore CA1040, CA1711, CA1715

// Named like the generated decoder namespace import, bridgeWire.
#pragma warning disable CS8981, CA1716, IDE1006
public sealed record bridgeWire(string Insulation);
#pragma warning restore CS8981, CA1716, IDE1006

// WireState: the state interface of WireViewModel and the wire shape of
// every generated client.
public sealed class WireViewModel : INotifyPropertyChanged
{
    private int _gauge = 12;

    public int Gauge
    {
        get => _gauge;
        set { _gauge = value; PropertyChanged?.Invoke(this, new(nameof(Gauge))); }
    }

    public bridgeWire Cable { get; } = new("pvc");
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class WireWindow : RunicWindow<WireViewModel>;

// ViewClient and connectView: the client interface and connect function of
// ViewViewModel, and the base interface and runtime function they use.
public sealed class ViewViewModel : INotifyPropertyChanged
{
    public string Title { get; } = "view";
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ViewWindow : RunicWindow<ViewViewModel>;

// BridgeInteractionContext and BridgeStreamOperation: BridgeViewModel's
// interaction context and Stream operation, and the runtime types they alias.
public sealed class BridgeViewModel : ReactiveObject, IDisposable
{
    public BridgeViewModel()
    {
        StreamCommand = ReactiveCommand.CreateFromTask<string, int>(value => Task.FromResult(value.Length));
        SaveCommand = ReactiveCommand.CreateFromTask<string, int>(value => Task.FromResult(value.Length));
    }

    public Interaction<string, bool> Confirm { get; } = new();

    [RunicCommandResult(BridgeCommandResultCardinality.Stream)]
    public ReactiveCommand<string, int> StreamCommand { get; }

    public ReactiveCommand<string, int> SaveCommand { get; }

    public void Dispose()
    {
        StreamCommand.Dispose();
        SaveCommand.Dispose();
    }
}

public sealed partial class BridgeWindow : RunicWindow<BridgeViewModel>;

// BridgeValidationState: the state of BridgeValidationViewModel and the
// generated validation summary type.
public sealed class BridgeValidationViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    public string Name { get; } = "bridge";
    public bool HasErrors => false;
    public System.Collections.IEnumerable GetErrors(string? propertyName) => Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class BridgeValidationWindow : RunicWindow<BridgeValidationViewModel>;

// defineInteractions: the interactions interface of defineViewModel and the
// runtime function that defines them.
#pragma warning disable CS8981, CA1716, IDE1006
public sealed class defineViewModel : ReactiveObject
{
    public Interaction<string, bool> Confirm { get; } = new();
    public string Title { get; } = "define";
}

public sealed partial class defineWindow : RunicWindow<defineViewModel>;
#pragma warning restore CS8981, CA1716, IDE1006

// mockTypedView: the mock factory of TypedViewViewModel and the runtime
// function every generated mock calls.
public sealed class TypedViewViewModel : INotifyPropertyChanged
{
    public string Title { get; } = "typed";
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class TypedViewWindow : RunicWindow<TypedViewViewModel>;

// pageShelfReferences: the page function of ShelfReferencesViewModel, which
// ShelfViewModel presents and imports, and the reference cache of
// ShelfViewModel's own page function.
public sealed class ShelfReferencesViewModel : INotifyPropertyChanged
{
    public string Title { get; } = "references";
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ShelfReferencesView : RunicView<ShelfReferencesViewModel>;

public sealed class ShelfViewModel : INotifyPropertyChanged
{
    public ShelfReferencesViewModel Shelved { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ShelfWindow : RunicWindow<ShelfViewModel>;
