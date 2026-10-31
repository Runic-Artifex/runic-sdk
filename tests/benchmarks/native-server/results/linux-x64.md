linux-x64, linux 7.3.0, .NET SDK 10.0.403; startup medians of 15 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.14 | 0.10 | 0.00 | NETCore.App, AspNetCore.App | 170.7 | 214.5 | 82.3 |
| fdd | Native | 10.64 | 0.10 | 0.00 | NETCore.App | 134 | 160.9 | 65.6 |
| sc | Kestrel | 113.71 | 0.10 | 20.47 | — | 167.2 | 208.4 | 80.1 |
| sc | Native | 89.39 | 0.10 | 0.00 | — | 129.6 | 154.3 | 63.6 |
| aot | Kestrel | 13.16 | 12.96 | 0.00 | — | 17.4 | 21.4 | 33.4 |
| aot | Native | 10.16 | 9.97 | 0.00 | — | 9 | 12.8 | 24.2 |


## S1: startup without OpenSSL and ICU, and the hello-world floor (2026-10-31)

Local runs of `measure.mjs` (`RUNIC_MEASURE_MODES="aot fdd"`, 21 runs) on the same machine, before (main `11b487a93`)
and after S1. The hello-world rows are a program without Runic that prints its URL line as serve-only does
(`hello-world/`): the rest of a row's "URL ready" is Runic's. In a UTF-8 locale, .NET's console loads ICU for its
charset lookup in any program that writes to it, serve-only included, so the second table runs with `LANG=C` to show
what the host itself loads. On this NixOS machine a framework-dependent process maps ICU and OpenSSL when it starts
(the Nix-packaged runtime links them), so their column means nothing for `fdd` here.

`de_DE.UTF-8`:

| Code | Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) | ICU, OpenSSL loaded |
| --- | --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| before | aot | hello world | 1.26 | 1.26 | 0.00 | — | 4.6 | — | 8.9 | ICU |
| before | aot | Kestrel | 13.21 | 13.02 | 0.00 | — | 18.4 | 22.6 | 33.5 | ICU, OpenSSL |
| before | aot | Native | 10.21 | 10.02 | 0.00 | — | 9.9 | 13.8 | 24.2 | ICU, OpenSSL |
| before | fdd | hello world | 0.11 | 0.10 | 0.00 | NETCore.App | 24.8 | — | 34.3 | ICU, OpenSSL |
| before | fdd | Kestrel | 9.17 | 0.10 | 0.00 | NETCore.App, AspNetCore.App | 190 | 244.1 | 82.4 | ICU, OpenSSL |
| before | fdd | Native | 10.66 | 0.10 | 0.00 | NETCore.App | 154.7 | 189.5 | 65.7 | ICU, OpenSSL |
| after | aot | hello world | 1.26 | 1.26 | 0.00 | — | 3.7 | — | 8.9 | ICU |
| after | aot | Kestrel | 13.22 | 13.02 | 0.00 | — | 15.3 | 19.6 | 28.7 | ICU |
| after | aot | Native | 10.22 | 10.03 | 0.00 | — | 8.6 | 12.1 | 19.1 | ICU |
| after | fdd | hello world | 0.11 | 0.10 | 0.00 | NETCore.App | 23.5 | — | 34.3 | ICU, OpenSSL |
| after | fdd | Kestrel | 9.17 | 0.10 | 0.00 | NETCore.App, AspNetCore.App | 178.8 | 225.7 | 78.9 | ICU, OpenSSL |
| after | fdd | Native | 10.67 | 0.10 | 0.00 | NETCore.App | 141.4 | 171 | 62.1 | ICU, OpenSSL |

`LANG=C`, NativeAOT:

| Code | Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) | ICU, OpenSSL loaded |
| --- | --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| before | aot | hello world | 1.26 | 1.26 | 0.00 | — | 2.9 | — | 4.1 | neither |
| before | aot | Kestrel | 13.21 | 13.02 | 0.00 | — | 18.2 | 21.9 | 33.4 | ICU, OpenSSL |
| before | aot | Native | 10.21 | 10.02 | 0.00 | — | 9.5 | 12.6 | 24.2 | ICU, OpenSSL |
| after | aot | hello world | 1.26 | 1.26 | 0.00 | — | 3.1 | — | 4 | neither |
| after | aot | Kestrel | 13.22 | 13.02 | 0.00 | — | 15.9 | 19.7 | 28.4 | ICU |
| after | aot | Native | 10.22 | 10.03 | 0.00 | — | 6.7 | 10.1 | 14.4 | neither |

ReadyToRun (framework-dependent, native server, after S1; `startup.mjs`, 21 runs, the same session): URL ready
139.7 → 93.7 ms, page served 165.8 → 114.0 ms, RSS 62.1 → 62.3 MiB, publish 14.7 → 24.8 MiB.


## S1: with the hello-world floor (CI, 2026-10-31)

`native-server.yml` platform job of PR #782 (S1). The hello-world rows are a program without Runic that prints its URL line as serve-only does.

linux-x64, linux 7.0.0-1017-azure, .NET SDK 10.0.403, ubuntu24 20261026.354.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) | ICU, OpenSSL loaded |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| fdd | hello world | 0.08 | 0.07 | 0.00 | NETCore.App | 27.8 | — | 29.4 | ICU |
| fdd | Kestrel | 9.14 | 0.07 | 0.00 | NETCore.App, AspNetCore.App | 202.6 | 264.4 | 72.5 | ICU |
| fdd | Native | 10.63 | 0.07 | 0.00 | NETCore.App | 165 | 199.6 | 57.5 | ICU |
| sc | hello world | 78.83 | 0.07 | 0.00 | — | 27.5 | — | 29.6 | ICU |
| sc | Kestrel | 113.71 | 0.07 | 20.47 | — | 184.8 | 238.9 | 73.5 | ICU |
| sc | Native | 89.37 | 0.07 | 0.00 | — | 157.9 | 191.6 | 58.2 | ICU |
| aot | hello world | 1.26 | 1.26 | 0.00 | — | 4.5 | — | 8.3 | ICU |
| aot | Kestrel | 13.25 | 13.08 | 0.00 | — | 21.2 | 29.6 | 27.6 | ICU |
| aot | Native | 10.19 | 10.02 | 0.00 | — | 11.4 | 16.1 | 18.8 | ICU |
