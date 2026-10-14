# Evaluates the Home Manager module against stand-ins for the Home Manager options it sets, and checks the user
# unit and the escaping of its command line. The NixOS module runs for real in nixos/packaging.
{ pkgs, self }:
let
  inherit (pkgs) lib;
  package = pkgs.writeShellScriptBin "my-app" "" // {
    meta.mainProgram = "my-app";
  };
  home = lib.evalModules {
    modules = [
      self.homeManagerModules.runic-applications
      {
        options.home.packages = lib.mkOption { type = lib.types.listOf lib.types.package; };
        options.systemd.user.services = lib.mkOption { type = lib.types.attrsOf lib.types.attrs; };
        config.programs.runic.applications = {
          my-app = {
            inherit package;
            userService = {
              enable = true;
              arguments = [
                "--tray"
                "two words"
                "50%"
                "$HOME"
                "a\"b\\c"
              ];
              environment.MY_APP_MODE = "background";
            };
          };
          other = {
            inherit package;
            enable = false;
          };
        };
      }
    ];
  };
  service = home.config.systemd.user.services.my-app;
  expected = ''"${lib.getExe package}" "--tray" "two words" "50%%" "$$HOME" "a\"b\\c"'';
in
assert lib.assertMsg (home.config.home.packages == [ package ]) "only enabled applications are installed";
assert lib.assertMsg (lib.attrNames home.config.systemd.user.services == [ "my-app" ]) "one user service";
assert lib.assertMsg (service.Service.ExecStart == expected) "ExecStart is ${service.Service.ExecStart}";
assert lib.assertMsg (service.Service.Environment == [ "MY_APP_MODE=background" ]) "environment";
assert lib.assertMsg (service.Install.WantedBy == [ "graphical-session.target" ]) "started with the graphical session";
pkgs.writeText "runic-home-manager-module" (builtins.toJSON service)
