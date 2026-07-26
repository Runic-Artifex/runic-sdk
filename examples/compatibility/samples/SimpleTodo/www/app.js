const form = document.querySelector("#composer");
const titleInput = document.querySelector("#new-title");
const validation = document.querySelector("#validation");
const tasks = document.querySelector("#tasks");
const emptyState = document.querySelector("#empty-state");
const stats = document.querySelector("#stats");

let busy = false;

function taskRow(item) {
  const row = document.createElement("li");
  row.className = item.isCompleted ? "task completed" : "task";

  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "toggle";
  toggle.textContent = item.isCompleted ? "✓" : "○";
  toggle.setAttribute("aria-label", item.isCompleted ? "Mark as active" : "Mark as complete");
  toggle.addEventListener("click", () => run(() => webui.call("todoToggle", item.id)));

  const text = document.createElement("span");
  text.className = "task-title";
  text.textContent = item.title;

  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "remove";
  remove.textContent = "Remove";
  remove.setAttribute("aria-label", `Remove ${item.title}`);
  remove.addEventListener("click", () => run(() => webui.call("todoRemove", item.id)));

  row.append(toggle, text, remove);
  return row;
}

function render(json) {
  const state = JSON.parse(json);
  tasks.replaceChildren(...state.items.map(taskRow));
  emptyState.hidden = state.items.length !== 0;
  stats.textContent = `${state.remaining} remaining · ${state.completed} completed`;
  validation.hidden = !state.error;
  validation.textContent = state.error ?? "";

  if (!state.error) {
    titleInput.value = state.draft;
  }
}

async function run(action) {
  if (busy) return;
  busy = true;
  document.body.classList.add("busy");
  try {
    render(await action());
  } catch {
    validation.hidden = false;
    validation.textContent = "The desktop backend could not complete the action.";
  } finally {
    busy = false;
    document.body.classList.remove("busy");
  }
}

form.addEventListener("submit", event => {
  event.preventDefault();
  run(() => webui.call("todoAdd", titleInput.value));
});

run(() => webui.call("todoSnapshot"));
