declare global {
  var __runicToolkit_sveltekit_e2e_result:
    | ((result: string) => Promise<unknown>)
    | undefined;
}

const deadlineMs = 30_000;

export function runNativeE2E(): void {
  const deadline = Date.now() + deadlineMs;
  let reported = false;

  const report = (result: string): void => {
    if (reported) return;
    const binding = globalThis.__runicToolkit_sveltekit_e2e_result;
    if (binding === undefined) {
      if (Date.now() < deadline) globalThis.setTimeout(() => report(result), 25);
      return;
    }
    reported = true;
    void binding(result).catch(() => undefined);
  };

  const drive = (): void => {
    const root = document.querySelector<HTMLElement>("[data-e2e-view]");
    const bootError = globalThis.__runicBootError;
    if (root === null) {
      if (bootError) report(`error|boot|${bootError}`);
      else if (Date.now() < deadline) requestAnimationFrame(drive);
      else report("error|timeout|SvelteKit did not mount");
      return;
    }

    const view = root.dataset.e2eView ?? "unknown";
    const progress = root.dataset.e2eProgress ?? "0";
    const status = root.dataset.e2eStatus ?? "unknown";
    const error = document.querySelector<HTMLElement>(".error")?.textContent?.trim();
    if (error) {
      report(`error|${view}|${error}`);
      return;
    }
    if (view === "Complete" && progress === "5") {
      report("pass|Complete|5");
      return;
    }
    if (Date.now() >= deadline) {
      report(`error|timeout|${view}|${progress}|${status}`);
      return;
    }
    if (status !== "connected") {
      requestAnimationFrame(drive);
      return;
    }

    if (view === "Welcome") click("next");
    else if (view === "Destination") {
      const description = document.querySelector("section p")?.textContent ?? "";
      click(description.includes("No destination") ? "select" : "next");
    } else if (view === "Features") click("install");
    requestAnimationFrame(drive);
  };

  requestAnimationFrame(drive);
}

function click(name: string): void {
  const button = document.querySelector<HTMLButtonElement>(`[data-e2e="${name}"]`);
  if (button !== null && !button.disabled) button.click();
}
