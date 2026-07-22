# WebUIToolkit.CommandLine

`WebUIToolkit.CommandLine` is the BCL-only, `net10.0` command kernel. It keeps
parser and hosting framework types outside the public model while providing an
immutable catalog, deterministic portable syntax adapter, closed typed handler
execution, source-generated result-codec seams, and human/JSON output dispatch.

Catalogs are built explicitly with `CommandCatalogBuilder`. Every executable
command registers a closed options binder, handler factory, and result codec;
the runtime performs no assembly scanning, reflection activation, or arbitrary
object serialization. Invalid names, aliases, option spellings, arity layouts,
and missing registrations fail together in deterministic definition order.

`CommandExecutor` creates exactly one `ICommandExecutionScope` for every valid
parsed invocation. Cancellation and unexpected failures become stable semantic
outcomes, the scope is disposed exactly once before presentation, and the exit
policy is checked up front so only success can map to process exit code zero.

The package does not reference a third-party parser or Generic Host. Hosting
composition remains a Wave C adapter concern.

## Catalog diagnostics

Catalog validation reports all issues in registration order through stable IDs:

| ID | Meaning |
| --- | --- |
| `WUTCLI0002` | Invalid or reserved command name/alias |
| `WUTCLI0003` | Empty description localization key |
| `WUTCLI0004` | Duplicate sibling command spelling |
| `WUTCLI0005` | Invalid option ID |
| `WUTCLI0006` | Duplicate option ID |
| `WUTCLI0007` | Invalid or reserved option spelling |
| `WUTCLI0008` | Duplicate option spelling |
| `WUTCLI0009` | Invalid option repeat policy |
| `WUTCLI0010` | Missing closed options binder |
| `WUTCLI0011` | Missing closed handler factory |
| `WUTCLI0012` | Missing closed result codec |
| `WUTCLI0013` | Invalid argument name or ID |
| `WUTCLI0014` | Duplicate argument ID |
| `WUTCLI0015` | Required argument follows an optional argument |
| `WUTCLI0016` | Non-final argument has unbounded arity |
| `WUTCLI0017` | Result codec lacks a payload identity or generated metadata |
