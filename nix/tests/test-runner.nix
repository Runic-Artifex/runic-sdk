# buildRunicApplication's doCheck with testRunner = "Microsoft.Testing.Platform": a TUnit test project, which
# rejects dotnet test's VSTest options, from tests/fixtures/nix/test-runner. The test writes a marker, so a check
# that ran no test fails too.
{ pkgs, runic }:
let
  fixture = ../../tests/fixtures/nix/test-runner;
in
runic.buildRunicApplication pkgs {
  pname = "runic-test-runner-fixture";
  version = "0.0.0";
  # The flake's source has only the files Git tracks, without build output.
  src = fixture;
  projectFile = "Fixture/Fixture.csproj";
  testProjectFile = "Fixture.Tests/Fixture.Tests.csproj";
  nugetLockFiles = [
    (fixture + "/Fixture/packages.lock.json")
    (fixture + "/Fixture.Tests/packages.lock.json")
  ];
  host = null;
  executables = [ "Fixture" ];
  doCheck = true;
  testRunner = "Microsoft.Testing.Platform";
  preCheck = ''
    export FIXTURE_TEST_MARKER=$PWD/test-marker
  '';
  postCheck = ''
    [[ $(< test-marker) == Greets_by_name ]]
  '';
}
