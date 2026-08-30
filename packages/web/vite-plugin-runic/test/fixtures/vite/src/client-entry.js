import { createRunicDiagnosticReporter } from "virtual:runic/client";

createRunicDiagnosticReporter("assets").report({
  kind: "event",
  label: "Asset fixture loaded",
});
