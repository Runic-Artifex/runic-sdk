{
  description = "Managed .NET runtime for web-powered desktop applications";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { nixpkgs, ... }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = function:
        nixpkgs.lib.genAttrs systems (system:
          function (import nixpkgs {
            inherit system;
            config.allowUnfree = true;
          }));
    in
    {
      devShells = forAllSystems (pkgs:
        let
          linuxRuntimePackages = with pkgs; [
            chromium
            gtk3
            webkitgtk_4_1
            xvfb-run
          ];
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              curl
              dotnet-sdk_10
              git
            ] ++ linuxRuntimePackages;

            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1
              export NUGET_PACKAGES="$PWD/.nuget/packages"
              export LD_LIBRARY_PATH="${pkgs.lib.makeLibraryPath linuxRuntimePackages}:$LD_LIBRARY_PATH"
              export WEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
            '';
          };
        });
    };
}
