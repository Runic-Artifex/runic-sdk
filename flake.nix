{
  description = "Runic SDK development environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  };

  outputs =
    { self, nixpkgs, ... }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
    in
    {
      # Packaging Runic applications: buildRunicApplication, mkDevShell and the NixOS and Home Manager modules.
      # See nix/README.md.
      lib = import ./nix/lib;
      # nix run github:Runic-Artifex/runic-sdk/v<version>#create -- MyApp --nix: Runic.Create without a .NET SDK on the
      # machine. See nix/README.md.
      apps = forAllSystems (system: {
        create = {
          type = "app";
          program = nixpkgs.lib.getExe self.packages.${system}.create;
          meta.description = "Create a Runic application with Runic.Create";
        };
      });
      overlays.default = final: prev: { buildRunicApplication = self.lib.buildRunicApplication final; };
      nixosModules.runic-applications = import ./nix/modules/nixos.nix;
      homeManagerModules.runic-applications = import ./nix/modules/home-manager.nix;

      # The status-icon example built with buildRunicApplication, under JIT and NativeAOT.
      checks.x86_64-linux =
        let
          examples = import ./nix/examples.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          };
        in
        {
          example-status-icon = examples.status-icon;
          example-status-icon-aot = examples.status-icon-aot;
          example-privileged-smoke = examples.privileged-smoke;
          builder-test-runner = import ./nix/tests/test-runner.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          };
        }
        // nixpkgs.lib.mapAttrs' (name: nixpkgs.lib.nameValuePair (if name == "default" then "builder-dotnet-tools" else "builder-dotnet-tools-${name}")) (
          import ./nix/tests/dotnet-tools.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          }
        )
        // {
          builder-completions = import ./nix/tests/completions.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          };
          home-manager-module = import ./nix/tests/modules.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            inherit self;
          };
          # The create app's wrapper builds (writeShellApplication runs ShellCheck); nix/README.md describes the
          # run that creates a project, which needs nuget.org.
          create = self.packages.x86_64-linux.create;
          builder-file-types = import ./nix/tests/file-types.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          };
          builder-locks = import ./nix/tests/locks.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            runic = self.lib;
          };
          builder-without-speech-synthesis = import ./nix/tests/speech-synthesis.nix {
            pkgs = import nixpkgs { system = "x86_64-linux"; };
            inherit self;
          };
        };

      # The portal backends are intentionally split into two images. A picker
      # or notification therefore cannot accidentally be served by the host
      # desktop's preferred backend.
      nixosConfigurations = {
        runic-headless-gnome = nixpkgs.lib.nixosSystem {
          system = "x86_64-linux";
          modules = [
            "${nixpkgs}/nixos/modules/virtualisation/nspawn-container"
            "${nixpkgs}/nixos/modules/virtualisation/guest-networking-options.nix"
            ./nixos/portal-container/gnome.nix
          ];
        };
        runic-headless-kde = nixpkgs.lib.nixosSystem {
          system = "x86_64-linux";
          specialArgs.runicXorg = false;
          modules = [
            "${nixpkgs}/nixos/modules/virtualisation/nspawn-container"
            "${nixpkgs}/nixos/modules/virtualisation/guest-networking-options.nix"
            ./nixos/portal-container/kde.nix
          ];
        };
        runic-headless-kde-xorg = nixpkgs.lib.nixosSystem {
          system = "x86_64-linux";
          specialArgs.runicXorg = true;
          modules = [
            "${nixpkgs}/nixos/modules/virtualisation/nspawn-container"
            "${nixpkgs}/nixos/modules/virtualisation/guest-networking-options.nix"
            ./nixos/portal-container/kde.nix
          ];
        };
      };

      # Opt-in CPU transcription for captured test-desktop speech; keep it out of
      # the normal SDK shell and do not require a GPU or a cloud service.
      packages = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          # Runic.Create with a .NET SDK from nixpkgs, for `nix run .#create`; see nix/create.nix.
          create = import ./nix/create.nix { inherit pkgs; };
          # Status icon checks on Plasma and Waybar; see nixos/status-icon.
          status-icon-plasma = (import ./nixos/status-icon { inherit pkgs; }).plasma;
          status-icon-waybar = (import ./nixos/status-icon { inherit pkgs; }).waybar;
          # Single instance and login startup on Plasma; see nixos/application-lifetime.
          application-lifetime-plasma = (import ./nixos/application-lifetime { inherit pkgs; }).plasma;
          # Privileged actions against a real Polkit; see nixos/privileged-operations.
          privileged-operations-polkit = (import ./nixos/privileged-operations { inherit pkgs; }).polkit;
          # Volumes against a real UDisks2 and Polkit; see nixos/volumes.
          volumes-udisks2 = (import ./nixos/volumes { inherit pkgs; }).udisks2;
          # Network status against a real NetworkManager without a portal; see nixos/network-status.
          network-status-networkmanager = (import ./nixos/network-status { inherit pkgs; }).networkmanager;
          # The global menu on Plasma X11, Plasma Wayland and GNOME; see nixos/global-menu.
          global-menu-plasma-x11 = (import ./nixos/global-menu { inherit pkgs; }).plasma-x11;
          global-menu-plasma-wayland = (import ./nixos/global-menu { inherit pkgs; }).plasma-wayland;
          global-menu-gnome = (import ./nixos/global-menu { inherit pkgs; }).gnome;
          # A packaged application installed through the NixOS module; see nixos/packaging.
          packaging-nixos = import ./nixos/packaging { inherit pkgs self; };
          desktop-flatpak-tools = pkgs.buildEnv {
            name = "runic-desktop-flatpak-tools";
            paths = [ pkgs.flatpak pkgs.bash pkgs.coreutils pkgs.nix ];
          };
          vm-whisper-model = pkgs.fetchurl {
            url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
            sha256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002";
          };
          vm-whisper = pkgs.whisper-cpp.override {
            cudaSupport = false;
            rocmSupport = false;
            vulkanSupport = false;
            withSDL = false;
            withFFmpegSupport = false;
          };
        });

      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
          inherit (pkgs) lib;
          # global.json disables SDK roll-forward and CI installs `.node-version`; nix/toolchain provides exactly
          # those versions (from the official binaries while nixpkgs is older) and fails evaluation otherwise.
          toolchain = import ./nix/toolchain { inherit pkgs; };
          dotnet = toolchain.dotnet-sdk;
          inherit (toolchain) nodejs;
          # act 0.2.89 needs the upstream artifact protocol fix for upload v7/download v8.
          # Remove this patch once the pinned nixpkgs act contains nektos/act#6115.
          actForCi = pkgs.act.overrideAttrs (old: {
            patches = (old.patches or [ ]) ++ [ ./eng/ci/act-artifacts.patch ];
          });
          # AT-SPI 2.60.6 leaks a DBusMessage whenever WebKit embeds an accessibility
          # tree. Preserve accessibility and release the sender's owned reference.
          # Remove this patch when the locked upstream source includes the fix.
          atSpiForRunic = pkgs.at-spi2-core.overrideAttrs (old: {
            patches = (old.patches or [ ]) ++ [ ./eng/native/at-spi2-core-release-embedded-message.patch ];
          });
          bunArchive =
            if system == "x86_64-linux" then
              {
                platform = "linux-x64";
                hash = "sha256-SQxqWbkwuEx42tLBeD8BdpDH1bxUasS1yOkkDULCz3U=";
              }
            else
              {
                platform = "linux-aarch64";
                hash = "sha256-NhWc7T3/G+CA3TE/q/vQR5oOPfBLPgZ6Exdqa1CjD5w=";
              };
          bunSource = pkgs.fetchzip {
            url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.4/bun-${bunArchive.platform}.zip";
            inherit (bunArchive) hash;
          };
          bun_1_4_4 = pkgs.stdenvNoCC.mkDerivation {
            pname = "bun";
            version = "1.4.4";
            src = bunSource;
            nativeBuildInputs = [ pkgs.autoPatchelfHook ];
            buildInputs = [ pkgs.stdenv.cc.cc.lib ];
            dontBuild = true;
            installPhase = ''
              mkdir -p "$out/bin"
              cp bun "$out/bin/bun"
              chmod +x "$out/bin/bun"
            '';
          };
          npmSource = pkgs.fetchzip {
            url = "https://registry.npmjs.org/npm/-/npm-12.2.0.tgz";
            hash = "sha256-qhUNqf1W0WGysd7g1u3AVim/wzCZ3uxGULmz5HyvDgA=";
          };
          npmForCompatibility = pkgs.runCommand "npm-12.2.0" {
            nativeBuildInputs = [ pkgs.makeWrapper ];
          } ''
            mkdir -p "$out/bin"
            # Keep real Node and the selected npm together for compatibility tests.
            ln -s "${nodejs}/bin/node" "$out/bin/node"
            makeWrapper "${nodejs}/bin/node" "$out/bin/npm" \
              --add-flags "${npmSource}/bin/npm-cli.js"
            makeWrapper "${nodejs}/bin/node" "$out/bin/npx" \
              --add-flags "${npmSource}/bin/npx-cli.js"
          '';
          pnpmArchive = if system == "x86_64-linux" then {
            platform = "linux-x64";
            hash = "sha256-BpfzKeM4Qa5DHc6aRgJnm2iMtJd3DRDXrUBU0gr+MgM=";
          } else {
            platform = "linux-arm64";
            hash = "sha256-wIbnJTrRsZgFtsnxkOg5oOaSUEtc/GaWv8eVT4GfIf0=";
          };
          pnpmForCompatibility = pkgs.stdenvNoCC.mkDerivation {
            pname = "pnpm";
            version = "12.9.1";
            src = pkgs.fetchzip {
              url = "https://registry.npmjs.org/@pnpm/exe.${pnpmArchive.platform}/-/exe.${pnpmArchive.platform}-12.9.1.tgz";
              inherit (pnpmArchive) hash;
            };
            nativeBuildInputs = [ pkgs.autoPatchelfHook ];
            buildInputs = [ pkgs.stdenv.cc.cc.lib ];
            dontBuild = true;
            installPhase = ''
              mkdir -p "$out/bin"
              cp pnpm "$out/bin/pnpm"
              chmod +x "$out/bin/pnpm"
            '';
          };
          linuxRuntimePackages = with pkgs; lib.optionals pkgs.stdenv.hostPlatform.isLinux [
            atSpiForRunic
            chromium
            gtk3
            gtk4
            webkitgtk_4_1
            webkitgtk_6_0
            glib
            glib-networking
            gsettings-desktop-schemas
            dbus
            xdg-desktop-portal
            xdg-desktop-portal-gtk
            (lib.getBin kdePackages.xdg-desktop-portal-kde)
            gst_all_1.gstreamer
            gst_all_1.gst-plugins-base
            gst_all_1.gst-plugins-good
            gst_all_1.gst-plugins-bad
            gst_all_1.gst-libav
            xvfb-run
            # A window manager for Xvfb: the GTK 4 placement smoke (--placement --window-manager) runs under Openbox.
            openbox
          ];
        in
        {
          # tests/templates/Test-NixTemplate.sh and the Nix workflow: the .NET SDK, Node and Bun to pack the
          # template, and Xvfb for its window check, without the full development shell's desktop stack.
          packaging = self.lib.mkDevShell pkgs {
            hosts = [ ];
            packageManagers = [ "bun" ];
            packages = with pkgs; [
              curl
              dbus
              git
              jq
              procps
              xvfb-run
            ];
          };
          default = pkgs.mkShell {
            packages = with pkgs; [
              git
              curl
              dotnet
              bun_1_4_4
              npmForCompatibility
              pnpmForCompatibility
              nodejs
              python3
              powershell
              actForCi
              actionlint

              # Required by the repository's Native AOT verification.
              clang
              pkg-config
              zlib
            ] ++ linuxRuntimePackages;

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${dotnet}/share/dotnet";
            RUNIC_CI_ACT = "${actForCi}/bin/act";
            DisableImplicitLibraryPacksFolder = "true";

            shellHook = ''
              # Keep interactive restores inside the repository workspace.
              export NUGET_PACKAGES="$PWD/.cache/nuget"
              ${lib.optionalString pkgs.stdenv.hostPlatform.isLinux ''
                export GIO_EXTRA_MODULES="${pkgs.glib-networking}/lib/gio/modules''${GIO_EXTRA_MODULES:+:$GIO_EXTRA_MODULES}"
                export GST_PLUGIN_SYSTEM_PATH_1_0="${lib.makeSearchPath "lib/gstreamer-1.0" (with pkgs.gst_all_1; [ gstreamer gst-plugins-base gst-plugins-good gst-plugins-bad gst-libav ])}''${GST_PLUGIN_SYSTEM_PATH_1_0:+:$GST_PLUGIN_SYSTEM_PATH_1_0}"
                export LD_LIBRARY_PATH="${lib.makeLibraryPath linuxRuntimePackages}:$LD_LIBRARY_PATH"
                export XDG_DATA_DIRS="${lib.concatMapStringsSep ":" (package: "${package}/share/gsettings-schemas/${package.name}") [ pkgs.gtk3 pkgs.gtk4 pkgs.gsettings-desktop-schemas ]}''${XDG_DATA_DIRS:+:$XDG_DATA_DIRS}"
                export PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        }
      );
    };
}
