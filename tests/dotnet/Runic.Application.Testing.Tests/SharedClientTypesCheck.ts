// [RunicSharedClient] (#839), type-checked with the generated clients: one component renders every ViewModel that
// implements ISharedResultPage through the generated shared client, and a registry maps each of their kinds to it,
// without a cast. A member that the shared client does not have, such as a renamed one, does not compile.
import { registryEntries, type PageReference, type ViewClient, type ViewRegistryPage, type ViewSource } from "@runic-artifex/views";
import type { NavHomePageReference } from "./obj/bridge-frontend/generated/navHome.js";
import {
  resultPageKinds, type ResultPageClient, type ResultPagePageReference, type ResultPageRunActionOperation, type ResultPageState,
} from "./obj/bridge-frontend/generated/resultPage.js";
import { pageSharedInvoices, type SharedInvoicesClient } from "./obj/bridge-frontend/generated/sharedInvoices.js";
import {
  pageSharedOrders, pageSharedOrdersCompact, type SharedOrdersClient, type SharedOrdersCompactPageReference, type SharedOrdersPageReference,
} from "./obj/bridge-frontend/generated/sharedOrders.js";
import type { SharedInvoicesPageReference } from "./obj/bridge-frontend/generated/sharedInvoices.js";
import type { SharedResultsState } from "./obj/bridge-frontend/generated/sharedResults.js";

// The shared component, written once against the shared client.
export async function renderResultPage(page: ResultPagePageReference): Promise<string> {
  const client: ResultPageClient = await page.connect();
  const state: ResultPageState = client.snapshot;
  await client.setFilter(null);
  await client.runAction(state.page.elements[0]?.id ?? "");
  const operation: ResultPageRunActionOperation = await client.startRunAction("o-1");
  void operation;
  // @ts-expect-error Only SharedOrdersViewModel publishes openOrders, so the shared state does not have it.
  void state.openOrders;
  // @ts-expect-error A renamed member does not compile: the shared state has `page`, not `content`.
  void state.content;
  // @ts-expect-error The shared client sets only the members the interface declares settable.
  void client.setPage;
  return `${page.kind}: ${state.page.title}`;
}

// Every implementing reference, of each of its Views, is a ResultPagePageReference, and a slot typed as the interface
// renders through the same component.
export const pages: readonly ResultPagePageReference[] = [pageSharedOrders("1"), pageSharedOrdersCompact("2"), pageSharedInvoices("3")];
export function renderSlot(state: SharedResultsState): Promise<string> | null {
  return state.current ? renderResultPage(state.current) : null;
}

// A framework's useView(page) infers the shared client from the shared reference.
declare function useView<TClient extends ViewClient>(source: ViewSource<TClient>): TClient;
export const viewed = (page: ResultPagePageReference): ResultPageState => {
  const client = useView(page);
  const inferred: [typeof client] extends [ResultPageClient] ? [ResultPageClient] extends [typeof client] ? true : false : false = true;
  return inferred ? client.snapshot : client.snapshot;
};

// A ViewModel's own client is the shared client too.
export const sharedClients: readonly ((client: SharedOrdersClient | SharedInvoicesClient) => ResultPageClient)[] = [client => client];

// The kinds are those of every View whose ViewModel implements the interface.
export const kinds: readonly ResultPagePageReference["kind"][] = resultPageKinds;
export const kindsComplete: ResultPagePageReference["kind"] extends (typeof resultPageKinds)[number] ? true : false = true;

// The frameworks' ViewRegistry types map each kind to a component of ViewRegistryPage<R, K>. With an invariant page, as
// an Angular input is, the shared component is accepted only because each generated reference names the shared one.
type Component<P> = { readonly page: { readonly read: () => P; readonly write: (value: P) => void } };
type Registry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: ViewRegistryPage<R, K> extends infer P ? P extends unknown ? Component<P> : never : never;
};
declare const ResultPageView: Component<ResultPagePageReference>;
declare const NavHomeView: Component<NavHomePageReference>;
type Results = SharedOrdersPageReference | SharedOrdersCompactPageReference | SharedInvoicesPageReference;

export const registry = { ...registryEntries(resultPageKinds, ResultPageView), navHome: NavHomeView } satisfies Registry<Results | NavHomePageReference | null>;
export const slotRegistry = { sharedOrders: ResultPageView, sharedInvoices: ResultPageView } satisfies Registry<SharedResultsState["current"]>;
// @ts-expect-error The shared component does not register for a kind outside the shared reference.
export const outside = { ...registryEntries(resultPageKinds, ResultPageView), navHome: ResultPageView } satisfies Registry<Results | NavHomePageReference>;
