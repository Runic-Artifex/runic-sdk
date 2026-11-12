// Trusted component mode (specs/application/README.md, "Runic instances"; host-protocol.md, `region-component/1`): a
// guest's Views as a custom element in the host page's own document. The `component` build target of
// `@runic-artifex/vite-plugin-runic` bundles this module, the guest's framework runtime and its own copy of this
// package into one ES module, which defines the element when it loads.
//
// Each element owns a Runic instance on its own Bridge and mounts the guest's Views in an open shadow root, with the
// bundle's CSS as constructable stylesheets. The bundle's copy of this package defaults to the instance of its one
// connected element (scope.ts), so code outside a provider never reaches the host page's instance. The element touches
// no document-level state: no history, no global listeners, no `<html lang dir>` and no document stylesheets.
import {
  createRunicInstance, detachedSlots, setDefaultSlots, slotsOf, type RunicInstance, type Slots,
} from "./scope.js";
import type { RunicBridgeClient } from "./transport.js";
import { syncDocumentLocale } from "./translated-text.js";

/** Where a component's Bridge connects: the guest's surface and the component token (`region-component/1`). */
export interface RunicComponentEndpoint {
  /** The WebSocket address of the guest's surface. */
  readonly url: string;
  /** The component token, a bearer credential of the guest. */
  readonly token: string;
}

/** What an element connects with: a Bridge, or the endpoint that {@link RunicComponentOptions.connect} turns into one. */
export type RunicComponentConnection = { readonly bridge: RunicBridgeClient } | RunicComponentEndpoint;

/** What a component's `mount` function gets for one element. */
export interface RunicComponentContext {
  /** The custom element, the shadow host. */
  readonly element: HTMLElement;
  /** Its open shadow root, whose adopted stylesheets are the bundle's CSS. */
  readonly root: ShadowRoot;
  /** The element in the shadow root to render the framework's root into. */
  readonly container: HTMLElement;
  /** The element's Runic instance: provide it to the framework binding (`RunicInstanceProvider` and the like). */
  readonly instance: RunicInstance;
  /** The element's tag name, also the prefix for other custom elements the component defines. */
  readonly tag: string;
}

/**
 * Renders the guest's Views into `context.container` within `context.instance`, and returns a function that removes
 * them. It runs inside `context.instance.run`.
 */
export type RunicComponentMount = (context: RunicComponentContext) => void | (() => void);

/** A stylesheet of the bundle: its text and the address that its relative `url()` references resolve against. */
export interface RunicComponentStyle {
  readonly text: string;
  readonly href: string;
}

/** Options of {@link defineRunicComponent}. */
export interface RunicComponentOptions {
  /**
   * The tag name. The build target generates it per guest and build (`<name>-<hash>`). When another definition already
   * holds it, such as the same build loaded from another guest's origin, the element is defined as `<tag>-2`, `<tag>-3`
   * and so on: {@link RunicComponentDefinition.tag} is the name it got.
   */
  readonly tag: string;
  readonly mount: RunicComponentMount;
  readonly styles?: readonly RunicComponentStyle[];
  /** Makes the Bridge of an element connected with an endpoint. Defaults to {@link connectComponentBridge}. */
  readonly connect?: (endpoint: RunicComponentEndpoint) => RunicBridgeClient;
}

/** A defined component. */
export interface RunicComponentDefinition {
  /** The tag name the element was defined with. */
  readonly tag: string;
  readonly element: CustomElementConstructor;
}

/** An element of a component. */
export interface RunicComponentElement extends HTMLElement {
  /**
   * Connects the element. It mounts the component while it is in the document, and again after it was removed and
   * added back. Connecting it again with another connection remounts it.
   */
  connect(connection: RunicComponentConnection): void;
  /** The element's instance while it is mounted. */
  readonly instance: RunicInstance | undefined;
}

/**
 * Turns a component endpoint into a Bridge. The component token flow and its admission (`region-component/1`) are not
 * part of this release yet, so this throws; connect an element with `{ bridge }` instead.
 */
export function connectComponentBridge(endpoint: RunicComponentEndpoint): RunicBridgeClient {
  void endpoint;
  throw new Error("Connecting a component to its guest with a component token (region-component/1) is not available in this release; connect it with { bridge }.");
}

// The connected elements of every component this copy defined; the copy's default instance is the only one's.
const mounted = new Set<{ readonly instance: RunicInstance | undefined }>();
let ambiguityReported = false;
let detached: Slots | undefined;

function bundleDefault(): Slots {
  if (mounted.size === 1) {
    const [only] = mounted;
    if (only?.instance !== undefined) return slotsOf(only.instance);
  }
  if (mounted.size > 1 && !ambiguityReported) {
    ambiguityReported = true;
    console.error("A Runic component is mounted more than once on this page, so code outside a binding's provider and " +
      "outside instance.run has no instance: it is unavailable. Call it inside instance.run or under the provider.");
  }
  return detached ??= detachedSlots();
}

const baseSheetText = ":host { display: block; }";
const relativeUrl = /url\(\s*(["']?)([^"')]*)\1\s*\)/g;

// Relative `url()` references of a stylesheet that the bundle loaded from the guest's origin, made absolute: a
// constructed stylesheet resolves them against the host page otherwise.
function absoluteUrls(text: string, href: string): string {
  return text.replace(relativeUrl, (match, quote: string, reference: string) => {
    if (reference === "" || reference.startsWith("#") || /^[a-z][a-z0-9+.-]*:/i.test(reference)) return match;
    try { return `url(${quote}${new URL(reference, href).href}${quote})`; }
    catch { return match; }
  });
}

/**
 * Defines the custom element of a component and returns its definition. The `component` build target of
 * `@runic-artifex/vite-plugin-runic` calls it with the generated tag and the bundle's CSS, and the guest's component
 * entry provides `mount`:
 *
 * ```tsx
 * // src/component.tsx
 * import { createRoot } from "react-dom/client";
 * import { RunicInstanceProvider } from "@runic-artifex/react";
 * import type { RunicComponentMount } from "@runic-artifex/views/component";
 *
 * const mount: RunicComponentMount = ({ container, instance }) => {
 *   const root = createRoot(container);
 *   root.render(<RunicInstanceProvider instance={instance}><App /></RunicInstanceProvider>);
 *   return () => root.unmount();
 * };
 * export default mount;
 * ```
 */
export function defineRunicComponent(options: RunicComponentOptions): RunicComponentDefinition {
  if (typeof options?.mount !== "function") throw new TypeError("defineRunicComponent needs a mount function.");
  if (typeof options.tag !== "string") throw new TypeError("defineRunicComponent needs a tag name.");
  const registry = globalThis.customElements;
  if (registry === undefined) throw new TypeError("defineRunicComponent needs a document with custom elements.");
  setDefaultSlots(bundleDefault);
  const connectEndpoint = options.connect ?? connectComponentBridge;
  const styles = options.styles ?? [];
  let sheets: CSSStyleSheet[] | undefined;
  // One set of constructed stylesheets for every element of the component; created with the first element, in the
  // document that has it.
  const adoptedSheets = (): CSSStyleSheet[] => sheets ??= [baseSheetText, ...styles.map((style) => absoluteUrls(style.text, style.href))]
    .map((text) => {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(text);
      return sheet;
    });

  let tag = options.tag;
  for (let suffix = 2; registry.get(tag) !== undefined; suffix += 1) tag = `${options.tag}-${suffix}`;
  const definedTag = tag;

  class RunicComponent extends HTMLElement implements RunicComponentElement {
    #connection: RunicComponentConnection | undefined;
    #instance: RunicInstance | undefined;
    #unmount: (() => void) | undefined;
    #teardown = false;
    readonly #root: ShadowRoot;

    constructor() {
      super();
      this.#root = this.attachShadow({ mode: "open" });
      this.#root.adoptedStyleSheets = adoptedSheets();
    }

    get instance(): RunicInstance | undefined { return this.#instance; }

    connect(connection: RunicComponentConnection): void {
      if (connection === null || typeof connection !== "object" ||
        !("bridge" in connection) && (typeof connection.url !== "string" || typeof connection.token !== "string"))
        throw new TypeError("A component connects with { bridge } or { url, token }.");
      this.#stop();
      this.#connection = connection;
      if (this.isConnected) this.#start();
    }

    connectedCallback(): void {
      this.#teardown = false;
      if (this.#connection !== undefined && this.#instance === undefined) this.#start();
    }

    disconnectedCallback(): void {
      // A move within the document disconnects and connects again in one task; only a removal ends the instance.
      this.#teardown = true;
      queueMicrotask(() => { if (this.#teardown && !this.isConnected) this.#stop(); });
    }

    #start(): void {
      const connection = this.#connection!;
      const bridge = "bridge" in connection ? connection.bridge : connectEndpoint(connection);
      const instance = createRunicInstance({ bridge });
      const container = this.ownerDocument.createElement("div");
      this.#root.replaceChildren(container);
      this.#instance = instance;
      mounted.add(this);
      const stopLocale = instance.run(() => syncDocumentLocale({ element: this }));
      let unmount: void | (() => void);
      try {
        unmount = instance.run(() => options.mount({ element: this, root: this.#root, container, instance, tag: definedTag }));
      } catch (error) {
        stopLocale();
        this.#end(instance);
        throw error;
      }
      this.#unmount = () => {
        try { if (typeof unmount === "function") instance.run(unmount); }
        finally { stopLocale(); }
      };
    }

    #stop(): void {
      const instance = this.#instance;
      if (instance === undefined) return;
      const unmount = this.#unmount;
      this.#unmount = undefined;
      try { unmount?.(); }
      catch (error) { console.error(error); }
      this.#end(instance);
    }

    #end(instance: RunicInstance): void {
      mounted.delete(this);
      this.#instance = undefined;
      this.#root.replaceChildren();
      instance.dispose();
    }
  }

  registry.define(definedTag, RunicComponent);
  return Object.freeze({ tag: definedTag, element: RunicComponent });
}
