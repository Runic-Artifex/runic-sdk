# Controlled clean-room profile conformance

This external-consumer linker accepts only the W70-001 frozen profile and
receipt artifacts. Each journey copies those explicit JSON inputs into a fresh
temporary directory, verifies the existing package-consumer receipts, and
emits a deterministic aggregate. It never opens a package source, browser,
network endpoint, or product project.

The linkage is deliberately narrow: direct `dotnet-runic` migration is the
frozen C# host/tool evidence; W20's package host-transport receipt plus the
frozen structural Bridge evidence prove the local Bridge; W60 Editor staging
plus W40's closed MF2/XLIFF/review-sidecar receipt prove the Editor; and the
W40 hosted package receipt plus W30 rollout authority prove D008 SSR and
hydration locale behavior. It is not publication, deployment, an update path,
or a native-platform certification claim.

```console
node eng/current-controlled-clean-room/verify.mjs run-twice \
  --profile /path/to/controlled-nonpublic-profile.json \
  --freeze /path/to/w70-freeze.json \
  --csharp /path/to/w60-tool.json \
  --bridge /path/to/w20-host-transport.json \
  --bridge-quality /path/to/w50-quality.json \
  --editor /path/to/w60-editor-candidate.json \
  --localization /path/to/w40-localization.json \
  --portable /path/to/w40-mf2.json --hosted /path/to/w40-hosted.json \
  --desktop /path/to/w40-desktop.json --rollout /path/to/w30-rollout.json
```
