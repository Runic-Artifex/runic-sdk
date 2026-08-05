# 03 — MVVM counter projection

**Difficulty:** Intermediate

This sample connects a familiar CommunityToolkit.Mvvm view model to
Runic Toolkit's session runtime. It shows what sits beneath a browser transport:
the client requests a snapshot, sends typed mutations, and receives small
revisioned patches to apply to its UI.

## Run it

From the repository root:

```console
dotnet run --project samples/03-MvvmCounterProjection
```

The output first shows the complete projected counter state. It then simulates
editing the value and pressing the increment button, printing the exact UI
changes each interaction produced.

## Guided code tour

1. `CounterViewModel.cs` is an ordinary CommunityToolkit view model.
   `[ObservableProperty]` generates `Count`; `[RelayCommand]` generates
   `IncrementCommand`.
2. `CounterJsonContext.cs` provides source-generated JSON metadata for the
   projected integer. No runtime reflection is required.
3. `Program.cs` registers the logical `samples.counter` contract. The adapter
   maps stable numeric member IDs to direct generated-property and command
   delegates.
4. `MvvmSessionRegistry` creates an independently owned view-model session.
   Each request observes a revision so clients can apply changes in order.
5. The snapshot contains all renderable members. Property and command requests
   return patches, which are the small changes a web or desktop renderer needs
   to apply.

## Try next

Add a `DecrementCommand`, bind it as member `3`, and print its command and
property patches. From there, the natural next step is an HTMX or JavaScript
transport that turns these same requests and patches into browser interactions.
