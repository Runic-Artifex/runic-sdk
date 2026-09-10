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
      # The portal backends are intentionally split into two images. A picker
      # or notification therefore cannot accidentally be served by the host
      # desktop's preferred backend.
      nixosConfigurations = {
        runic-portal-kde = nixpkgs.lib.nixosSystem {
          system = "x86_64-linux";
          specialArgs.runicSource = self.outPath;
          specialArgs.runicDevShell = self.devShells.x86_64-linux.default;
          specialArgs.portalDesktop = "kde";
          modules = [ ./nixos/portal-vm/kde.nix ];
        };
        runic-portal-gnome = nixpkgs.lib.nixosSystem {
          system = "x86_64-linux";
          specialArgs.runicSource = self.outPath;
          specialArgs.runicDevShell = self.devShells.x86_64-linux.default;
          specialArgs.portalDesktop = "gnome";
          modules = [ ./nixos/portal-vm/gnome.nix ];
        };
      };

      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
          inherit (pkgs) lib;
          dotnet = pkgs.dotnetCorePackages.sdk_10_0;
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
                hash = "sha256-Nq85/hrOkT7M4VJ9Uc7Kr9e+uNx8unD4d30RzaQlYxc=";
              }
            else
              {
                platform = "linux-aarch64";
                hash = "sha256-E9EOo0ihjqTrVS7RffpXUBeqdkvRoYnXAmscz1BmNwo=";
              };
          bunSource = pkgs.fetchzip {
            url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.2/bun-${bunArchive.platform}.zip";
            inherit (bunArchive) hash;
          };
          bun_1_4_2 = pkgs.stdenvNoCC.mkDerivation {
            pname = "bun";
            version = "1.4.2";
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
            url = "https://registry.npmjs.org/npm/-/npm-12.0.2.tgz";
            hash = "sha256-GMlNf3g1qGZESoES60OH2OYHXJ7Kv1v15HhYEw20fmc=";
          };
          npmForCompatibility = pkgs.runCommand "npm-12.0.2" {
            nativeBuildInputs = [ pkgs.makeWrapper ];
          } ''
            mkdir -p "$out/bin"
            # Keep real Node and the selected npm together for compatibility tests.
            ln -s "${pkgs.nodejs_24}/bin/node" "$out/bin/node"
            makeWrapper "${pkgs.nodejs_24}/bin/node" "$out/bin/npm" \
              --add-flags "${npmSource}/bin/npm-cli.js"
            makeWrapper "${pkgs.nodejs_24}/bin/node" "$out/bin/npx" \
              --add-flags "${npmSource}/bin/npx-cli.js"
          '';
          pnpmArchive = if system == "x86_64-linux" then {
            platform = "linux-x64";
            hash = "sha256-4mngwZG2hfp3rEPqywKezJ0ZO/8OxdB8jxatjTcIP2U=";
          } else {
            platform = "linux-arm64";
            hash = "sha256-94c7TD59PdJeY8STn9Bln8sfYkzn6Jd8In5zMSvmZQ4=";
          };
          pnpmForCompatibility = pkgs.stdenvNoCC.mkDerivation {
            pname = "pnpm";
            version = "12.3.4";
            src = pkgs.fetchzip {
              url = "https://registry.npmjs.org/@pnpm/exe.${pnpmArchive.platform}/-/exe.${pnpmArchive.platform}-12.3.4.tgz";
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
          ];
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              git
              curl
              dotnet
              bun_1_4_2
              npmForCompatibility
              pnpmForCompatibility
              nodejs_24
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
                export WEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
                export PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        }
      );
    };
}
