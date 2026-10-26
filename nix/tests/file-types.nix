# buildRunicApplication's file types and URL schemes: the project in tests/fixtures/nix/file-types declares
# RunicApplicationFileTypes and RunicApplicationUrlSchemes, and the package installs their shared-mime-info
# definitions and registers them in the desktop entry named after ApplicationId, as `dotnet runic package` does in a deb
# or rpm (tools/dotnet-runic/Packaging, PackageManifests.MimeInfo and the MimeType= of linux/app.desktop).
{ pkgs, runic }:
let
  fixture = ../../tests/fixtures/nix/file-types;
  # What `dotnet runic package` writes for the fixture's project; tests/dotnet/Runic.Application.Tool.Tests (PackageTests.cs)
  # checks the package command against the same files.
  expected = fixture + "/expected";
in
runic.buildRunicApplication pkgs {
  pname = "runic-file-types-fixture";
  version = "0.0.0";
  # The flake's source has only the files Git tracks, without build output.
  src = fixture;
  projectFile = "App/App.csproj";
  host = null;
  executables = [ "notes" ];
  desktopItems = [
    # The application's entry, which declares a MIME type of its own and runs the program without a field code.
    (pkgs.makeDesktopItem {
      name = "org.example.Notes";
      desktopName = "Notes & Co";
      exec = "notes";
      mimeTypes = [ "text/plain" ];
      actions.new-window = {
        name = "New Window";
        exec = "notes --new-window";
      };
    })
    # Another entry, which is not named after ApplicationId and stays as it is.
    (pkgs.makeDesktopItem {
      name = "org.example.Notes.Settings";
      desktopName = "Notes Settings";
      exec = "notes --settings";
    })
  ];
  meta.mainProgram = "notes";
  doInstallCheck = true;
  # shared-mime-info is run by its path: as an input, its setup hook would build a MIME cache in the package.
  nativeInstallCheckInputs = [
    pkgs.desktop-file-utils
    pkgs.libxml2
  ];
  installCheckPhase = ''
    runHook preInstallCheck
    entry=$out/share/applications/org.example.Notes.desktop
    cat "$entry"
    diff -u ${expected}/org.example.Notes.xml $out/share/mime/packages/org.example.Notes.xml
    xmllint --noout $out/share/mime/packages/org.example.Notes.xml
    # The entry's own type first, then the schemes and file types in the package command's order.
    grep -qxF "MimeType=text/plain;$(sed 's/^MimeType=//' ${expected}/MimeType.txt)" "$entry"
    grep -qx 'Exec=notes %U' "$entry"
    # Only the [Desktop Entry] group is changed: an action keeps its command.
    grep -qx 'Exec=notes --new-window' "$entry"
    [[ $(grep -c '^MimeType=' "$entry") == 1 ]]
    desktop-file-validate "$entry"
    ! grep -q '^MimeType=' $out/share/applications/org.example.Notes.Settings.desktop
    grep -qx 'Exec=notes --settings' $out/share/applications/org.example.Notes.Settings.desktop

    # shared-mime-info reads the definitions: each glob names its type.
    mime=$(mktemp -d)
    mkdir -p "$mime/packages"
    cp $out/share/mime/packages/org.example.Notes.xml "$mime/packages/"
    ${pkgs.shared-mime-info}/bin/update-mime-database "$mime"
    grep -q ':application/x-example-notes:\*\.notes$' "$mime/globs2"
    grep -q ':application/x-notes-tar.gz:\*\.tar\.gz$' "$mime/globs2"
    runHook postInstallCheck
  '';
}
