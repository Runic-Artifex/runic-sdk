# Application Bridge

The Application Bridge is the planned public boundary between a .NET
application and its browser presentation. It describes application behavior,
not ViewModel shape.

```text
Application UI
    -> generated or inferred typed client
    -> Effect ApplicationBridge service
    -> one bounded CS-WebUI binary channel
    -> generated C# decoder and dispatcher
    -> explicit application handlers
    -> domain services and workflows
```

Commands use named tags such as `InitializeApplication`, `Navigate`,
`StartInstallation`, and `CancelOperation`. Long-running commands return a
receipt with an operation identifier; progress and completion arrive through a
validated event stream. TypeScript interruption does not imply backend
cancellation.

The backend owns workflows, permissions, navigation decisions, privileged
resource selection, persistence, and destructive confirmation. The frontend
owns presentation and transient interaction state. Both sides consume the same
committed contract artifacts.

The detailed decision and migration order are in
[ADR 0015](../adr/0015-effect-schema-application-bridge.md); the implementation
acceptance criteria remain tracked in
[issue #5](https://github.com/Runic-Artifex/runic-toolkit/issues/5).
