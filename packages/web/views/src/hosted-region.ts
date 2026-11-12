// <runic-hosted-region> (specs/application/README.md, "Hosted regions"; specs/desktop/host-protocol.md, "Regions"): a
// place in a host page where the host application shows a guest application's Window. The element declares its region
// to the page's own producer over the page's Bridge, reports its size, place, visibility and focus, and shows what the
// producer pushes: a cross-origin frame of the guest's page, or its fallback content. It never talks to the guest, and it
// gives the page no reference to the frame, so a later embedding (component mode) can replace the frame.
//
// One client per page runtime keeps the connected elements by name. The latest connected element of a name owns the
// region; an earlier one shows its fallback until the owner disconnects. Requests for one name go out in order.
import { BridgeError } from "./errors.js";
import { hostedRegionsFeature, isHostedRegionName, maximumHostedRegions, type HostedRegionPush, type HostedRegionPushKind } from "./hosted-region-wire.js";
import type { Push } from "./protocol.js";
import { sharedRuntimeFor, type SharedRuntime } from "./runtime.js";
import { pageInstance, slotsOf } from "./scope.js";
import { reportBridgeError, waitForBridgeIn, type RunicBridgeClient } from "./transport.js";

// The page's Views connection: the only place this module reaches the page runtime. A host page's regions belong to
// the page instance (TC1), whatever instance is current where the element was created.
const pageConnection = {
  waitForBridge: (): Promise<RunicBridgeClient> => waitForBridgeIn(slotsOf(pageInstance())),
  runtime: (bridge: RunicBridgeClient): SharedRuntime => sharedRuntimeFor(slotsOf(pageInstance()), bridge),
  reportError: (error: unknown): void => reportBridgeError(slotsOf(pageInstance()), error),
};

/** The tag of the element. */
export const hostedRegionTag = "runic-hosted-region";

/**
 * What a hosted region shows, reflected in its `state` attribute:
 * - `waiting`: no Window shows in the region yet, or the page is not connected to its host yet;
 * - `loading`: the region's frame loads the guest's page;
 * - `attached`: the frame shows the guest's page;
 * - `detached`: the guest's Window is detached from the region and may come back, as after a lost connection;
 * - `ended`: the guest's Window closed or its application went away;
 * - `unsupported`: the host does not implement hosted regions, the region's name is invalid, or the host pushed an
 *   embedding this page does not know.
 */
export type HostedRegionState = "waiting" | "loading" | "attached" | "detached" | "ended" | "unsupported";

/** How the region's height is set: `fill`, by the page's own layout (the default), or `content-height`, by the guest. */
export type HostedRegionSizing = "fill" | "content-height";

/** The detail of `runicregionstatechange`. */
export interface HostedRegionStateChangeDetail {
  readonly state: HostedRegionState;
  readonly previous: HostedRegionState;
}

/** The detail of `runicregiontitlechange`: the guest page's title, or `null` when it has none. */
export interface HostedRegionTitleChangeDetail {
  readonly title: string | null;
}

/**
 * `<runic-hosted-region name="check">`: a region where the host application shows a guest's Window. Attributes:
 * `name` (1 to 64 ASCII letters, digits, `.`, `_` or `-`), `sizing` (`fill` or `content-height`) and `label`, the
 * region's accessible name, which overrides the guest page's title. It reflects `state` and `focused`, and dispatches
 * `runicregionstatechange` and `runicregiontitlechange`. Light-DOM children with `slot="fallback"` show unless the region
 * is attached; a child with `slot` set to a state (`loading`, `ended`, …) shows instead in that state. `::part(frame)`
 * styles the frame.
 */
export interface RunicHostedRegionElement extends HTMLElement {
  /** The `name` attribute. */
  name: string;
  /** The `sizing` attribute: `fill` unless it is `content-height`. */
  sizing: HostedRegionSizing;
  /** The `label` attribute, or `null`. */
  label: string | null;
  /** What the region shows now. */
  readonly state: HostedRegionState;
  /** The guest page's title as the host passed it on, or `null`. */
  readonly guestTitle: string | null;
  /** Whether the keyboard focus is in the region. */
  readonly focused: boolean;
}

declare global {
  interface HTMLElementTagNameMap {
    "runic-hosted-region": RunicHostedRegionElement;
  }
  interface HTMLElementEventMap {
    runicregionstatechange: CustomEvent<HostedRegionStateChangeDetail>;
    runicregiontitlechange: CustomEvent<HostedRegionTitleChangeDetail>;
  }
}

/**
 * Defines `<runic-hosted-region>` in the page's custom element registry, once: a later call, also of another copy of
 * this package, does nothing. Does nothing where there is no DOM, as during server-side rendering. The framework
 * components call it themselves.
 */
export function defineHostedRegion(): void {
  const registry = (globalThis as { customElements?: CustomElementRegistry }).customElements;
  if (registry === undefined || typeof HTMLElement === "undefined" || registry.get(hostedRegionTag) !== undefined) return;
  registry.define(hostedRegionTag, createElementClass());
}

// The frame's fixed sandbox: script and its own origin, forms; no top-level navigation, pop-ups, modals or downloads.
const frameSandbox = "allow-scripts allow-same-origin allow-forms";
// How long a Tab key press may precede the focus entering the region, and how long the page's own input keeps a focus
// entry from counting as a click into the frame.
const tabWindow = 1_000;
const ownInputWindow = 5_000;
// A report waits for the next animation frame, or at most this long where frames do not run.
const reportFallback = 100;

type FocusSource = "tab" | "shift-tab" | "pointer" | "script";

// The page's own keyboard and pointer input (focusSource), seen before any handler of the page, once per page.
interface InputWatch {
  tab: { readonly shift: boolean; readonly time: number } | undefined;
  lastOwnInput: number;
}

let watching: InputWatch | undefined;

// One watch per copy of this package: it keeps no state of the runtime, only what the page's own input was.
function inputWatch(): InputWatch {
  if (watching) return watching;
  const watch: InputWatch = { tab: undefined, lastOwnInput: -Infinity };
  watching = watch;
  addEventListener("keydown", event => {
    watch.lastOwnInput = performance.now();
    watch.tab = event.key === "Tab" && !event.ctrlKey && !event.altKey && !event.metaKey ? { shift: event.shiftKey, time: performance.now() } : undefined;
  }, true);
  // A Tab that moved the focus to an element of the page itself is spent. The focus that a Tab moves into a frame of
  // another process can arrive after the key's keyup, so the keyup does not end it.
  addEventListener("focusin", event => {
    if ((event.target as Element | null)?.localName !== hostedRegionTag) watch.tab = undefined;
  }, true);
  addEventListener("pointerdown", () => { watch.lastOwnInput = performance.now(); watch.tab = undefined; }, true);
  return watch;
}

// How the focus entered the region (hosted-regions/1, "focusSource"): the Tab key the page just saw, else a click in the
// frame, which gives the page transient user activation without input of its own, else script.
function classifyFocus(watch: InputWatch, programmatic: boolean): FocusSource {
  if (programmatic) return "script";
  const now = performance.now();
  const tab = watch.tab;
  watch.tab = undefined;
  if (tab !== undefined && now - tab.time <= tabWindow) return tab.shift ? "shift-tab" : "tab";
  const activation = (navigator as { userActivation?: { readonly isActive?: boolean } }).userActivation;
  if (activation?.isActive === true && now - watch.lastOwnInput > ownInputWindow) return "pointer";
  return "script";
}

// The element's side of the client.
interface Region {
  readonly regionName: string;
  readonly regionSizing: HostedRegionSizing;
  /** The client's state changed: waiting for a connection, declared, or not available. */
  clientState(state: "waiting" | "declared" | "detached" | "unsupported"): void;
  /** A push for the region's name. */
  receive(push: HostedRegionPush): void;
  /** The report to send now, or undefined when the region cannot report. */
  report(): Readonly<Record<string, unknown>> | undefined;
}

// The client of one page runtime: the connected regions by name, latest last, and the declarations of the connection.
class HostedRegions {
  private readonly regions = new Map<string, Region[]>();
  private readonly declared = new Set<string>();
  // The requests of a name go out one after another.
  private readonly queues = new Map<string, Promise<unknown>>();
  // The last report sent per name, so an unchanged one is not sent again.
  private readonly reported = new Map<string, string>();
  private runtime: SharedRuntime | undefined;
  private starting: Promise<void> | undefined;
  private supported: boolean | undefined;
  private connection = 0;

  add(region: Region): void {
    const list = this.regions.get(region.regionName) ?? [];
    if (list.includes(region)) return;
    const previous = list.at(-1);
    list.push(region);
    this.regions.set(region.regionName, list);
    void this.start();
    if (this.supported === false) { region.clientState("unsupported"); return; }
    region.clientState("waiting");
    // The latest element of a name owns the region: the earlier one's declaration ends, and the host attaches anew.
    if (previous) {
      previous.clientState("detached");
      if (this.declared.delete(region.regionName)) this.send(region.regionName, { kind: "hosted-region-remove", name: region.regionName });
    }
    if (this.runtime?.connected === true && this.supported === true) this.declare(region);
  }

  remove(region: Region): void {
    const list = this.regions.get(region.regionName);
    if (!list) return;
    const index = list.indexOf(region);
    if (index < 0) return;
    const owner = index === list.length - 1;
    list.splice(index, 1);
    if (list.length === 0) this.regions.delete(region.regionName);
    if (!owner) return;
    if (this.declared.delete(region.regionName)) this.send(region.regionName, { kind: "hosted-region-remove", name: region.regionName });
    this.reported.delete(region.regionName);
    // The earlier element of the name owns the region again.
    const next = list.at(-1);
    if (next && this.supported === true && this.runtime?.connected === true) this.declare(next);
  }

  // The region's report changed; sent unless it equals the last one sent.
  report(region: Region): void {
    if (!this.declared.has(region.regionName) || this.owner(region.regionName) !== region) return;
    const report = region.report();
    if (!report) return;
    const text = JSON.stringify(report);
    if (this.reported.get(region.regionName) === text) return;
    this.reported.set(region.regionName, text);
    this.send(region.regionName, { kind: "hosted-region-state", name: region.regionName, ...report });
  }

  private owner(name: string): Region | undefined {
    return this.regions.get(name)?.at(-1);
  }

  private declare(region: Region): void {
    const name = region.regionName;
    if (this.declared.has(name)) return;
    if (this.declared.size >= maximumHostedRegions) { region.clientState("unsupported"); return; }
    this.declared.add(name);
    this.reported.delete(name);
    const connection = this.connection;
    this.send(name, { kind: "hosted-region-declare", name, sizing: region.regionSizing }).then(reply => {
      if (connection !== this.connection || this.owner(name) !== region) return;
      if (reply === undefined) return;
      if (!reply) { this.declared.delete(name); region.clientState("unsupported"); return; }
      region.clientState("declared");
      this.report(region);
    }, () => undefined);
  }

  // Sends a request after the earlier requests of its name; resolves with whether it succeeded, or undefined when the
  // connection was lost.
  private send(name: string, message: Readonly<Record<string, unknown>>): Promise<boolean | undefined> {
    const runtime = this.runtime;
    if (!runtime) return Promise.resolve(undefined);
    const previous = this.queues.get(name) ?? Promise.resolve();
    const sent = previous.then(async () => {
      try {
        const reply = await runtime.request(message);
        return reply.ok;
      } catch (error) {
        if (error instanceof BridgeError && (error.kind === "disconnected" || error.kind === "failed")) return undefined;
        pageConnection.reportError(error);
        return undefined;
      }
    });
    const settled = sent.then(() => undefined, () => undefined);
    this.queues.set(name, settled);
    void settled.then(() => { if (this.queues.get(name) === settled) this.queues.delete(name); });
    return sent;
  }

  private start(): Promise<void> {
    return this.starting ??= (async () => {
      let bridge;
      while (bridge === undefined) {
        try { bridge = await pageConnection.waitForBridge(); }
        catch (error) {
          // Without a Bridge the page shows its regions' fallback; one that does not connect is waited for.
          if (error instanceof BridgeError && error.kind === "unavailable") { this.unsupported(); return; }
        }
      }
      const runtime = pageConnection.runtime(bridge);
      this.runtime = runtime;
      if (typeof runtime.onWindowPush !== "function") { this.unsupported(); return; }
      for (const kind of ["hosted-region-content", "hosted-region-update", "hosted-region-empty", "hosted-region-focus"] satisfies HostedRegionPushKind[])
        runtime.onWindowPush(kind, push => this.receive(push));
      runtime.onConnection(connected => { void this.connected(connected); });
      await this.connected(runtime.connected);
    })();
  }

  private async connected(connected: boolean): Promise<void> {
    const runtime = this.runtime!;
    const connection = ++this.connection;
    this.declared.clear();
    this.reported.clear();
    this.queues.clear();
    if (!connected) {
      for (const list of this.regions.values()) list.at(-1)?.clientState("detached");
      return;
    }
    try { await (runtime.ready ?? runtime.whenGreeted)?.call(runtime); }
    catch { return; }
    if (connection !== this.connection) return;
    this.supported = runtime.features?.has(hostedRegionsFeature) === true;
    if (!this.supported) { this.unsupported(); return; }
    for (const list of this.regions.values()) {
      const owner = list.at(-1);
      if (owner) this.declare(owner);
    }
  }

  private unsupported(): void {
    this.supported = false;
    for (const list of this.regions.values()) for (const region of list) region.clientState("unsupported");
  }

  private receive(push: Push): void {
    const regionPush = push as HostedRegionPush;
    if (!this.declared.has(regionPush.name)) return;
    this.owner(regionPush.name)?.receive(regionPush);
  }
}

let pageClient: HostedRegions | undefined;

// The client of the page, one per copy of this package; the runtime it uses is the page's.
function client(): HostedRegions {
  return pageClient ??= new HostedRegions();
}

function createElementClass(): CustomElementConstructor {
  return class HostedRegionElement extends HTMLElement implements RunicHostedRegionElement, Region {
    static readonly observedAttributes = ["name", "sizing", "label"];

    readonly #shadow: ShadowRoot;
    readonly #content: HTMLDivElement;
    readonly #fallback: HTMLDivElement;
    #state: HostedRegionState = "waiting";
    #registered: { readonly name: string; readonly sizing: HostedRegionSizing } | undefined;
    #frame: HTMLIFrameElement | undefined;
    #guestTitle: string | null = null;
    #height: number | undefined;
    #focused = false;
    #focusSource: FocusSource | null = null;
    #programmatic = false;
    #visible = false;
    #scheduled = false;
    #stops: (() => void)[] = [];

    constructor() {
      super();
      this.#shadow = this.attachShadow({ mode: "closed" });
      const style = document.createElement("style");
      style.textContent = `
:host { display: block; position: relative; }
:host([hidden]) { display: none; }
.content { display: contents; }
iframe { display: block; box-sizing: border-box; border: 0; width: 100%; height: 100%; }
.content.loading iframe { visibility: hidden; }
.fallback.over { position: absolute; inset: 0; }
`;
      this.#content = document.createElement("div");
      this.#content.className = "content";
      this.#fallback = document.createElement("div");
      this.#fallback.className = "fallback";
      this.#fallback.setAttribute("part", "fallback");
      this.#shadow.append(style, this.#content, this.#fallback);
      // Attributes only from connectedCallback on: a constructor that document.createElement runs may not add any.
      this.#renderShadow();
    }

    get state(): HostedRegionState { return this.#state; }
    get guestTitle(): string | null { return this.#guestTitle; }
    get focused(): boolean { return this.#focused; }
    get regionName(): string { return this.#registered?.name ?? ""; }
    get regionSizing(): HostedRegionSizing { return this.#registered?.sizing ?? "fill"; }

    // Reflected attributes, so frameworks that set properties (React 19, Vue, Angular) and ones that set attributes agree.
    get name(): string { return this.getAttribute("name") ?? ""; }
    set name(value: string) { this.setAttribute("name", value); }
    get sizing(): HostedRegionSizing { return this.getAttribute("sizing") === "content-height" ? "content-height" : "fill"; }
    set sizing(value: HostedRegionSizing) { this.setAttribute("sizing", value); }
    get label(): string | null { return this.getAttribute("label"); }
    set label(value: string | null | undefined) {
      if (value === null || value === undefined) this.removeAttribute("label");
      else this.setAttribute("label", value);
    }

    connectedCallback(): void {
      this.#render();
      this.#register();
      if (this.#stops.length > 0) return;
      const watch = inputWatch();
      const schedule = () => this.#schedule();
      const resize = new ResizeObserver(schedule);
      resize.observe(this);
      const intersection = new IntersectionObserver(entries => {
        const last = entries.at(-1);
        if (last) this.#visible = last.isIntersecting;
        schedule();
      });
      intersection.observe(this);
      let resolution: MediaQueryList | undefined;
      const watchResolution = () => {
        resolution?.removeEventListener("change", onResolution);
        resolution = matchMedia(`(resolution: ${devicePixelRatio}dppx)`);
        resolution.addEventListener("change", onResolution);
      };
      const onResolution = () => { watchResolution(); schedule(); };
      watchResolution();
      // The focus is in the region when the page's focused element is the region (its frame, retargeted). Engines tell
      // it differently: a Tab or a programmatic focus fires focusin on the element, while a click into a cross-origin
      // frame of another process (Chromium, WebView2) fires only the window's blur, after which the region is the
      // active element.
      const gain = () => {
        if (this.#focused || document.activeElement !== this) return;
        this.#focused = true;
        this.#focusSource = classifyFocus(watch, this.#programmatic);
        this.toggleAttribute("focused", true);
        schedule();
      };
      // Checked after the focus moved: between focusout and the next focusin the page's active element is its body. A
      // window that loses the focus keeps the region as its active element, and the region keeps the page's focus.
      const lose = () => {
        setTimeout(() => {
          if (this.#focused && document.activeElement !== this) {
            this.#focused = false;
            this.#focusSource = null;
            this.toggleAttribute("focused", false);
            schedule();
          }
          gain();
        }, 0);
      };
      const windowFocus = (event: Event) => { if (event.target === globalThis) { gain(); lose(); } };
      this.addEventListener("focusin", gain);
      this.addEventListener("focusout", lose);
      addEventListener("blur", windowFocus, true);
      addEventListener("focus", windowFocus, true);
      document.addEventListener("focusin", lose);
      addEventListener("scroll", schedule, { capture: true, passive: true });
      addEventListener("resize", schedule);
      this.#stops = [
        () => resize.disconnect(),
        () => intersection.disconnect(),
        () => resolution?.removeEventListener("change", onResolution),
        () => this.removeEventListener("focusin", gain),
        () => this.removeEventListener("focusout", lose),
        () => removeEventListener("blur", windowFocus, true),
        () => removeEventListener("focus", windowFocus, true),
        () => document.removeEventListener("focusin", lose),
        () => removeEventListener("scroll", schedule, { capture: true }),
        () => removeEventListener("resize", schedule),
      ];
    }

    disconnectedCallback(): void {
      for (const stop of this.#stops.splice(0)) stop();
      this.#unregister();
      // A frame launch address is one-time: a frame that stayed in the tree would load it again when the element is
      // inserted again.
      this.#removeFrame();
      this.#focused = false;
      this.#focusSource = null;
      this.toggleAttribute("focused", false);
      this.#setState("waiting");
    }

    attributeChangedCallback(name: string, previous: string | null, value: string | null): void {
      if (previous === value) return;
      if (name === "label") { this.#applyTitle(); return; }
      // An upgrade reports the attributes before connectedCallback, which registers the element.
      if (!this.isConnected || this.#stops.length === 0) return;
      // A new name or sizing is a new declaration.
      this.#unregister();
      this.#removeFrame();
      this.#register();
    }

    #register(): void {
      const name = this.getAttribute("name");
      const sizing: HostedRegionSizing = this.getAttribute("sizing") === "content-height" ? "content-height" : "fill";
      if (!isHostedRegionName(name)) { this.#setState("unsupported"); return; }
      this.#registered = { name, sizing };
      client().add(this);
    }

    #unregister(): void {
      if (!this.#registered) return;
      client().remove(this);
      this.#registered = undefined;
    }

    clientState(state: "waiting" | "declared" | "detached" | "unsupported"): void {
      if (state === "declared") { if (this.#state === "unsupported") this.#setState("waiting"); return; }
      this.#removeFrame();
      this.#setState(state);
    }

    receive(push: HostedRegionPush): void {
      switch (push.kind) {
        case "hosted-region-content":
          if (push["embedding"] !== "frame") {
            this.#removeFrame();
            this.#setState("unsupported");
            return;
          }
          this.#guestTitle = typeof push["title"] === "string" ? push["title"] : null;
          this.#showFrame(push["src"] as string, push["allow"] as string);
          this.dispatchEvent(new CustomEvent("runicregiontitlechange", { detail: { title: this.#guestTitle } }));
          return;
        case "hosted-region-update":
          if (Object.hasOwn(push, "title")) {
            this.#guestTitle = typeof push["title"] === "string" ? push["title"] : null;
            this.#applyTitle();
            this.dispatchEvent(new CustomEvent("runicregiontitlechange", { detail: { title: this.#guestTitle } }));
          }
          if (typeof push["height"] === "number" && this.regionSizing === "content-height") {
            this.#height = push["height"];
            this.#applyHeight();
          }
          return;
        case "hosted-region-empty": {
          this.#removeFrame();
          const reason = push["reason"];
          this.#setState(reason === "waiting" || reason === "ended" ? reason : "detached");
          return;
        }
        case "hosted-region-focus": {
          // The host moves the focus into the region only while the page holds none (host-protocol.md, "Focus in a
          // region"); such focus counts as the guest's.
          const active = document.activeElement;
          if (!this.#frame || active !== null && active !== document.body && active !== this) return;
          this.#programmatic = true;
          try { this.#frame.focus(); }
          finally { this.#programmatic = false; }
          return;
        }
      }
    }

    report(): Readonly<Record<string, unknown>> | undefined {
      if (!this.isConnected) return undefined;
      const rect = (this.#frame ?? this).getBoundingClientRect();
      const clamp = (value: number, minimum: number, maximum: number) =>
        Math.min(maximum, Math.max(minimum, Math.round((Number.isFinite(value) ? value : 0) * 100) / 100));
      const hidden = typeof this.checkVisibility === "function" ? !this.checkVisibility({ visibilityProperty: true }) : false;
      return {
        width: clamp(rect.width, 0, 32_767),
        height: clamp(rect.height, 0, 32_767),
        scale: clamp(devicePixelRatio, 0.25, 8),
        x: clamp(rect.left, -32_768, 32_767),
        y: clamp(rect.top, -32_768, 32_767),
        visible: this.#visible && !hidden,
        focused: this.#focused,
        focusSource: this.#focused ? this.#focusSource : null,
      };
    }

    #schedule(): void {
      if (this.#scheduled || !this.#registered) return;
      this.#scheduled = true;
      let timer: ReturnType<typeof setTimeout> | undefined;
      let frame: number | undefined;
      const send = () => {
        if (frame !== undefined) cancelAnimationFrame(frame);
        clearTimeout(timer);
        this.#scheduled = false;
        if (this.#registered) client().report(this);
      };
      frame = requestAnimationFrame(send);
      timer = setTimeout(send, reportFallback);
    }

    #showFrame(src: string, allow: string): void {
      this.#removeFrame();
      const frame = document.createElement("iframe");
      frame.setAttribute("sandbox", frameSandbox);
      frame.setAttribute("allow", allow);
      frame.setAttribute("referrerpolicy", "no-referrer");
      frame.setAttribute("part", "frame");
      frame.addEventListener("load", () => {
        if (this.#frame === frame && this.#state === "loading") this.#setState("attached");
      });
      this.#frame = frame;
      this.#applyTitle();
      this.#applyHeight();
      this.#content.append(frame);
      this.#setState("loading");
      // Set last: the address loads once, in the frame as it is in the tree.
      frame.src = src;
      this.#schedule();
    }

    #removeFrame(): void {
      const frame = this.#frame;
      if (!frame) return;
      this.#frame = undefined;
      frame.remove();
      this.#schedule();
    }

    #applyTitle(): void {
      if (!this.#frame) return;
      const label = this.getAttribute("label");
      this.#frame.title = label !== null && label.length > 0 ? label : this.#guestTitle ?? "";
    }

    #applyHeight(): void {
      if (!this.#frame) return;
      this.#frame.style.height = this.regionSizing === "content-height" && this.#height !== undefined ? `${this.#height}px` : "";
    }

    #setState(state: HostedRegionState): void {
      const previous = this.#state;
      this.#state = state;
      this.#render();
      if (previous === state) return;
      this.dispatchEvent(new CustomEvent("runicregionstatechange", { detail: { state, previous } }));
    }

    #render(): void {
      this.setAttribute("state", this.#state);
      this.#renderShadow();
    }

    #renderShadow(): void {
      this.#content.classList.toggle("loading", this.#state === "loading");
      this.#fallback.classList.toggle("over", this.#state === "loading");
      if (this.#state === "attached") { this.#fallback.replaceChildren(); return; }
      // The slot of the state, with the fallback slot as its own fallback content.
      const current = this.#fallback.firstElementChild as HTMLSlotElement | null;
      if (current?.name !== this.#state) {
        const stateSlot = document.createElement("slot");
        stateSlot.name = this.#state;
        const fallbackSlot = document.createElement("slot");
        fallbackSlot.name = "fallback";
        stateSlot.append(fallbackSlot);
        this.#fallback.replaceChildren(stateSlot);
      }
    }
  };
}
