# Single instance and login startup on Plasma 6, in a VM. It runs
# tests/native/Runic.Lifetime.Smoke (published with NativeAOT; its path in
# RUNIC_LIFETIME_SMOKE), launches it again so the running instance receives the
# launch, enables login startup, logs in again and expects the smoke to start
# with the session. It needs KVM and runs outside the sandbox:
#
#   nix build .#application-lifetime-plasma.driver
#   RUNIC_LIFETIME_SMOKE=/path/to/Runic.Lifetime.Smoke result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
{
  plasma = pkgs.testers.runNixOSTest {
    name = "runic-application-lifetime-plasma";
    nodes.machine = {
      imports = [ (pkgs.path + "/nixos/tests/common/user-account.nix") ];
      virtualisation = {
        memorySize = 4096;
        cores = 4;
      };
      # Kept across logins, unlike the user's runtime directory.
      systemd.tmpfiles.rules = [ "d /var/lib/test 0777 root root -" ];
      services.xserver.enable = true;
      services.displayManager.plasma-login-manager.enable = true;
      services.displayManager.defaultSession = "plasmax11";
      services.desktopManager.plasma6.enable = true;
      services.displayManager.autoLogin = {
        enable = true;
        user = "alice";
      };
    };
    testScript = ''
      import os
      from datetime import timedelta

      smoke = "/var/lib/test/lifetime"
      log_file = "/var/lib/test/log"
      entry = "/home/alice/.config/autostart/dev.runic.LifetimeSmoke.desktop"

      def user(command):
          return machine.succeed(
              "runuser -u alice -- env DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
              f" XDG_RUNTIME_DIR=/run/user/1000 {command}"
          )

      def logged(line):
          machine.wait_until_succeeds(f"grep -qF -- '{line}' {log_file}", timeout=timedelta(seconds=60))

      def session():
          machine.wait_until_succeeds("pgrep -u alice plasmashell", timeout=timedelta(minutes=5))
          machine.wait_until_succeeds("test -S /run/user/1000/bus")

      start_all()
      session()
      machine.copy_from_host(os.environ["RUNIC_LIFETIME_SMOKE"], smoke)
      machine.succeed(f"chmod 755 {smoke}")

      with subtest("A second launch reaches the running instance"):
          user(f"systemd-run --user --unit lifetime {smoke} {log_file} first")
          logged("primary [/var/lib/test/log first]")
          forwarded = user(f"env XDG_ACTIVATION_TOKEN=token-1 {smoke} {log_file} second")
          assert forwarded.startswith("forwarded [/var/lib/test/log second]"), forwarded
          logged("launched [/var/lib/test/log second]")
          logged(" token=token-1 ")

      with subtest("Login startup writes the autostart entry"):
          user(f"{smoke} {log_file} --enable-login")
          logged("enable success")
          logged("enabled True")
          print(machine.succeed(f"cat {entry}"))
          machine.succeed(f"grep -qx 'X-Runic-Application=dev.runic.LifetimeSmoke' {entry}")
          user(f"{smoke} {log_file} --quit")
          logged("quit")
          machine.wait_until_fails("pgrep -f '^/var/lib/test/[l]ifetime'")

      with subtest("The next login starts the application"):
          # Log out completely, user manager included, then log in again.
          machine.succeed("systemctl stop display-manager.service")
          machine.succeed("loginctl terminate-user alice || true")
          machine.wait_until_fails("systemctl is-active user@1000.service", timeout=timedelta(minutes=2))
          machine.succeed("systemctl start display-manager.service")
          session()
          logged("primary [/var/lib/test/log --from-login]")
          machine.screenshot("relogin")

      with subtest("Disabling removes the entry"):
          user(f"{smoke} {log_file} --disable-login")
          logged("disable success")
          logged("enabled False")
          machine.fail(f"test -e {entry}")
          user(f"{smoke} {log_file} --quit")
          machine.wait_until_fails("pgrep -f '^/var/lib/test/[l]ifetime'")
          print(machine.succeed(f"cat {log_file}"))
    '';
  };
}
