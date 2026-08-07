# Setup Application

This neutral desktop Setup wizard is the reference vertical for the official
Runic Toolkit Application Bridge. It consumes only published NuGet and npm
packages—there are no project references into the toolkit repository.

The frontend owns presentation state in React. Effect Schema defines every wire
value, one managed Effect runtime owns the connection, and an Effect Stream
delivers validated host events. The C# generator reads only the committed JSON
Schemas and canonical manifest. Application handlers own navigation policy,
opaque destination selection, installation work, progress, and cancellation.

Run against the native host:

```bash
npm install
npm run verify
dotnet run --project samples/03-SetupApplication
```

Run the deterministic host-only acceptance path:

```bash
dotnet run --project samples/03-SetupApplication -- --smoke-test
```

Run the frontend without a native host:

```bash
npm run dev:mock --workspace @runic-artifex/setup-application
```
