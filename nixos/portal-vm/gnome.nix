{ lib, ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.gdm.enable = true;
  services.desktopManager.gnome.enable = true;

  # Disposable manual-test guest: waiting for a notification click must not
  # lock the desktop or hide the controls under test.
  programs.dconf.profiles.user.databases = [ {
    settings = {
      "org/gnome/desktop/session".idle-delay = lib.gvariant.mkUint32 0;
      "org/gnome/desktop/screensaver".lock-enabled = false;
    };
  } ];

  # GNOME's upstream gnome-portals.conf chooses its implementation where it is
  # available and the GTK backend for FileChooser, Notification and other
  # interfaces GNOME does not export. The GNOME module installs both backends.
}
