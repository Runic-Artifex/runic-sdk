{
  lib,
  pkgs,
  portalDesktop,
  runicDevShell,
  runicSource,
  ...
}:

let
  portalTest = pkgs.writeShellApplication {
    name = "runic-portal-test";
    runtimeInputs = [
      pkgs.coreutils
      pkgs.nix
      pkgs.rsync
    ];
    text = ''
      usage() {
        printf '%s\n' \
          'Usage: runic-portal-test <settings|native|gtk4|activation-live|activation-submit|activation-receive|notifications|open|choose|reveal>' \
          "" \
          'Runs a source snapshot mounted by the Runic portal VM.' \
          'The desktop-service modes open real portal UI; notifications waits for the Open result action.'
      }

      mode="''${1:-}"
      case "$mode" in
        settings)
          project="tests/dotnet/Runic.Platform.Linux.Portal.Tests"
          arguments=(--settings)
          ;;
        native)
          project="tests/dotnet/Runic.Platform.Prototype.Tests"
          arguments=(--native)
          ;;
        gtk4|activation-live|activation-submit|activation-receive)
          project="tests/native/Runic.Desktop.Gtk4.Smoke"
          arguments=()
          export RUNIC_TEST_APP_ID="com.runic.tests.Activation"
          if test "$mode" != gtk4; then
            arguments=("--notification-''${mode#activation-}")
          fi
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
        -h|--help|"") usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
      esac

      source=/home/runic/src
      cache=/home/runic/.cache
      workspace="$cache/runic-portal-workspace"
      marker="$workspace/.runic-source"
      mkdir -p "$cache"
      test -d "$source" || { echo "Runic source share is unavailable." >&2; exit 1; }
      # /home/runic/src is a stable read-only mount point. Compare the immutable
      # flake source path instead, so a rebuilt VM refreshes its copy.
      source_identity="${toString runicSource}"
      candidate=""
      cleanup_candidate() {
        test -z "$candidate" || rm -rf "$candidate"
      }
      trap cleanup_candidate EXIT
      if test ! -f "$marker" || test "$(cat "$marker")" != "$source_identity"; then
        candidate=$(mktemp -d "$cache/runic-portal-workspace.next.XXXXXX")
        rsync -a --chmod=u+rwX --exclude=.git "$source/" "$candidate/"
        printf '%s\n' "$source_identity" > "$candidate/.runic-source"
        rm -rf "$workspace"
        mv "$candidate" "$workspace"
        candidate=""
      fi

      cd "$workspace"
      # Enter the immutable mounted flake, rather than treating this growing
      # build workspace as a path flake. Its shell hook supplies the locked Bun,
      # .NET wrapper and GTK/WebKit runtime library paths.
      # This is intentionally repeated: Bun makes it a cheap no-op after the
      # first successful install, while a failed first install remains retryable.
      nix develop "$source" --command bun install --frozen-lockfile
      export DOTNET_CLI_HOME="$PWD/.cache/dotnet"
      export NUGET_PACKAGES="$PWD/.cache/nuget"
      export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
      nix develop "$source" --command dotnet run --project "$project" -c Release -- "''${arguments[@]}"
    '';
  };
  activationReceiver = pkgs.writeShellScript "runic-activation-receiver" ''
    export RUNIC_TEST_ACTIVATION_RECEIPT=/home/runic/.cache/runic-activation-receipt
    exec > /home/runic/.cache/runic-activation-receive.log 2>&1
    exec ${portalTest}/bin/runic-portal-test activation-receive
  '';
  activationService = pkgs.writeTextDir "share/dbus-1/services/com.runic.tests.Activation.service" ''
    [D-BUS Service]
    Name=com.runic.tests.Activation
    Exec=${activationReceiver}
  '';
  activationDesktopItem = pkgs.makeDesktopItem {
    name = "com.runic.tests.Activation";
    desktopName = "Runic Activation Test";
    comment = "Notification activation and GTK4 focus fixture";
    exec = "${portalTest}/bin/runic-portal-test activation-live";
    terminal = false;
    categories = [ "Development" ];
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
  # The VM shares the exact Git-aware flake source snapshot. The helper keeps
  # one bounded on-disk workspace and refreshes it by immutable source identity.
  virtualisation.vmVariant = {
    virtualisation = {
      memorySize = 4096;
      cores = 4;
      diskSize = 12 * 1024;
      # Register the locked toolchain closure already shared from the host.
      # Extra store writes belong on disk, not the default half-RAM tmpfs.
      additionalPaths = [ runicDevShell ];
      writableStoreUseTmpfs = false;
      diskImage = "./runic-portal-${portalDesktop}.qcow2";
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
  system.name = "runic-portal-${portalDesktop}";
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
  nix.settings.experimental-features = [
    "nix-command"
    "flakes"
  ];
  services.dbus.enable = true;
  services.dbus.packages = [ activationService ];
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
    activationDesktopItem
    activationService
    git
    curl
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
