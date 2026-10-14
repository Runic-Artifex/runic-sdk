# Home Manager module: programs.runic.applications.<name> installs a Runic application for one user and,
# optionally, starts it in the graphical session.
#
#   imports = [ runic-sdk.homeManagerModules.runic-applications ];
#   programs.runic.applications.my-app = {
#     package = my-app.packages.${pkgs.system}.default;
#     userService.enable = true;
#   };
#
# polkitd reads policies only from the system: an application with privileged actions also needs the NixOS
# module (or the package in environment.systemPackages) for its Polkit policy.
{
  config,
  lib,
  ...
}:
let
  quoting = import ./systemd-quoting.nix lib;
  enabled = lib.filterAttrs (_: application: application.enable) config.programs.runic.applications;
  services = lib.filterAttrs (_: application: application.userService.enable) enabled;
in
{
  options.programs.runic.applications = lib.mkOption {
    type = lib.types.attrsOf (
      lib.types.submodule [
        (import ./application-options.nix { inherit lib; })
        (
          { config, ... }:
          {
            executable = lib.mkDefault (lib.getExe config.package);
          }
        )
      ]
    );
    default = { };
    description = "Runic applications to install for this user.";
  };

  config = lib.mkIf (enabled != { }) {
    home.packages = lib.mapAttrsToList (_: application: application.package) enabled;

    systemd.user.services = lib.mapAttrs (name: application: {
      Unit = {
        Description = application.userService.description;
        PartOf = [ "graphical-session.target" ];
        After = [ "graphical-session.target" ];
      };
      Service = {
        ExecStart = quoting.execArguments ([ application.executable ] ++ application.userService.arguments);
        Environment = lib.mapAttrsToList quoting.environment application.userService.environment;
        Restart = "on-failure";
        RestartSec = 5;
      };
      Install.WantedBy = [ "graphical-session.target" ];
    }) services;
  };
}
