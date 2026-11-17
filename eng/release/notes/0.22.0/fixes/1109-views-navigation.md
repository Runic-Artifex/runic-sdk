- Size: `Runic.Application.Views` and `Runic.Navigation` no longer use the LINQ operators
  that make every other LINQ query in a NativeAOT application larger (`Skip`, `OrderBy`,
  `GroupBy`, `MaxBy`, `Concat`, `SelectMany`), and about 30 internal records that never
  compare by value or print themselves are now plain classes. Behaviour is unchanged. This
  takes about 125 KB more off a CS-WebUI executable on Linux (#1109).
