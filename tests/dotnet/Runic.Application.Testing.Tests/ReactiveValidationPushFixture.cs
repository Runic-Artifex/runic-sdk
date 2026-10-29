using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;

namespace Runic.Application.Testing.Tests;

// Pushed validation of a ReactiveValidationObject: a property rule, a rule that names no member (Approved), a nested
// ReactiveValidationObject and a collection of them. ReactiveValidationObject raises ErrorsChanged only for the members
// its rules named before, so a change of the Approved rule while the Name rule already fails changes neither
// ErrorsChanged nor HasErrors: the Bridge observes the ValidationContext.
public sealed class ReactiveRulesViewModel : ReactiveValidationObject
{
    internal const string NameTooLong = "Name must be at most 8 characters.";
    internal const string ApprovalRequired = "Approval is required.";

    private readonly ValidationSignal _shortName = new(true);
    private readonly ValidationSignal _approved = new(true);

    public ReactiveRulesViewModel()
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
    } = string.Empty;

    public ReactiveRulesAddress Address { get; } = new();

    public ObservableCollection<ReactiveRulesLine> Lines { get; } = [new()];

    // The rule that names no member.
    internal bool Approved { set => _approved.Value = value; }
}

// A nested DTO with its own ValidationContext: a property rule and a rule that names no member.
public sealed class ReactiveRulesAddress : ReactiveValidationObject
{
    internal const string CityRequired = "City is required.";
    internal const string Unverified = "The address is not verified.";

    private readonly ValidationSignal _city = new(true);
    private readonly ValidationSignal _verified = new(true);

    public ReactiveRulesAddress()
    {
        this.ValidationRule(vm => vm.City, _city, CityRequired);
        this.ValidationRule(_verified, Unverified);
    }

    public string City
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _city.Value = value.Length > 0;
        }
    } = "Graz";

    internal bool Verified { set => _verified.Value = value; }
}

// A collection row with its own ValidationContext.
public sealed class ReactiveRulesLine : ReactiveValidationObject
{
    internal const string AmountPositive = "Amount must be positive.";
    internal const string Blocked = "The line is blocked.";

    private readonly ValidationSignal _positive = new(true);
    private readonly ValidationSignal _open = new(true);

    public ReactiveRulesLine()
    {
        this.ValidationRule(vm => vm.Amount, _positive, AmountPositive);
        this.ValidationRule(_open, Blocked);
    }

    public int Amount
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _positive.Value = value > 0;
        }
    } = 1;

    internal bool Open { set => _open.Value = value; }
}

public sealed partial class ReactiveRulesWindow : Runic.Application.Views.RunicWindow<ReactiveRulesViewModel>;

// D053.3: a ViewModel and a nested DTO that keep their validation in a ValidationContext without implementing
// INotifyDataErrorInfo. They have the validation member; their ValidationContext is not part of the contract.
public sealed class ValidatableOnlyViewModel : ReactiveObject, ReactiveUI.Validation.Abstractions.IValidatableViewModel
{
    internal const string NameRequired = "Name is required.";
    internal const string ApprovalRequired = "Approval is required.";

    private readonly ValidationSignal _named = new(true);
    private readonly ValidationSignal _approved = new(true);

    public ValidatableOnlyViewModel()
    {
        this.ValidationRule(vm => vm.Name, _named, NameRequired);
        this.ValidationRule(_approved, ApprovalRequired);
    }

    public ReactiveUI.Validation.Contexts.IValidationContext ValidationContext { get; } =
        new ReactiveUI.Validation.Contexts.ValidationContext();

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _named.Value = value.Length > 0;
        }
    } = "set";

    public ValidatableOnlyAddress Address { get; } = new();

    internal bool Approved { set => _approved.Value = value; }
}

public sealed class ValidatableOnlyAddress : ReactiveObject, ReactiveUI.Validation.Abstractions.IValidatableViewModel
{
    internal const string CityRequired = "City is required.";

    private readonly ValidationSignal _city = new(true);

    public ValidatableOnlyAddress() => this.ValidationRule(vm => vm.City, _city, CityRequired);

    public ReactiveUI.Validation.Contexts.IValidationContext ValidationContext { get; } =
        new ReactiveUI.Validation.Contexts.ValidationContext();

    public string City
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _city.Value = value.Length > 0;
        }
    } = "Graz";
}

public sealed partial class ValidatableOnlyWindow : Runic.Application.Views.RunicWindow<ValidatableOnlyViewModel>;
