// Type-level checks for ViewRegistry; checked by svelte-check in the test script, never run.
import type { ViewRegistry } from "../src/views/index.js";
import Counter from "./fixtures/Counter.svelte";
import type { Page } from "./fixtures/pages.js";
import Welcome from "./fixtures/Welcome.svelte";

export const complete = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page>;
// @ts-expect-error A registry must have a component for every kind.
export const missing = { counter: Counter } satisfies ViewRegistry<Page>;
// @ts-expect-error A component's page prop must accept the reference of its kind.
export const wrong = { counter: Welcome, welcome: Welcome } satisfies ViewRegistry<Page>;
