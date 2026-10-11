using System.Windows.Input;
using Runic.Navigation;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Navigation.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
#else
using ReactiveUI;
using Runic.Navigation.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
#endif

// A consumer of the packed navigation ReactiveUI adapter and nothing else from Runic: no
// Runic.Application, generator or host (package-smoke.mjs checks the restored graph). Model-context
// scheduling comes from Runic.Application.ReactiveUI, so this consumer uses ReactiveUI's default scheduler.
await using var context = new RunicModelContext();
await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
var region = navigator.CreateRegion<string>(new object(), NavigationTarget.Borrow("home"));
var seen = new List<string?>();
using var subscription = region.WhenCurrentChanged().Subscribe(new Observer<string?>(seen.Add));
Require((await region.PushAsync(NavigationTarget.Borrow("detail"))) is NavigationResult<string>.Committed, "The push did not commit.");
using var back = region.CreateBackCommand();
for (var attempt = 0; attempt < 200 && !((ICommand)back).CanExecute(null); attempt++) await Task.Delay(10);
Require(((ICommand)back).CanExecute(null), "The back command was disabled with history.");
var finished = new TaskCompletionSource<NavigationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
using var execution = back.Execute(FlavorUnit.Default).Subscribe(new Observer<NavigationOutcome>(result => finished.TrySetResult(result)));
Require((await finished.Task.WaitAsync(TimeSpan.FromSeconds(10))).Kind == NavigationOutcomeKind.Committed, "Back did not commit.");
Require(seen.SequenceEqual(["home", "detail", "home"]), $"WhenCurrentChanged emitted {string.Join(", ", seen)}.");
Console.WriteLine("NAVIGATION_REACTIVEUI_CONSUMER_OK");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Observer<T>(Action<T> next) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception error) => throw new InvalidOperationException("The sequence failed.", error);
    public void OnNext(T value) => next(value);
}
