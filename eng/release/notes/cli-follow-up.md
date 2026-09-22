# CLI follow-up — 0.3.0-preview.1 historical record

> Historical follow-up. The changes described here were incorporated into the
> 0.3.0-preview.1 release train; this draft is retained for migration context and
> is not an outstanding release work list. Use [the current release policy](../README.md)
> for release procedure.

This draft describes the changes that were intended for the 0.3.0-preview.1
coordinated SDK preview. It is retained as historical context for the migration
from 0.2.0-preview.1.

- Define small CLIs with `CommandApp` and ordinary typed methods. String and
  no-result commands need no application JSON context or handwritten binder.
- Use generated command trees, shared options, environment fallbacks, enums,
  nullable inputs, repeated values, converters and validation.
- Get generated help, safe typo suggestions, hidden-entry metadata, file/directory
  hints, numeric bounds and option relationships from the same catalog.
- Customize a generated command's human result with `builder.Present<T>` while
  preserving its machine payload identity and JSON codec.
- Add the optional `Runic.CommandLine.Spectre` package for styled help, literal-safe
  output, progress and explicit prompts. The core has no Spectre dependency.
- Hosted applications get the same presentation and validation while retaining
  their own services, lifetime token and UI launch decisions.
- First-party .NET tools use the shared runner/presentation. `runic-bridge` uses
  Effect CLI in Node/Bun, with useful help, examples and completion.

After publication, update the Runic packages together to the selected SDK preview;
add `Runic.CommandLine.Spectre` at that same version if using its presenter.
See the package README and `examples/command-line` for complete runnable sources.

## Migration and boundaries

Non-nullable scalar options without a C# default are required; use a nullable
parameter or explicit default for optional values. Global options that reuse local
definitions must have matching names, arity and alias sets. A root command or
alias named `completion` retains ownership rather than being intercepted.

Framework presentation now honors cancellation and reports nonfatal failures to
the private observer. A failed output stream is not retried and may contain a
partial frame; callers must check the process exit code. Global `System.Console`
writes remain outside invocation-console isolation.

Completions provide static catalog candidates and filesystem hints for path
options, not full command-context filtering or remote completion. Hidden entries
are omitted from discovery, not protected by authorization. File checks do not
replace safe application IO or grant filesystem permissions. Native GTK and portal changes are documented separately; their platform-specific
validation must not be inferred from these CLI checks.
