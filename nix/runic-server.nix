# The native Desktop server (native/runic-server, X1) built from source with the crates native/Cargo.lock
# pins, and its Rust tests run in the check phase (they bind loopback sockets, which the build sandbox has). The
# result has lib/librax_server.so, lib/librax_server.a and include/rax_server.h; a Nix-built application can
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
      # The workspace's other members (members = ["runic-server", "crates/*"]): Cargo loads every member.
      ../native/crates
    ];
  };
  cargoLock.lockFile = ../native/Cargo.lock;
  cargoBuildFlags = [ "-p" "runic-artifex-server" ];
  cargoTestFlags = [ "-p" "runic-artifex-server" ];
  postInstall = ''
    install -Dm644 runic-server/include/rax_server.h "$out/include/rax_server.h"
  '';
  meta = {
    description = "Experimental native HTTP and WebSocket server for Runic Desktop surfaces";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
  };
}
