# Application Bridge

The Application Bridge is the public boundary between a .NET
application and its browser presentation. It describes application behavior,
not ViewModel shape.

```text
Application UI
    -> generated or inferred typed client
    -> Effect ApplicationBridge service
    -> one bounded CS-WebUI binary channel
    -> generated C# decoder and dispatcher
    -> annotated application members or Effect-generated handlers
    -> domain services and workflows
```

Commands use named tags such as `Navigate`,
`StartInstallation`, and `CancelOperation`. Long-running commands return a
receipt with an operation identifier; progress and completion arrive through a
validated event stream. TypeScript interruption does not imply backend
cancellation.

The backend owns workflows, permissions, navigation decisions, privileged
resource selection, persistence, and destructive confirmation. The frontend
owns presentation and transient interaction state. Both sides consume the same
committed contract artifacts.

The contract and compilation model is described in
[ADR 0019](../adr/0019-runic-bridge-ir-contract-toolchain.md). C# members are the
default authority; Effect remains an explicit whole-contract alternative.
Initialization is built-in snapshot plumbing. Session-scoped DI composition and
referenced source modules are generated without runtime reflection.
