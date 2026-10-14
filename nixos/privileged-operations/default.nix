# Privileged actions against a real Polkit, in a VM. It installs tests/native/Runic.Privileged.Smoke (published
# with NativeAOT; its publish directory in RUNIC_PRIVILEGED_SMOKE) and its generated policy, then runs each
# action as an ordinary user: one a rule allows, one a rule denies, one that needs a password with no
# authentication agent, and with an agent that dismisses the request, and one while polkitd is stopped. It needs
# KVM and runs outside the sandbox:
#
#   nix build .#privileged-operations-polkit.driver
#   RUNIC_PRIVILEGED_SMOKE=/path/to/publish result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
let
  agentPython = pkgs.python3.withPackages (p: [ p.dbus-next ]);
in
{
  polkit = pkgs.testers.runNixOSTest {
    name = "runic-privileged-operations-polkit";
    nodes.machine = {
      imports = [ (pkgs.path + "/nixos/tests/common/user-account.nix") ];
      security.polkit.enable = true;
      security.polkit.enablePkexecWrapper = true;
      security.polkit.extraConfig = ''
        polkit.addRule(function(action, subject) {
          if (action.id == "dev.runic.privileged-smoke.allowed" && subject.user == "alice") return polkit.Result.YES;
          if (action.id == "dev.runic.privileged-smoke.denied") return polkit.Result.NO;
        });
      '';
      # The declared program path: a directory only root can change.
      systemd.tmpfiles.rules = [
        "d /var/lib/runic-privileged 0755 root root -"
        "d /etc/polkit-1/actions 0755 root root -"
      ];
    };
    testScript = ''
      import os
      import shlex

      program = "/var/lib/runic-privileged/Runic.Privileged.Smoke"
      policy = "/etc/polkit-1/actions/dev.runic.privileged-smoke.policy"
      publish = os.environ["RUNIC_PRIVILEGED_SMOKE"]

      def run(*arguments, prefix=""):
          command = " ".join(shlex.quote(a) for a in [program, "run", *arguments])
          output = machine.succeed(f"cd /tmp && runuser -u alice -- env RUNIC_CALLER_SECRET=1 {prefix}{command} 2>&1")
          print(output)
          return output

      def result(output):
          return [line for line in output.splitlines() if line.startswith("result: ")][-1]

      machine.wait_for_unit("multi-user.target")
      machine.copy_from_host(f"{publish}/Runic.Privileged.Smoke", program)
      machine.copy_from_host(f"{publish}/share/polkit-1/actions/dev.runic.privileged-smoke.policy", policy)
      machine.succeed(f"chown root:root {program} {policy} && chmod 755 {program} && chmod 644 {policy}")
      machine.succeed("systemctl restart polkit.service")
      machine.wait_until_succeeds("pkaction --action-id dev.runic.privileged-smoke.prompt --verbose | grep -q 'exec.argv1.*prompt'")
      print(machine.succeed("pkaction --action-id dev.runic.privileged-smoke.prompt --verbose"))

      with subtest("An allowed action runs as root with only its argument vector"):
          output = run("allowed", "report", "3", "two words", "; touch /tmp/injected", "$(id)", "`id`", "a'b\"c")
          assert result(output) == "result: completed 3", output
          assert "out: helper dev.runic.privileged-smoke.allowed caller=1000 cwd=/root" in output, output
          for argument in ["two words", "; touch /tmp/injected", "$(id)", "`id`", "a'b\"c"]:
              assert f"out: arg <{argument}>" in output, argument
          assert "err: helper stderr line" in output, output
          assert "RUNIC_CALLER_SECRET" not in output, "pkexec passed the caller's environment"
          machine.fail("test -e /tmp/injected")

      with subtest("A rule that refuses is denied"):
          output = run("denied", "report", "0")
          assert result(output) == "result: denied", output
          assert "out: helper" not in output, output

      with subtest("No authentication agent is unavailable"):
          output = run("prompt", "report", "0")
          assert result(output) == "result: unavailable BackendUnavailable", output

      with subtest("Dismissing the authentication request is distinct from denial"):
          output = run("prompt", "--wait-for-go", "report", "0", prefix="AGENT_MODE=dismiss ${agentPython}/bin/python3 ${./agent.py} ")
          assert "agent: dismissing dev.runic.privileged-smoke.prompt: Authentication is required to run the <prompt> smoke step & check escaping." in output, output
          assert result(output) == "result: dismissed", output

      with subtest("Cancelling while the authentication dialog is open stops pkexec before the program starts"):
          output = run("prompt", "--wait-for-go", "--cancel-on", "agent-ready", "report", "0",
                       prefix="AGENT_MODE=hold ${agentPython}/bin/python3 ${./agent.py} ")
          assert "agent: holding dev.runic.privileged-smoke.prompt" in output, output
          assert result(output) == "result: cancelled", output
          assert "out: helper" not in output, output

      with subtest("Cancelling a running action closes its input and reports how it ended"):
          output = run("allowed", "--cancel-on", "helper waiting", "wait")
          assert "out: helper stopped by caller" in output, output
          assert result(output) == "result: completed 5", output

      with subtest("A program other users could replace is never started"):
          machine.succeed(f"chmod 777 {program}")
          output = machine.succeed(f"runuser -u alice -- {program} run allowed report 0 2>&1")
          print(output)
          assert result(output).startswith("result: failed InvalidData"), output
          machine.succeed(f"chmod 755 {program}")

      with subtest("Without polkitd the action is unavailable"):
          # NixOS's unit lives in /etc, which outranks a runtime mask; a runtime drop-in makes D-Bus activation fail.
          machine.succeed("mkdir -p /run/systemd/system/polkit.service.d"
                          " && printf '[Service]\\nExecStartPre=/run/current-system/sw/bin/false\\n' > /run/systemd/system/polkit.service.d/fail.conf"
                          " && systemctl daemon-reload && systemctl stop polkit.service")
          output = run("allowed", "report", "0")
          assert result(output) == "result: unavailable BackendUnavailable", output
          machine.succeed("rm -r /run/systemd/system/polkit.service.d && systemctl daemon-reload"
                          " && systemctl reset-failed polkit.service && systemctl start polkit.service")
          assert result(run("allowed", "report", "0")) == "result: completed 0"

      print(machine.succeed("journalctl -t pkexec --no-pager"))
    '';
  };
}
