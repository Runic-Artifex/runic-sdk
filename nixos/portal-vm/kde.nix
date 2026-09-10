{ ... }:
{
  imports = [ ./common.nix ];

  services.displayManager.sddm.enable = true;
  services.desktopManager.plasma6.enable = true;

  # Plasma's upstream kde-portals.conf selects KDE for the normal interfaces,
  # GTK as the documented Settings fallback, and plasmanotify for Notification.
  # The plasma module also installs each matching implementation.
}
