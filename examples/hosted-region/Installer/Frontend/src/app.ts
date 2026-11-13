import { defineHostedRegion } from "@runic-artifex/views/experimental";
import { connectInstaller } from "./generated/installer";

// <runic-hosted-region>: it reports its size, visibility and focus to the installer's backend over this page's Bridge,
// and shows the frame or the content for its state.
defineHostedRegion();

const installer = await connectInstaller();
const element = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const steps = ["welcome", "check", "finish"] as const;
const back = element<HTMLButtonElement>("back");
const next = element<HTMLButtonElement>("next");

const render = () => {
  const state = installer.snapshot;
  for (const step of steps) {
    element(step).hidden = state.step !== step;
    const item = element(`step-${step}`);
    if (state.step === step) item.setAttribute("aria-current", "step");
    else item.removeAttribute("aria-current");
  }
  // The check page's title, as the check reported it: "System check: passed" shows as "(passed)".
  element("check-title").textContent = state.checkTitle.startsWith("System check: ") ? `(${state.checkTitle.slice(14)})` : "";
  element("check-waiting").textContent = state.checkState.startsWith("failed")
    ? `The system check could not start (${state.checkState.slice(8)}).`
    : "Starting the system check…";
  element("check-note").textContent = state.checkNote;
  element<HTMLOutputElement>("check-event").value = state.checkEvent;
  back.disabled = !state.canBack;
  next.disabled = !state.canNext;
};
installer.subscribe(render);
render();

back.addEventListener("click", async () => { await installer.back(); });
next.addEventListener("click", async () => { await installer.next(); });
element("restart-check").addEventListener("click", async () => { await installer.restartCheck(); });

// The element's own events: its state, as the page shows it, and the guest page's title, which also names the frame
// (its accessible name) unless the element has a label.
const region = document.querySelector("runic-hosted-region")!;
region.addEventListener("runicregionstatechange", event => {
  const { state, previous } = (event as CustomEvent<{ state: string; previous: string }>).detail;
  console.debug(`check region: ${previous} → ${state}`);
});
