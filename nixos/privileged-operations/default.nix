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
        "d /run/runic-privileged-smoke 0755 root root -"
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
          # A SHELL that /etc/shells does not list, as in a development shell, made pkexec exit with 127.
          output = machine.succeed(f"cd /tmp && runuser -u alice -- env RUNIC_CALLER_SECRET=1 SHELL=/nix/store/00000000000000000000000000000000-bash/bin/bash {prefix}{command} 2>&1")
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
          assert "out: helper dev.runic.privileged-smoke.allowed caller=1000 cwd=/\n" in output + "\n", output
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

      smoke_log = "/run/runic-privileged-smoke/log"

      def logged(line):
          machine.wait_until_succeeds(f"grep -qF -- '{line}' {smoke_log}", timeout=30)

      def start_caller(*arguments):
          # setpriv executes the caller directly, so its process ID is the caller's.
          command = " ".join(shlex.quote(a) for a in [program, "run", *arguments])
          machine.succeed(f"rm -f {smoke_log}")
          return machine.succeed(f"setpriv --reuid=alice --regid=users --init-groups {command} >/dev/null 2>&1 & echo $!").strip()

      with subtest("The helper resets the working directory, umask, ignored signals and limits the caller chose"):
          state = machine.succeed(
              # nohup ignores SIGHUP; an ignored signal survives exec.
              "runuser -u alice -- sh -c \"umask 000; ulimit -S -n 256; cd /tmp;"
              f" exec nohup /run/wrappers/bin/pkexec --keep-cwd {program} allowed state\" 2>&1")
          print(state)
          before_line = next(l for l in state.splitlines() if l.startswith("state before "))
          after_line = next(l for l in state.splitlines() if l.startswith("state after "))
          field = lambda line, key: line.split(f" {key}=")[1].split()[0]
          assert field(before_line, "cwd") == "/tmp" and field(before_line, "umask") == "0000", f"--keep-cwd and the caller's umask reach the program: {before_line}"
          assert field(after_line, "cwd") == "/" and field(after_line, "umask") == "0022", after_line
          assert "state created mode=644" in state, state
          before, after = int(field(before_line, "sigign"), 16), int(field(after_line, "sigign"), 16)
          assert before & 1 and not after & 1, f"SIGHUP was ignored ({before:x}) and is default again ({after:x})"
          assert after & ~(1 << 12) == 0, f"only SIGPIPE, which .NET ignores itself, stays ignored: {after:x}"
          assert int(field(after_line, "sigblk"), 16) == 0, after_line
          assert "state limit Max core file size: 0 0" in state, state
          nofile = int(state.split("state limit Max open files: ")[1].split()[0])
          assert nofile >= 1024, f"the caller's soft limit of 256 open files was raised: {nofile}"

      with subtest("When the caller dies, a helper that watches its input stops"):
          caller = start_caller("allowed", "wait")
          logged("waiting")
          machine.succeed(f"kill -KILL {caller}")
          logged("stopped by caller")
          machine.wait_until_fails("pgrep -u root -f '[R]unic.Privileged.Smoke allowed wait'", timeout=30)

      with subtest("When the caller dies, a helper that ignores its input runs on, and a child writing to the closed output fails"):
          caller = start_caller("allowed", "ignore")
          logged("ignoring input")
          machine.succeed(f"kill -KILL {caller}")
          logged("finished")
          ignore = machine.succeed(f"cat {smoke_log}")
          print(ignore)
          # .NET's console drops writes to a closed pipe silently, so the helper itself goes on.
          assert "still running 6" in ignore and "standard output is closed" not in ignore, ignore
          # The child inherits an ignored SIGPIPE from the .NET helper, so its write fails with EPIPE instead.
          assert "child exited with 1" in ignore, "a child writing to the closed output fails"

      with subtest("Without pam_systemd the root helper stays in the caller's cgroup, which the caller can kill"):
          machine.succeed("loginctl enable-linger alice")
          machine.wait_for_unit("user@1000.service")
          machine.succeed(f"rm -f {smoke_log}")
          machine.succeed("runuser -u alice -- env XDG_RUNTIME_DIR=/run/user/1000 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
                          f" systemd-run --user --unit=privileged-cgroup {program} run allowed ignore")
          logged("ignoring input")
          helper = machine.succeed(f"grep -o 'pid [0-9]*' {smoke_log}").split()[1]
          cgroup = machine.succeed(f"cut -d: -f3 /proc/{helper}/cgroup").strip()
          print(f"helper {helper} (uid {machine.succeed(f'stat -c %u /proc/{helper}').strip()}) is in {cgroup}")
          assert "/user@1000.service/" in cgroup, cgroup
          machine.succeed(f"runuser -u alice -- sh -c 'echo 1 > /sys/fs/cgroup{cgroup}/cgroup.kill'")
          machine.wait_until_fails(f"test -e /proc/{helper}", timeout=30)
          machine.fail(f"grep -q finished {smoke_log}")

      print(machine.succeed("journalctl -t pkexec --no-pager"))
    '';
  };
}
