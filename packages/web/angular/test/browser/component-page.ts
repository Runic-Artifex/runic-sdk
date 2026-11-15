// The page of component.browser.test.ts: an Angular guest's component (runicComponentMount) defined with
// defineRunicComponent, as the `component` build target does, on a host page with a strict Content-Security-Policy.
// Like the unit tests it runs the partially compiled package in JIT mode; examples/hosted-components builds the same
// with Vite and the Analog plugin, ahead of time.
import "@angular/compiler";
import { Component, ViewEncapsulation } from "@angular/core";
import { defaultInstance } from "@runic-artifex/views";
import { defineRunicComponent, type RunicComponentElement } from "@runic-artifex/views/component";
import { connectView } from "@runic-artifex/views/generated";
import * as wire from "@runic-artifex/views/generated/wire";
import { createMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import { injectView } from "@runic-artifex/angular";
import { runicComponentMount } from "@runic-artifex/angular/experimental";

type State = { readonly count: number };
const counter = {
  connect: () => connectView<State>({ contract: "Tests.Counter:fp", route: "counter", members: { count: value => wire.integer(value, 0, 1_000) } }),
};

class Badge {}
// A child with no encapsulation: its styles apply in the whole shadow root, never in the host page.
Component({ selector: "test-badge", standalone: true, encapsulation: ViewEncapsulation.None, template: `<i class="badge">badge</i>`,
  styles: `.badge { font-style: normal; text-decoration: underline; }` })(Badge);

class Counter {
  readonly view = injectView(counter);
  owner = "";
  // An event handler runs outside the injection context: the bundle's default instance is the element's.
  whose(): void { this.owner = String((defaultInstance().bridge as { name?: string } | undefined)?.name); }
}
Component({
  selector: "test-counter",
  standalone: true,
  imports: [Badge],
  template: `<span class="label">count {{ view.state()?.count ?? "…" }}</span><test-badge />
    <button type="button" (click)="whose()">owner</button><output>{{ owner }}</output>`,
  styles: `:host { display: block; border-inline-start: 3px solid var(--host-accent, black); } .label { color: rgb(200, 0, 0); font-weight: 700; }`,
})(Counter);

const definition = defineRunicComponent({
  tag: "test-angular-guest-1a2b3c4d",
  styles: [{ text: ".bundle { color: rgb(0, 128, 0); }", href: location.href }],
  mount: runicComponentMount(Counter),
});

const guests = new Map<string, { element: RunicComponentElement; mock: MockBridge; counter: ReturnType<MockBridge["view"]> }>();
const componentTest = {
  tag: definition.tag,
  snapshot() {
    return {
      head: document.head.innerHTML,
      html: [...document.documentElement.attributes].map(attribute => `${attribute.name}=${attribute.value}`),
      sheets: document.styleSheets.length,
      adopted: document.adoptedStyleSheets.length,
      body: [...document.body.children].map(child => child.tagName.toLowerCase()),
    };
  },
  mount(key: string, count: number) {
    const mock = createMockBridge();
    const view = mock.view("counter", { state: { count } });
    const bridge = {
      name: key,
      isConnected: () => mock.isConnected(),
      request: (message: string) => mock.request(message),
      onMessage: (listener: (message: string) => void) => mock.onMessage(listener),
      onConnectionChange: (listener: (connected: boolean) => void) => mock.onConnectionChange(listener),
    };
    const element = document.createElement(definition.tag) as RunicComponentElement;
    element.id = key;
    element.connect({ bridge });
    document.getElementById("slot")!.append(element);
    guests.set(key, { element, mock, counter: view });
  },
  update(key: string, count: number) { guests.get(key)!.counter.update({ count }); },
  text(key: string) { return guests.get(key)!.element.shadowRoot!.textContent ?? ""; },
  styles(key: string) {
    const root = guests.get(key)!.element.shadowRoot!;
    const style = (selector: string) => getComputedStyle(root.querySelector(selector)!);
    return {
      color: style(".label").color, weight: style(".label").fontWeight, decoration: style(".badge").textDecorationLine,
      border: getComputedStyle(root.firstElementChild!).borderInlineStartColor, styleElements: root.querySelectorAll("style, link").length,
      adopted: root.adoptedStyleSheets.length,
    };
  },
  async click(key: string) {
    const root = guests.get(key)!.element.shadowRoot!;
    root.querySelector("button")!.click();
    for (let attempt = 0; attempt < 100 && root.querySelector("output")!.textContent === ""; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    return root.querySelector("output")!.textContent;
  },
  async remove(key: string) {
    const { element } = guests.get(key)!;
    element.remove();
    await new Promise(resolve => setTimeout(resolve, 0));
    return { instance: element.instance === undefined, children: element.shadowRoot!.childNodes.length, adopted: element.shadowRoot!.adoptedStyleSheets.length };
  },
  async again(key: string) {
    const { element } = guests.get(key)!;
    document.getElementById("slot")!.append(element);
    for (let attempt = 0; attempt < 100 && !/count \d/.test(element.shadowRoot!.textContent ?? ""); attempt++) {
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    return element.shadowRoot!.adoptedStyleSheets.length;
  },
};
Object.assign(globalThis, { componentTest, testApi: () => componentTest });
