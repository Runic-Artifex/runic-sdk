# Comparative stress observation

This harness runs Runic Desktop and the maintained CS-WebUI baseline on the
same host with the same bounded HTTP workload. It retains raw per-request
latencies alongside startup time, measured-workload completion time,
throughput, managed allocations, peak working set, source revisions, managed
assembly hashes, the CS-WebUI native-library hash, and a host fingerprint.

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

The current maintained CS-WebUI server-only baseline faults while destroying
its native window on Linux. Its adapter records that limitation explicitly and
exits only after it has emitted the completed measurements. Runic Desktop uses
its normal graceful-disposal boundary.
