win-x64, win32 10.0.26100, .NET SDK 10.0.403, win25-vs2026 20261021.278.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.44 | 0.16 | 0.00 | NETCore.App, AspNetCore.App | 206.7 | 261.6 | 50.1 |
| fdd | Native | 10.59 | 0.16 | 0.00 | NETCore.App | 167.2 | 209 | 40.9 |
| sc | Kestrel | 114.63 | 0.16 | 21.12 | — | 185.3 | 233.9 | 50.3 |
| sc | Native | 87.26 | 0.16 | 0.00 | — | 150 | 181.1 | 41 |
| aot | Kestrel | 13.93 | 13.31 | 0.00 | — | 29.4 | 33.6 | 21.4 |
| aot | Native | 9.44 | 9.19 | 0.00 | — | 13.5 | 18.7 | 13.6 |


## S1: with the hello-world floor (CI, 2026-10-31)

`native-server.yml` platform job of PR #782 (S1). The hello-world rows are a program without Runic that prints its URL line as serve-only does. A NativeAOT hello world takes 11.1 ms here, the Runic example on the native server 17.2 ms.

win-x64, win32 10.0.26100, .NET SDK 10.0.403, win25-vs2026 20261021.278.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) | ICU, OpenSSL loaded |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| fdd | hello world | 0.16 | 0.15 | 0.00 | NETCore.App | 62.8 | — | 22 | — |
| fdd | Kestrel | 9.49 | 0.16 | 0.00 | NETCore.App, AspNetCore.App | 331.1 | 424.5 | 50.3 | — |
| fdd | Native | 10.64 | 0.16 | 0.00 | NETCore.App | 256.8 | 310.1 | 41 | — |
| sc | hello world | 76.84 | 0.15 | 0.00 | — | 63 | — | 21.9 | — |
| sc | Kestrel | 114.69 | 0.16 | 21.12 | — | 317.1 | 398.8 | 50.3 | — |
| sc | Native | 87.32 | 0.16 | 0.00 | — | 260.4 | 311.9 | 40.9 | — |
| aot | hello world | 1.07 | 1.07 | 0.00 | — | 11.1 | — | 7.1 | — |
| aot | Kestrel | 14.08 | 13.45 | 0.00 | — | 42.5 | 49 | 21.3 | — |
| aot | Native | 9.57 | 9.31 | 0.00 | — | 17.2 | 24.3 | 13.1 | — |
