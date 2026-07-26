# 01 — Hello lifecycle

**Difficulty:** Beginner

This is the smallest useful WebUIToolkit application. It teaches how the
hosting layer separates application startup, mode execution, and shutdown.
There is no package feed setup: the sample references the projects in `src/`
directly.

## Run it

From the repository root:

```console
dotnet run --project samples/01-HelloLifecycle
```

You will see a workspace start, a user-interface launch mode run, and the
workspace close again. The messages are application behavior, not test
assertions.

## Guided code tour

1. `Program.cs` creates `GenericHostWebUIToolkitApplicationBuilder`, the
   convenient bridge between .NET Generic Host and WebUIToolkit.
2. `WorkspaceParticipant` represents infrastructure with an owned lifecycle.
   Participants start in phase order and stop during application teardown.
3. `WelcomeMode` handles `LaunchKind.UserInterface`. A real desktop or web
   front end can replace this tiny console runner without changing the
   surrounding lifecycle.
4. `Build()` freezes the composition. `RunAsync()` then validates, starts,
   routes, and stops it.

## Try next

Add a second startup participant with phase
`ApplicationStartPhase.Integrations` and observe its start/stop ordering.
