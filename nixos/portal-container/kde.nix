{ config, lib, pkgs, ... }:
{
  imports = [ ./common.nix ];
  networking.hostName = "runic-headless-kde";
  system.name = "runic-headless-kde";
  services.desktopManager.plasma6.enable = true;
  # Include the real PowerDevil/UPower service even without physical power devices.
  powerManagement.enable = true;
  services.displayManager.sddm.enable = false;
  systemd.user.services.runic-headless-desktop = {
    description = "Independent headless Plasma test session";
    wantedBy = [ "default.target" ];
    environment = {
      XDG_SESSION_TYPE = "wayland";
      XDG_CURRENT_DESKTOP = "KDE";
      WAYLAND_DISPLAY = "wayland-0";
      LIBGL_ALWAYS_SOFTWARE = "1";
      QT_ACCESSIBILITY = "1";
    };
    serviceConfig = {
      ExecStart = "${pkgs.kdePackages.plasma-workspace}/bin/startplasma-wayland";
      Restart = "no";
    };
  };
  # Retain the normal Plasma session services; only replace KWin's output backend.
  systemd.user.services.plasma-kwin_wayland.overrideStrategy = "asDropin";
  systemd.user.services.plasma-kwin_wayland.path = [ "/run/current-system/sw" ];
  systemd.user.services.plasma-kwin_wayland.serviceConfig.ExecStart = [
    ""
    "${lib.getBin pkgs.kdePackages.kwin}/bin/kwin_wayland_wrapper --xwayland --virtual --width 1280 --height 800"
  ];
  systemd.user.settings.Manager.DefaultEnvironment = [
    "XDG_CURRENT_DESKTOP=KDE" "XDG_SESSION_TYPE=wayland" "WAYLAND_DISPLAY=wayland-0" "QT_ACCESSIBILITY=1"
  ];
  i18n.inputMethod = {
    enable = true;
    type = "fcitx5";
    fcitx5 = {
      waylandFrontend = true;
      addons = [ pkgs.qt6Packages.fcitx5-chinese-addons ];
      settings.inputMethod = {
        "Groups/0" = { Name = "Default"; "Default Layout" = "us"; DefaultIM = "pinyin"; };
        "Groups/0/Items/0" = { Name = "keyboard-us"; Layout = ""; };
        "Groups/0/Items/1" = { Name = "pinyin"; Layout = ""; };
        GroupOrder."0" = "Default";
      };
    };
  };
  environment.etc."xdg/kwinrc".text = ''
    [Wayland]
    InputMethod=${config.i18n.inputMethod.package}/share/applications/org.fcitx.Fcitx5.desktop
  '';
  environment.etc."xdg/kscreenlockerrc".text = ''
    [Daemon]
    Autolock=false
    LockOnResume=false
  '';
}
