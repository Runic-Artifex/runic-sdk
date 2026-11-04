import { connectDocument } from "./generated/document";

// A document Window: IRunicWindowService.FindOrOpenAsync opens one per path and focuses it on the next request.
const doc = await connectDocument();
const path = document.querySelector<HTMLHeadingElement>("#path")!;
const project = document.querySelector<HTMLOutputElement>("#project")!;

const render = () => {
  const state = doc.snapshot;
  path.textContent = state.path;
  document.title = state.path;
  project.value = state.projectTitle;
};
doc.subscribe(render);
render();

document.querySelector("#close")!.addEventListener("click", async () => { await doc.close(); });
