{
  description = "Modern .NET bindings for WebUI";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    webui = {
      # Pin upstream main so native builds and managed examples are reproducible.
      url = "github:webui-dev/webui/52f9e75b92faf9a23fd150b3c60051c4ec85fc69";
      flake = false;
    };
  };

  outputs = { self, nixpkgs, webui }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];

      forAllSystems = f:
        nixpkgs.lib.genAttrs systems (system:
          f (import nixpkgs {
            inherit system;
            config.allowUnfree = true;
          }));
    in
    {
      packages = forAllSystems (pkgs:
        let
          webuiNative = pkgs.stdenv.mkDerivation {
            pname = "webui-2";
            version = "2.5.0-beta.4";
            src = webui;

            nativeBuildInputs = [
              pkgs.cmake
              pkgs.ninja
            ];

            cmakeFlags = [
              "-DBUILD_SHARED_LIBS=ON"
              "-DWEBUI_BUILD_EXAMPLES=OFF"
              "-DBUILD_TESTING=OFF"
              "-DWEBUI_OUT_LIB_NAME=webui-2"
              "-DWEBUI_USE_TLS=OFF"
            ];

            meta = with pkgs.lib; {
              description = "WebUI native shared library used by CS-WebUI";
              homepage = "https://webui.me/";
              license = licenses.mit;
              platforms = platforms.unix;
            };
          };
          webuiStressC = pkgs.stdenv.mkDerivation {
            pname = "webui-stress-c";
            version = "2.5.0-beta.4";
            src = webui;

            nativeBuildInputs = [ pkgs.gnumake ];

            buildPhase = ''
              runHook preBuild
              make -C examples/C/stress_test BUILD_LIB=true CC=gcc
              runHook postBuild
            '';

            installPhase = ''
              runHook preInstall
              mkdir -p "$out/bin"
              cp examples/C/stress_test/main "$out/bin/webui-stress-c"
              runHook postInstall
            '';
          };
        in
        {
          webui-native = webuiNative;
          webui-stress-c = webuiStressC;
          default = webuiNative;
        });

      checks = forAllSystems (pkgs: {
        webui-native = self.packages.${pkgs.stdenv.hostPlatform.system}.webui-native;
        abi = pkgs.runCommand "cswebui-abi" {
          nativeBuildInputs = [
            pkgs.bash
            pkgs.coreutils
            pkgs.diffutils
            pkgs.gnugrep
            pkgs.gnused
            pkgs.git
            pkgs.python3
          ];
        } ''
          ${pkgs.bash}/bin/bash ${self}/eng/prepare-experimental-webui-header.sh ${self}/eng/abi/experimental-base-webui.h $TMPDIR/experimental-webui.h
          ${pkgs.bash}/bin/bash ${self}/eng/validate-webui-abi.sh --official ${webui}/include/webui.h --experimental $TMPDIR/experimental-webui.h --check
          touch "$out"
        '';
      });

      devShells = forAllSystems (pkgs:
        let
          inherit (pkgs) lib;
          webuiNative = self.packages.${pkgs.stdenv.hostPlatform.system}.webui-native;
          isLinux = pkgs.stdenv.hostPlatform.isLinux;
          nativeLibraryName = if pkgs.stdenv.hostPlatform.isDarwin
            then "libwebui-2.dylib"
            else "libwebui-2.so";
          linuxRuntimePackages = with pkgs; lib.optionals isLinux [
            chromium
            gtk3
            webkitgtk_4_1
            xvfb-run
          ];
          linuxLibraryPath = lib.makeLibraryPath linuxRuntimePackages;
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              clang
              cmake
              curl
              dotnet-sdk_10
              git
              jq
              ninja
              pkg-config
              unzip
            ] ++ linuxRuntimePackages;

            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1
              export NUGET_PACKAGES="$PWD/.nuget/packages"
              export CSWEBUI_NATIVE_LIBRARY="${webuiNative}/lib/${nativeLibraryName}"
              ${lib.optionalString isLinux ''
                export LD_LIBRARY_PATH="${linuxLibraryPath}:$LD_LIBRARY_PATH"
                export WEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        });
    };
}
