# Messages: Kestrel and the native server (linux-x64)

`tests/benchmarks/Runic.Desktop.ServerBenchmarks` (BenchmarkDotNet, in-process, Release): one Runic Desktop surface on
either server and a page-side `ClientWebSocket` that speaks the surface's packet profile, as `runic-desktop-views.js`
does. The handler replies with a fixed message, so the producer is not measured. Microseconds per request or push.

NixOS linux-x64, .NET SDK 10.0.403, Rust 1.98.1, 2026-10-31, on a shared 24-thread workstation with a load average of
6–10 from other work, so single runs vary by up to ±15 % (more for 256 KiB). Each figure is the median of the runs' own
medians:

- before: X2's native server (main `441b1e9e0`), 2 full runs;
- after: X3, 3 full runs;
- Kestrel: the same code in all 5 runs.

"After vs Kestrel" compares the after runs with the Kestrel rows of the same runs.

| Benchmark | Message | Kestrel | Native before | Native after | After vs Kestrel |
| --- | ---: | ---: | ---: | ---: | ---: |
| Request round trip (latency) | 256 B | 21.7 | 28.4 | 20.9 | −3 % |
| Request round trip (latency) | 16 KiB | 47.2 | 50.9 | 37.9 | −17 % |
| Request round trip (latency) | 256 KiB | 384 | 454 | 269 | −34 % |
| 64 requests in flight, per request | 256 B | 7.69 | 8.36 | 6.66 | −13 % |
| 64 requests in flight, per request | 16 KiB | 19.8 | 16.9 | 14.1 | −29 % |
| 64 requests in flight, per request | 256 KiB | 244 | 206 | 123 | −50 % |
| 64 pushes, per push | 256 B | 1.06 | 3.93 | 0.97 | −8 % |
| 64 pushes, per push | 16 KiB | 7.06 | 7.60 | 3.97 | −44 % |
| 64 pushes, per push | 256 KiB | 99.6 | 50.3 | 51.2 | −49 % |

The 256 KiB round trip was the row that needed the most work. Run on its own (`RUNIC_BENCHMARK_SIZES=262144`, one
server per process, alternating, 3 pairs), it measured 370 µs on Kestrel and 307 µs on the native server (medians;
Kestrel 354–428, native 288–331). Before the exact-size receive (`IWholeMessageSocket`), the same pairs measured 340
against 372 µs.

Windows and macOS: `native-server.yml`'s platform legs measure size and startup, not these rows, so no CI figures exist
for them; the Desktop suite runs on the native server on both.

Reproduce with `eng/native/build-runic-server.sh`, then
`dotnet run -c Release --project tests/benchmarks/Runic.Desktop.ServerBenchmarks -- --filter '*'`.
`RUNIC_BENCHMARK_SERVERS` and `RUNIC_BENCHMARK_SIZES` (comma-separated) select cases.
