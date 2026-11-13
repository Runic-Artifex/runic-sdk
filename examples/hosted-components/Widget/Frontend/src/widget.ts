import { connectWidget } from "./generated/widget";

// The widget's view, for its own page (main.ts) and its component (component.ts). It connects the Window's ViewModel in
// the Runic instance that is current when it is called: the page's, or in a component the element's own.
export async function renderWidget(container: HTMLElement, signal?: AbortSignal): Promise<string> {
  const widget = await connectWidget({ signal });
  signal?.addEventListener("abort", () => widget.dispose(), { once: true });

  const card = container.ownerDocument.createElement("section");
  card.className = "widget";
  const heading = container.ownerDocument.createElement("h2");
  const status = container.ownerDocument.createElement("p");
  const ping = container.ownerDocument.createElement("button");
  ping.type = "button";
  card.append(heading, status, ping);
  container.replaceChildren(card);

  const render = () => {
    const state = widget.snapshot;
    heading.textContent = state.name;
    status.textContent = `Process ${state.processId}, running for ${state.seconds} s`;
    ping.textContent = `Ping (${state.pings})`;
    ping.disabled = !state.canPing;
  };
  widget.subscribe(render);
  render();
  ping.addEventListener("click", async () => { await widget.ping(); });
  return widget.snapshot.name;
}
