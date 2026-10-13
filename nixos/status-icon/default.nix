# The status icon on real status areas, in VMs: Plasma 6, and Waybar's tray on
# Hyprland. Each boots a desktop, runs tests/native/Runic.StatusIcon.Smoke
# (published with NativeAOT; its path in RUNIC_STATUS_ICON_SMOKE), clicks the
# icon and its menu through the status area, and restarts the watcher. They need
# KVM and run outside the sandbox:
#
#   nix build .#status-icon-plasma.driver
#   RUNIC_STATUS_ICON_SMOKE=/path/to/Runic.StatusIcon.Smoke result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
let
  userAccount = pkgs.path + "/nixos/tests/common/user-account.nix";
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
  };
  # Shared test helpers: the smoke app runs in alice's session and logs to /run/test/log.
  helpers = ''
    import os
    import subprocess
    from datetime import timedelta

    smoke = os.environ["RUNIC_STATUS_ICON_SMOKE"]

    def user(command):
        return machine.succeed(
            "runuser -u alice -- env DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
            f" XDG_RUNTIME_DIR=/run/user/1000 {command}"
        )

    def logged(line):
        machine.wait_until_succeeds(f"grep -qF '{line}' /run/test/log", timeout=timedelta(seconds=60))

    def items():
        return user(
            "busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher"
            " org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems"
        )

    def find_icon(left, top, width, height):
        # The smoke icon is a #1e5ac8 square; status areas scale and tint it, so match any clear blue.
        # An icon covers a few hundred pixels; more means the status area is not drawn yet.
        for _ in range(30):
            machine.screenshot("find")
            pixels = subprocess.run(
                ["${pkgs.imagemagick}/bin/magick", str(machine.out_dir / "find.png"), "-crop", f"{width}x{height}+{left}+{top}",
                 "+repage", "-depth", "8", "txt:-"],
                check=True, capture_output=True, text=True,
            ).stdout
            blue = []
            for line in pixels.splitlines()[1:]:
                position, colour = line.split(":", 1)
                r, g, b = (int(value) for value in colour.split("(")[1].split(")")[0].split(",")[:3])
                if b > 170 and b - r > 90 and b - g > 40:
                    blue.append(tuple(int(value) for value in position.split(",")))
            if 50 < len(blue) < width * height // 4:
                x = sum(point[0] for point in blue) // len(blue)
                y = sum(point[1] for point in blue) // len(blue)
                return f"{left + x} {top + y}"
            machine.sleep(2)
        raise Exception("The status icon is not in the status area")

    def start_smoke():
        machine.copy_from_host(smoke, "/run/test/smoke")
        machine.succeed("chmod 755 /run/test/smoke")
        user("systemd-run --user --unit smoke /run/test/smoke /run/test/log")
        logged("show success")
        machine.wait_until_succeeds(
            "runuser -u alice -- env DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
            " busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher"
            " org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems | grep -q StatusNotifierItem-",
            timeout=timedelta(seconds=30),
        )
  '';
in
{
  plasma = pkgs.testers.runNixOSTest {
    name = "runic-status-icon-plasma";
    nodes.machine = {
      imports = [ common ];
      # X11, as nixpkgs' own Plasma test, so xdotool can click the panel.
      services.xserver.enable = true;
      services.displayManager.plasma-login-manager.enable = true;
      services.displayManager.defaultSession = "plasmax11";
      services.desktopManager.plasma6.enable = true;
      services.displayManager.autoLogin = {
        enable = true;
        user = "alice";
      };
      environment.systemPackages = [ pkgs.xdotool ];
    };
    testScript = helpers + ''
      def desktop_window():
          # A loaded host can take minutes; xwininfo fails until X answers.
          machine.wait_until_succeeds("DISPLAY=:0 xwininfo -root -tree | grep -q \"Desktop \"", timeout=timedelta(minutes=5))

      def xdotool(arguments):
          machine.succeed(f"DISPLAY=:0 xdotool {arguments}")
          machine.sleep(2)

      # The tray, left of the clock in the panel along the bottom.
      def icon():
          return find_icon(960, 760, 200, 30)


      start_all()
      with subtest("Plasma starts"):
          machine.wait_for_file("/run/user/1000/xauth_*")
          machine.wait_until_succeeds("test -s /run/user/1000/xauth_*")
          machine.succeed("xauth merge /run/user/1000/xauth_*")
          machine.wait_until_succeeds("pgrep plasmashell")
          desktop_window()
          machine.sleep(10)

      with subtest("The icon registers with Plasma's watcher"):
          start_smoke()
          print(items())
          machine.sleep(3)
          machine.screenshot("plasma-tray")

      with subtest("Plasma activates the icon and its menu"):
          xdotool(f"mousemove {icon()} click 1")
          logged("activated Primary -")
          xdotool(f"mousemove {icon()} click 3")
          machine.screenshot("plasma-menu")
          # Down selects Open window, then Check daily.
          xdotool("key Down Down Return")
          logged("activated MenuItem daily")
          logged("update daily success")
          x, y = (int(value) for value in icon().split())
          xdotool(f"mousemove {x} {y} click 3")
          xdotool("key Down Down Down")
          # More is the third row of the menu that opens above the icon.
          # Plasma fills a submenu when it first opens, so it shows on the second hover.
          xdotool(f"mousemove {x + 40} {y - 62}")
          xdotool(f"mousemove {x + 40} {y - 96}")
          xdotool(f"mousemove {x + 40} {y - 62}")
          machine.sleep(3)
          machine.screenshot("plasma-submenu")
          # The submenu opens to the left, at the screen edge, level with More.
          xdotool(f"mousemove {x - 80} {y - 62} click 1")
          logged("activated MenuItem attention")
          logged("update attention success")
          machine.screenshot("plasma-attention")

      with subtest("The icon registers again after the watcher restarts"):
          print(user("systemctl --user list-units --no-pager --plain 'plasma-*'"))
          user("systemctl --user restart plasma-kded6.service")
          machine.wait_until_succeeds(
              "runuser -u alice -- env DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
              " busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher"
              " org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems | grep -q StatusNotifierItem-",
              timeout=timedelta(seconds=60),
          )
          user("systemctl --user restart plasma-plasmashell.service")
          desktop_window()
          machine.sleep(10)
          machine.screenshot("plasma-restarted")

      with subtest("Quit from the menu"):
          xdotool(f"mousemove {icon()} click 3")
          xdotool("key Up Return")
          logged("quit")
          print(machine.succeed("cat /run/test/log"))
    '';
  };
}
