- The NativeAOT size budgets (`tests/scaling/aot-size`) name a size regression
  where it happens: `aot/framework/System.Net.Http` and
  `aot/framework/System.Net.Security` fail when either assembly is back in the
  CS-WebUI fixture on linux-x64 (limit 0), and `aot/assembly/<Runic assembly>`
  budgets the mstat bytes of Runic.Application.Views, Runic.Navigation,
  Runic.Application.Views.CsWebUi and Runic.Assets. A Runic Desktop host fixture
  (`aot/desktop/*`, no presentation server) and the CS-WebUI fixture with
  Translations (`aot/views-translations/executable-bytes`) are measured in the
  same step. `aot/executable-bytes` is tightened to the size after 0.22's
  reductions.
