# ReactiveUI G6 tests

This executable suite proves the Wave F ReactiveUI surface:

- ReactiveUI.SourceGenerators generated properties and commands are visible to
  the post-generator semantic compiler.
- typed command inputs and outputs cross the frozen MVVM v1 boundary;
- `CanExecute`, `IsExecuting`, scheduler delivery, and sanitized faults are
  projected without reflection;
- activation and every Rx subscription are disposed exactly once;
- the same closed adapter publishes and runs under trimming and Native AOT.
