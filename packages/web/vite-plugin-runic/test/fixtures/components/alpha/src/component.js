// The alpha guest's component: React, mounted in the element's shadow root within the element's instance.
import { createElement, useState } from "react";
import { createRoot } from "react-dom/client";
import { RunicInstanceProvider, useView } from "@runic-artifex/react";
import { defaultInstance } from "@runic-artifex/views";
import { counter } from "../../counter.js";
import "./style.css";

function Counter() {
  const view = useView(counter);
  // An event handler runs outside the provider and outside instance.run: the bundle's default instance answers.
  const [owner, setOwner] = useState("");
  return createElement("div", null,
    createElement("span", { className: "label" }, `alpha ${view.state?.count ?? "…"}`),
    createElement("span", { className: "badge" }),
    createElement("button", { onClick: () => setOwner(String(defaultInstance().bridge?.name)) }, "owner"),
    createElement("output", null, owner));
}

export default function mount({ container, instance }) {
  const root = createRoot(container);
  root.render(createElement(RunicInstanceProvider, { instance }, createElement(Counter)));
  return () => root.unmount();
}
