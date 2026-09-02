{
  description = "RunicToolkit development environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
    cs-webui.url = "github:Runic-Artifex/cs-webui";
  };

  outputs =
    { nixpkgs, cs-webui, ... }:
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
          inherit (pkgs) lib;
          dotnet = pkgs.dotnetCorePackages.sdk_10_0;
          bunArchive =
            if system == "x86_64-linux" then
              {
                platform = "linux-x64";
                hash = "sha256-Poy0vf7yJ/hzk33QiQj5gnshI5Q7dfbaMD7xgwiyDKw=";
              }
            else
              {
                platform = "linux-aarch64";
                hash = "sha256-rIfaywTWWN3ELVH9DtPfrkuAGjrwi7DJYUeKPS1Zd04=";
              };
          bunSource = pkgs.fetchzip {
            url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.0/bun-${bunArchive.platform}.zip";
            inherit (bunArchive) hash;
          };
          bun_1_4_0 = pkgs.runCommand "bun-1.4.0" { } ''
            mkdir -p "$out/bin"
            cp "${bunSource}/bun" "$out/bin/bun"
            chmod +x "$out/bin/bun"
          '';
          csWebUiNative = cs-webui.packages.${system}.webui-native;
          nativeLibraryName =
            if pkgs.stdenv.hostPlatform.isDarwin then
              "libwebui-2.dylib"
            else
              "libwebui-2.so";
          linuxRuntimePackages = with pkgs; lib.optionals pkgs.stdenv.hostPlatform.isLinux [
            chromium
            gtk3
            webkitgtk_4_1
            xvfb
          ];
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnet
              bun_1_4_0
              nodejs_24
              powershell

              # Required by the repository's Native AOT verification.
              clang
              zlib
            ] ++ linuxRuntimePackages;

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${dotnet}/share/dotnet";
            DisableImplicitLibraryPacksFolder = "true";

            shellHook = ''
              # Keep interactive restores inside the repository workspace.
              export NUGET_PACKAGES="$PWD/.direnv/nuget"
              export CSWEBUI_NATIVE_LIBRARY="${csWebUiNative}/lib/${nativeLibraryName}"
              ${lib.optionalString pkgs.stdenv.hostPlatform.isLinux ''
                export LD_LIBRARY_PATH="${lib.makeLibraryPath linuxRuntimePackages}:$LD_LIBRARY_PATH"
                export WEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        }
      );
    };
}
