// A React page as the runic-app template's: the framework binding, a generated client and a Translations runtime.
import "./bridge.js";
import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { useCommand, useText, useView } from "@runic-artifex/react";
import { configureTranslatedText, describeBridgeError, syncDocumentLocale } from "@runic-artifex/views";
import { connectCounter } from "../../../../../../../examples/first-window/Frontend/src/generated/counter.ts";

syncDocumentLocale();
configureTranslatedText({ resolve: reference => reference.fallbackText ?? reference.key, getLocale: () => "en" });

const counter = { connect: connectCounter };

function App() {
  const { state, client, error } = useView(counter);
  const increment = useCommand(() => client?.increment());
  const title = useText({ version: 2, catalog: "app", contractFingerprint: "sha256:1", key: "title", arguments: {}, fallbackText: "Counter" });
  return createElement("main", null,
    createElement("h1", null, title),
    createElement("p", { className: "count" }, String(state?.count ?? "…")),
    createElement("button", { disabled: !client, onClick: () => void increment.run() }, "Increment"),
    createElement("p", { className: "error" }, error === undefined ? "" : describeBridgeError(error)));
}

createRoot(document.getElementById("app")).render(createElement(App));
