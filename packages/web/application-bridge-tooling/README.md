# `@runic-artifex/application-bridge-tooling`

Compiles a handwritten Effect Schema Application Bridge definition into the
canonical Runic Bridge IR consumed by the .NET source generator.

```sh
runic-bridge generate
runic-bridge check
runic-bridge watch
runic-bridge diff old.bridge.ir.json Contract/bridge.ir.json
```

The default paths are `src/application.bridge.ts`, `../Contract/bridge.ir.json`,
and `src/application.bridge.generated.ts`, relative to the current frontend
package. Override them with `--source`, `--ir`, and `--facade`.
