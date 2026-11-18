- Size: `Runic.Application.Views` and `Runic.Navigation` compile less framework code in a
  NativeAOT application. Internal dictionary keys and async results that were value types
  are now classes, so they share the framework's generic code instead of compiling their
  own copies; the two packages share one fallback trace logger; and a few LINQ chains
  over value types are loops. Behaviour is unchanged. This takes about 150 KB off a
  CS-WebUI executable on Linux.
