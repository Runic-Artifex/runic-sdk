# NixOS module: programs.runic.applications.<name> installs a Runic application system-wide, registers its
# Polkit policy and, optionally, starts it in every user's graphical session.
#
#   imports = [ runic-sdk.nixosModules.runic-applications ];
#   programs.runic.applications.my-app = {
#     package = my-app.packages.${pkgs.system}.default;
#     userService.enable = true;
#   };
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
    description = "Runic applications to install.";
  };

  config = lib.mkIf (enabled != { }) {
    # The package's share/applications, share/icons and share/polkit-1/actions join the system profile; polkitd
    # reads the policies from /run/current-system/sw/share/polkit-1/actions.
    environment.systemPackages = lib.mapAttrsToList (_: application: application.package) enabled;
    # Privileged actions run through pkexec, which needs its setuid wrapper (/run/wrappers/bin/pkexec).
    security.polkit.enable = lib.mkDefault true;
    security.polkit.enablePkexecWrapper = lib.mkDefault true;

    systemd.user.services = lib.mapAttrs (name: application: {
      inherit (application.userService) description environment;
      wantedBy = [ "graphical-session.target" ];
      partOf = [ "graphical-session.target" ];
      after = [ "graphical-session.target" ];
      serviceConfig = {
        ExecStart = quoting.execArguments ([ application.executable ] ++ application.userService.arguments);
        Restart = "on-failure";
        RestartSec = 5;
      };
    }) services;
  };
}
