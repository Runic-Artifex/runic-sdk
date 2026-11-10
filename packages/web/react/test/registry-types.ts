// Type-level checks for ViewRegistry; compiled by `tsc` in the test script, never run.
import { Component } from "react";
import { registryEntries, type sharedPageReferences } from "@runic-artifex/views";
import type { ViewRegistry } from "../dist/index.js";

type Counter = { readonly kind: "counter"; connect(): Promise<unknown> };
type Welcome = { readonly kind: "welcome"; connect(): Promise<unknown> };
type Page = Counter | Welcome;

declare function CounterPage(props: { readonly page: Counter }): null;
declare function WelcomePage(props: { readonly page: Welcome }): null;

export const complete = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
// @ts-expect-error A registry must have a component for every kind.
export const missing = { counter: CounterPage } satisfies ViewRegistry<Page>;
// @ts-expect-error A component's page prop must accept the reference of its kind.
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

declare function ResultPageView(props: { readonly page: ResultPage }): null;
declare class ResultPageClass extends Component<{ readonly page: ResultPage }> {}
declare class UnnamedClass extends Component<{ readonly page: Unnamed }> {}

export const shared = { orders: ResultPageView, invoices: ResultPageClass, counter: CounterPage } satisfies ViewRegistry<Slot>;
export const sharedEntries = { ...registryEntries(["orders", "invoices"] as const, ResultPageClass), counter: CounterPage } satisfies ViewRegistry<Slot>;
// @ts-expect-error A shared component registers only for the kinds of its shared reference.
export const sharedWrongKind = { orders: ResultPageView, invoices: ResultPageView, counter: ResultPageClass } satisfies ViewRegistry<Slot>;
// @ts-expect-error A class component's page must be the kind's reference or a shared reference it names.
export const unnamed = { orders: UnnamedClass, invoices: ResultPageView, counter: CounterPage } satisfies ViewRegistry<Slot>;
// A reference without shared references keeps the plain check.
export const plain = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<Page>;
