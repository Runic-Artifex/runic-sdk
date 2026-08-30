# Controlled native-shell capability certification

This is a local-only certificate for one observed Nix Linux/X64 managed-refusal
profile. It binds W70-001 and the pinned W50 native-shell consumer receipt,
then certifies only the deterministic unavailable result
`private-file-handler-streaming-unavailable` with absent WebView as actionable
capabilities. The pinned CS-WebUI ABI check passes and is recorded separately
from the managed WebView/streaming refusal. It makes no platform-success,
browser, file-read,
public-listener, cross-platform, publication, signing, or update claim.

```console
node eng/current-native-shell-certification/verify.mjs run-twice \
  --profile /path/to/controlled-nonpublic-profile.json \
  --freeze /path/to/w70-freeze.json \
  --native /path/to/w50-native-shell.json \
  --cs-webui /path/to/cs-webui
```
