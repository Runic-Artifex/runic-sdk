# desktop-integration.py, which buildRunicApplication runs in postFixup, with a package that installs its own
# shared-mime-info definition in postInstall: the definition, with its magic and sub-class-of, stays as it is, the desktop
# entry still lists the type, and a definition without a declared type fails the build. builder-file-types covers the
# definition the build writes itself.
{ pkgs }:
let
  script = ../lib/desktop-integration.py;
  properties = pkgs.writeText "properties.json" (
    builtins.toJSON {
      Properties = {
        ApplicationId = "org.example.Worlds";
        ApplicationTitle = "Worlds";
        AssemblyName = "Worlds";
        RunicApplicationFileTypes = ".world=application/x-example-world;.atlas";
        RunicApplicationUrlSchemes = "";
      };
    }
  );
  own = pkgs.writeText "org.example.Worlds.xml" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
      <mime-type type="application/x-example-world">
        <comment>World</comment>
        <sub-class-of type="application/zip"/>
        <glob pattern="*.world"/>
        <magic priority="60">
          <match type="string" offset="30" value="mimetypeapplication/x-example-world"/>
        </magic>
      </mime-type>
      <mime-type type="application/x-worlds-atlas">
        <comment>Atlas</comment>
        <glob pattern="*.atlas"/>
      </mime-type>
    </mime-info>
  '';
  # Without the .atlas type, which RunicApplicationFileTypes declares.
  incomplete = pkgs.writeText "org.example.Worlds.xml" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
      <mime-type type="application/x-example-world">
        <glob pattern="*.world"/>
      </mime-type>
    </mime-info>
  '';
  entry = pkgs.writeText "org.example.Worlds.desktop" ''
    [Desktop Entry]
    Type=Application
    Name=Worlds
    Exec=worlds
  '';
in
pkgs.runCommand "runic-desktop-integration-check"
  {
    nativeBuildInputs = [
      pkgs.python3
      pkgs.desktop-file-utils
    ];
  }
  ''
    package() {
      local prefix=$1 definition=$2
      install -Dm644 ${entry} "$prefix/share/applications/org.example.Worlds.desktop"
      if [[ -n $definition ]]; then install -Dm644 "$definition" "$prefix/share/mime/packages/org.example.Worlds.xml"; fi
    }

    # The package's own definition stays, byte for byte, and the desktop entry lists both types.
    package kept ${own}
    python3 -I ${script} ${properties} App.csproj kept
    cmp ${own} kept/share/mime/packages/org.example.Worlds.xml
    grep -qx 'MimeType=application/x-example-world;application/x-worlds-atlas;' kept/share/applications/org.example.Worlds.desktop
    grep -qx 'Exec=worlds %U' kept/share/applications/org.example.Worlds.desktop
    desktop-file-validate kept/share/applications/org.example.Worlds.desktop
    mime=$(mktemp -d)
    mkdir "$mime/packages"
    cp kept/share/mime/packages/org.example.Worlds.xml "$mime/packages/"
    ${pkgs.shared-mime-info}/bin/update-mime-database "$mime"
    grep -q 'application/x-example-world' "$mime/magic"
    grep -qx 'application/x-example-world application/zip' "$mime/subclasses"

    # A link to a definition, as a postInstall that links files of another package installs it, stays a link.
    package linked ""
    mkdir -p linked/share/mime/packages
    ln -s ${own} linked/share/mime/packages/org.example.Worlds.xml
    python3 -I ${script} ${properties} App.csproj linked
    [[ $(readlink linked/share/mime/packages/org.example.Worlds.xml) == ${own} ]]

    # Without one, the build writes a definition by extension.
    package written ""
    python3 -I ${script} ${properties} App.csproj written
    grep -qF '<glob pattern="*.atlas"/>' written/share/mime/packages/org.example.Worlds.xml

    # A definition without a declared type fails, naming it.
    package incomplete ${incomplete}
    if python3 -I ${script} ${properties} App.csproj incomplete 2> error.txt; then
      echo "a definition without application/x-worlds-atlas was accepted" >&2
      exit 1
    fi
    cat error.txt
    grep -q 'does not define application/x-worlds-atlas' error.txt

    touch $out
  ''
