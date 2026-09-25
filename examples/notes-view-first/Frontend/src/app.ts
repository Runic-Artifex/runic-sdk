import { mountContent, type ViewTemplates } from "./content.js";
import { connectShell, type ShellState } from "./generated/shell.js";
import { mountSidebar } from "./sidebar.js";
import { mountHome } from "./home.js";
import { mountDocument } from "./document.js";
import { mountConfirmNavigation } from "./modal.js";

const sidebarHost = document.querySelector<HTMLElement>("#sidebar")!;
const mainHost = document.querySelector<HTMLElement>("#main")!;
const modalHost = document.querySelector<HTMLElement>("#modal")!;
const status = document.querySelector<HTMLElement>("#status")!;

// Each outlet chooses its own component and connection lifetime. This is a
// framework-neutral mount contract; Angular/Svelte can supply their own mount
// adapters without changing the generated ViewModel contracts.
const sidebarViews = { sidebar: mountSidebar } satisfies ViewTemplates<ShellState["sidebar"]>;
const mainViews = {
  home: mountHome,
  document: mountDocument,
} satisfies ViewTemplates<ShellState["main"]>;
const dialogViews = {
  confirmNavigation: mountConfirmNavigation,
} satisfies ViewTemplates<NonNullable<ShellState["dialog"]>>;

function report(error: unknown): void {
  status.textContent = String(error);
  status.classList.add("error");
}

try {
  const shell = await connectShell();
  const unmountSidebar = mountContent(sidebarHost, shell, "sidebar", sidebarViews, report);
  const unmountMain = mountContent(mainHost, shell, "main", mainViews, report);
  const unmountDialog = mountContent(modalHost, shell, "dialog", dialogViews, report);
  const unsubscribe = shell.subscribe(state => {
    modalHost.hidden = state.dialog === null;
  });
  status.textContent = "Connected.";
  window.addEventListener("beforeunload", () => {
    unsubscribe();
    unmountDialog();
    unmountMain();
    unmountSidebar();
    shell.dispose();
  });
} catch (error) {
  report(error);
}
