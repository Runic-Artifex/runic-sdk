{ config, lib, pkgs, runicXorg, ... }:
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
    requires = lib.optional runicXorg "runic-xorg.service";
    after = lib.optional runicXorg "runic-xorg.service";
    environment = {
      XDG_SESSION_TYPE = if runicXorg then "x11" else "wayland";
      XDG_CURRENT_DESKTOP = "KDE";
      ${if runicXorg then "DISPLAY" else "WAYLAND_DISPLAY"} = if runicXorg then ":0" else "wayland-0";
      LIBGL_ALWAYS_SOFTWARE = "1";
      QT_ACCESSIBILITY = "1";
    };
    serviceConfig = {
      ExecStart = "${pkgs.kdePackages.plasma-workspace}/bin/startplasma-${if runicXorg then "x11" else "wayland"}";
      Restart = "no";
    };
  };
  # Retain the normal Plasma session services; only replace KWin's output backend.
  systemd.user.services.plasma-kwin_wayland.overrideStrategy = "asDropin";
  systemd.user.services.plasma-kwin_wayland.path = [ "/run/current-system/sw" ];
  systemd.user.services.plasma-kwin_wayland.serviceConfig.ExecStart = [
    ""
    "${lib.getBin pkgs.kdePackages.kwin}/bin/kwin_wayland_wrapper --xwayland --virtual --width 2560 --height 1600"
  ];
  systemd.user.settings.Manager.DefaultEnvironment = [
    "XDG_CURRENT_DESKTOP=KDE" "XDG_SESSION_TYPE=${if runicXorg then "x11" else "wayland"}"
    (if runicXorg then "DISPLAY=:0" else "WAYLAND_DISPLAY=wayland-0") "QT_ACCESSIBILITY=1"
  ];
  i18n.inputMethod = {
    enable = true;
    type = "fcitx5";
    fcitx5 = {
      waylandFrontend = !runicXorg;
      addons = [ pkgs.qt6Packages.fcitx5-chinese-addons ];
      settings.inputMethod = {
        "Groups/0" = { Name = "Default"; "Default Layout" = "us"; DefaultIM = "pinyin"; };
        "Groups/0/Items/0" = { Name = "keyboard-us"; Layout = ""; };
        "Groups/0/Items/1" = { Name = "pinyin"; Layout = ""; };
        GroupOrder."0" = "Default";
      };
    };
  };
  environment.systemPackages = lib.optionals runicXorg [ pkgs.kdePackages.kwin-x11 pkgs.xrandr pkgs.xsettingsd pkgs.xorg.xorgserver ];
  systemd.user.services.runic-xorg = lib.mkIf runicXorg {
    description = "Private software-only Xorg test display";
    serviceConfig = {
      ExecStart = "${pkgs.xorg.xorgserver}/bin/Xorg :0 -config /etc/runic-xorg.conf -logfile /home/runic/Xorg.log -noreset -nolisten tcp";
      ExecStartPost = "${pkgs.writeShellScript "wait-runic-xorg" ''
        for attempt in $(seq 1 100); do
          ${pkgs.xprop}/bin/xprop -display :0 -root >/dev/null 2>&1 && exit 0
          sleep 0.1
        done
        exit 1
      ''}";
      Restart = "no";
    };
  };
  environment.etc."runic-xorg.conf" = lib.mkIf runicXorg { text = ''
    Section "ServerFlags"
      Option "AutoAddDevices" "false"
      Option "AutoAddGPU" "false"
      Option "AllowMouseOpenFail" "true"
    EndSection
    Section "Files"
      ModulePath "${pkgs.xorg.xf86videodummy}/lib/xorg/modules"
      ModulePath "${pkgs.xorg.xorgserver}/lib/xorg/modules"
    EndSection
    Section "Device"
      Identifier "dummy"
      Driver "dummy"
      VideoRam 256000
    EndSection
    Section "Monitor"
      Identifier "monitor"
      HorizSync 30-200
      VertRefresh 30-200
      Modeline "2560x1600" 268.50 2560 2608 2640 2720 1600 1603 1609 1646 +HSync -VSync
    EndSection
    Section "Screen"
      Identifier "screen"
      Device "dummy"
      Monitor "monitor"
      DefaultDepth 24
      SubSection "Display"
        Depth 24
        Modes "2560x1600"
        Virtual 5120 3200
      EndSubSection
    EndSection
  ''; };
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
