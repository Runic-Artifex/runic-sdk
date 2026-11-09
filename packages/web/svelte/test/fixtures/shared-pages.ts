import type { sharedPageReferences } from "@runic-artifex/views";
import type { CounterPage } from "./pages.js";

// Shared clients ([RunicSharedClient]): generated references name the shared reference they extend under
// sharedPageReferences, so one component registers for each of its kinds without a cast.
type ResultClient = { readonly title: string };
export type ResultPage = { readonly kind: "orders" | "invoices"; connect(): Promise<ResultClient> };
type Orders = { readonly kind: "orders"; connect(): Promise<ResultClient & { readonly total: number }>; readonly [sharedPageReferences]?: ResultPage };
type Invoices = { readonly kind: "invoices"; connect(): Promise<ResultClient>; readonly [sharedPageReferences]?: ResultPage };
export type Slot = Orders | Invoices | CounterPage | null;
