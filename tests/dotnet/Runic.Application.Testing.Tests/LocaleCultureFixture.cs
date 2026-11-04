using System.Collections;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// FlowCultureFromLocale: the culture a setter, a command (across an await) and validation see.
public sealed partial class CultureToolkitViewModel : ObservableObject, INotifyDataErrorInfo
{
    [ObservableProperty]
    public partial int Amount { get; set; }

    [ObservableProperty]
    public partial string SetterCulture { get; set; } = "";

    [ObservableProperty]
    public partial string Formatted { get; set; } = "";

    partial void OnAmountChanged(int value)
    {
        SetterCulture = CultureInfo.CurrentCulture.Name;
        ErrorsChanged?.Invoke(this, new(nameof(Amount)));
    }

    [RelayCommand]
    private async Task FormatAsync()
    {
        await Task.Yield();
        Formatted = $"{Amount:N0} {CultureInfo.CurrentUICulture.Name}";
    }

    public bool HasErrors => Amount > 1000;

    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Amount) && Amount > 1000
        ? new[] { $"{Amount:N0} is more than {1000:N0}." }
        : [];

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

public sealed partial class CultureToolkitWindow : RunicWindow<CultureToolkitViewModel>;

public sealed class CultureReactiveViewModel : ReactiveValidationObject
{
    public CultureReactiveViewModel(IRunicModelContext modelContext)
    {
        this.ValidationRule(vm => vm.Amount, amount => amount <= 1000, amount => $"{amount:N0} is more than {1000:N0}.");
        FormatCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(async _ =>
        {
            await Task.Yield();
            Formatted = $"{Amount:N0} {CultureInfo.CurrentUICulture.Name}";
            return RxVoid.Default;
        }, ReactiveSchedulers.For(modelContext));
    }

    public int Amount
    {
        get;
        set
        {
            SetterCulture = CultureInfo.CurrentCulture.Name;
            this.RaiseAndSetIfChanged(ref field, value);
        }
    }

    public string SetterCulture
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string Formatted
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public ReactiveCommand<RxVoid, RxVoid> FormatCommand { get; }
}

public sealed partial class CultureReactiveWindow : RunicWindow<CultureReactiveViewModel>;
