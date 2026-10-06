export type Page =
  | { readonly kind: "counter"; connect(): Promise<unknown> }
  | { readonly kind: "welcome"; connect(): Promise<unknown> };
export type CounterPage = Extract<Page, { readonly kind: "counter" }>;
export type WelcomePage = Extract<Page, { readonly kind: "welcome" }>;
