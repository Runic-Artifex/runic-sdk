import { mountContent, type ViewTemplates } from "./content.js";
import { connectShell, type ShellState } from "./generated/shell.js";
import { mountHome } from "./home.js";
import { mountDocument } from "./document.js";

const main = document.querySelector<HTMLElement>("#main")!;
const status = document.querySelector<HTMLElement>("#status")!;
const homeButton = document.querySelector<HTMLButtonElement>("[data-go=home]")!;
const documentButton = document.querySelector<HTMLButtonElement>("[data-go=document]")!;
const views = { home: mountHome, document: mountDocument } satisfies ViewTemplates<NonNullable<ShellState["main"]>>;

function report(error: unknown): void { status.textContent = String(error); status.classList.add("error"); }

try {
  const shell = await connectShell();
  const unmount = mountContent(main, shell, "main", views, report);
  // Home is the shell's Back: the document's leave guard decides whether it commits.
  const unsubscribe = shell.subscribe(state => {
    homeButton.setAttribute("aria-current", state.main?.kind === "home" ? "page" : "false");
    documentButton.setAttribute("aria-current", state.main?.kind === "document" ? "page" : "false");
    homeButton.disabled = !state.canOpenHome;
    documentButton.disabled = !state.canOpenDocument;
  });
  homeButton.addEventListener("click", () => { void shell.openHome().catch(report); });
  documentButton.addEventListener("click", () => { void shell.openDocument().catch(report); });
  status.textContent = "Connected.";
  window.addEventListener("beforeunload", () => { unsubscribe(); unmount(); shell.dispose(); });
} catch (error) { report(error); }
