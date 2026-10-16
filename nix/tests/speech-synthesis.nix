# Evaluates buildRunicApplication's withSpeechSynthesis without building WebKitGTK: false puts a WebKitGTK configured
# without speech synthesis and without flite on the wrapper's library path, for both GTK hosts; the default keeps the
# WebKitGTK of nixpkgs, which the binary cache has.
{ pkgs, self }:
let
  inherit (pkgs) lib;
  webkitOf =
    host: withSpeechSynthesis:
    lib.findFirst (package: lib.hasPrefix "webkitgtk" (package.pname or "")) (throw "no WebKitGTK for ${host}") (
      self.lib.buildRunicApplication pkgs {
        pname = "speech-synthesis-check";
        version = "1";
        src = ./.;
        projectFile = "App.csproj";
        inherit host withSpeechSynthesis;
      }
    ).dotnetRuntimeDeps;
  check =
    host: stock:
    let
      without = webkitOf host false;
    in
    assert lib.assertMsg ((webkitOf host true).drvPath == stock.drvPath) "${host}: the default changed WebKitGTK";
    assert lib.assertMsg (without.drvPath != stock.drvPath) "${host}: withSpeechSynthesis = false kept WebKitGTK";
    assert lib.assertMsg (lib.elem "-DENABLE_SPEECH_SYNTHESIS=OFF" without.cmakeFlags) "${host}: speech synthesis not disabled";
    assert lib.assertMsg (!lib.elem pkgs.flite without.buildInputs) "${host}: flite is still a build input";
    host;
in
pkgs.runCommand "runic-builder-without-speech-synthesis" {
  checked = [
    (check "desktop" pkgs.webkitgtk_4_1)
    (check "desktop-gtk4" pkgs.webkitgtk_6_0)
  ];
} "touch $out"
