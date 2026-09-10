{ lib, pkgs, ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.gdm.enable = true;
  services.desktopManager.gnome.enable = true;
  i18n.inputMethod = {
    enable = true;
    type = "ibus";
    ibus.engines = [ pkgs.ibus-engines.libpinyin ];
  };

  # Disposable manual-test guest: waiting for a notification click must not
  # lock the desktop or hide the controls under test.
  programs.dconf.profiles.user.databases = [ {
    settings = {
      "org/gnome/desktop/session".idle-delay = lib.gvariant.mkUint32 0;
      "org/gnome/desktop/screensaver".lock-enabled = false;
      "org/gnome/desktop/interface".toolkit-accessibility = true;
      "org/gnome/desktop/input-sources".sources = [
        (lib.gvariant.mkTuple [ "xkb" "us" ])
        (lib.gvariant.mkTuple [ "ibus" "libpinyin" ])
      ];
    };
  } ];

  # GNOME's upstream gnome-portals.conf chooses its implementation where it is
  # available and the GTK fallback for interfaces it does not export.
  # The GNOME module installs both backends.
}
