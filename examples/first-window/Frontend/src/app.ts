import { connectCounter, type CounterState } from "./generated/counter.js";

const count = document.querySelector<HTMLElement>("#count")!;
const step = document.querySelector<HTMLInputElement>("#step")!;
const increment = document.querySelector<HTMLButtonElement>("#increment")!;
const status = document.querySelector<HTMLElement>("#status")!;
let editingStep = false;

try {
  const counter = await connectCounter();
  function render(state: CounterState): void {
    count.textContent = String(state.count);
    if (!editingStep) step.value = String(state.step);
    increment.disabled = !state.canIncrement;
  }
  counter.subscribe(render);
  status.textContent = "Connected to the .NET ViewModel.";

  increment.addEventListener("click", async () => {
    try {
      await counter.increment();
      status.textContent = "Incremented.";
    } catch (error) {
      status.textContent = String(error);
    }
  });

  step.addEventListener("focus", () => { editingStep = true; });
  step.addEventListener("blur", async () => {
    try {
      await counter.setStep(Number(step.value));
      status.textContent = "Step updated.";
    } catch (error) {
      status.textContent = String(error);
    } finally {
      editingStep = false;
      render(counter.snapshot);
    }
  });
} catch (error) {
  status.textContent = `Connection failed: ${String(error)}`;
}
