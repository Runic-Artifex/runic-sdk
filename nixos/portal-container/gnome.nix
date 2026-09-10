# Experimental systemd-nspawn desktop. This probes independent headless services;
# it is not yet a replacement for the full native VM acceptance suite.
{ pkgs, ... }:
{
  virtualisation.vlans = [ ];
  networking.hostName = "runic-headless-gnome";
  system.name = "runic-headless-gnome";
  system.stateVersion = "26.11";
  users.users.runic = {
    isNormalUser = true;
    uid = 1000;
    linger = true;
    initialPassword = "runic";
  };
  hardware.graphics.enable = true; # Includes llvmpipe; no GPU device is passed through.
  services.dbus.enable = true;
  services.desktopManager.gnome.enable = true;
  services.displayManager.gdm.enable = false;
  services.pipewire = { enable = true; pulse.enable = true; };
  programs.dconf.enable = true;
  environment.systemPackages = with pkgs; [ gnome-shell gtk4 (lib.getBin glib) dbus ];
  systemd.user.services.runic-headless-desktop = {
    description = "Independent headless GNOME test compositor";
    wantedBy = [ "default.target" ];
    environment = {
      XDG_SESSION_TYPE = "wayland";
      XDG_CURRENT_DESKTOP = "GNOME";
      WAYLAND_DISPLAY = "runic-wayland";
      LIBGL_ALWAYS_SOFTWARE = "1";
    };
    serviceConfig = {
      ExecStart = "${pkgs.gnome-shell}/bin/gnome-shell --wayland --no-x11 --headless --virtual-monitor 1280x800 --wayland-display runic-wayland";
      Restart = "no";
    };
  };
  systemd.user.settings.Manager.DefaultEnvironment = [
    "XDG_CURRENT_DESKTOP=GNOME" "XDG_SESSION_TYPE=wayland" "WAYLAND_DISPLAY=runic-wayland"
  ];
}
