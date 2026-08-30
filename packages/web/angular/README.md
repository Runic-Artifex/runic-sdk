# @runic-artifex/angular

Angular DI and signal projection for the framework-neutral Runic Application
Bridge. The package owns Angular lifecycle integration; the supplied Application
Bridge Layer remains the only Effect runtime owner.

```ts
bootstrapApplication(App, {
  providers: [provideApplicationBridge({
    controller: createApplicationBridgeController(
      CounterContract,
      ApplicationBridgeLive(CounterContract, createDesktopFrameChannel()),
    ),
    snapshotFromEvent: event => event._tag === "CounterChanged" ? event.snapshot : undefined,
  })],
});
```

Components inject the typed facade with `injectApplicationBridge()`. The same
provided controller can target Runic Desktop, CS-WebUI compatibility, a hosted
WebSocket, or a mock Layer; it does not bind Angular to a particular host or let
Angular own the controller runtime. `DestroyRef` releases Angular's event
subscription; the application composition that created the controller disposes
its Effect scope during application teardown.
