import { reportRunicDiagnostic } from "virtual:runic/client";
import { revision } from "./hmr-value.js";

reportRunicDiagnostic({
  source: "translations",
  kind: "event",
  label: "SSR fixture loaded",
});

export { revision };
