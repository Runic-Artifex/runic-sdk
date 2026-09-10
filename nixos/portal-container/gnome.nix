{ lib, pkgs, ... }:
{
  imports = [ ./common.nix ];
  networking.hostName = "runic-headless-gnome";
  system.name = "runic-headless-gnome";
  services.desktopManager.gnome.enable = true;
  services.displayManager.gdm.enable = false;
  i18n.inputMethod = {
    enable = true;
    type = "ibus";
    ibus.engines = [ pkgs.ibus-engines.libpinyin ];
  };
  systemd.user.services.runic-headless-desktop = {
    description = "Independent headless GNOME test session";
    wantedBy = [ "default.target" ];
    environment = {
      XDG_SESSION_TYPE = "wayland";
      XDG_CURRENT_DESKTOP = "GNOME";
      WAYLAND_DISPLAY = "runic-wayland";
      LIBGL_ALWAYS_SOFTWARE = "1";
    };
    serviceConfig = {
      ExecStart = "${pkgs.gnome-session}/bin/gnome-session --session=gnome";
      Restart = "no";
    };
  };
  systemd.user.services."org.gnome.Shell@".overrideStrategy = "asDropin";
  systemd.user.services."org.gnome.Shell@".serviceConfig.ExecStart = [
    ""
    "${pkgs.gnome-shell}/bin/gnome-shell --wayland --no-x11 --headless --virtual-monitor 1280x800 --wayland-display runic-wayland"
  ];
  # Keep the ordinary session manager/portal dependencies. Only the compositor
  # backend changes; starting Shell alone omits graphical-session startup.
  programs.dconf.profiles.user.databases = [ {
    locks = [ "/org/gnome/desktop/input-sources/sources" ];
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
  systemd.user.settings.Manager.DefaultEnvironment = [
    "XDG_CURRENT_DESKTOP=GNOME" "XDG_SESSION_TYPE=wayland" "WAYLAND_DISPLAY=runic-wayland"
  ];
}
