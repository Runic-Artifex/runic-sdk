import { connectAccount } from "./generated/account";

// The window-modal "choose account" dialog: choosing completes its result and closes it; Cancel closes it without one.
const account = await connectAccount();
const list = document.querySelector<HTMLUListElement>("#accounts")!;

const render = () => {
  const state = account.snapshot;
  list.replaceChildren(...state.accounts.map(name => {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = name === state.current ? `${name} (current)` : name;
    button.addEventListener("click", async () => { await account.choose(name); });
    item.append(button);
    return item;
  }));
};
account.subscribe(render);
render();

document.querySelector("#cancel")!.addEventListener("click", async () => { await account.cancel(); });
