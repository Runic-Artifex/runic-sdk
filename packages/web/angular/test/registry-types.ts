// Type-level checks for ViewRegistry; compiled by `tsc -p tsconfig.test.json` in the test script, never run.
import type { InputSignal } from "@angular/core";
import type { ViewRegistry } from "../dist/esm/index.js";

type Counter = { readonly kind: "counter"; connect(): Promise<unknown> };
type Welcome = { readonly kind: "welcome"; connect(): Promise<unknown> };
type Page = Counter | Welcome;

declare class CounterPage { readonly page: InputSignal<Counter>; }
declare class WelcomePage { readonly page: InputSignal<Welcome>; }

export const complete = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
// @ts-expect-error A registry must have a component for every kind.
export const missing = { counter: CounterPage } satisfies ViewRegistry<Page>;
// @ts-expect-error A component's page input must accept the reference of its kind.
export const wrong = { counter: WelcomePage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
