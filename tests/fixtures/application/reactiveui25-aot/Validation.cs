using ReactiveUI;
using ReactiveUI.Validation.Contexts;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Runic.Application.Views;

namespace ReactiveUi25AotProof;

// RunicArtifex.ReactiveUI.Validation under NativeAOT: the generated Bridge reads and observes the ValidationContext of a
// ReactiveValidationObject and of a ViewModel that implements only IValidatableViewModel.
public sealed class AotRulesViewModel : ReactiveValidationObject
{
    public const string NameTooLong = "Name must be at most 8 characters.";
    public const string ApprovalRequired = "Approval is required.";

    private readonly AotSignal _shortName = new(true);
    private readonly AotSignal _approved = new(true);

    public AotRulesViewModel()
    {
        this.ValidationRule(vm => vm.Name, _shortName, NameTooLong);
        this.ValidationRule(_approved, ApprovalRequired);
    }

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _shortName.Value = value.Length <= 8;
        }
    } = "";

    public void Approve(bool approved) => _approved.Value = approved;
}

public sealed partial class AotRulesWindow : RunicWindow<AotRulesViewModel>;

public sealed class AotValidatableOnlyViewModel : ReactiveObject, ReactiveUI.Validation.Abstractions.IValidatableViewModel
{
    public const string NameRequired = "Name is required.";

    private readonly AotSignal _named = new(false);

    public AotValidatableOnlyViewModel() => this.ValidationRule(vm => vm.Name, _named, NameRequired);

    public IValidationContext ValidationContext { get; } = new ValidationContext();

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _named.Value = value.Length > 0;
        }
    } = "";
}

public sealed partial class AotValidatableOnlyWindow : RunicWindow<AotValidatableOnlyViewModel>;

// A replaying boolean rule input without a scheduler.
internal sealed class AotSignal(bool initial) : IObservable<bool>
{
    private readonly List<IObserver<bool>> _observers = [];
    private bool _value = initial;

    internal bool Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            foreach (var observer in _observers.ToArray()) observer.OnNext(value);
        }
    }

    public IDisposable Subscribe(IObserver<bool> observer)
    {
        _observers.Add(observer);
        observer.OnNext(Value);
        return new Unsubscribe(() => _observers.Remove(observer));
    }

    private sealed class Unsubscribe(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
