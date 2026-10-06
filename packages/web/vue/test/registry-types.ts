// Type-level checks for ViewRegistry; compiled by `tsc` in the test script, never run.
import { defineComponent, type PropType } from "vue";
import type { ViewRegistry } from "../dist/index.js";

type Counter = { readonly kind: "counter"; connect(): Promise<unknown> };
type Welcome = { readonly kind: "welcome"; connect(): Promise<unknown> };
type Page = Counter | Welcome;

const CounterPage = defineComponent({ props: { page: { type: Object as PropType<Counter>, required: true } } });
const WelcomePage = defineComponent({ props: { page: { type: Object as PropType<Welcome>, required: true } } });

export const complete = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
// @ts-expect-error A registry must have a component for every kind.
export const missing = { counter: CounterPage } satisfies ViewRegistry<Page>;
// @ts-expect-error A component's page prop must accept the reference of its kind.
export const wrong = { counter: WelcomePage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
