# What each Runic host loads at run time on Linux. The libraries are loaded with dlopen, so they are
# runtime dependencies on LD_LIBRARY_PATH (and the NativeAOT executable's RUNPATH), never bundled.
#
#   desktop       Runic Desktop on GTK 3 and WebKitGTK 4.1
#   desktop-gtk4  Runic Desktop on GTK 4 and WebKitGTK 6.0
#   cswebui       CS-WebUI, which opens an installed browser. GTK and WebKitGTK stay off the library path:
#                 CS-WebUI initialises GTK whenever it can load it, even when serving without a display.
#   null          no host, such as a command-line program
{
  lib,
  glib,
  glib-networking,
  gsettings-desktop-schemas,
  gtk3,
  gtk4,
  webkitgtk_4_1,
  webkitgtk_6_0,
  wrapGAppsHook3,
  wrapGAppsHook4,
  gst_all_1,
}:
let
  gstreamer = with gst_all_1; [
    gstreamer
    gst-plugins-base
    gst-plugins-good
  ];
  desktop = gtk: webkit: wrapHook: {
    libraries = [
      glib
      gtk
      webkit
    ];
    # wrapGAppsHook turns these into XDG_DATA_DIRS (GSettings schemas), GIO_EXTRA_MODULES (TLS) and, with
    # media, GST_PLUGIN_SYSTEM_PATH_1_0 in the wrapper.
    buildInputs = [
      glib
      glib-networking
      gsettings-desktop-schemas
      gtk
      webkit
    ];
    inherit wrapHook gstreamer;
  };
  hosts = {
    desktop = desktop gtk3 webkitgtk_4_1 wrapGAppsHook3;
    desktop-gtk4 = desktop gtk4 webkitgtk_6_0 wrapGAppsHook4;
    cswebui = none;
  };
  none = {
    libraries = [ ];
    buildInputs = [ ];
    wrapHook = null;
    gstreamer = [ ];
  };
in
host:
if host == null then
  none
else
  hosts.${host} or (throw "Runic: host must be one of ${lib.concatStringsSep ", " (lib.attrNames hosts)}, not ${toString host}")
