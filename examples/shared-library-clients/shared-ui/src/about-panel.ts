// The about panel, shared by every application that presents SharedUi's AboutViewModel. It types the connection with
// the client that the SharedUi build generated into this package, so a changed ViewModel is a type error here.
import { UpdateChannelValues, type AboutClient, type AboutState } from "./generated/runic-clients.js";

/** Renders the about panel into `host` and returns its cleanup, which disposes `about`. */
export function mountAboutPanel(host: HTMLElement, about: AboutClient): () => void {
  host.innerHTML = `<h2></h2>
    <p class="version"></p>
    <label>Channel <select class="channel"></select></label>
    <button class="check">Check for updates</button>
    <p class="update-status" role="status"></p>`;
  const heading = host.querySelector("h2")!;
  const version = host.querySelector<HTMLElement>(".version")!;
  const channel = host.querySelector<HTMLSelectElement>(".channel")!;
  const check = host.querySelector<HTMLButtonElement>(".check")!;
  const status = host.querySelector<HTMLElement>(".update-status")!;
  channel.append(...UpdateChannelValues.map(value => new Option(value, value)));

  const render = (state: AboutState) => {
    heading.textContent = `About ${state.productName}`;
    version.textContent = `Version ${state.version}`;
    channel.value = state.channel;
    check.disabled = !state.canCheckForUpdates;
    status.textContent = state.status;
  };
  const report = (error: unknown) => { status.textContent = String(error); };
  // A check waits for the channel write before it, so it checks the channel the user chose.
  let pending: Promise<unknown> = Promise.resolve();
  const onChannel = () => { pending = about.setChannel(channel.value as AboutState["channel"]).catch(report); };
  const onCheck = () => { pending = pending.then(() => about.checkForUpdates()).catch(report); };
  channel.addEventListener("change", onChannel);
  check.addEventListener("click", onCheck);
  const unsubscribe = about.subscribe(render);
  return () => {
    channel.removeEventListener("change", onChannel);
    check.removeEventListener("click", onCheck);
    unsubscribe();
    about.dispose();
    host.replaceChildren();
  };
}
