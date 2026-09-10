{ pkgs, ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.sddm.enable = true;
  services.desktopManager.plasma6.enable = true;

  # No GTK/GNOME provider is installed in this image. Every portal interface
  # used by the fixture is explicitly assigned to KDE.
  xdg.portal = {
    extraPortals = [ pkgs.kdePackages.xdg-desktop-portal-kde ];
    config = {
      common.default = [ "kde" ];
      plasma.default = [ "kde" ];
    };
  };
}
