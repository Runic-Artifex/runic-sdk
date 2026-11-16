// The Runic DevTools extension's view models: what the panel and the Elements sidebar show, derived from Runic
// Inspect's `snapshot()` and `describe(element)` (vite-plugin-runic's inspect-snapshot.ts and inspect-data.ts). Plain
// data and strings, no DOM, so they are testable on their own; render.js draws them with text nodes only.

/** A duration in milliseconds as the panel shows it. */
export function formatDuration(milliseconds) {
  if (typeof milliseconds !== "number" || !Number.isFinite(milliseconds)) return "";
  if (milliseconds < 0.1) return "<0.1 ms";
  if (milliseconds < 10) return `${milliseconds.toFixed(1)} ms`;
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`;
  return `${(milliseconds / 1000).toFixed(2)} s`;
}

/** A short, single-line rendering of a state value. */
export function formatValue(value, maximum = 120) {
  let text;
  if (value === undefined) text = "";
  else if (typeof value === "string") text = JSON.stringify(value);
  else {
    try { text = JSON.stringify(value) ?? String(value); } catch { text = String(value); }
  }
  return text.length > maximum ? `${text.slice(0, maximum - 1)}…` : text;
}

/** `EditorViewModel.cs:42` for a location of Runic Inspect. */
export function locationLabel(location) {
  if (typeof location?.file !== "string") return "";
  const name = location.file.split(/[\\/]/u).pop();
  return typeof location.line === "number" ? `${name}:${location.line}` : name;
}

/** The C# ViewModel's short name from a source map id or a contract: `Demo.EditorViewModel:F1` → `EditorViewModel`. */
export function shortType(value) {
  if (typeof value !== "string" || value.length === 0) return "";
  return value.split(":", 1)[0].split(".").pop();
}

/** What the panel says when the page has no data. */
export function statusMessage(status) {
  switch (status) {
    case "missing":
      return "No Runic development client on this page. Runic Inspect is part of vite-plugin-runic's development client: run the page under `vite dev` or `dotnet runic dev` (a production build has none), and keep runic({ inspect }) on.";
    case "unsupported":
      return "This page's vite-plugin-runic is older than the extension: update it to 0.21 or later for the panel's data.";
    default:
      return "";
  }
}

/** The panel's rows of the page's Views. */
export function viewRows(snapshot) {
  return (snapshot?.views ?? []).map((view) => ({
    key: view.route,
    route: view.route,
    viewModel: view.viewModel?.name ?? shortType(view.viewModel?.id ?? view.contract),
    symbol: view.viewModel?.symbol ?? "",
    location: view.viewModel?.location,
    revision: typeof view.revision === "number" ? String(view.revision) : "",
    presentations: typeof view.presentations === "number" ? view.presentations : undefined,
    members: stateMembers(view.state),
  }));
}

/** A state's members as `name` and short value rows. */
export function stateMembers(state) {
  if (typeof state !== "object" || state === null || Array.isArray(state)) {
    return state === undefined || state === null ? [] : [{ name: "(value)", value: formatValue(state) }];
  }
  return Object.entries(state).map(([name, value]) => ({ name, value: formatValue(value) }));
}

/** The panel's rows of the page's recent Bridge calls, newest first. */
export function callRows(snapshot) {
  return (snapshot?.calls ?? []).map((call) => {
    const model = shortType(call.viewModel ?? call.contract) || call.route;
    const outcome = typeof call.outcome === "string" ? call.reason === undefined ? call.outcome : `${call.outcome} (${call.reason})`
      : "running";
    return {
      key: String(call.id),
      id: call.id,
      label: `${model}.${call.member}`,
      kind: call.kind,
      route: call.route,
      outcome,
      state: typeof call.outcome === "string" ? call.outcome : "running",
      duration: formatDuration(call.duration),
      revision: typeof call.revision === "number" ? String(call.revision) : "",
      handler: call.handler === undefined ? "" : `${call.handler.name} (${formatDuration(call.handler.duration)})`,
      symbol: call.symbol ?? "",
      location: call.location,
    };
  });
}

/**
 * The Elements sidebar's sections for a description of the selected element: its component, ViewModels, bound members,
 * commands, recent calls and texts, each item with an optional location to open, and the notes.
 */
export function describeSections(description) {
  if (description === undefined || description === null) return [];
  const sections = [];
  const element = description.element ?? {};
  const name = `${element.tag ?? "element"}${element.id ? `#${element.id}` : ""}${(element.classes ?? []).map((name) => `.${name}`).join("")}`;
  sections.push({ title: "Element", items: [{ text: name, detail: element.text ? formatValue(element.text, 80) : "" }] });
  if (description.component) {
    const component = description.component;
    sections.push({
      title: "Component",
      items: [{
        text: `${component.name} (${component.framework})`,
        detail: (component.owners ?? []).length > 0 ? `in ${component.owners.join(" › ")}` : "",
        location: component.location,
      }],
    });
  }
  const viewModels = description.viewModels ?? [];
  if (viewModels.length > 0) {
    sections.push({
      title: "ViewModels",
      items: viewModels.map((viewModel) => ({
        text: viewModel.name || shortType(viewModel.id ?? viewModel.contract) || viewModel.route,
        detail: `route ${viewModel.route}`,
        location: viewModel.location,
      })),
    });
  }
  const members = description.members ?? [];
  if (members.length > 0) {
    sections.push({
      title: "Bindings",
      items: members.map((member) => ({
        text: `${member.member} = ${formatValue(member.value)}`,
        detail: [member.kind, member.evidence === "call" ? "seen in a call" : `matched by ${member.evidence}`, member.symbol]
          .filter(Boolean).join(" · "),
        location: member.location,
      })),
    });
  }
  const commands = description.commands ?? [];
  if (commands.length > 0) {
    sections.push({
      title: "Commands",
      items: commands.map((command) => ({
        text: command.symbol ?? `${shortType(command.viewModel) || command.route}.${command.member}`,
        detail: [
          command.evidence === "source" ? "found in the component's source" : `${command.calls} call${command.calls === 1 ? "" : "s"}`,
          command.canExecute === undefined ? "" : `can execute: ${formatValue(command.canExecute)}`,
        ].filter(Boolean).join(" · "),
        location: command.location,
        frontend: command.frontend,
      })),
    });
  }
  const calls = description.calls ?? [];
  if (calls.length > 0) {
    sections.push({
      title: "Recent calls",
      items: calls.map((call) => ({
        text: `${call.kind} ${call.member}`,
        detail: [call.outcome ?? "running", formatDuration(call.duration), typeof call.revision === "number" ? `rev ${call.revision}` : ""]
          .filter(Boolean).join(" · "),
      })),
    });
  }
  const texts = [...(description.texts ?? []).map((text) => ({ text: formatValue(text.text, 80), detail: `${text.catalog}/${text.key} (${text.path})` })),
    ...(description.messages ?? []).map((message) => ({
      text: `${message.catalog}/${message.key}`, detail: [message.locale, message.in.join(", ")].join(" · "), location: message.location,
    }))];
  if (texts.length > 0) sections.push({ title: "Texts", items: texts });
  const notes = description.notes ?? [];
  if (notes.length > 0) sections.push({ title: "Notes", items: notes.map((note) => ({ text: note, note: true })) });
  return sections;
}
