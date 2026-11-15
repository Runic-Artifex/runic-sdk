// Trusted component mode for Angular guests (docs/hosted-regions.md, "Component mode"): the mount function that the
// `component` build target of `@runic-artifex/vite-plugin-runic` calls for each element. It starts one Angular
// application per element, zoneless, with the element's Runic instance, and renders the guest's standalone component
// into the element's shadow root.
//
// The component contract (`@runic-artifex/views/component`) allows no document stylesheets. Angular adds component
// styles as `<style>` elements to a style host: `document.head`, or the shadow root that contains the root component.
// Inside a shadow root they would apply, but a `<style>` element needs `'unsafe-inline'` in the host page's
// `style-src`, which the page should not have to grant. So the application's shared styles host (Angular's
// `ɵSHARED_STYLES_HOST`, which `DomRendererFactory2` and the root component use) is replaced by one that adopts each
// style as a constructable stylesheet in the shadow roots Angular hands it and nowhere else: nothing reaches the host
// document, and the host page's Content-Security-Policy does not apply to the sheets.
import {
  DestroyRef,
  destroyPlatform,
  inject,
  type ApplicationRef,
  type EnvironmentProviders,
  type Provider,
  type Type,
  provideZonelessChangeDetection,
  ɵSHARED_STYLES_HOST as SHARED_STYLES_HOST,
} from "@angular/core";
import { createApplication } from "@angular/platform-browser";
import type { RunicComponentMount } from "@runic-artifex/views/component";
// The primary entry point, not ./instance.js: this entry point's bundle would get a second RUNIC_INSTANCE token.
import { provideRunicInstance } from "@runic-artifex/angular";

/** Options of {@link runicComponentMount}. */
export interface RunicComponentMountOptions {
  /**
   * The providers of each element's application, after the ones the mount function adds (zoneless change detection,
   * the element's Runic instance and the shadow root's styles). Leave out the document-level ones, such as the router's
   * history, `provideBrowserGlobalErrorListeners` and `Title`: a component touches no document-level state.
   */
  readonly providers?: ReadonlyArray<Provider | EnvironmentProviders>;
}

/**
 * The mount function of an Angular guest's component: the default export of the component build's entry module.
 * Each element of the component gets an Angular application of its own, zoneless, whose `RUNIC_INSTANCE` is the
 * element's Runic instance, and `component`, a standalone component, renders in the element's shadow root. Its styles
 * (`styles`, `styleUrl`, with any `ViewEncapsulation` but `ShadowDom`) become constructable stylesheets of that shadow
 * root. Removing the element destroys the application and removes its stylesheets.
 *
 * ```ts
 * // src/component.ts, the `component.entry` of runic() in vite.config.ts
 * import { runicComponentMount } from "@runic-artifex/angular/experimental";
 * import { Widget } from "./app/widget";
 *
 * export default runicComponentMount(Widget);
 * ```
 */
export function runicComponentMount(component: Type<unknown>, options: RunicComponentMountOptions = {}): RunicComponentMount {
  if (typeof component !== "function") throw new TypeError("runicComponentMount needs a standalone component.");
  const extra = options.providers ?? [];
  return ({ root, container, instance }) => {
    let ended = false;
    let application: ApplicationRef | undefined;
    applications++;
    // createApplication resolves after the application's initializers; the element may be removed before.
    createApplication({
      providers: [
        provideZonelessChangeDetection(),
        provideRunicInstance(instance),
        { provide: SHARED_STYLES_HOST, useFactory: () => shadowRootStyles(root) },
        ...extra,
      ],
    }).then((created) => {
      if (ended) {
        created.destroy();
        release();
        return;
      }
      application = created;
      try { created.bootstrap(component, container); }
      catch (error) { console.error("The Angular component could not start.", error); }
    }, (error: unknown) => {
      release();
      if (!ended) console.error("The Angular component could not start.", error);
    });
    return () => {
      ended = true;
      const started = application;
      application = undefined;
      if (started === undefined) return;
      try { if (!started.destroyed) started.destroy(); }
      finally { release(); }
    };
  };
}

// The applications of this bundle that started or are starting. The bundle's copy of Angular creates its platform
// with the first; the last one destroys it, and with it what the platform added to the page (in development builds,
// the Angular DevTools listener on `window`).
let applications = 0;

function release(): void {
  if (--applications === 0) destroyPlatform();
}

// Ends with the application's injector.
function shadowRootStyles(root: ShadowRoot): ShadowRootStyles {
  const styles = new ShadowRootStyles(root);
  inject(DestroyRef).onDestroy(() => styles.destroy());
  return styles;
}

interface Usage<T> {
  usage: number;
  readonly value: T;
}

/**
 * The shared styles host of one element's application. Angular adds the shadow root that contains the root component
 * as a host (and the shadow root of each `ViewEncapsulation.ShadowDom` component); any other host, such as
 * `document.head` for a component created outside the shadow root, gets nothing.
 */
class ShadowRootStyles {
  readonly #hosts = new Set<ShadowRoot>();
  readonly #inline = new Map<string, Usage<CSSStyleSheet>>();
  // A style URL of a component built with external runtime styles: a <link> in each shadow root.
  readonly #external = new Map<string, Usage<HTMLLinkElement[]>>();

  constructor(root: ShadowRoot) {
    this.addHost(root);
  }

  addStyles(styles: readonly string[], urls?: readonly string[]): void {
    for (const style of styles) {
      const record = this.#inline.get(style);
      if (record !== undefined) {
        record.usage++;
        continue;
      }
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(style);
      this.#inline.set(style, { usage: 1, value: sheet });
      for (const host of this.#hosts) adopt(host, sheet);
    }
    for (const url of urls ?? []) {
      const record = this.#external.get(url);
      if (record !== undefined) {
        record.usage++;
        continue;
      }
      this.#external.set(url, { usage: 1, value: [...this.#hosts].map((host) => appendLink(host, url)) });
    }
  }

  removeStyles(styles: readonly string[], urls?: readonly string[]): void {
    for (const style of styles) {
      const record = this.#inline.get(style);
      if (record === undefined || --record.usage > 0) continue;
      this.#inline.delete(style);
      for (const host of this.#hosts) unadopt(host, [record.value]);
    }
    for (const url of urls ?? []) {
      const record = this.#external.get(url);
      if (record === undefined || --record.usage > 0) continue;
      this.#external.delete(url);
      for (const link of record.value) link.remove();
    }
  }

  addHost(node: Node): void {
    if (!isShadowRoot(node) || this.#hosts.has(node)) return;
    this.#hosts.add(node);
    for (const { value } of this.#inline.values()) adopt(node, value);
    for (const [url, record] of this.#external) record.value.push(appendLink(node, url));
  }

  removeHost(node: Node): void {
    if (!isShadowRoot(node) || !this.#hosts.delete(node)) return;
    unadopt(node, [...this.#inline.values()].map(({ value }) => value));
    for (const record of this.#external.values()) {
      for (const link of record.value.filter((link) => link.parentNode === node)) {
        link.remove();
        record.value.splice(record.value.indexOf(link), 1);
      }
    }
  }

  destroy(): void {
    const sheets = [...this.#inline.values()].map(({ value }) => value);
    for (const host of this.#hosts) unadopt(host, sheets);
    for (const record of this.#external.values()) for (const link of record.value) link.remove();
    this.#hosts.clear();
    this.#inline.clear();
    this.#external.clear();
  }
}

function isShadowRoot(node: Node): node is ShadowRoot {
  return typeof ShadowRoot !== "undefined" && node instanceof ShadowRoot;
}

function adopt(host: ShadowRoot, sheet: CSSStyleSheet): void {
  host.adoptedStyleSheets = [...host.adoptedStyleSheets, sheet];
}

function unadopt(host: ShadowRoot, sheets: readonly CSSStyleSheet[]): void {
  if (sheets.length === 0) return;
  host.adoptedStyleSheets = host.adoptedStyleSheets.filter((sheet) => !sheets.includes(sheet));
}

function appendLink(host: ShadowRoot, url: string): HTMLLinkElement {
  const link = host.ownerDocument.createElement("link");
  link.rel = "stylesheet";
  link.href = url;
  return host.appendChild(link);
}
