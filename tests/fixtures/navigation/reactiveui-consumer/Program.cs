using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Navigation.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using Runic.Navigation.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

// A consumer of the packed navigation ReactiveUI adapter and nothing else from Runic: no
// Runic.Application, generator or host (package-smoke.mjs checks the restored graph).
var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using var scope = container.CreateAsyncScope();
var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
var scheduler = scope.ServiceProvider.GetRequiredService<FlavorScheduler>();
Require(ReferenceEquals(scheduler, scope.ServiceProvider.GetRequiredService<FlavorScheduler>()),
    "Two resolutions for one context returned distinct schedulers.");
Require(ReferenceEquals(scheduler, container.GetRequiredService<IRunicReactiveSchedulerProvider>().For(context)),
    "The provider returned another scheduler than the container.");

await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
var region = navigator.CreateRegion<string>(new object(), NavigationTarget.Borrow("home"));
var seen = new List<string?>();
using var subscription = region.WhenCurrentChanged().Subscribe(new Observer<string?>(seen.Add));
Require((await region.PushAsync(NavigationTarget.Borrow("detail"))) is NavigationResult<string>.Committed, "The push did not commit.");
var back = region.CreateBackCommand(scheduler);
for (var attempt = 0; attempt < 200 && !((ICommand)back).CanExecute(null); attempt++) await Task.Delay(10);
Require(((ICommand)back).CanExecute(null), "The back command was disabled with history.");
var finished = new TaskCompletionSource<NavigationResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
using var execution = back.Execute(FlavorUnit.Default).Subscribe(new Observer<NavigationResult<string>>(result => finished.TrySetResult(result)));
Require(await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)) is NavigationResult<string>.Committed, "Back did not commit.");
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
