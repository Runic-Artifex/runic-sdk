# Web workspace

The framework-neutral SDK and each supported framework adapter own separate
workspace packages under `web/packages`.

Wave E adds independent `@webuitoolkit/mvvm-react`,
`@webuitoolkit/mvvm-vue`, and `@webuitoolkit/mvvm-svelte` packages over the
frozen SDK. Wave F adds `@webuitoolkit/mvvm-angular` without introducing an
adapter-to-adapter dependency. The G5 and G6 compatibility contracts live in
`protocol/mvvm/g5/framework-adapter-matrix.json` and
`protocol/mvvm/g6/extended-adapter-matrix.json`.
