# Web workspace

The framework-neutral SDK and each supported framework adapter own separate
workspace packages under `web/packages`.

Wave E adds independent `@webuitoolkit/mvvm-react`,
`@webuitoolkit/mvvm-vue`, and `@webuitoolkit/mvvm-svelte` packages over the
frozen SDK. Their shared compatibility contract is
`protocol/mvvm/g5/framework-adapter-matrix.json`; Angular remains deferred to
Wave F.
