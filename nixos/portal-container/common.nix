# Shared isolated desktop services for the managed Linux container runner.
{ config, lib, pkgs, ... }:
{
  imports = [ ../desktop-automation.nix ];
  services.flatpak.enable = true;
  virtualisation.vlans = [ ];
  system.stateVersion = "26.11";
  users.users.runic = {
    isNormalUser = true;
    uid = 1000;
    linger = true;
    initialPassword = "runic";
  };
  hardware.graphics.enable = true; # Includes llvmpipe; no GPU device is passed through.
  services.dbus.enable = true;
  services.pipewire = { enable = true; pulse.enable = true; };
  programs.dconf.enable = true;
  # Independent synthetic output for screen-reader capture; no host audio bind.
  services.pipewire.extraConfig.pipewire."90-runic-test-sink" = {
    "context.objects" = [ {
      factory = "adapter";
      args = {
        "factory.name" = "support.null-audio-sink";
        "node.name" = "runic-test-speakers";
        "node.description" = "Runic test speakers";
        "media.class" = "Audio/Sink";
        "audio.position" = [ "FL" "FR" ];
      };
    } ];
  };
  # Generic Linux NativeAOT publishes use /lib64/ld-linux-x86-64.so.2.
  # Preserve /proc/self/exe and GTK's application identity when starting them.
  programs.nix-ld.enable = true;
  environment.systemPackages = with pkgs; [ gtk4 (lib.getBin glib) dbus
    config.system.build.runicContainerFixture config.system.build.runicSessionProbe
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
        # These limits resolve against current_user_ns(), not the host namespace.
        # Flatpak needs to set its child's max_user_namespaces to zero.
        "${pkgs.util-linux}/bin/mount --bind /run/runic-proc-private/proc/sys/user /proc/sys/user"
      ];
      ExecStop = [
        "${pkgs.util-linux}/bin/umount /proc/sys/user"
        "${pkgs.util-linux}/bin/umount /run/runic-proc-private/proc"
      ];
    };
  };
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
}
