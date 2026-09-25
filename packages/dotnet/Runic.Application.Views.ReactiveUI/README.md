# ReactiveUI integration probe

This assembly references ReactiveUI and the shared Bridge, with no
CommunityToolkit dependency. It offers typed logical `ReactiveRunicView<T>`
and `ReactiveRunicWindow<T>` classes implementing `IViewFor<T>`, a typed
ReactiveUI view locator, a `RoutingState` projection, and ViewModel activation
over an explicit web mount lease. The command descriptor awaits the command's
observable completion, error, or cancellation.

The console probe tests nested routers, a default and alternate View contract,
two Views sharing one ViewModel, replacement while mounted, a typed Window,
and command outcomes. It invokes `OnWebMounted` and `OnWebUnmounted` directly.
When the core uses `WindowContentSession.AttachPresentation`, every web outlet
receives its own `ReactiveRunicView` instance and therefore its own activation
lease; ReactiveUI keeps the shared ViewModel active until the final View
releases its lease.
The separate [Reactive Notes](../../apps/notes-reactive-views/README.md)
browser app sends those signals through generated page references and verifies
`WhenActivated` across two Views. Splat logger registration is supplied by
the app.
An app must also observe ReactiveUI `ThrownExceptions` for commands it creates.
The build coordinator now uses a separate ReactiveUI command inspector and
emits awaited observable invokers. It does not yet select build modules based
on which integration package the app installed.
