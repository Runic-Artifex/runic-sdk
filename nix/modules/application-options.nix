# The options of one Runic application, shared by the NixOS and Home Manager modules.
{ lib }:
{ name, ... }:
{
  options = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Whether to install the application.";
    };
    package = lib.mkOption {
      type = lib.types.package;
      description = ''
        The application, for example from runic-sdk.lib.buildRunicApplication. Its desktop entries, icons and
        Polkit policy (share/applications, share/icons, share/polkit-1/actions) are installed with it.
      '';
    };
    executable = lib.mkOption {
      type = lib.types.str;
      defaultText = lib.literalExpression "lib.getExe package";
      description = "The program the user service starts.";
    };
    userService = {
      enable = lib.mkEnableOption ''
        a systemd user service that starts the application in the graphical session, for an application that
        runs in the background, such as one with a status icon. A launch from the desktop entry reaches the
        running instance when the application claims a single instance (ApplicationInstance.ClaimAsync). Use it
        instead of the application's own login startup, whose entry names one store path
      '';
      arguments = lib.mkOption {
        type = lib.types.listOf lib.types.str;
        default = [ ];
        example = [ "--background" ];
        description = "Arguments of the background instance. Each is passed as one argument, without a shell.";
      };
      environment = lib.mkOption {
        type = lib.types.attrsOf lib.types.str;
        default = { };
        description = "Environment variables of the service.";
      };
      description = lib.mkOption {
        type = lib.types.str;
        default = "${name} in the background";
        description = "The unit's description.";
      };
    };
  };
}
