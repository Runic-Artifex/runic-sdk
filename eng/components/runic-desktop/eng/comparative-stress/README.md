# Comparative stress observation

This harness runs Runic Desktop and the maintained CS-WebUI baseline on the
same host with the same bounded HTTP workload. It retains raw per-request
latencies alongside startup time, measured-workload completion time,
throughput, managed allocations, peak working set, source revisions, managed
assembly hashes, the CS-WebUI native-library hash, and a host fingerprint.
The current `/2` receipt host fingerprint includes the actual Node and .NET SDK
versions used to build and run the adapters. Historical `/1` observations
remain verifiable but are not accepted into the native certification matrix.
The maintained CS-WebUI source baseline is the exact revision in
`cs-webui-revision.txt`; a different checkout fails before measurement.

The result is an observation, not a release SLA or a cross-host benchmark. The
harness performs no publication, upload, tag, or release action. Both source
trees must be clean so the recorded revisions identify the measured code.

Run it from a shell that supplies .NET 10.0.302 or later and Node.js 24:

```sh
CSWEBUI_NATIVE_LIBRARY=/absolute/path/to/libwebui-2.so \
RUNIC_CS_WEBUI_REPOSITORY=/absolute/path/to/cs-webui \
bash eng/run-comparative-stress.sh artifacts/comparative-stress.json
```

Verify an existing receipt with:

```sh
node eng/comparative-stress/run.mjs verify artifacts/comparative-stress.json
```

Offline verification checks the closed supported host fingerprint recorded by
the receipt; it does not require the verifier to run on that same host. The
measurement path additionally requires the freshly produced receipt to match
the current host before it writes any evidence.

After the native workflow downloads all three hosted artifacts, it closes them
into one matrix receipt and verifies that receipt back against the raw files:

```sh
node eng/comparative-stress/verify-native-matrix.mjs run artifacts/native \
  "$DESKTOP_REVISION" "$CS_WEBUI_REVISION" > artifacts/native-matrix.json
node eng/comparative-stress/verify-native-matrix.mjs verify \
  artifacts/native artifacts/native-matrix.json
```

The matrix accepts exactly `win-x64`, `osx-x64`, and `osx-arm64`, requires one
workload and the exact Desktop/CS-WebUI revisions across all receipts, and
retains each raw receipt's digest. It remains observation evidence rather than
a cross-host performance comparison or release SLA.

The current maintained CS-WebUI server-only baseline faults while destroying
its native window on Linux. Its adapter records that limitation explicitly and
exits only after it has emitted the completed measurements. Runic Desktop uses
its normal graceful-disposal boundary.
