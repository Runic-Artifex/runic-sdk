# Web workspace

The framework-neutral SDK and each supported framework adapter own separate
workspace packages under `web/packages`.

Wave E adds independent `@runic-artifex/mvvm-react`,
`@runic-artifex/mvvm-vue`, and `@runic-artifex/mvvm-svelte` packages over the
frozen SDK. Wave F adds `@runic-artifex/mvvm-angular` without introducing an
adapter-to-adapter dependency. The G5 and G6 compatibility contracts live in
`protocol/mvvm/g5/framework-adapter-matrix.json` and
`protocol/mvvm/g6/extended-adapter-matrix.json`.
