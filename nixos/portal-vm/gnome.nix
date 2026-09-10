{ ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.gdm.enable = true;
  services.desktopManager.gnome.enable = true;

  # GNOME's upstream gnome-portals.conf chooses its implementation where it is
  # available and the GTK backend for FileChooser, Notification and other
  # interfaces GNOME does not export. The GNOME module installs both backends.
}
