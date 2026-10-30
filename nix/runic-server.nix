# The native Desktop server (native/runic-server, X1) built from source with the crates native/Cargo.lock
# pins, and its Rust tests run in the check phase (they bind loopback sockets, which the build sandbox has). The
# result has lib/librunic_server.so, lib/librunic_server.a and include/runic_server.h; a Nix-built application can
# use it instead of the prebuilt library in the Runic.Desktop.NativeServer package (rc-design/x1-native-server.md).
{ pkgs }:
let
  inherit (pkgs) lib;
  workspace = builtins.fromTOML (builtins.readFile ../native/Cargo.toml);
in
pkgs.rustPlatform.buildRustPackage {
  pname = "runic-server";
  inherit (workspace.workspace.package) version;
  src = lib.fileset.toSource {
    root = ../native;
    fileset = lib.fileset.unions [
      ../native/Cargo.toml
      ../native/Cargo.lock
      ../native/rustfmt.toml
      ../native/runic-server
    ];
  };
  cargoLock.lockFile = ../native/Cargo.lock;
  cargoBuildFlags = [ "-p" "runic-server" ];
  cargoTestFlags = [ "-p" "runic-server" ];
  postInstall = ''
    install -Dm644 runic-server/include/runic_server.h "$out/include/runic_server.h"
  '';
  meta = {
    description = "Experimental native HTTP and WebSocket server for Runic Desktop surfaces";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
  };
}
