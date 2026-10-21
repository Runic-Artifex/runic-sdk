# Runic Assets tests

TUnit contract tests for safe paths, deterministic
manifests, portable archives, embedded and development-directory sources,
CS-WebUI and ASP.NET Core adapters, caching metadata, cancellation, and the
framework-neutral core assembly boundary.

`DevelopmentDirectoryTests` covers the development-directory source and its
watcher on Linux, Windows and macOS: the managed CI job runs it on Linux and
the native job on Windows and macOS
(`--treenode-filter "/*/*/DevelopmentDirectoryTests/*"`). The other classes
(archives, the packer) need Linux no-follow directory handles.
