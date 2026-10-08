# Runic.Navigation.Wpf

WPF hosts for [Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions. It depends on `Runic.Navigation` and the WPF framework only: no
`Runic.Application`, generator, build assets or web View. The package provides
the following types in the `Runic.Navigation.Wpf` namespace. The XAML namespace
is `https://runic-artifex.eu/xaml/navigation`, with the prefix `rn`.

- `DispatcherModelContext`, an `IRunicModelContext` that runs model turns and
  navigation hooks on the WPF dispatcher.
- `NavigationHost`, a `ContentControl` that presents a region's current entry.
- `NavigationDialogHost`, which shows each entry of a region in its own owned
  dialog window.
- `AddRunicWpfNavigation()`, which registers the navigator on the dispatcher.

The package targets `net10.0-windows` and isn't trimmable, because WPF isn't.
Its API is experimental, like the navigator's: suppress `RUNICNAV001` to use it.

```sh
dotnet add package Runic.Navigation.Wpf --prerelease
```

## Quick start

This page builds the core of the
[WPF navigation example](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-navigation/README.md):
a notes list, a detail page that asks before losing edits, and a confirm
dialog with a typed result. The example adds nested tabs and headless tests,
and its
[comparison](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-navigation/COMPARISON.md)
sets the same app against Prism, ReactiveUI and CrissCross.

**1. Install and register.** Add this package and
`Microsoft.Extensions.DependencyInjection`, and suppress `RUNICNAV001`. Then
register the navigator and one service that holds the window's regions:

```csharp
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = new ServiceCollection()
            .AddRunicWpfNavigation(options => options.UseViewNamingConvention()) // NotesViewModel -> NotesView
            .AddSingleton<NoteStore>()
            .AddSingleton<AppRegions>()
            .BuildServiceProvider();
        var regions = _services.GetRequiredService<AppRegions>();
        new MainWindow { DataContext = regions }.Show();
        await regions.Main.ResetAsync<NotesViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // starts disposal without waiting; see "Disposal at exit"
        base.OnExit(e);
    }
}
```

**2. A region and its host.** A region is a navigation stack. ViewModels
inject `AppRegions` to navigate, and the window binds to it. The regions start
empty, because an initial ViewModel that injects `AppRegions` would be a DI
cycle; `App` resets `Main` instead.

```csharp
public sealed class AppRegions
{
    public AppRegions(RunicNavigator navigator)
    {
        Main = navigator.CreateRegion<object>(this);
        Dialog = navigator.CreateRegion<object>(this);
    }

    public NavigationRegion<object> Main { get; }
    public NavigationRegion<object> Dialog { get; }
}
```

```xml
<Window xmlns:rn="https://runic-artifex.eu/xaml/navigation" ...>
  <DockPanel>
    <Button DockPanel.Dock="Top" Content="Back"
            Command="NavigationCommands.BrowseBack" CommandTarget="{Binding ElementName=Host}" />
    <rn:NavigationDialogHost Region="{Binding Dialog}" />
    <rn:NavigationHost x:Name="Host" Region="{Binding Main}" />
  </DockPanel>
</Window>
```

`NavigationHost` shows the current entry with its View, `NotesView` for
`NotesViewModel`. Back can't execute while a Back is in flight, so a double
click pops once. `NavigationDialogHost` shows each entry of `Dialog` in a
window owned by this one.

**3. Push a page with a typed input, and ask before losing its edits.**
`PushAsync<T>()` creates the ViewModel from the container. One that
implements `INavigationInitialize<TInput>` receives its input before it's
shown. `INavigationDepartureGuard` is asked on every exit: Back, a push from
code, or a parent closing. `LeaveConfirmation.InDialog` asks in the dialog
region and runs the discard only when the departure commits, so a cancelled or
superseded Back keeps the edits:

```csharp
// In NotesViewModel
await regions.Main.PushAsync<NoteViewModel, int>(note.Id);

public sealed class NoteViewModel : ObservableObject, INavigationInitialize<int>, INavigationDepartureGuard
{
    private readonly NoteStore _store;
    private readonly LeaveConfirmation _leave;
    private Note _note = new(0, "");
    private string _title = "";

    public NoteViewModel(NoteStore store, AppRegions regions)
    {
        _store = store;
        _leave = LeaveConfirmation.InDialog(regions.Dialog,
            () => NavigationTarget.Create<ConfirmViewModel, string>("Discard unsaved changes?"),
            hasUnsavedChanges: () => Title != _note.Title,
            discard: () => Title = _note.Title);
    }

    public string Title { get => _title; set => SetProperty(ref _title, value); }

    public ValueTask InitializeAsync(NavigationEntryContext entry, int id, CancellationToken token)
    {
        _note = _store.Get(id);
        Title = _note.Title;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
        _leave.CanDepartAsync(departure, token);
}
```

**4. A dialog with a result.** Push the dialog with `PushForResult<TResult>`
and await its completion. The dialog answers through the entry it received:

```csharp
// In NotesViewModel
var answer = await regions.Dialog.PushForResult<bool>(
    NavigationTarget.Create<ConfirmViewModel, string>($"Delete '{note.Title}'?")).Completion;
if (answer is NavigationCompletion<bool>.Completed { Value: true })
    store.Delete(note.Id);

public sealed class ConfirmViewModel : ObservableObject, INavigationInitialize<string>
{
    private NavigationEntryContext? _entry;

    public string Message { get; private set; } = "";
    public Task YesAsync() => _entry!.CompleteAsync(true).AsTask(); // the OK button
    public Task NoAsync() => _entry!.DismissAsync().AsTask();       // Cancel; Esc and the close button dismiss too

    public ValueTask InitializeAsync(NavigationEntryContext entry, string message, CancellationToken token)
    {
        (_entry, Message) = (entry, message);
        return ValueTask.CompletedTask;
    }
}
```

The same `ConfirmViewModel` serves the guard in step 3: only `Completed(true)`
leaves. To push a ViewModel you built, use `NavigationTarget.Own<object>(instance)`;
for a factory, `NavigationTarget.Create<object>(services => ...)`. With the
default singleton navigator, each container-built entry gets its own service
scope, which is disposed when the entry retires.

## Registration

`AddRunicWpfNavigation(configure)` calls `AddRunicNavigation()` and replaces two
of its registrations:

- `IRunicModelContext` becomes a singleton `DispatcherModelContext`. It uses
  `RunicWpfNavigationOptions.Dispatcher`, or `Application.Current.Dispatcher`
  when it's first resolved. Without either, resolution throws
  `InvalidOperationException`. It never creates a dispatcher on the resolving
  thread.
- `RunicNavigator` gets `NavigatorLifetime` (`Singleton` by default, or
  `Scoped` for one navigator per window scope). `Transient` throws.
  `CreateEntryScopes` defaults to `true` for a singleton navigator. `Scoped`
  combined with `CreateEntryScopes = true` throws.

The package assumes **one UI thread**. An app with a dispatcher per window
thread builds one `DispatcherModelContext` and one navigator per thread itself.

## Presenting a region

`NavigationHost` creates a **new `ContentPresenter` and View for each entry**,
also for two entries of the same type or one borrowed instance pushed twice.
Text, scroll position and selection therefore never leak from one entry to the
next. It looks for a View in this order:

1. the host's `ViewLocator`;
2. the `INavigationViewLocator` registered in the navigator's services, which
   `MapView<TViewModel, TView>()` and `UseViewNamingConvention()` add;
3. the implicit `DataTemplate` for the content's type or a base type. WPF
   doesn't match templates by interface.

When none presents the content, the host logs event 1082 once per type.

- A locator View is created from the entry's services. The first constructor
  parameter whose type is a class the content can be assigned to, meaning the
  content's own type or one of its base classes but never `object`, gets
  **the entry's content**, never another instance from the container. So
  `DocumentView(DocumentViewModel model)` binds to the pushed ViewModel.
  Interface and `object` parameters, such as `INotifyPropertyChanged` or
  `IDisposable`, and all other parameters come from the entry's services, also
  when the content implements them. When several constructors take the
  content, the one marked `[ActivatorUtilitiesConstructor]` wins, then, as
  with `ActivatorUtilities`, the longest one whose other parameters are all
  registered services (keyed with `[FromKeyedServices]`) or have default
  values. `[ServiceKey]` parameters aren't supported. A View without a content
  parameter is created with `ActivatorUtilities`. The View's `DataContext` is the content unless the View
  sets one.
- `UseViewNamingConvention()` maps `FooViewModel` to `FooView`, then `FooPage`,
  in the same assembly, also from a `.ViewModels` namespace to `.Views`. When
  the ViewModels live in a separate assembly, pass the View assemblies:
  `UseViewNamingConvention(typeof(MainWindow).Assembly)`. There a View in
  another namespace matches by name when exactly one View type has it. The
  convention uses reflection; prefer `MapView` for an app you trim.
- The host sets its own `Content`. Don't set `Content`, `ContentTemplate` or
  `ContentTemplateSelector` on it; present entries with implicit
  `DataTemplate`s or a locator.
- Retention keeps the ViewModel, not the View. On Back the host builds a new
  View for the retained ViewModel, so state that must survive belongs in the
  ViewModel.
- An unloaded host unsubscribes and never touches entries. It reads the
  region again when it's loaded.
- `NavigationCommands.BrowseBack` (Alt+Left, the mouse back button) goes back
  while `CanGoBack` and not `IsTransitioning`. Set `HandlesBrowseBack="False"`
  to handle it yourself.

## Dialogs

`NavigationDialogHost` is an invisible element that you place in the owner
window. It shows one owned window per entry of its region, bottom to top. It
uses `Show()` and never `ShowDialog()`, so a dialog doesn't run a nested
message loop:

- `Modality="Application"` (the default) disables the thread's other visible
  top-level windows while a dialog is open, as `ShowDialog` does.
  `Modality="Owner"` disables only the owner chain. Dialog hosts share one
  disable count per window, so a host never re-enables a window that another
  host or the app disabled.
- `WindowStyle` styles each dialog window: title, chrome, size. Templates
  come from the host's position, because a dialog window doesn't inherit the
  owner's resources.
- When the host isn't inside a `Window` (for example in a `Popup`), it can't
  own dialog windows and logs event 1086 once.
- Closing a dialog (the close button, Alt+F4, or Esc with `CloseOnEscape`)
  goes Back in the region, or clears a single dialog. The dialog's guards run,
  and a veto keeps the window open.
- When the owner window closes, the host releases the windows it disabled and
  clears the region once.

A dialog window's `DataContext` is the entry's content, so the style can bind
the title to the ViewModel:

```xml
<rn:NavigationDialogHost Region="{Binding Dialog}">
  <rn:NavigationDialogHost.WindowStyle>
    <Style TargetType="Window">
      <Setter Property="Title" Value="{Binding Title}" />
      <Setter Property="ResizeMode" Value="NoResize" />
    </Style>
  </rn:NavigationDialogHost.WindowStyle>
</rn:NavigationDialogHost>
```

Return a result through the entry. A dialog pushed with
`PushForResult<TResult>` completes with `entry.CompleteAsync(value)` and
cancels with `entry.DismissAsync()`, using the `NavigationEntryContext` it
captured in `InitializeAsync`.

> [!WARNING]
> Don't set `Window.DialogResult` and don't rely on `IsCancel` or `IsDefault`
> buttons to close a dialog. `DialogResult` throws on a window shown with
> `Show()`, and these buttons only close windows shown with `ShowDialog()`.

### Asking before leaving

A guard can ask with a dialog in the dialog region through
`LeaveConfirmation.InDialog`. Only `Completed(true)` confirms:

```csharp
public sealed class DocumentViewModel : INavigationDepartureGuard
{
    private readonly LeaveConfirmation _leave;

    public DocumentViewModel(AppRegions regions) =>
        _leave = LeaveConfirmation.InDialog(regions.Dialog,
            () => NavigationTarget.Own<object>(new ConfirmViewModel("Discard the unsaved edits?")),
            () => IsDirty, DiscardChanges);

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
        _leave.CanDepartAsync(departure, token);
}
```

The guard runs on the UI thread, and so do its awaits. It doesn't pump. A
guard may also call `MessageBox.Show` and return the answer, but other
regions can then commit through the message box's nested loop.

## Threading

`DispatcherModelContext` treats all UI-thread code as inside a model turn:

- `InvokeAsync` on the UI thread runs the turn inline. From another thread it
  queues the turn on the dispatcher at the context's priority.
- Guards, `InitializeAsync`, `ResumeAsync`, factories and owned disposal run
  on the UI thread, each in its own dispatcher operation, so they can change
  models and touch controls directly.
- A turn must not pump: no `ShowDialog`, `MessageBox` or `.Wait()` inside
  `InvokeAsync`, a commit's `PropertyChanged` handler or an `OnCommitted`
  action. A turn that starts inside another turn's nested loop is logged once
  as event 1081.
- Never block the UI thread on navigation with `.Result` or `.Wait()`: the
  commit needs the dispatcher.
- A posted turn that throws goes to `UnhandledTurnException`, or is logged as
  event 1080. It never reaches `Dispatcher.UnhandledException`.
- The context closes when the dispatcher starts shutting down, and tells the
  navigator through its `Closed` token. Requests then end `Rejected(Closed)` at
  once, and transitions in flight are cancelled.
- A hook that is still awaiting when the dispatcher shuts down continues on
  the thread pool, so disposing the navigator doesn't hang. Turns that the
  close drops are reported on the thread that closes the context.

**Decorators must forward the hook scheduler and the close signal.** A context
that wraps `DispatcherModelContext` must implement `IRunicModelHookScheduler`
and `IRunicModelContextLifetime` and forward them. Otherwise hooks run on the
thread pool instead of the UI thread, and the navigator learns about a shutdown
only when a turn fails, so a request can wait behind a hook that ignores its
cancellation:

```csharp
public sealed class TracingContext(DispatcherModelContext inner)
    : IRunicModelContext, IRunicModelHookScheduler, IRunicModelContextLifetime
{
    // ... forward the IRunicModelContext members ...
    public CancellationToken Closed => inner.Closed;
    public Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken) =>
        inner.RunHookAsync(hook, cancellationToken);
}
```

## Disposal at exit

**Synchronous**, as in the quick start: `ServiceProvider.Dispose()` in
`OnExit`, also common in Generic Host WPF templates, calls
`RunicNavigator.Dispose()`. That starts the navigator's disposal and returns
without waiting, so owned cleanup may not finish before the process exits.

**Asynchronous**, when owned content must finish its cleanup: dispose the
provider after the dispatcher stops, so the context is closed and the navigator
retires the remaining entries at once. Do it after `app.Run()` in a custom
`Main` (set the `App.xaml` build action to `Page`), or through the Generic
Host's `StopAsync`:

```csharp
[STAThread]
public static void Main()
{
    var services = new ServiceCollection().AddRunicWpfNavigation() /* ... */.BuildServiceProvider();
    new App(services).Run();
    services.DisposeAsync().AsTask().GetAwaiter().GetResult(); // the dispatcher has stopped
}
```

Never block the UI thread on `DisposeAsync` while the dispatcher runs, for
example in `OnExit`: the navigator's turns need the dispatcher.

## ReactiveUI views

ReactiveUI apps need no extra package. A locator that asks ReactiveUI's
`ViewLocator` gives each entry its own View:

```csharp
public sealed class ReactiveNavigationViewLocator : INavigationViewLocator
{
    public FrameworkElement? ResolveView(INavigationEntry entry)
    {
        if (ViewLocator.Current.ResolveView(entry.Content) is not { } view) return null;
        view.ViewModel = entry.Content;
        return view as FrameworkElement;
    }
}
```

Set it as the host's `ViewLocator`, or register it as the
`INavigationViewLocator` singleton after `AddRunicWpfNavigation()`. Binding
`rxui:ViewModelViewHost` to `Main.Current` also works, but it reuses its View
across contents of the same type.

## Logging

The hosts log under the category `Runic.Navigation.Wpf`. `DispatcherModelContext`
logs through its `ILogger<DispatcherModelContext>`, so its category is the full
type name, `Runic.Navigation.Wpf.DispatcherModelContext`. Without an
`ILoggerFactory` or logger, warnings and errors go to
`System.Diagnostics.Trace`.

| Event | Level | Meaning |
| --- | --- | --- |
| 1080 | Error | A posted turn threw and the context has no `UnhandledTurnException` handler. |
| 1081 | Warning | A turn started inside another turn's nested message loop (once per context). |
| 1082 | Warning | No locator or `DataTemplate` presents a content type (once per type). |
| 1083 | Error | A dialog window failed to show; the host clears the dialog region once. |
| 1084 | Warning | A posted turn was dropped because the context closed, and it has no handler. |
| 1085 | Error | The `UnhandledTurnException` handler threw. |
| 1086 | Warning | A dialog host was loaded outside a `Window` (once per host). |

Events up to 1089 are reserved for the package.
