# Managed native-shell consumer

This is the bounded W50 package-consumer proof for the current Linux CS-WebUI
profile. It consumes a directly packed `CsWebUi`/`CsWebUi.Native` feed and a
directly published Runic Translations Editor directory plus its archive. No
product project reference, browser launch, public listener, or browser visual
test is permitted.

Run the isolated double journey with:

```console
RUNIC_W50_NATIVE_SHELL_CSWEBUI_FEED=/path/to/feed \
RUNIC_W50_NATIVE_SHELL_CSWEBUI_VERSION=2.5.0-beta.4.5 \
RUNIC_W50_NATIVE_SHELL_EDITOR_DIRECTORY=/path/to/Runic.Translations.Editor \
RUNIC_W50_NATIVE_SHELL_EDITOR_ARCHIVE=/path/to/Runic.Translations.Editor.tar.gz \
node eng/current-native-shell/verify.mjs run-twice > native-shell-receipt.json
node eng/current-native-shell/verify.mjs verify-twice native-shell-receipt.json
```

The receipt binds all supplied artifact hashes and the exact observed host/runtime
facts. In the current pinned native library profile it requires the explicit
`private-file-handler-streaming-unavailable` refusal while preserving evidence
that the managed Editor configured a private loopback listener, exact loopback
origin, generated bridge identity, high-contrast propagation, WebView capability,
and cleanup. It does not claim that an asset was read, a browser was launched, or
any W70 certification occurred.
