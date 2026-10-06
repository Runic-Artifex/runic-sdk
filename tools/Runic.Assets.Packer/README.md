# Runic.Assets.Packer

`Runic.Assets.Packer` writes a directory, usually a frontend build output such
as Vite's `dist`, to a canonical Runic Assets archive. The archive format is
specified in [archive-v1](../../specs/assets/archive-v1.md).

Most applications never run the packer themselves. Set `RunicAssetsDist` in the
application project and `dotnet build` runs it and embeds the archive (see the
[Runic.Assets README](../../packages/dotnet/Runic.Assets/README.md#embed-a-vite-build)).
Run the packer directly only when the archive is produced in a separate step,
for example:

- a CI job that builds the frontend once and passes the archive to several
  .NET builds, which embed it with `RunicAssetsEmbeddedArchive`;
- an application that loads an archive file at run time with
  `AssetArchive.Read`;
- inspecting exactly what the build would embed.

## Run it

The packer ships inside the `Runic.Assets` NuGet package and runs on the .NET 10
runtime. After a restore it is in the NuGet package folder:

```sh
packer="$HOME/.nuget/packages/runic.assets/<VERSION>/tools/net10.0/Runic.Assets.Packer.dll"
dotnet "$packer" Client.Web/dist artifacts/app.runic-assets --trusted-generated-output
```

Use the folder from `dotnet nuget locals global-packages --list` if
`NUGET_PACKAGES` points elsewhere. Inside this repository, build
`tools/Runic.Assets.Packer` and use
`tools/Runic.Assets.Packer/bin/<Configuration>/net10.0/Runic.Assets.Packer.dll`.
MSBuild finds the packaged copy itself; set `RunicAssetsPackerPath` to use
another build.

```text
Runic.Assets.Packer [pack] <source-directory> <destination-archive> [options]
```

`pack` is the default command and may be omitted.

| Argument or option | Meaning |
| --- | --- |
| `<source-directory>` | Directory to package. Every regular file below it becomes an asset. |
| `<destination-archive>` | Archive to write. The packer writes a temporary file next to it and then replaces it. If the destination is inside the source directory, it is excluded automatically. |
| `--entry-point <path>` | Entry document relative to the source directory. Defaults to `index.html`. It must exist and must not be excluded. |
| `--exclude <paths>` | Asset paths to omit, separated by semicolons. Repeat the option to add more. Paths use `/` and are relative to the source directory. |
| `--trusted-generated-output` | Read the directory as output generated earlier in the same trusted build. See below. |
| `--output human\|json` | Output format. JSON writes one `runic.commandline/1` result with the `runic.assets.pack-result/1` payload `{ "ArchiveLength": <bytes> }`. |
| `--help`, `--version` | Show help or the version. |

The archive is deterministic: the same files produce the same bytes, so it can
be cached or compared with `cmp`.

## `--trusted-generated-output`

Without this option the packer opens every directory and file through pinned
Linux handles. A file or directory that is swapped for a symbolic link while the
packer runs cannot redirect it outside the source directory. This mode requires
Linux; on other systems the packer fails with `RAS1004`.

With `--trusted-generated-output` the packer uses ordinary file APIs and works
on Linux, Windows and macOS. It still rejects symbolic links and reparse points
that it sees, but it assumes that nobody changes the directory while it runs.
The MSBuild integration always uses this mode, because the directory is output
of the same build.

Use the option when the source directory was produced by your own build or CI
job and no other process can write to it. Leave it off on Linux when the
directory can be modified by a less trusted process, such as a shared upload or
a directory another user can write.

## Exit codes and errors

| Exit code | Meaning |
| --- | --- |
| `0` | The archive was written. |
| `2` | Invalid command line, such as a missing argument. |
| `3` | The source directory does not exist (`RAS1001`). |
| `4` | The entry point does not exist or was excluded (`RAS1002`). |
| `5` | Packing failed (`RAS1003`), for example because of an invalid asset path, a symbolic link or an I/O error, or the pinned mode is unavailable on this OS (`RAS1004`). |

Human output writes the result to standard output and the error message to
standard error. A failed run leaves an existing destination archive unchanged.
