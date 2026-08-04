{
  description = "Runic Command Line development environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  };

  outputs =
    { nixpkgs, ... }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
    in
    {
      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
          dotnet = pkgs.dotnetCorePackages.sdk_10_0;
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnet
              nodejs_24
              powershell

              # Required by the repository's Native AOT verification.
              clang
              zlib
            ];

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${dotnet}/share/dotnet";
            DisableImplicitLibraryPacksFolder = "true";

            shellHook = ''
              # Keep interactive restores separate from the verification scripts,
              # which intentionally build several different packages at version 1.0.0.
              export NUGET_PACKAGES="$PWD/.direnv/nuget"
            '';
          };
        }
      );
    };
}
