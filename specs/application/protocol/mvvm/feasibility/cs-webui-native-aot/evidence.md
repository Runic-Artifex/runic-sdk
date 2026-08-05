# Execution evidence index

The complete immutable pins, exact commands, actual external solution/project/API identifiers, selected logs, exit codes, and the two failed browser-driver attempts are preserved in [commands-and-results.txt](commands-and-results.txt).

The raw transcript uses a `.txt` extension deliberately: external `cs-webui` retains its real upstream identifiers in reproducible commands without presenting those identifiers as first-party RunicToolkit names to the repository namespace scanner.

The reusable high-level scenario is preserved as inert source transcripts under [probe](probe/README.md). Reproduction copies those transcripts to a temporary directory, supplies the pinned external checkout, publishes a Windows x64 native executable, and keeps a headless browser alive until the WebUI JavaScript bridge completes its managed callback.

Verified final outcome:

```text
READY http://localhost:17743
CALLBACK argument=native-aot client=0
PASS browser-to-managed-to-browser callback
process exit 0
```

An HTTP page load alone is not counted as success. Two short-lived DOM-dump browser attempts loaded the page but failed to establish the WebSocket callback before exiting; each probe timed out with exit `3`.
