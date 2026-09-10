#!/usr/bin/env bash
# Run in the SDK's locked development shell. This is a Linux x64 test fixture.
set -euo pipefail
runtime_version=${1:?Pass the matching Microsoft.NETCore.App version from dotnet --list-runtimes}
[[ "$runtime_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid runtime version' >&2; exit 2; }
repo=$(cd "$(dirname "$0")/../../../.." && pwd)
work="$repo/artifacts/gtk4-flatpak"
mkdir -p "$work"
export NUGET_HTTP_CACHE_PATH="$repo/.cache/nuget-http"
cat > "$work/Download.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
  </PropertyGroup>
  <ItemGroup>
    <PackageDownload Include="Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64" Version="[$runtime_version]" />
  </ItemGroup>
</Project>
EOF
dotnet restore "$work/Download.csproj" --configfile "$repo/NuGet.config"
runtime="$repo/.cache/nuget/microsoft.netcore.app.runtime.nativeaot.linux-x64/$runtime_version/runtimes/linux-x64"
# Keep the pinned SDK/compiler, but link the matching portable target runtime.
# Nix's bundled runtime embeds Nix-specific ICU and OpenSSL dlopen paths.
dotnet publish "$repo/tests/native/Runic.Desktop.Gtk4.Smoke" -c Release -r linux-x64 \
  -p:PublishAot=true -p:RuntimeFrameworkVersion="$runtime_version" \
  -p:IlcFrameworkPath="$runtime/lib/" -p:IlcFrameworkNativePath="$runtime/native/" \
  -p:IlcSdkPath="$runtime/native/" -p:NativeIntermediateOutputPath="$work/native/" \
  -o "$work/publish"
cp "$work/publish/Runic.Desktop.Gtk4.Smoke" "$work/Runic.Desktop.Gtk4.Smoke"
# Only adapt the disposable test artifact, not the normal Nix publish output.
patchelf --set-interpreter /lib64/ld-linux-x86-64.so.2 --remove-rpath "$work/Runic.Desktop.Gtk4.Smoke"
printf 'Portable test executable: %s\n' "$work/Runic.Desktop.Gtk4.Smoke"
