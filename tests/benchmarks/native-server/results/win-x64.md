win-x64, win32 10.0.26100, .NET SDK 10.0.403, win25-vs2026 20261021.278.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.44 | 0.16 | 0.00 | NETCore.App, AspNetCore.App | 206.7 | 261.6 | 50.1 |
| fdd | Native | 10.59 | 0.16 | 0.00 | NETCore.App | 167.2 | 209 | 40.9 |
| sc | Kestrel | 114.63 | 0.16 | 21.12 | — | 185.3 | 233.9 | 50.3 |
| sc | Native | 87.26 | 0.16 | 0.00 | — | 150 | 181.1 | 41 |
| aot | Kestrel | 13.93 | 13.31 | 0.00 | — | 29.4 | 33.6 | 21.4 |
| aot | Native | 9.44 | 9.19 | 0.00 | — | 13.5 | 18.7 | 13.6 |
