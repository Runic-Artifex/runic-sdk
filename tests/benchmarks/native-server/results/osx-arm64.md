osx-arm64, darwin 25.6.0, .NET SDK 10.0.403, macos26 20261013.0424.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.17 | 0.12 | 0.00 | NETCore.App, AspNetCore.App | 439.2 | 533.2 | 108.7 |
| fdd | Native | 10.41 | 0.12 | 0.00 | NETCore.App | 311.9 | 356 | 90.8 |
| sc | Kestrel | 120.46 | 0.12 | 22.81 | — | 277.5 | 334.3 | 108.8 |
| sc | Native | 93.02 | 0.12 | 0.00 | — | 200.3 | 226.1 | 90.9 |
| aot | Kestrel | 12.05 | 11.83 | 0.00 | — | 137.4 | 187.5 | 52.5 |
| aot | Native | 8.87 | 8.65 | 0.00 | — | 67.1 | 74.9 | 43.8 |


## S1: with the hello-world floor (CI, 2026-10-31)

`native-server.yml` platform job of PR #782 (S1). The hello-world rows are a program without Runic that prints its URL line as serve-only does. On this runner a NativeAOT hello world prints its URL after 8.7 ms, the Runic example on the native server after 77.1 ms: almost all of macOS's time to URL is Runic's, not process start.

osx-arm64, darwin 25.6.0, .NET SDK 10.0.403, macos26 20261026.0455.2; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) | ICU, OpenSSL loaded |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| fdd | hello world | 0.12 | 0.12 | 0.00 | NETCore.App | 53.4 | — | 36.6 | — |
| fdd | Kestrel | 9.23 | 0.12 | 0.00 | NETCore.App, AspNetCore.App | 251.2 | 307.9 | 109.1 | — |
| fdd | Native | 10.47 | 0.12 | 0.00 | NETCore.App | 297.3 | 341 | 91.3 | — |
| sc | hello world | 82.73 | 0.12 | 0.00 | — | 41.9 | — | 36.6 | — |
| sc | Kestrel | 120.51 | 0.12 | 22.81 | — | 359.9 | 429.8 | 109.3 | — |
| sc | Native | 93.07 | 0.12 | 0.00 | — | 341.4 | 389.7 | 91.3 | — |
| aot | hello world | 1.23 | 1.23 | 0.00 | — | 8.7 | — | 7 | — |
| aot | Kestrel | 12.15 | 11.93 | 0.00 | — | 129.2 | 160.2 | 53 | — |
| aot | Native | 8.98 | 8.76 | 0.00 | — | 77.1 | 101 | 45 | — |
