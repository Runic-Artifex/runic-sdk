{
  lib,
  pkgs,
  runicSource,
  ...
}:

let
  portalTest = pkgs.writeShellApplication {
    name = "runic-portal-test";
    runtimeInputs = [
      pkgs.coreutils
      pkgs.dotnetCorePackages.sdk_10_0
      pkgs.rsync
    ];
    text = ''
            usage() {
              cat <<'EOF'
      Usage: runic-portal-test <settings|notifications|open|choose|reveal>

      Runs a source snapshot mounted by the Runic portal VM. The desktop-service modes
      open real portal UI; notifications waits for the Open result action.
      EOF
            }

            mode="''${1:-}"
            case "$mode" in
              settings)
                project="tests/dotnet/Runic.Platform.Linux.Portal.Tests"
                arguments=(--settings)
                ;;
              notifications|open|choose|reveal)
                project="tests/dotnet/Runic.Platform.Prototype.Tests"
                arguments=(--native-services)
                export RUNIC_TEST_APP_ID="com.runic.tests.Portal"
                case "$mode" in
                  notifications) export RUNIC_TEST_SERVICE=Notifications ;;
                  open) export RUNIC_TEST_SERVICE=Open ;;
                  choose) export RUNIC_TEST_SERVICE=ChooseApplication ;;
                  reveal) export RUNIC_TEST_SERVICE=Reveal ;;
                esac
                ;;
              -h|--help|"") usage; exit "''${mode:+0}" ;;
              *) usage >&2; exit 2 ;;
      esac

      source=/home/runic/src
      mkdir -p /home/runic/.cache/nuget /home/runic/.cache/nuget-http /home/runic/.cache/dotnet
      test_root=$(mktemp -d /home/runic/.cache/runic-portal-test.XXXXXX)
      trap 'rm -rf "$test_root"' EXIT
      rsync -a --exclude=.git "$source/" "$test_root/source/"
            cd "$test_root/source"
            export DOTNET_CLI_HOME=/home/runic/.cache/dotnet
            export NUGET_PACKAGES=/home/runic/.cache/nuget
            export NUGET_HTTP_CACHE_PATH=/home/runic/.cache/nuget-http
      dotnet run --project "$project" -c Release -- "''${arguments[@]}"
    '';
  };
  portalDesktopItem = pkgs.makeDesktopItem {
    name = "com.runic.tests.Portal";
    desktopName = "Runic Portal Test";
    comment = "Identity for Runic's portal integration fixture";
    exec = "${portalTest}/bin/runic-portal-test notifications";
    terminal = true;
    categories = [ "Development" ];
  };
in
{
  # The VM shares the exact Git-aware flake source snapshot. The helper copies
  # it to its own disk before build/restore, leaving source inputs immutable.
  virtualisation.vmVariant = {
    virtualisation = {
      memorySize = 4096;
      cores = 4;
      diskSize = 12 * 1024;
      graphics = true;
      resolution = {
        x = 1440;
        y = 900;
      };
      sharedDirectories.runic-source = {
        source = runicSource;
        target = "/home/runic/src";
      };
    };
  };

  boot.kernelParams = [ "console=ttyS0" ];
  # These defaults make the configurations valid NixOS systems as well as VM
  # variants, which lets `nix flake check` evaluate them. qemu-vm mounts the
  # label on its disposable image and bypasses the boot loader.
  boot.loader.grub = {
    enable = true;
    device = "/dev/vda";
  };
  fileSystems."/" = {
    device = "/dev/disk/by-label/nixos";
    fsType = "ext4";
  };
  networking.hostName = "runic-portal";
  networking.useDHCP = lib.mkDefault true;
  time.timeZone = "UTC";

  users.users.runic = {
    isNormalUser = true;
    description = "Runic portal test user";
    extraGroups = [ "wheel" ];
    initialPassword = "runic";
  };

  security.polkit.enable = true;
  programs.dconf.enable = true;
  services.dbus.enable = true;
  services.pipewire = {
    enable = true;
    pulse.enable = true;
  };
  services.xserver.enable = true;
  services.displayManager.autoLogin = {
    enable = true;
    user = "runic";
  };

  xdg = {
    portal = {
      enable = true;
      xdgOpenUsePortal = true;
    };
    mime.enable = true;
  };

  environment.systemPackages = with pkgs; [
    portalTest
    portalDesktopItem
    git
    curl
    dotnetCorePackages.sdk_10_0
    nodejs_24
    bun
    rsync

    # Match the Linux native runtime dependencies supplied by the SDK flake.
    gtk3
    gtk4
    webkitgtk_4_1
    webkitgtk_6_0
    glib-networking
    gsettings-desktop-schemas
    dbus
    xdg-desktop-portal
  ];

  system.stateVersion = "26.11";
}
