# `nix run github:Runic-Artifex/runic-sdk/v<version>#create -- MyApp --nix`: Runic.Create of this checkout's version
# (eng/Versions.props), run with `dotnet dnx` from the .NET SDK, Node and package managers that mkDevShell gives a
# project, so a machine without a .NET 10 SDK can create the project whose flake then provides one. dnx downloads
# Runic.Create, and the creator the matching Runic.Application.Templates, from nuget.org, as `dnx Runic.Create` does.
{ pkgs }:
let
  inherit (pkgs) lib;
  version = lib.head (
    builtins.match ".*<RunicSdkVersion>([^<]+)</RunicSdkVersion>.*" (builtins.readFile ../eng/Versions.props)
  );
  dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
in
pkgs.writeShellApplication {
  name = "runic-create";
  runtimeInputs = [
    dotnet-sdk
    pkgs.nodejs_24
    pkgs.pnpm
    pkgs.bun
  ];
  runtimeEnv = {
    DOTNET_ROOT = "${dotnet-sdk}/share/dotnet";
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
  };
  # The creator's arguments follow --, as with dnx Runic.Create@<version> -- <arguments>.
  text = ''
    exec dotnet dnx --yes "Runic.Create@${version}" -- "$@"
  '';
  meta = {
    description = "Create a Runic application with Runic.Create ${version}";
    mainProgram = "runic-create";
    platforms = lib.platforms.linux;
  };
}
