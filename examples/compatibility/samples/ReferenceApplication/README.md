# Package-only reference application

This executable is the Wave G neutral reference consumer. It contains no project
references and is intentionally excluded from the repository solution: the G7
verifier restores it only from the isolated NuGet feed produced from packed
WebUIToolkit artifacts.

The application runs seven independent scenarios concurrently:

- deterministic Hosting startup and shutdown;
- MVVM mutation, acknowledgement, reconnect snapshot replacement, and exact-once disposal;
- Flow navigation, back-stack, presentation, and operation completion;
- compiled Text Resources with a locale transition;
- parser-neutral typed command-line invocation;
- bounded dependency-notice loading;
- framework-neutral WebUi asset selection and streaming.

Run it through the release rehearsal:

```powershell
./eng/verify-wave-g.ps1
```

Direct restore is not supported because the `1.0.0` packages exist only in the
isolated rehearsal feed until the publication hold is resolved.
