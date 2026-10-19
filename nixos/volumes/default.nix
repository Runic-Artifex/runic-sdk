# Volumes against a real UDisks2 and Polkit, in a VM with an empty second disk. It installs tests/native/Runic.Volumes.Smoke
# (published with NativeAOT; its publish directory in RUNIC_VOLUMES_SMOKE), formats the second disk and, as ordinary
# users, lists, mounts and unmounts it: alice through a Polkit rule that allows mounting, bob without one. A watch
# started by alice reports a mount made by root, a loop device that appears and disappears, and UDisks2 stopping. It
# needs KVM and runs outside the sandbox:
#
#   nix build .#volumes-udisks2.driver
#   RUNIC_VOLUMES_SMOKE=/path/to/publish result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
{
  udisks2 = pkgs.testers.runNixOSTest {
    name = "runic-volumes-udisks2";
    nodes.machine = {
      imports = [ (pkgs.path + "/nixos/tests/common/user-account.nix") ];
      services.udisks2.enable = true;
      security.polkit.enable = true;
      # The second disk is not removable, so mounting it asks for org.freedesktop.udisks2.filesystem-mount-system.
      security.polkit.extraConfig = ''
        polkit.addRule(function(action, subject) {
          if (action.id.indexOf("org.freedesktop.udisks2.filesystem-mount") == 0 && subject.user == "alice") return polkit.Result.YES;
        });
      '';
      virtualisation.emptyDiskImages = [ 64 ];
      environment.systemPackages = [ pkgs.e2fsprogs pkgs.dosfstools pkgs.util-linux ];
      systemd.tmpfiles.rules = [ "d /var/lib/runic-volumes 0755 root root -" ];
    };
    testScript = ''
      import os
      import shlex

      program = "/var/lib/runic-volumes/Runic.Volumes.Smoke"
      publish = os.environ["RUNIC_VOLUMES_SMOKE"]
      disk = "/org/freedesktop/UDisks2/block_devices/vdb"

      def smoke(user, *arguments):
          command = " ".join(shlex.quote(a) for a in [program, *arguments])
          output = machine.succeed(f"cd /tmp && runuser -u {user} -- {command} 2>&1")
          print(output)
          return output

      def result(output):
          return [line for line in output.splitlines() if line.startswith("result: ")][-1]

      def volume(output, id):
          return next(line for line in output.splitlines() if line.startswith(f"volume {id} "))

      machine.wait_for_unit("multi-user.target")
      machine.copy_from_host(f"{publish}/Runic.Volumes.Smoke", program)
      machine.succeed(f"chown root:root {program} && chmod 755 {program}")
      machine.succeed("mkfs.ext4 -q -L RUNICDATA /dev/vdb && udevadm settle")

      with subtest("An ordinary user lists the formatted disk"):
          output = smoke("alice", "list")
          assert result(output).startswith("result: success "), output
          line = volume(output, disk)
          assert "name=RUNICDATA fs=ext4 mounts=[] " in line, line
          assert "removable=False system=True locked=False mount=True unmount=False eject=False" in line, line

      with subtest("A user whom Polkit allows mounts and unmounts it"):
          output = smoke("alice", "mount", disk)
          assert result(output).startswith(f"result: success volume {disk} name=RUNICDATA fs=ext4 mounts=[/run/media/alice/RUNICDATA] "), output
          machine.succeed("findmnt -n -o SOURCE /run/media/alice/RUNICDATA | grep -qx /dev/vdb")
          assert "mounts=[/run/media/alice/RUNICDATA] " in volume(smoke("alice", "list"), disk)
          assert result(smoke("alice", "mount", disk)).startswith(f"result: success volume {disk} name=RUNICDATA fs=ext4 mounts=[/run/media/alice/RUNICDATA] "), "mounting a mounted volume returns it"

      with subtest("Another user cannot unmount it, and the disk cannot be ejected"):
          output = smoke("bob", "unmount", disk)
          assert result(output) == "result: failed PermissionDenied org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain", output
          machine.succeed("findmnt /run/media/alice/RUNICDATA")
          assert result(smoke("alice", "eject", disk)) == "result: unavailable BackendUnavailable"

      with subtest("Unmounting twice succeeds"):
          assert result(smoke("alice", "unmount", disk)) == "result: success unmounted"
          machine.fail("findmnt /dev/vdb")
          assert result(smoke("alice", "unmount", disk)) == "result: success unmounted"

      with subtest("A user whom Polkit does not allow is denied"):
          output = smoke("bob", "mount", disk)
          assert result(output) == "result: failed PermissionDenied org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain", output
          machine.fail("findmnt /dev/vdb")

      with subtest("An unknown volume is not found"):
          assert result(smoke("alice", "mount", "/org/freedesktop/UDisks2/block_devices/missing")) == "result: failed NotFound "

      with subtest("A watch reports changes other processes make"):
          watch_log = "/tmp/watch.log"
          machine.succeed(f"runuser -u alice -- {program} watch 120 > {watch_log} 2>&1 & echo $! > /tmp/watch.pid")

          def watched(pattern):
              machine.wait_until_succeeds(f"grep -qE -- {shlex.quote(pattern)} {watch_log}", timeout=60)

          watched(r"^change: snapshot [0-9]+$")
          machine.succeed("udisksctl mount --no-user-interaction -b /dev/vdb")
          watched(rf"^change: changed volume {disk} name=RUNICDATA fs=ext4 mounts=\[/run/media/root/RUNICDATA\] ")
          machine.succeed("udisksctl unmount --no-user-interaction -b /dev/vdb")
          watched(rf"^change: changed volume {disk} name=RUNICDATA fs=ext4 mounts=\[\] ")

          machine.succeed("truncate -s 16M /tmp/loop.img && mkfs.vfat -n RUNICLOOP /tmp/loop.img")
          loop = machine.succeed("udisksctl loop-setup --no-user-interaction -f /tmp/loop.img").split(" as ")[1].strip().rstrip(".")
          loop_id = "/org/freedesktop/UDisks2/block_devices/" + os.path.basename(loop)
          watched(rf"^change: added volume {loop_id} name=RUNICLOOP fs=vfat ")
          machine.succeed(f"udisksctl loop-delete --no-user-interaction -b {loop}")
          watched(rf"^change: removed {loop_id}$")

          # The watch reads the objects again once the name is gone, which D-Bus activates UDisks2 for and cancels a
          # waiting stop job.
          machine.succeed("systemctl stop --no-block udisks2.service")
          watched(r"^change: unavailable BackendUnavailable$")
          machine.succeed("systemctl start udisks2.service")
          machine.wait_until_succeeds(f"test $(grep -cE '^change: snapshot [0-9]+$' {watch_log}) -ge 2", timeout=60)
          machine.succeed("kill $(cat /tmp/watch.pid)")
          print(machine.succeed(f"cat {watch_log}"))

      print(machine.succeed("journalctl -u udisks2 --no-pager"))
    '';
  };
}
