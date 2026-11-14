import { appearance, onAppearance } from "@runic-artifex/views";
import { defineHostedRegion } from "@runic-artifex/views/experimental";
import { connectInstaller } from "./generated/installer";

// <runic-hosted-region>: it reports its size, visibility and focus to the installer's backend over this page's Bridge,
// and shows the frame or the content for its state.
defineHostedRegion();

const installer = await connectInstaller();
const element = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const steps = ["welcome", "check", "finish"] as const;
const design = element<HTMLSelectElement>("design");
const back = element<HTMLButtonElement>("back");
const next = element<HTMLButtonElement>("next");

// The installer's own design: a chosen one as it is, "System" from the page's appearance. The check's page maps its
// appearance in the same way, with the installer's choice as its host's (Check/Frontend/src/app.ts).
const applyDesign = () => {
  const chosen = installer.snapshot.design;
  const look = appearance();
  const root = document.documentElement;
  root.style.colorScheme = chosen === "dark" ? "dark" : chosen === "system" ? look.colorScheme ?? "" : "light";
  root.dataset["contrast"] = chosen === "high-contrast" ? "more" : chosen === "system" ? look.contrast ?? "no-preference" : "no-preference";
};
onAppearance(applyDesign);

const render = () => {
  const state = installer.snapshot;
  design.value = state.design;
  applyDesign();
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

design.addEventListener("change", async () => { await installer.chooseDesign(design.value); });
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
