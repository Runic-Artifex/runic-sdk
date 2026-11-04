import { connectCounter } from "./generated/counter";

const counter = await connectCounter();
const count = document.querySelector<HTMLOutputElement>("#count")!;
const increment = document.querySelector<HTMLButtonElement>("#increment")!;
const hide = document.querySelector<HTMLButtonElement>("#hide")!;
const render = () => { count.value = String(counter.snapshot.count); };
counter.subscribe(render);
render();
increment.addEventListener("click", async () => { await counter.increment(); });
hide.addEventListener("click", async () => { await counter.hide(); });
