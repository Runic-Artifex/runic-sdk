// Runs Runic Desktop's WebKitGTK keyboard script in Chromium: its sequential focus
// order must match Chromium's own Tab order, the wrap must respect focus traps and
// controls that cannot take focus, and only trusted events may wrap or zoom.
// Needs PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH (the dev shell and CI's install-browser
// set it). Run with: bun run test tests/native/Runic.Desktop.WebViewSmoke/keyboard-script.test.mjs
import { afterAll, beforeAll, beforeEach, describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { chromium } from "playwright-core";

const script = readFileSync(new URL("../../../packages/dotnet/Runic.Desktop/Internal/WebKitGtkKeyboard.js", import.meta.url), "utf8");
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
if (!executablePath) throw new Error("Set PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH to a Chromium executable.");

let browser;
let page;
beforeAll(async () => { browser = await chromium.launch({ executablePath, headless: true }); });
afterAll(async () => { await browser?.close(); });
beforeEach(async () => { page = await browser.newPage(); });

async function load(body) {
  await page.setContent(`<!doctype html><html><body>${body}</body></html>`);
  await page.addScriptTag({ content: script });
}

const deepActive = () => page.evaluate(() => {
  let active = document.activeElement;
  while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
  return active && active !== document.body ? active.id : "none";
});

const sequence = () => page.evaluate(() => runicDesktopKeyboard.sequence().map(element => element.id));

// Chromium's own order: Tab from an unfocused page, once per control.
async function chromiumOrder(count) {
  const order = [];
  for (let step = 0; step < count; step++) {
    await page.keyboard.press("Tab");
    order.push(await deepActive());
  }
  return order;
}

const ordering = `
  <button id="zero-first">0</button>
  <button id="two" tabindex="2">2</button>
  <button id="one" tabindex="1">1</button>
  <div inert><button id="inert">inert</button></div>
  <button id="disabled" disabled>disabled</button>
  <fieldset disabled><button id="in-disabled-fieldset">fieldset</button></fieldset>
  <button id="invisible" style="visibility: hidden">invisible</button>
  <div hidden><button id="hidden">hidden</button></div>
  <a id="link" href="#x">link</a>
  <span id="negative" tabindex="-1">negative</span>
  <div id="editable" contenteditable="true">editable</div>
  <button id="zero-last">0</button>`;

const shadow = `
  <button id="before">before</button>
  <div id="host"><button id="light-three" tabindex="3">light 3</button><button id="light-zero">light 0</button></div>
  <button id="after">after</button>
  <script>
    const root = document.getElementById("host").attachShadow({ mode: "open" });
    root.innerHTML = '<button id="shadow-two" tabindex="2">s2</button><slot></slot><button id="shadow-one" tabindex="1">s1</button><button id="shadow-zero">s0</button>';
  </script>`;

describe("sequential focus order", () => {
  test("positive tabindex first, skipping inert, disabled, hidden and negative controls", async () => {
    await load(ordering);
    const expected = ["one", "two", "zero-first", "link", "editable", "zero-last"];
    expect(await sequence()).toEqual(expected);
    expect(await chromiumOrder(expected.length)).toEqual(expected);
  });

  test("an open shadow root and its slot are scopes in place of their host and slot", async () => {
    await load(shadow);
    const expected = ["before", "shadow-one", "shadow-two", "light-three", "light-zero", "shadow-zero", "after"];
    expect(await sequence()).toEqual(expected);
    expect(await chromiumOrder(expected.length)).toEqual(expected);
  });

  test("frames are left out", async () => {
    await load(`<button id="first">first</button><iframe id="frame" srcdoc="<button>inner</button>"></iframe>`);
    expect(await sequence()).toEqual(["first"]);
  });

  test("an open modal dialog is the only scope", async () => {
    await load(`<button id="outside">outside</button><dialog id="dialog"><button id="d1">1</button><button id="d2">2</button></dialog>`);
    await page.evaluate(() => document.getElementById("dialog").showModal());
    expect(await sequence()).toEqual(["d1", "d2"]);
  });
});

describe("wrap", () => {
  const wrap = backward => page.evaluate(backward => {
    document.activeElement?.blur();
    runicDesktopKeyboard.wrap(backward);
  }, backward);

  test("focuses the first or last control", async () => {
    await load(ordering);
    await wrap(false);
    expect(await deepActive()).toBe("one");
    await wrap(true);
    expect(await deepActive()).toBe("zero-last");
  });

  test("enters a radio group at its checked button", async () => {
    await load(`<input type="radio" name="r" id="r1"><input type="radio" name="r" id="r2" checked><input type="radio" name="r" id="r3"><button id="after">after</button>`);
    await wrap(false);
    expect(await deepActive()).toBe("r2");
    await wrap(true);
    expect(await deepActive()).toBe("after");
  });

  test("focuses into an open shadow root", async () => {
    await load(shadow);
    await wrap(true);
    expect(await deepActive()).toBe("after");
    await page.evaluate(() => document.getElementById("before").remove());
    await wrap(false);
    expect(await deepActive()).toBe("shadow-one");
  });

  test("stays inside an open modal dialog", async () => {
    await load(`<button id="outside">outside</button><dialog id="dialog"><button id="d1">1</button><button id="d2">2</button></dialog>`);
    await page.evaluate(() => document.getElementById("dialog").showModal());
    await wrap(false);
    expect(await deepActive()).toBe("d1");
    await wrap(true);
    expect(await deepActive()).toBe("d2");
  });

  test("accepts a focus trap that moves focus elsewhere", async () => {
    await load(`<button id="first">first</button><button id="second">second</button><button id="trap">trap</button>`);
    const calls = await page.evaluate(() => {
      let calls = 0;
      const first = document.getElementById("first");
      const focus = first.focus.bind(first);
      first.focus = options => { calls++; focus(options); };
      first.addEventListener("focus", () => document.getElementById("trap").focus());
      document.activeElement?.blur();
      runicDesktopKeyboard.wrap(false);
      return calls;
    });
    expect(calls).toBe(1);
    expect(await deepActive()).toBe("trap");
  });

  test("moves on from a control that cannot take focus, a bounded number of times", async () => {
    await load(Array.from({ length: 12 }, (_, index) => `<button id="b${index}">${index}</button>`).join(""));
    const tried = await page.evaluate(() => {
      const tried = [];
      for (const button of document.querySelectorAll("button").values().take(2)) button.focus = () => tried.push(button.id);
      document.activeElement?.blur();
      runicDesktopKeyboard.wrap(false);
      return tried;
    });
    expect(tried).toEqual(["b0", "b1"]);
    expect(await deepActive()).toBe("b2");

    const attempts = await page.evaluate(() => {
      let attempts = 0;
      for (const button of document.querySelectorAll("button")) button.focus = () => attempts++;
      document.activeElement?.blur();
      runicDesktopKeyboard.wrap(false);
      return attempts;
    });
    expect(attempts).toBe(8);
    expect(await deepActive()).toBe("none");
  });
});

describe("trusted events only", () => {
  const scrollable = `<button id="first">first</button><button id="last">last</button><div style="height: 300vh"></div>
    <script>window.zoom = []; window.webkit = { messageHandlers: { runicZoom: { postMessage: message => zoom.push(message) } } };</script>`;

  test("a forged Tab does not wrap", async () => {
    await load(scrollable);
    await page.focus("#last");
    await page.evaluate(() => {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Tab", bubbles: true }));
      document.getElementById("last").blur();
    });
    await page.waitForTimeout(100);
    expect(await deepActive()).toBe("none");
  });

  test("Control and the wheel zooms without scrolling; a forged wheel does nothing", async () => {
    await load(scrollable);
    await page.mouse.move(100, 100);
    await page.evaluate(() => {
      window.dispatchEvent(new KeyboardEvent("keydown", { key: "Control" }));
      document.body.dispatchEvent(new WheelEvent("wheel", { deltaY: 100, ctrlKey: true, bubbles: true, cancelable: true }));
    });
    expect(await page.evaluate(() => zoom)).toEqual([]);
    await page.keyboard.down("Control");
    await page.mouse.wheel(0, 120);
    await page.mouse.wheel(0, -120);
    await page.keyboard.up("Control");
    await page.waitForTimeout(100);
    expect(await page.evaluate(() => zoom)).toEqual([-1, 1]);
    expect(await page.evaluate(() => scrollY)).toBe(0);
  });

  test("a page that cancels Control and the wheel keeps it", async () => {
    await load(scrollable + `<script>document.addEventListener("wheel", event => { if (event.ctrlKey) event.preventDefault(); }, { passive: false });</script>`);
    await page.mouse.move(100, 100);
    await page.keyboard.down("Control");
    await page.mouse.wheel(0, 120);
    await page.keyboard.up("Control");
    await page.waitForTimeout(100);
    expect(await page.evaluate(() => zoom)).toEqual([]);
  });
});
