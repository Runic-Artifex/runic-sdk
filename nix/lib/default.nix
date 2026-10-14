# The Runic SDK's Nix library, exported by the flake as `lib`. Each function takes the consumer's nixpkgs first:
#
#   runic-sdk.lib.buildRunicApplication pkgs { pname = "my-app"; ... }
#   runic-sdk.lib.mkDevShell pkgs { hosts = [ "desktop" ]; }
#
# The overlay (overlays.default) adds pkgs.buildRunicApplication for callPackage-style package files.
{
  buildRunicApplication = pkgs: pkgs.callPackage ./build-runic-application.nix { };
  nugetDepsFromLockFiles = pkgs: pkgs.callPackage ./nuget-lock-deps.nix { inherit (pkgs.dotnetCorePackages) fetchNupkg; };
  frontendNodeModules = pkgs: pkgs.callPackage ./frontend-node-modules.nix { nodejs = pkgs.nodejs_24; };
  mkDevShell = pkgs: pkgs.callPackage ./dev-shell.nix { };
}
