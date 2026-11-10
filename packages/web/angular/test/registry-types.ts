// Type-level checks for ViewRegistry; compiled by `tsc -p tsconfig.test.json` in the test script, never run.
import type { InputSignal } from "@angular/core";
import { registryEntries, type sharedPageReferences } from "@runic-artifex/views";
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
// A region slot's reference is nullable; the registry checks its non-null kinds.
export const slot = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<Page | null>;
// @ts-expect-error A nullable reference still needs a component for every kind.
export const slotMissing = { counter: CounterPage } satisfies ViewRegistry<Page | null>;

// Shared clients ([RunicSharedClient]): generated references name the shared reference they extend under
// sharedPageReferences, so one component registers for each of its kinds without a cast.
type ResultClient = { readonly title: string };
type ResultPage = { readonly kind: "orders" | "invoices"; connect(): Promise<ResultClient> };
type Orders = { readonly kind: "orders"; connect(): Promise<ResultClient & { readonly total: number }>; readonly [sharedPageReferences]?: ResultPage };
type Invoices = { readonly kind: "invoices"; connect(): Promise<ResultClient>; readonly [sharedPageReferences]?: ResultPage };
// A reference that Orders and Invoices match structurally but do not name.
type Unnamed = { readonly kind: "orders" | "invoices"; connect(): Promise<unknown> };
type Slot = Orders | Invoices | Counter | null;

declare class ResultPageView { readonly page: InputSignal<ResultPage>; }
declare class UnnamedView { readonly page: InputSignal<Unnamed>; }

export const shared = { orders: ResultPageView, invoices: ResultPageView, counter: CounterPage } satisfies ViewRegistry<Slot>;
export const sharedEntries = { ...registryEntries(["orders", "invoices"] as const, ResultPageView), counter: CounterPage } satisfies ViewRegistry<Slot>;
// @ts-expect-error A shared component registers only for the kinds of its shared reference.
export const sharedWrongKind = { orders: ResultPageView, invoices: ResultPageView, counter: ResultPageView } satisfies ViewRegistry<Slot>;
// @ts-expect-error A page input must be the kind's reference or a shared reference it names.
export const unnamed = { orders: UnnamedView, invoices: ResultPageView, counter: CounterPage } satisfies ViewRegistry<Slot>;
