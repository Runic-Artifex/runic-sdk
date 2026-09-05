# `@runic-artifex/application-bridge-tooling`

Compiles C# bridge members (the default for new applications) or an explicit
whole-contract Effect definition into canonical Runic Bridge IR and a frontend
facade. C# inspection uses the .NET SDK and the managed inspector shipped with
`dotnet runic`. Source generators do not write committed artifacts.

```sh
runic-bridge generate --authority csharp --project ../MyApp.csproj
runic-bridge check --authority csharp --project ../MyApp.csproj
runic-bridge watch --authority csharp --project ../MyApp.csproj
runic-bridge generate --authority effect --source src/application.bridge.ts
runic-bridge diff old.bridge.ir.json ../Contract/bridge.ir.json
```

Outputs default to `../Contract/bridge.ir.json` and
`src/application.bridge.generated.ts`, relative to the frontend package. Override
with `--ir` and `--facade`. Generation validates candidates before replacing
changed outputs; errors preserve the last-good files. Watch mode tracks imported
Effect files or the C# project graph. C# facades export generated Effect schemas
and decoded/encoded aliases. Effect facades retain the original schema objects.
Initialization is always built-in and snapshot-driven.
