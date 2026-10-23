# The Linux global menu (B10, D036.9) on real desktops, in VMs: Plasma 6 on X11 and on Wayland, and GNOME. Each boots
# the desktop and runs tests/native/Runic.Desktop.Gtk4.Smoke --global-menu-session for GTK 3 and GTK 4 (published with
# NativeAOT; its path in RUNIC_GLOBAL_MENU_SMOKE).
# - Plasma: with the Global Menu applet in the panel, kded owns com.canonical.AppMenu.Registrar and the window's bar goes
#   global. On X11 kded knows the window (GetMenuForWindow); on both, KWin reports the window's application menu
#   (hasApplicationMenu, read by a KWin script) and plasmashell reads the menu (GetLayout from plasmashell's connection,
#   seen by dbus-monitor). Hiding and showing the window keeps it, as GTK 3 replaces its wl_surface. Removing the applet
#   brings the bar back into the window; adding it again moves it out.
# - GNOME: no global menu. The bar stays in the window, also while another client owns the registrar, because the
#   compositor does not offer org_kde_kwin_appmenu.
# They need KVM and run outside the sandbox:
#
#   nix build .#global-menu-plasma-x11.driver
#   RUNIC_GLOBAL_MENU_SMOKE=/path/to/Runic.Desktop.Gtk4.Smoke result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
let
  lib = pkgs.lib;
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
  smokeEnvironment = lib.concatStringsSep " " [
    "--setenv=LD_LIBRARY_PATH=${lib.makeLibraryPath (with pkgs; [ gtk3 gtk4 webkitgtk_4_1 webkitgtk_6_0 glib ])}"
    "--setenv=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
    "--setenv=LIBGL_ALWAYS_SOFTWARE=1"
  ];
  # Owns the registrar name on the session bus, as a GNOME extension could; for the GNOME case.
  registrar = pkgs.writers.writePython3 "runic-test-registrar" {
    libraries = [ pkgs.python3Packages.pygobject3 ];
  } ''
    from gi.repository import Gio, GLib
    Gio.bus_own_name(Gio.BusType.SESSION, "com.canonical.AppMenu.Registrar",
                     Gio.BusNameOwnerFlags.NONE, None, None, None)
    GLib.MainLoop().run()
  '';
  common = {
    imports = [ userAccount ];
    virtualisation = {
      memorySize = 4096;
      cores = 4;
      resolution = {
        x = 1280;
        y = 800;
      };
    };
    systemd.tmpfiles.rules = [ "d /run/test 0777 root root -" ];
    environment.systemPackages = [ pkgs.dbus pkgs.gobject-introspection ];
    environment.etc."runic-test/registrar".source = registrar;
  };
  helpers = ''
    import os
    from datetime import timedelta

    smoke = os.environ["RUNIC_GLOBAL_MENU_SMOKE"]
    bus = "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus XDG_RUNTIME_DIR=/run/user/1000"

    def user(command):
        return machine.succeed(f"runuser -u alice -- env {bus} {command}")

    def logged(toolkit, line, timeout=60):
        machine.wait_until_succeeds(f"grep -qF -- '{line}' /run/test/{toolkit}.log", timeout=timedelta(seconds=timeout))

    def read_log(toolkit):
        return machine.succeed(f"cat /run/test/{toolkit}.log")

    def install_smoke():
        machine.copy_from_host(smoke, "/run/test/smoke")
        machine.succeed("chmod 755 /run/test/smoke")

    def start_smoke(toolkit):
        machine.succeed(f"rm -f /run/test/{toolkit}.log*")
        user(f"systemd-run --user --unit smoke-{toolkit} ${smokeEnvironment}"
             f" -p StandardOutput=file:/run/test/{toolkit}.out -p StandardError=file:/run/test/{toolkit}.err"
             f" /run/test/smoke --global-menu-session {toolkit} /run/test/{toolkit}.log")
        try:
            logged(toolkit, "open ")
        except Exception:
            print(machine.execute(f"cat /run/test/{toolkit}.out /run/test/{toolkit}.err")[1])
            raise

    def stop_smoke(toolkit):
        machine.succeed(f"touch /run/test/{toolkit}.log.quit")
        logged(toolkit, "closed")
        print(read_log(toolkit))

    def registrar_owned():
        return "true" in user("busctl --user call org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus NameHasOwner s com.canonical.AppMenu.Registrar")
  '';
  # Plasma's session, the Global Menu applet and what KWin, kded and plasmashell report about the window.
  plasmaHelpers = helpers + ''
    import re

    script_runs = 0

    def plasma_script(source):
        user("busctl --user call org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell evaluateScript s "
             + "'" + source + "'")

    def add_applet():
        plasma_script('panels()[0].addWidget("org.kde.plasma.appmenu")')
        machine.wait_until_succeeds(
            f"runuser -u alice -- env {bus} busctl --user call org.freedesktop.DBus /org/freedesktop/DBus"
            " org.freedesktop.DBus NameHasOwner s com.canonical.AppMenu.Registrar | grep -q true",
            timeout=timedelta(seconds=60))

    def remove_applet():
        plasma_script('panels().forEach(function (panel) { panel.widgets("org.kde.plasma.appmenu").forEach(function (widget) { widget.remove(); }); })')
        machine.wait_until_succeeds(
            f"runuser -u alice -- env {bus} busctl --user call org.freedesktop.DBus /org/freedesktop/DBus"
            " org.freedesktop.DBus NameHasOwner s com.canonical.AppMenu.Registrar | grep -q false",
            timeout=timedelta(seconds=60))

    # KWin's view of the window: activates it and reports Window.hasApplicationMenu through a KWin script.
    def kwin_application_menu(toolkit):
        global script_runs
        script_runs += 1
        tag = f"RUNIC-APPMENU-{script_runs}"
        machine.succeed(f"cat > /run/test/{tag}.js << 'EOF'\n"
                        f"const windows = workspace.windowList().filter(w => w.caption.indexOf('Runic Global Menu {toolkit}') >= 0);\n"
                        f"windows.forEach(w => {{ workspace.activeWindow = w; console.info('{tag} ' + w.hasApplicationMenu); }});\n"
                        f"if (windows.length === 0) console.info('{tag} none');\n"
                        "EOF")
        machine.succeed(f"chmod 644 /run/test/{tag}.js")
        reply = user(f"busctl --user call org.kde.KWin /Scripting org.kde.kwin.Scripting loadScript ss /run/test/{tag}.js {tag}")
        script = reply.split()[1]
        user(f"busctl --user call org.kde.KWin /Scripting/Script{script} org.kde.kwin.Script run")
        machine.wait_until_succeeds(f"journalctl _UID=1000 --no-pager | grep -q '{tag} '", timeout=timedelta(seconds=30))
        user(f"busctl --user call org.kde.KWin /Scripting org.kde.kwin.Scripting unloadScript s {tag}")
        line = machine.succeed(f"journalctl _UID=1000 --no-pager | grep '{tag} ' | tail -n 1")
        return line.split(f"{tag} ", 1)[1].strip()

    def start_monitor():
        user("systemd-run --user --unit dbusmenu-monitor -p StandardOutput=file:/run/test/monitor.log"
             " stdbuf -oL dbus-monitor --session \"type='method_call',interface='com.canonical.dbusmenu',member='GetLayout'\"")

    # plasmashell read the window's menu: a GetLayout from plasmashell's connection to a Runic menu path.
    def plasmashell_read_menu(since):
        def check(_):
            senders = re.findall(r"sender=(\S+) -> destination=\S+ serial=\d+ path=/org/runicartifex/MenuBar/\d+; interface=com.canonical.dbusmenu; member=GetLayout",
                                 machine.succeed(f"tail -c +{since + 1} /run/test/monitor.log"))
            for sender in set(senders):
                status, reply = machine.execute(f"runuser -u alice -- env {bus} busctl --user call org.freedesktop.DBus /org/freedesktop/DBus"
                                                f" org.freedesktop.DBus GetConnectionUnixProcessID s {sender}")
                # The wrapped executable's name is .plasmashell-wrapped.
                if status == 0 and "plasmashell" in machine.succeed(f"readlink /proc/{reply.split()[1]}/exe"):
                    return True
            return False
        retry(check, timeout=timedelta(seconds=60))

    def monitor_size():
        return int(machine.succeed("stat -c %s /run/test/monitor.log").strip())

    def plasma_case(toolkit, x11):
        with subtest(f"{toolkit}: the bar goes into Plasma's global menu"):
            since = monitor_size()
            start_smoke(toolkit)
            logged(toolkit, "x11" if x11 else "wayland")
            logged(toolkit, "Global")
            if x11:
                found = re.search(r"^xid ([0-9a-f]+)$", read_log(toolkit), re.M)
                assert found is not None, read_log(toolkit)
                xid = found.group(1)
                menu = user(f"busctl --user call com.canonical.AppMenu.Registrar /com/canonical/AppMenu/Registrar"
                            f" com.canonical.AppMenu.Registrar GetMenuForWindow u {int(xid, 16)}")
                print(menu)
                assert "/org/runicartifex/MenuBar/" in menu, menu
            retry(lambda _: kwin_application_menu(toolkit) == "true", timeout=timedelta(seconds=60))
            plasmashell_read_menu(since)
            machine.screenshot(f"{toolkit}-global")

        with subtest(f"{toolkit}: the global menu keeps the window after it is hidden and shown"):
            since = monitor_size()
            machine.succeed(f"touch /run/test/{toolkit}.log.hide")
            logged(toolkit, "shown ")
            print(read_log(toolkit))
            retry(lambda _: kwin_application_menu(toolkit) == "true", timeout=timedelta(seconds=60))
            plasmashell_read_menu(since)

        with subtest(f"{toolkit}: the bar follows the applet"):
            remove_applet()
            logged(toolkit, "placement Window")
            add_applet()
            machine.wait_until_succeeds(f"tail -n 1 /run/test/{toolkit}.log | grep -qx 'placement Global'", timeout=timedelta(seconds=60))
            retry(lambda _: kwin_application_menu(toolkit) == "true", timeout=timedelta(seconds=60))
            stop_smoke(toolkit)

    def plasma_session():
        machine.wait_until_succeeds("pgrep -u alice plasmashell", timeout=timedelta(minutes=5))
        machine.wait_until_succeeds("test -S /run/user/1000/bus")
        machine.wait_until_succeeds(
            f"runuser -u alice -- env {bus} busctl --user call org.kde.plasmashell /PlasmaShell org.freedesktop.DBus.Peer Ping",
            timeout=timedelta(minutes=2))
        machine.sleep(10)
        install_smoke()
        start_monitor()
        add_applet()
        machine.screenshot("plasma-applet")
  '';
  plasma = x11: pkgs.testers.runNixOSTest {
    name = "runic-global-menu-plasma-${if x11 then "x11" else "wayland"}";
    nodes.machine = {
      imports = [ common ];
      services.xserver.enable = x11;
      services.displayManager.plasma-login-manager.enable = true;
      services.displayManager.defaultSession = if x11 then "plasmax11" else "plasma";
      services.desktopManager.plasma6.enable = true;
      services.displayManager.autoLogin = {
        enable = true;
        user = "alice";
      };
    };
    testScript = plasmaHelpers + ''
      start_all()
      with subtest("Plasma starts with the Global Menu applet"):
          plasma_session()
      plasma_case("gtk3", ${if x11 then "True" else "False"})
      plasma_case("gtk4", ${if x11 then "True" else "False"})
      print(machine.succeed("grep -c GetLayout /run/test/monitor.log"))
    '';
  };
in
{
  plasma-x11 = plasma true;
  plasma-wayland = plasma false;

  gnome = pkgs.testers.runNixOSTest {
    name = "runic-global-menu-gnome";
    nodes.machine = {
      imports = [ common ];
      services.displayManager.gdm.enable = true;
      services.desktopManager.gnome.enable = true;
      services.displayManager.autoLogin = {
        enable = true;
        user = "alice";
      };
      services.gnome.gnome-initial-setup.enable = false;
      # The welcome tour would cover the window in the screenshots.
      environment.gnome.excludePackages = [ pkgs.gnome-tour ];
    };
    testScript = helpers + ''
      start_all()
      with subtest("GNOME starts"):
          machine.wait_until_succeeds("pgrep -u alice gnome-shell", timeout=timedelta(minutes=5))
          machine.wait_until_succeeds("test -S /run/user/1000/bus")
          machine.wait_until_succeeds("test -S /run/user/1000/wayland-0", timeout=timedelta(minutes=2))
          machine.sleep(10)
          install_smoke()

      for toolkit in ["gtk3", "gtk4"]:
          with subtest(f"{toolkit}: the bar stays in the window"):
              start_smoke(toolkit)
              logged(toolkit, f"open {toolkit} wayland placement=Window")
              machine.sleep(3)
              machine.screenshot(f"gnome-{toolkit}")

          with subtest(f"{toolkit}: also while another client owns the registrar"):
              assert not registrar_owned()
              user("systemd-run --user --unit registrar /etc/runic-test/registrar")
              machine.wait_until_succeeds(
                  f"runuser -u alice -- env {bus} busctl --user call org.freedesktop.DBus /org/freedesktop/DBus"
                  " org.freedesktop.DBus NameHasOwner s com.canonical.AppMenu.Registrar | grep -q true",
                  timeout=timedelta(seconds=30))
              machine.sleep(3)
              stop_smoke(toolkit)
              assert "placement Global" not in read_log(toolkit), read_log(toolkit)
              user("systemctl --user stop registrar")
    '';
  };
}
