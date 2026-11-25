import { mountAboutPanel } from "@acme/shared-ui";
import type { SupportClient } from "@acme/shared-ui/generated";
import { connectShell } from "./generated/shell.js";

const status = document.querySelector<HTMLElement>("#status")!;

function mountSupport(support: SupportClient): void {
  const report = document.querySelector<HTMLButtonElement>("#report")!;
  const reports = document.querySelector<HTMLElement>("#reports")!;
  support.subscribe(state => {
    reports.textContent = `${state.reportsSent} reports sent`;
    report.disabled = !state.canReportProblem;
  });
  report.addEventListener("click", () => { support.reportProblem().catch(error => { status.textContent = String(error); }); });
}

try {
  const shell = await connectShell();
  document.querySelector("#title")!.textContent = shell.snapshot.title;
  // `about` and `support` are page references whose types and page functions come from @acme/shared-ui/generated:
  // this application generated no client for them.
  mountAboutPanel(document.querySelector<HTMLElement>("#about")!, await shell.snapshot.about.connect());
  mountSupport(await shell.snapshot.support.connect());
  status.textContent = "Connected.";
} catch (error) {
  status.textContent = String(error);
}
