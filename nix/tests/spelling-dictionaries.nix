# Evaluates buildRunicApplication's spellingDictionaries without building: the dictionaries' share directories go on
# the wrapper's XDG_DATA_DIRS for both GTK hosts, nothing is added without them, and a host without WebKitGTK rejects
# them.
{ pkgs, self }:
let
  inherit (pkgs) lib;
  dictionaries = [
    pkgs.hunspellDicts.de_CH
    pkgs.hunspellDicts.en_US
  ];
  build =
    host: spellingDictionaries:
    self.lib.buildRunicApplication pkgs {
      pname = "spelling-dictionaries-check";
      version = "1";
      src = ./.;
      projectFile = "App.csproj";
      inherit host spellingDictionaries;
    };
  expected = [
    "--prefix"
    "XDG_DATA_DIRS"
    ":"
    "${pkgs.hunspellDicts.de_CH}/share:${pkgs.hunspellDicts.en_US}/share"
  ];
  check =
    host:
    # buildDotnetModule appends its own LD_LIBRARY_PATH.
    assert lib.assertMsg (lib.take 4 (build host dictionaries).makeWrapperArgs == expected)
      "${host}: the wrapper does not put the dictionaries on XDG_DATA_DIRS";
    assert lib.assertMsg (!lib.elem "XDG_DATA_DIRS" (build host [ ]).makeWrapperArgs)
      "${host}: the wrapper changed XDG_DATA_DIRS without dictionaries";
    host;
in
assert lib.assertMsg (!(builtins.tryEval (build "cswebui" dictionaries).makeWrapperArgs).success)
  "cswebui accepted spellingDictionaries";
pkgs.runCommand "runic-builder-spelling-dictionaries" {
  checked = [
    (check "desktop")
    (check "desktop-gtk4")
  ];
} "touch $out"
