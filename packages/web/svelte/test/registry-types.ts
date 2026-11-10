// Type-level checks for ViewRegistry; checked by svelte-check in the test script, never run.
import type { ViewRegistry } from "../src/views/index.js";
import Counter from "./fixtures/Counter.svelte";
import type { Page } from "./fixtures/pages.js";
import Welcome from "./fixtures/Welcome.svelte";
import ResultPage from "./fixtures/ResultPage.svelte";
import type { Slot } from "./fixtures/shared-pages.js";
import { registryEntries } from "@runic-artifex/views";

export const complete = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page>;
// @ts-expect-error A registry must have a component for every kind.
export const missing = { counter: Counter } satisfies ViewRegistry<Page>;
// @ts-expect-error A component's page prop must accept the reference of its kind.
export const wrong = { counter: Welcome, welcome: Welcome } satisfies ViewRegistry<Page>;
// A region slot's reference is nullable; the registry checks its non-null kinds.
export const slot = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page | null>;
// @ts-expect-error A nullable reference still needs a component for every kind.
export const slotMissing = { counter: Counter } satisfies ViewRegistry<Page | null>;

// Shared clients ([RunicSharedClient]): one component registers for each kind of its shared reference without a cast.
export const shared = { orders: ResultPage, invoices: ResultPage, counter: Counter } satisfies ViewRegistry<Slot>;
export const sharedEntries = { ...registryEntries(["orders", "invoices"] as const, ResultPage), counter: Counter } satisfies ViewRegistry<Slot>;
// @ts-expect-error A shared component registers only for the kinds of its shared reference.
export const sharedWrongKind = { orders: ResultPage, invoices: ResultPage, counter: ResultPage } satisfies ViewRegistry<Slot>;
