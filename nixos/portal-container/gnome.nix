# Experimental systemd-nspawn desktop. This probes independent headless services;
# it is not yet a replacement for the full native VM acceptance suite.
{ config, lib, pkgs, ... }:
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
  # Generic Linux NativeAOT publishes use /lib64/ld-linux-x86-64.so.2.
  # Preserve /proc/self/exe and GTK's application identity when starting them.
  programs.nix-ld.enable = true;
  environment.systemPackages = with pkgs; [ gnome-shell gtk4 (lib.getBin glib) dbus
    (makeDesktopItem {
      name = "com.runic.tests.Activation";
      desktopName = "Runic GTK4 Test";
      exec = "${config.system.build.runicContainerFixture}/bin/runic-container-fixture /home/runic/Runic.Desktop.Gtk4.Smoke --usability";
      terminal = false;
      categories = [ "Development" ];
    })
  ];
  # nspawn masks parts of /proc. Linux then rejects a nested proc mount as
  # "too revealing" unless this PID namespace also has a pristine proc mount.
  # Keep that mount inaccessible to the test user; never bind the host's proc.
  systemd.services.runic-pristine-proc = {
    description = "Permit nested WebKit proc mounts within the container PID namespace";
    wantedBy = [ "multi-user.target" ];
    requiredBy = [ "user@1000.service" ];
    before = [ "user@1000.service" ];
    serviceConfig = {
      Type = "oneshot";
      RemainAfterExit = true;
      RuntimeDirectory = "runic-proc-private";
      RuntimeDirectoryMode = "0700";
      # The mount must remain visible in the guest mount namespace.
      PrivateMounts = false;
      ExecStart = [
        "${pkgs.coreutils}/bin/mkdir -p /run/runic-proc-private/proc"
        "${pkgs.util-linux}/bin/mount -t proc -o nosuid,nodev,noexec proc /run/runic-proc-private/proc"
      ];
      ExecStop = "${pkgs.util-linux}/bin/umount /run/runic-proc-private/proc";
    };
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
  systemd.user.services."org.gnome.Shell@".serviceConfig.ExecStart = [
    ""
    "${pkgs.gnome-shell}/bin/gnome-shell --wayland --no-x11 --headless --virtual-monitor 1280x800 --wayland-display runic-wayland"
  ];
  # Keep the ordinary session manager/portal dependencies. Only the compositor
  # backend changes; starting Shell alone omits graphical-session startup.
  programs.dconf.profiles.user.databases = [ {
    settings = {
      "org/gnome/desktop/session".idle-delay = lib.gvariant.mkUint32 0;
      "org/gnome/desktop/screensaver".lock-enabled = false;
      "org/gnome/desktop/interface".toolkit-accessibility = true;
    };
  } ];
  system.build.runicContainerFixture = pkgs.writeShellApplication {
    name = "runic-container-fixture";
    text = ''
      export LD_LIBRARY_PATH="${lib.makeLibraryPath (with pkgs; [ icu openssl gtk4 webkitgtk_6_0 glib ])}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
      export GIO_EXTRA_MODULES="${pkgs.glib-networking}/lib/gio/modules"
      exec "$@"
    '';
  };
  system.build.runicSessionProbe = pkgs.writeShellApplication {
    name = "runic-session-probe";
    runtimeInputs = [
      (pkgs.python3.withPackages (ps: [ ps.pygobject3 ]))
      pkgs.systemd
    ];
    text = ''
      export GI_TYPELIB_PATH="${lib.makeSearchPath "lib/girepository-1.0" [ pkgs.glib pkgs.gobject-introspection ]}"
      exec python3 ${./probe-session.py}
    '';
  };
  systemd.user.settings.Manager.DefaultEnvironment = [
    "XDG_CURRENT_DESKTOP=GNOME" "XDG_SESSION_TYPE=wayland" "WAYLAND_DISPLAY=runic-wayland"
  ];
}
