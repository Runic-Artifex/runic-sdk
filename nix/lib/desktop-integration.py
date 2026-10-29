"""The file types and URL schemes of a Runic application, as `dotnet runic package` installs them on Linux.

usage: desktop-integration.py <properties.json> <project file> <prefix>

<properties.json> is the output of `dotnet msbuild <project> -getProperty:ApplicationId,ApplicationTitle,AssemblyName,
RunicApplicationFileTypes,RunicApplicationUrlSchemes`. With file types, writes their shared-mime-info definitions to
<prefix>/share/mime/packages/<ApplicationId>.xml, unless the package already installs that file (in postInstall): a
definition of the application's own, with magic or sub-class-of, stays as it is, and a type of RunicApplicationFileTypes
that it does not define is reported. With file types or URL schemes, adds their MIME types to MimeType=
of the desktop entry <prefix>/share/applications/<ApplicationId>.desktop (or, without it, of every desktop entry
there), and %U to its Exec= when it has no field code for files or URLs.

The parsing and the output follow tools/dotnet-runic/Packaging (PackageIdentity.ParseFileTypes, ParseUrlSchemes,
LinuxMimeType and PackageManifests.MimeInfo and AddLinuxFields), so a Nix package and the deb, rpm, AppImage and
Flatpak of the same project agree. Keep the two in step.
"""

import json
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ElementTree

APPLICATION_ID = re.compile(r"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$")
URL_SCHEME = re.compile(r"^[a-z][a-z0-9+.-]*$")
EXTENSION = re.compile(r"^\.[a-z0-9]+([.+-][a-z0-9]+)*$")
MIME_TYPE = re.compile(r"^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}$", re.IGNORECASE)
NON_MIME_CHARACTERS = re.compile(r"[^a-z0-9.+-]")
SEPARATORS = re.compile(r"[;, \t\r\n]+")
FIELD_CODE = re.compile(r"%[fFuU]")


def fail(message):
    print(f"error: buildRunicApplication: {message}", file=sys.stderr)
    sys.exit(1)


def split(value):
    return [entry for entry in SEPARATORS.split(value) if entry]


def url_schemes(value):
    schemes = [scheme.lower() for scheme in split(value)]
    for scheme in schemes:
        if not URL_SCHEME.match(scheme) or scheme in ("http", "https", "file"):
            fail(f"RunicApplicationUrlSchemes has '{scheme}', which is not a URL scheme an application can register.")
    return schemes


def file_types(value):
    types = []
    for entry in split(value):
        parts = entry.split("=")
        extension = parts[0].lower()
        if len(parts) > 2 or len(extension) > 64 or not EXTENSION.match(extension):
            fail(f"RunicApplicationFileTypes has '{entry}', which is not an extension with its dot, such as .notes or "
                 ".notes=application/x-example-notes.")
        mime_type = parts[1] if len(parts) == 2 else None
        if mime_type is not None and not MIME_TYPE.match(mime_type):
            fail(f"RunicApplicationFileTypes has '{entry}', whose MIME type is not type/subtype.")
        if any(existing == extension for existing, _ in types):
            fail(f"RunicApplicationFileTypes has {extension} twice.")
        types.append((extension, mime_type.lower() if mime_type is not None else None))
    return types


def linux_mime_type(application_id, extension, mime_type):
    if mime_type is not None:
        return mime_type
    last = application_id[application_id.rfind(".") + 1:].lower()
    return f"application/x-{last}-{NON_MIME_CHARACTERS.sub('-', extension[1:])}"


def escape_xml(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def mime_info(application_id, name, types):
    text = '<?xml version="1.0" encoding="UTF-8"?>\n<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">'
    for extension, mime_type in types:
        text += (f'\n  <mime-type type="{escape_xml(linux_mime_type(application_id, extension, mime_type))}">'
                 f"\n    <comment>{name} document ({extension})</comment>"
                 f'\n    <glob pattern="*{extension}"/>'
                 "\n  </mime-type>")
    return text + "\n</mime-info>\n"


def defined_mime_types(path):
    """The MIME types that a shared-mime-info definition defines, or None when it is not one."""
    try:
        root = ElementTree.parse(path).getroot()
    except (ElementTree.ParseError, OSError):
        return None
    namespace = "{http://www.freedesktop.org/standards/shared-mime-info}"
    if root.tag != namespace + "mime-info":
        return None
    return {element.get("type", "").lower() for element in root.findall(namespace + "mime-type")}


def write(path, text, mode):
    """Replaces the file, or the link to one, with a file of this text."""
    directory = os.path.dirname(path)
    os.makedirs(directory, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=directory)
    with os.fdopen(descriptor, "w", encoding="utf-8") as file:
        file.write(text)
    os.chmod(temporary, mode)
    os.replace(temporary, path)


def add_to_desktop_entry(path, mime_types):
    with open(path, encoding="utf-8") as file:
        lines = file.read().splitlines()
    group = None
    entry_end = None
    mime_line = None
    for index, line in enumerate(lines):
        if line.startswith("["):
            if group == "[Desktop Entry]":
                entry_end = index
            group = line.strip()
            continue
        if group != "[Desktop Entry]":
            continue
        if line.startswith("MimeType="):
            mime_line = index
        elif line.startswith("Exec=") and not FIELD_CODE.search(line):
            # The package command's entry runs `<executable> %U`: the desktop passes the files and URLs it opens.
            lines[index] = line.rstrip() + " %U"
    if group == "[Desktop Entry]":
        entry_end = len(lines)
    if entry_end is None:
        fail(f"{path} has no [Desktop Entry] group.")
    if mime_line is None:
        while entry_end > 0 and lines[entry_end - 1].strip() == "":
            entry_end -= 1
        lines.insert(entry_end, "MimeType=")
        mime_line = entry_end
    existing = [value for value in lines[mime_line][len("MimeType="):].split(";") if value]
    lines[mime_line] = "MimeType=" + "".join(value + ";" for value in existing + [t for t in mime_types if t not in existing])
    write(path, "\n".join(lines) + "\n", 0o444)


def main(arguments):
    if len(arguments) != 3:
        fail("usage: desktop-integration.py <properties.json> <project file> <prefix>")
    properties_file, project, prefix = arguments
    with open(properties_file, encoding="utf-8") as file:
        properties = json.load(file)["Properties"]

    def get(name):
        return (properties.get(name) or "").strip()

    schemes = url_schemes(get("RunicApplicationUrlSchemes"))
    types = file_types(get("RunicApplicationFileTypes"))
    if not schemes and not types:
        return
    application_id = get("ApplicationId")
    if len(application_id) > 50 or not APPLICATION_ID.match(application_id):
        fail(f"{project} declares file types or URL schemes, so its ApplicationId '{application_id}' must be a reverse-DNS "
             "name of ASCII letters and digits, each part starting with a letter, at most 50 characters (org.example.Notes).")
    assembly_name = get("AssemblyName") or os.path.splitext(os.path.basename(project))[0]
    name = escape_xml(get("ApplicationTitle") or assembly_name)

    if types:
        path = os.path.join(prefix, "share", "mime", "packages", application_id + ".xml")
        if os.path.lexists(path):
            # The application's own definition, which may say more than an extension can: magic, sub-class-of.
            defined = defined_mime_types(path)
            if defined is None:
                fail(f"{path} is not a shared-mime-info definition (<mime-info xmlns=\"http://www.freedesktop.org/"
                     "standards/shared-mime-info\">). Install a valid one, or none to have the build write it.")
            missing = [linux_mime_type(application_id, extension, mime_type) for extension, mime_type in types
                       if linux_mime_type(application_id, extension, mime_type) not in defined]
            if missing:
                fail(f"{path} does not define {', '.join(missing)}, which RunicApplicationFileTypes of {project} "
                     "declares. Define each in it, with <mime-type type=\"...\">, or remove the file to have the build "
                     "write it.")
            print(f"Kept the file types that the package installs in {path}")
        else:
            write(path, mime_info(application_id, name, types), 0o444)
            print(f"Installed the file types of {project} in {path}")

    mime_types = [f"x-scheme-handler/{scheme}" for scheme in schemes] + [
        linux_mime_type(application_id, extension, mime_type) for extension, mime_type in types]
    applications = os.path.join(prefix, "share", "applications")
    own = os.path.join(applications, application_id + ".desktop")
    if os.path.exists(own):
        entries = [own]
    elif os.path.isdir(applications):
        entries = sorted(os.path.join(applications, entry) for entry in os.listdir(applications) if entry.endswith(".desktop"))
    else:
        entries = []
    if not entries:
        print(f"{project} declares file types or URL schemes, but the package has no desktop entry to register them in "
              "(desktopItems).", file=sys.stderr)
    for entry in entries:
        add_to_desktop_entry(entry, mime_types)
        print(f"Added {';'.join(mime_types)} to {entry}")


if __name__ == "__main__":
    main(sys.argv[1:])
