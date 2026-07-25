# WebUIToolkit.MVVM.ReactiveUI

`WebUIToolkit.MVVM.ReactiveUI` maps explicit ReactiveUI properties and
`ReactiveCommand<TInput,TOutput>` instances onto the frozen
`webuitoolkit.mvvm/1` binding seam.

The adapter uses closed delegates and source-generated `JsonTypeInfo<T>` values.
It performs no reflection or dynamic discovery. Command results become typed
protocol payloads; `CanExecute`, `IsExecuting`, and `ThrownExceptions` are
observed on an explicit scheduler. An optional activation lease and every Rx
subscription are disposed exactly once with the web View.

The supported ReactiveUI range is 22.3.1 through 23.x. G6 exercises both range
endpoints, ReactiveUI.SourceGenerators 3.1.0 generated members, managed package
consumers, trimming, and Native AOT.

Publication remains blocked by ADR 0004.
