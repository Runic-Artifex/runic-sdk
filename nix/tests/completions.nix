# buildRunicApplication's completions: a Runic.CommandLine program's bash, zsh and fish scripts, printed by its
# built-in completion command and installed where the shells look, from tests/fixtures/nix/completions.
{ pkgs, runic }:
let
  fixture = ../../tests/fixtures/nix/completions;
in
runic.buildRunicApplication pkgs {
  pname = "runic-completions-fixture";
  version = "0.0.0";
  # The flake's source has only the files Git tracks, without build output.
  src = fixture;
  projectFile = "App/App.csproj";
  nugetLockFiles = [ (fixture + "/App/packages.lock.json") ];
  host = null;
  executables = [ "hello" ];
  completions = { };
  meta.mainProgram = "hello";
  doInstallCheck = true;
  installCheckPhase = ''
    runHook preInstallCheck
    grep -q _runic_hello $out/share/bash-completion/completions/hello.bash
    grep -qx '#compdef hello' <(head -n 1 $out/share/zsh/site-functions/_hello)
    grep -q "complete -c 'hello'" $out/share/fish/vendor_completions.d/hello.fish
    runHook postInstallCheck
  '';
}
