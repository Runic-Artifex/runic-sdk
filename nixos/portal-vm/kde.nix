{ config, pkgs, ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.sddm.enable = true;
  services.desktopManager.plasma6.enable = true;

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
  # KWin must launch the IME to provide its Wayland input-method socket.
  environment.etc."xdg/kwinrc".text = ''
    [Wayland]
    InputMethod=${config.i18n.inputMethod.package}/share/applications/org.fcitx.Fcitx5.desktop
  '';

  # Plasma's upstream kde-portals.conf selects KDE for the normal interfaces,
  # GTK as the documented Settings fallback, and plasmanotify for Notification.
  # The plasma module also installs each matching implementation.
}
