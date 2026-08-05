# RunicToolkit MVVM binding compiler

`RunicToolkit.MVVM.BindingCompiler` is the deterministic command-line host for the
`RunicToolkit.MVVM.Build` compiler contract. The package is a .NET tool and contains
no runtime binding interpreter.

## Install

```console
dotnet tool install --global RunicToolkit.MVVM.BindingCompiler --version 1.0.0
```

## Commands

```text
runic-toolkit-bindings compile [--output <file>|-] <bindings.rtkmvvm> [...]
runic-toolkit-bindings validate <bindings.rtkmvvm> [...]
runic-toolkit-bindings --version
```

`--version` writes the tool package version as a single line.

`compile` writes the generated C# contract to standard output unless `--output`
names a file. A changed file is replaced atomically only after compilation succeeds;
an existing byte-identical file is left untouched so its timestamp remains stable.
`validate` performs the same parsing and semantic checks without writing generated
code. Input files are normalized relative to the current directory and sorted with
ordinal comparison, so argument order does not affect output.

Compiler diagnostics are written to standard error in deterministic MSBuild form,
including the complete one-based half-open source range:

```text
views/customer.rtkmvvm(3,8,3,21): error RTKMVVM1001: diagnostic message
```

Exit code `0` means success, `1` means one or more `RTKMVVM` compilation errors,
and `2` means invalid arguments, inaccessible input/output, invalid UTF-8, or an
input limit violation. Generated code is never written when compilation fails.

## Input and safety limits

- At most 512 command-line arguments and 256 input files are accepted.
- Each argument is limited to 32,768 characters.
- Each UTF-8 input is limited to 1 MiB; all inputs together are limited to 16 MiB.
- A UTF-8 BOM is accepted and removed. Malformed UTF-8 is rejected.
- Input paths lexically outside the current project directory, logical paths longer
  than 1,024 characters, control characters, and duplicate normalized paths are
  rejected. Prefix a path beginning with `-` by the `--` option terminator.
- Response-file expansion is intentionally unsupported.

The tool package and its `RunicToolkit.MVVM.Build` dependency are versioned together
at `1.0.0`. Committed package locks are portable and do not contain runtime-specific
dependency graphs.
