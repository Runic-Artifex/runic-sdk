{ pkgs, ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.gdm.enable = true;
  services.desktopManager.gnome.enable = true;

  # No GTK/KDE provider is installed in this image. Every portal interface used
  # by the fixture is explicitly assigned to GNOME.
  xdg.portal = {
    extraPortals = [ pkgs.xdg-desktop-portal-gnome ];
    config = {
      common.default = [ "gnome" ];
      gnome.default = [ "gnome" ];
    };
  };
}
