using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;

namespace Runic.Application.Testing.Tests;

// #478: two property rules and one object rule. ReactiveValidationObject.GetErrors(null)
// returns all three messages, so the Bridge reads its ValidationContext instead.
public sealed class ReactivePasswordViewModel : ReactiveValidationObject
{
    internal const string TooShort = "Password must be at least 7 characters long.";
    internal const string Mismatch = "The confirmation does not match the new password.";
    internal const string Common = "Choose a less common password.";

    private readonly ValidationSignal _longEnough = new(true);
    private readonly ValidationSignal _matches = new(true);
    private readonly ValidationSignal _uncommon = new(true);

    public ReactivePasswordViewModel()
    {
        this.ValidationRule(vm => vm.NewPassword, _longEnough, TooShort);
        this.ValidationRule(vm => vm.ConfirmPassword, _matches, Mismatch);
        this.ValidationRule(_uncommon, Common);
    }

    public string NewPassword
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            Revalidate();
        }
    } = string.Empty;

    public string ConfirmPassword
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            Revalidate();
        }
    } = string.Empty;

    private void Revalidate()
    {
        _longEnough.Value = NewPassword.Length >= 7;
        _matches.Value = NewPassword == ConfirmPassword;
        _uncommon.Value = NewPassword != "abc";
    }
}

public sealed partial class ReactivePasswordWindow : Runic.Application.Views.RunicWindow<ReactivePasswordViewModel>;

// A replaying boolean observable; the fixture needs no scheduler or expression observation.
internal sealed class ValidationSignal(bool value) : IObservable<bool>
{
    private readonly List<IObserver<bool>> _observers = [];
    private bool _value = value;

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
        observer.OnNext(_value);
        return new Subscription(() => _observers.Remove(observer));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
