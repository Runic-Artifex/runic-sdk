# Window close confirmation

Use `DesktopWindowOptions.ConfirmCloseAsync` when closing a native window can discard
application state. The application owns the decision and its UI; Desktop owns native
interception and window lifetime. No viewmodel or CommunityToolkit adapter is involved.

```csharp
await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Embedded,
    ConfirmCloseAsync = async cancellationToken =>
    {
        // An application-specific function owned by the presentation's state layer.
        var answer = await surface.ExecuteJavaScriptAsync(
            "return await window.confirmUnsavedChanges?.() === true;",
            TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
        return answer == "true";
    },
});

// Application close buttons should use the same policy as the title-bar close button.
bool closed = await window.RequestCloseAsync(cancellationToken);
```

The [customer reference](../../../examples/customer-migration/README.md)
provides a complete draft-owned dialog through `--native` mode. A clean editor
approves immediately; a dirty editor offers Keep editing or Discard and close.
Escape denies closing. An active operation denies closing and asks the user to
finish or cancel it first. Missing or disconnected presentation state denies closing.

## Contract

- The native close event is suppressed immediately. The callback runs on the thread
  pool, so the OS event loop and bridge remain responsive while it awaits a dialog.
  Marshal any native UI work to its owning thread; JavaScript calls already use the
  authenticated presentation channel.
- `true` approves destruction; `false`, an exception or cancellation keeps the
  window open. Failures produce a redacted trace message. A later close request retries.
- Concurrent native and `RequestCloseAsync` attempts share the pending decision.
  Cancelling one request's caller token stops that caller's wait; it does not cancel
  another caller's decision. Forced shutdown cancels the callback's lifetime token.
- There is no automatic prompt deadline. A user may leave a confirmation unanswered.
  Bound external calls yourself: the reference's script call has a ten-minute timeout.
- `CloseAsync`, surface/host disposal and application shutdown **bypass confirmation**.
  They can release resources even if a callback ignores cancellation. A late approval
  cannot close a subsequently opened replacement window.
- This intercepts ordinary user close requests. It does not promise interception of
  OS session termination, process killing, crashes, or power loss. Durable drafts
  require a separate persistence policy.

## Platform and custom-host support

| Presentation            | Interception                                                       | Forced destruction                         |
| ----------------------- | ------------------------------------------------------------------ | ------------------------------------------ |
| Windows WebView2        | `WM_CLOSE` (also used by the existing WebView2 close notification) | `DestroyWindow` on the owning UI thread    |
| Linux GTK 3 / WebKitGTK | `delete-event` returns true while the decision runs                | `gtk_widget_destroy` on the GTK dispatcher |
| macOS WKWebView         | `windowShouldClose:` returns false while the decision runs         | `NSWindow.close` on the main thread        |
| Installed browser       | No native close confirmation capability                            | Existing browser lifecycle                 |

The native hooks follow the platform contracts for
[Windows close requests](https://learn.microsoft.com/en-us/windows/win32/learnwin32/closing-the-window),
[GTK delete-event](https://docs.gtk.org/gtk3/signal.Widget.delete-event.html), and
[AppKit windowShouldClose](https://developer.apple.com/documentation/appkit/nswindowdelegate/windowshouldclose%28_%3A%29).

Configured confirmation requires `BrowserKind.Embedded` without `EmbeddedThenBrowser`.
Rejecting unsupported configuration avoids silently dropping a data-loss guard.
`DesktopWindowCapabilities.CloseConfirmation` reflects the actual embedded host.

Existing custom hosts remain source compatible: `IDesktopWindowHost.SupportsCloseConfirmation`
defaults to false. To opt in, return true and honor `DesktopWindowHostOptions.CloseRequested`:
when it is set, suppress the native user close event and invoke the callback instead.
Keep `CloseAsync` unconditional and raise `Closed` after native destruction. An opted-out
host is rejected before opening when the application supplies a confirmation policy.

AppKit still requires a main-thread entry point and event pumping. The dedicated
Desktop smoke test provides that runner. The higher-level asynchronous
`DesktopApplicationHost` does not yet provide it, so the customer reference's native
mode currently targets Windows/Linux; browser mode remains available on macOS.

## Verification

Managed tests cover veto/retry, shared decisions, callback failure/cancellation,
caller cancellation, forced shutdown, stale window references, surface reuse,
custom-host opt-in and unsupported browser policies. The live customer browser test
covers clean/dirty decisions, Keep editing, Escape, discard and an active save.

`Runic.Desktop.WebViewSmoke` sends actual native close requests: `WM_CLOSE`,
`gtk_window_close` scheduled on the GTK thread, or AppKit `performClose:` on the main
thread. It checks bridge responsiveness during the pending decision, veto, retry and
approved destruction. Root CI runs this smoke on Linux x64, Windows x64 and macOS Apple Silicon. Local execution evidence is recorded in the
[reference verification record](../../../examples/customer-migration/VERIFICATION.md);
adding CI coverage is not evidence that a remote platform run has passed.

## Presentation-owned native work

Application Bridge scoped services can implement
`IApplicationPresentationLifetime` and register the same scoped instance under
that interface. Both Application transports invoke its `StopAsync()` before
waiting for in-flight commands; the DI scope is disposed after command drain.
The service must cancel pending UI, wait for acquired access release and reject
new work. Reconnect retains the service instance.

With an explicit embedded Desktop host and such hooks, Application composes its
close interception with `ConfirmCloseAsync`: an application veto keeps the scope
alive; approval drains presentation services before the native owner closes.
Application stop follows the same drain-before-destruction order. Browser fallback
cannot promise native close interception and must not expose an owned picker.

`DesktopWindow.SupportsNativeDispatch`, `CheckNativeAccess()` and
`DispatchNativeAsync(Action<nint>, CancellationToken)` expose the verified built-in
native owner boundary. The callback receives an HWND on Windows, GtkWindow on Linux
or NSWindow on macOS; these identities are not interchangeable. Queued cancellation
prevents invocation. Once a callback starts, its completion is observed even if the
caller cancels. Custom embedded hosts do not automatically gain this capability.
Never retain the handle beyond the owning presentation or send it to JavaScript.

On macOS use `ApplicationHost.Run()` from a synchronous process entry point, or
`DesktopEventLoop.Run(Func<Task>)` when composing Desktop directly. The loop remains
active through asynchronous cleanup. It does not migrate a worker thread into the
AppKit process main thread.
