# Component fixtures

Two guests, each with a page and a `component` build target (`runic({ component })`): `alpha` renders with React and
`beta` with Vue. Both style `.label` and connect the route `counter`, so `component.browser.test.mjs` can show that two
components on one page share no tags, CSS or state.
