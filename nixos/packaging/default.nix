# Applications packaged with buildRunicApplication and installed through the NixOS module, in a VM. The status-icon
# example (NativeAOT) runs as the module's user service in an X session, a second launch reaches it, and
# systemctl --user stop ends it in order; the privileged smoke's Polkit policy is registered from the system profile,
# and pkexec runs its action through the package's wrapper. Both packages are built in the sandbox, so the test needs only KVM:
#
#   nix build .#packaging-nixos -L
{ pkgs, self }:
let
  examples = import ../../nix/examples.nix {
    inherit pkgs;
    runic = self.lib;
  };
  statusIcon = examples.status-icon-aot;
  smoke = examples.privileged-smoke;
in
pkgs.testers.runNixOSTest {
  name = "runic-packaging-nixos";
  nodes.machine = {
    imports = [
      (pkgs.path + "/nixos/tests/common/user-account.nix")
      self.nixosModules.runic-applications
    ];
    virtualisation.memorySize = 2048;
    services.xserver.enable = true;
    services.xserver.windowManager.icewm.enable = true;
    services.displayManager.autoLogin = {
      enable = true;
      user = "alice";
    };
    programs.runic.applications = {
      status-icon = {
        package = statusIcon;
        userService = {
          enable = true;
          # Serving without a window: the Window the second launch opens reports its URL in the journal.
          environment.RUNIC_APPLICATION_SERVE_ONLY = "1";
        };
      };
      privileged-smoke.package = smoke;
    };
    security.polkit.extraConfig = ''
      polkit.addRule(function(action, subject) {
        if (action.id == "dev.runic.privileged-smoke.allowed" && subject.user == "alice") return polkit.Result.YES;
      });
    '';
    systemd.tmpfiles.rules = [ "d /run/runic-privileged-smoke 0755 root root -" ];
  };
  testScript = ''
    from datetime import timedelta

    def user(command):
        return machine.succeed(
            "runuser -u alice -- env DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
            f" XDG_RUNTIME_DIR=/run/user/1000 PATH=/run/current-system/sw/bin {command} 2>&1"
        )

    machine.wait_for_x()

    with subtest("The desktop entry and the icon are installed"):
        machine.succeed("grep -qx 'Exec=StatusIconSample' /run/current-system/sw/share/applications/dev.runic.examples.StatusIcon.desktop")
        machine.succeed("test -f /run/current-system/sw/share/icons/hicolor/512x512/apps/dev.runic.examples.StatusIcon.png")

    with subtest("The user service starts with the graphical session"):
        machine.wait_until_succeeds("systemctl --machine=alice@.host --user is-active status-icon.service", timeout=timedelta(minutes=2))
        print(user("systemctl --user cat status-icon.service"))

    with subtest("A second launch reaches the user service's instance"):
        # The second instance hands its launch over and ends; one that became primary would run until the timeout.
        user("timeout 30 StatusIconSample")
        journal = "journalctl --no-pager -o cat _SYSTEMD_USER_UNIT=status-icon.service"
        machine.wait_until_succeeds(f"{journal} | grep -q RUNIC_APPLICATION_URL=", timeout=timedelta(seconds=60))
        url = machine.succeed(f"{journal} | grep -o 'RUNIC_APPLICATION_URL=[^ ]*' | tail -n 1 | cut -d= -f2-").strip()
        machine.succeed(f"curl -sf -o /dev/null -w '%{{http_code}}' '{url}' | grep -qE '^(200|302)$'")

    with subtest("The privileged actions' policy is registered"):
        program = "${smoke}/bin/Runic.Privileged.Smoke"
        details = machine.succeed("pkaction --action-id dev.runic.privileged-smoke.allowed --verbose")
        print(details)
        assert f"org.freedesktop.policykit.exec.path -> {program}" in details, details
        machine.succeed("pkaction --action-id dev.runic.privileged-smoke.prompt")

    with subtest("pkexec runs the action through the package's wrapper"):
        output = user("Runic.Privileged.Smoke run allowed report 3 'two words'")
        print(output)
        assert "result: completed 3" in output, output
        assert "out: helper dev.runic.privileged-smoke.allowed caller=1000" in output, output
        assert "out: arg <two words>" in output, output

    with subtest("systemctl --user stop ends the user service in order"):
        # SIGTERM ends the windowless application through DesktopHost.ShutdownRequested (#279): it hides the icon,
        # releases the single instance and exits 0, long before systemd's stop timeout escalates to SIGKILL.
        user("timeout 10 systemctl --user stop status-icon.service")
        result = user("systemctl --user show status-icon.service -p ActiveState -p Result -p ExecMainCode -p ExecMainStatus")
        print(result)
        assert "ActiveState=inactive" in result, result
        assert "Result=success" in result, result
        # Exited (code 1) with status 0, not killed by the signal.
        assert "ExecMainCode=1" in result, result
        assert "ExecMainStatus=0" in result, result
  '';
}
