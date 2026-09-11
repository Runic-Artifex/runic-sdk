{ lib, pkgs, ... }:
let
  accessibilityInspector = pkgs.writeShellApplication {
    name = "runic-atspi";
    runtimeInputs = [ (pkgs.python3.withPackages (ps: [ ps.pyatspi ps.pygobject3 ])) ];
    text = ''
      export RUNIC_LIBEI="${pkgs.libei}/lib/libei.so.1"
      export GI_TYPELIB_PATH="${lib.makeSearchPath "lib/girepository-1.0" [ pkgs.at-spi2-core pkgs.glib pkgs.gobject-introspection ]}''${GI_TYPELIB_PATH:+:$GI_TYPELIB_PATH}"
      exec python3 "$@"
    '';
  };
  portalAutomation = pkgs.writeShellApplication {
    name = "runic-portal-automate";
    text = ''
      exec ${accessibilityInspector}/bin/runic-atspi ${../tests/native/Runic.Desktop.Gtk4.Smoke}/automate-usability.py "$@"
    '';
  };
  notificationAutomation = pkgs.writeShellApplication {
    name = "runic-notification-automate";
    text = ''
      exec ${accessibilityInspector}/bin/runic-atspi ${../tests/native/Runic.Desktop.Gtk4.Smoke}/automate-notifications.py "$@"
    '';
  };
in {
  system.build.runicAtspi = accessibilityInspector;
  system.build.runicAutomation = portalAutomation;
  system.build.runicNotificationAutomation = notificationAutomation;
  environment.systemPackages = [ accessibilityInspector portalAutomation notificationAutomation pkgs.orca pkgs.xprop ];
}
