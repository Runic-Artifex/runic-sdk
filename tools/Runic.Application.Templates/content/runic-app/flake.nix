{
  description = "RunicWindowApp, a Runic application";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    runic-sdk = {
      url = "github:Runic-Artifex/runic-sdk/v__RUNIC_NUGET_VERSION__";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      runic-sdk,
    }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
      inherit (pkgs) lib;
      # Builds in the Nix sandbox without network access: NuGet packages come from the committed lock files and
      # the frontend's dependencies from its lock file.
      application =
        { nativeAot }:
        runic-sdk.lib.buildRunicApplication pkgs {
          pname = "RunicWindowApp";
          version = "0.1.0";
          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.difference ./. (
              lib.fileset.unions [
                # Local tools are for development; the build needs none of them.
                (lib.fileset.maybeMissing ./.config)
                ./flake.nix
                (lib.fileset.maybeMissing ./flake.lock)
              ]
            );
          };
          projectFile = "RunicWindowApp.csproj";
          # dotnet restore and dotnet restore -p:PublishAot=true write them; see RunicWindowApp.csproj.
          nugetLockFiles = [
            (if nativeAot then ./packages.aot.lock.json else ./packages.lock.json)
#if (tests)
            ./RunicWindowApp.Tests/packages.lock.json
#endif
          ];
#if (tests)
          # The build runs the TUnit tests, a Microsoft.Testing.Platform test application, under the JIT.
          testProjectFile = "RunicWindowApp.Tests/RunicWindowApp.Tests.csproj";
          doCheck = true;
          testRunner = "Microsoft.Testing.Platform";
#endif
          frontend = {
            packageManager = "__PACKAGE_MANAGER_NAME__";
#if (packageManager == "npm")
            # Each package is fetched from package-lock.json; set a hash to fetch them as one download instead.
            hash = null;
#else
            # Build once, then replace this with the hash that Nix reports. Update it with the lock file.
            hash = lib.fakeHash;
#endif
          };
          host = "__RUNIC_HOST__";
          inherit nativeAot;
          executables = [ "RunicWindowApp" ];
          # The desktop entry and icon are named after ApplicationId in the project file. The build adds the
          # project's RunicApplicationFileTypes and RunicApplicationUrlSchemes to this entry (MimeType=) and installs
          # the file types' MIME definitions; %U passes the files and URLs the desktop opens.
          desktopItems = [
            (pkgs.makeDesktopItem {
              name = "__RUNIC_APPLICATION_ID__";
              desktopName = "__RUNIC_APPLICATION_NAME__";
              exec = "RunicWindowApp %U";
              icon = "__RUNIC_APPLICATION_ID__";
              categories = [ "Utility" ];
            })
          ];
          icons = {
            scalable = ./icons/icon.svg;
            "512x512" = ./icons/icon.png;
          };
          iconName = "__RUNIC_APPLICATION_ID__";
          meta.mainProgram = "RunicWindowApp";
        };
    in
    {
      packages.${system} = {
        default = application { nativeAot = false; };
        native-aot = application { nativeAot = true; };
      };
#if (tests)
      # nix flake check builds the application, which runs the tests.
      checks.${system}.default = self.packages.${system}.default;
#endif
      devShells.${system}.default = runic-sdk.lib.mkDevShell pkgs {
        hosts = [ "__RUNIC_HOST__" ];
        packageManagers = [ "__PACKAGE_MANAGER_NAME__" ];
      };
    };
}
