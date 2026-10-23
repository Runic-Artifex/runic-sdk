# Network status against a real NetworkManager where no desktop portal runs (B17, D039.3). The machine reaches a router
# on a private network; NetworkManager's connectivity check asks the router's web server, whose answer the test changes.
# It installs tests/native/Runic.Volumes.Smoke (published with NativeAOT; its publish directory in
# RUNIC_VOLUMES_SMOKE) and reads and watches the status as an ordinary user without a session bus: the expected answer
# is the internet, a wrong answer a captive portal, a metered connection profile metered, and the connection taken
# down no network. It needs KVM and runs outside the sandbox:
#
#   nix build .#network-status-networkmanager.driver
#   RUNIC_VOLUMES_SMOKE=/path/to/publish result/bin/nixos-test-driver --no-interactive -o out
{ pkgs }:
{
  networkmanager = pkgs.testers.runNixOSTest {
    name = "runic-network-status-networkmanager";
    nodes.router = {
      networking.firewall.allowedTCPPorts = [ 80 ];
      services.nginx.enable = true;
      services.nginx.virtualHosts.default = {
        default = true;
        root = "/srv/www";
      };
      systemd.tmpfiles.rules = [
        "d /srv/www 0755 root root -"
        "f /srv/www/check 0644 root root - online"
      ];
    };
    nodes.machine = { lib, ... }: {
      imports = [ (pkgs.path + "/nixos/tests/common/user-account.nix") ];
      # NetworkManager owns eth1 through a profile of its own, with the router as gateway; the test driver's static
      # address is dropped (the driver numbers the nodes by name: the router is 192.168.1.2). eth0, the driver's control link, stays unmanaged.
      networking.interfaces.eth1.ipv4.addresses = lib.mkForce [ ];
      networking.useDHCP = false;
      networking.networkmanager = {
        enable = true;
        unmanaged = [ "eth0" ];
        # No "Wired connection 1" with DHCP takes over once the profile is down.
        settings.main.no-auto-default = "*";
        settings.connectivity = {
          enabled = true;
          uri = "http://192.168.1.2/check";
          response = "online";
          interval = 5;
        };
        ensureProfiles.profiles.lan = {
          connection = { id = "lan"; type = "ethernet"; interface-name = "eth1"; autoconnect = true; };
          ipv4 = { method = "manual"; address1 = "192.168.1.100/24,192.168.1.2"; };
          ipv6.method = "disabled";
        };
      };
      systemd.tmpfiles.rules = [ "d /var/lib/runic-network 0755 root root -" ];
    };
    testScript = ''
      import os
      import shlex

      program = "/var/lib/runic-network/Runic.Volumes.Smoke"
      publish = os.environ["RUNIC_VOLUMES_SMOKE"]

      def smoke(*arguments):
          command = " ".join(shlex.quote(a) for a in [program, *arguments])
          output = machine.succeed(f"cd /tmp && runuser -u alice -- env -u DBUS_SESSION_BUS_ADDRESS {command} 2>&1")
          print(output)
          return output

      def result(output):
          return [line for line in output.splitlines() if line.startswith("result: ")][-1]

      def connectivity(expected):
          machine.wait_until_succeeds(f"nmcli -t networking connectivity check | grep -qx {expected}", timeout=60)

      start_all()
      router.wait_for_unit("nginx.service")
      router.wait_for_open_port(80)
      machine.wait_for_unit("NetworkManager.service")
      machine.copy_from_host(f"{publish}/Runic.Volumes.Smoke", program)
      machine.succeed(f"chown root:root {program} && chmod 755 {program}")
      machine.succeed("nmcli connection up lan")
      connectivity("full")

      machine.succeed(f"runuser -u alice -- env -u DBUS_SESSION_BUS_ADDRESS {program} network-watch 90 > /tmp/watch.log 2>&1 &")

      with subtest("The check's expected answer is the internet"):
          output = smoke("network")
          assert result(output) == "result: success network available=True connectivity=Internet metered=False", output

      with subtest("A wrong answer is a captive portal"):
          router.succeed("printf 'Sign in to the hotel network' > /srv/www/check")
          connectivity("portal")
          output = smoke("network")
          assert result(output).startswith("result: success network available=True connectivity=CaptivePortal "), output
          router.succeed("printf online > /srv/www/check")
          connectivity("full")

      with subtest("A metered connection profile is metered"):
          machine.succeed("nmcli connection modify lan connection.metered yes && nmcli connection up lan")
          connectivity("full")
          output = smoke("network")
          assert result(output) == "result: success network available=True connectivity=Internet metered=True", output

      with subtest("Without a connection there is no network"):
          machine.succeed("nmcli connection down lan")
          print(machine.succeed("nmcli -t -f STATE,CONNECTIVITY general; nmcli -t device"))
          connectivity("none")
          output = smoke("network")
          assert result(output).startswith("result: success network available=False connectivity=None "), output

      with subtest("A watch reports each state"):
          machine.wait_until_succeeds("grep -q 'connectivity=None' /tmp/watch.log", timeout=60)
          watched = machine.succeed("cat /tmp/watch.log")
          print(watched)
          states = [line for line in watched.splitlines() if line.startswith("state: ")]
          for expected in ["connectivity=Internet metered=False", "connectivity=CaptivePortal", "connectivity=Internet metered=True", "available=False connectivity=None"]:
              assert any(expected in state for state in states), f"{expected} missing from {states}"
          assert all(states[i] != states[i + 1] for i in range(len(states) - 1)), "a state was reported twice in a row"
    '';
  };
}
